using System;
using System.Drawing;
using System.Windows.Forms;
using MediaRipperEncoder.Forms.Controls;
using MediaRipperEncoder.Models;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Forms
{
    /// <summary>
    /// Re-editable settings dialog, opened from the main window's Tools → Settings menu.
    /// Hosts the same shared editor as the first-run wizard. Unlike the wizard, this allows
    /// saving even when something doesn't currently validate — the user may be mid-way
    /// through fixing a moved path — but it warns first so a broken save is never silent.
    ///
    /// Layout mirrors the wizard: a fixed button bar docked at the bottom (always visible)
    /// with the fields scrolling above it, so Save/Cancel can never be hidden behind the
    /// fields when the window is shrunk.
    /// </summary>
    public class SettingsForm : BaseForm
    {
        private readonly AppSettings _settings;
        private readonly SettingsEditorControl _editor;

        // Advanced tab: scratch-folder housekeeping.
        private CheckBox _deleteScratchAfterHandoff;

        // Advanced tab: network / mapped-drive rip source.
        private CheckBox _networkRipEnabled;
        private TextBox _networkRipSource;
        private CheckBox _networkRipSearchSubfolders;

        // Advanced tab: HandBrake presets (shared panel, also used by the Setup Wizard).
        private PresetPathsPanel _presetPanel;

        // Appearance tab.
        private ComboBox _themeCombo;

        // Music tab.
        private ComboBox _musicFormatCombo;

        public SettingsForm(AppSettings settings)
        {
            _settings = settings;

            Text = "Settings — " + AppInfo.DisplayName;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ClientSize = new Size(760, 720);
            MinimumSize = new Size(520, 340);

            // Inner content panel owns scrolling; disable form-level auto-scroll from BaseForm
            // so the docked button bar stays put.
            AutoScroll = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // scrolling content
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); // fixed button bar

            var tabs = new TabControl { Dock = DockStyle.Fill };

            // --- General tab: the shared editor, in a scrolling panel ---
            var generalTab = new TabPage("General");
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            _editor = new SettingsEditorControl { Location = new Point(0, 10) };
            _editor.LoadFrom(settings);
            content.Controls.Add(_editor);
            generalTab.Controls.Add(content);

            // --- Appearance tab: theme ---
            var appearanceTab = new TabPage("Appearance");
            appearanceTab.Controls.Add(BuildAppearancePanel(settings));

            // --- Music tab: output format ---
            var musicTab = new TabPage("Music");
            musicTab.Controls.Add(BuildMusicPanel(settings));

            // --- Advanced tab: network / mapped-drive rip source ---
            var advancedTab = new TabPage("Advanced");
            advancedTab.Controls.Add(BuildAdvancedPanel(settings));

            tabs.TabPages.Add(generalTab);
            tabs.TabPages.Add(appearanceTab);
            tabs.TabPages.Add(musicTab);
            tabs.TabPages.Add(advancedTab);

            layout.Controls.Add(tabs, 0, 0);
            layout.Controls.Add(BuildButtonBar(), 0, 1);

            Controls.Add(layout);
        }

        private Control BuildAppearancePanel(AppSettings settings)
        {
            var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14) };

            var heading = new Label
            {
                Text = "Theme",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(14, 14)
            };

            var themeLabel = new Label { Text = "Window theme:", AutoSize = true, Location = new Point(14, 46) };
            _themeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(120, 42),
                Size = new Size(240, 23)
            };
            // Order MUST match the ThemePreference enum (System=0, Light=1, Dark=2).
            _themeCombo.Items.AddRange(new object[]
            {
                "Match Windows setting",
                "Light",
                "Dark"
            });
            _themeCombo.SelectedIndex = (int)settings.Theme;

            var blurb = new Label
            {
                Text = "Applies to all AutoRipper windows as soon as you click Save — no restart needed. " +
                       "\"Match Windows setting\" follows the Windows light/dark app mode (on Windows 7/8, " +
                       "which has no such setting, it means Light). A few Windows-drawn details (tab headers, " +
                       "progress bars) always stay light — that's a Windows limitation, not a broken theme.",
                AutoSize = false,
                Location = new Point(14, 78),
                Size = new Size(660, 64)
            };

            panel.Controls.Add(heading);
            panel.Controls.Add(themeLabel);
            panel.Controls.Add(_themeCombo);
            panel.Controls.Add(blurb);
            return panel;
        }

        private Control BuildMusicPanel(AppSettings settings)
        {
            var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14) };

            var heading = new Label
            {
                Text = "Music ripping (audio CDs)",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(14, 14)
            };

            var blurb = new Label
            {
                Text = "Audio CD ripping is BUILT IN — no extra program needed. Insert a music CD and use " +
                       "\"Scan disc && configure\" like any other disc: the album is identified from the disc " +
                       "itself (MusicBrainz), you confirm the edition and tracks, and files land in your Music " +
                       "library folder with full tags and cover art.",
                AutoSize = false,
                Location = new Point(14, 40),
                Size = new Size(660, 64)
            };

            var formatLabel = new Label { Text = "Output format:", AutoSize = true, Location = new Point(14, 112) };
            _musicFormatCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(120, 108),
                Size = new Size(280, 23)
            };
            System.Collections.Generic.List<Services.Music.MusicFormat> formats = Services.Music.MusicFormat.All();
            int selected = 0;
            for (int i = 0; i < formats.Count; i++)
            {
                _musicFormatCombo.Items.Add(formats[i]);
                if (formats[i].FormatId.Equals(settings.MusicFormatId ?? "flac", StringComparison.OrdinalIgnoreCase))
                {
                    selected = i;
                }
            }
            _musicFormatCombo.SelectedIndex = selected;

            var formatBlurb = new Label
            {
                Text = "FLAC is the archival choice: identical audio to the CD at roughly two-thirds the size, " +
                       "and every media server plays it. \"(tested)\" marks formats verified against a real CD — " +
                       "additional formats may arrive in future updates.",
                AutoSize = false,
                Location = new Point(14, 144),
                Size = new Size(660, 48)
            };

            panel.Controls.Add(heading);
            panel.Controls.Add(blurb);
            panel.Controls.Add(formatLabel);
            panel.Controls.Add(_musicFormatCombo);
            panel.Controls.Add(formatBlurb);
            return panel;
        }

        private Control BuildAdvancedPanel(AppSettings settings)
        {
            var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14) };

            // ---- HandBrake presets (shared panel — same fields appear in the Setup Wizard) ----
            _presetPanel = new PresetPathsPanel { Location = new Point(0, 14) };
            _presetPanel.LoadFrom(settings);
            panel.Controls.Add(_presetPanel);

            int py = _presetPanel.Bottom;
            var presetDivider = new Label { BorderStyle = BorderStyle.Fixed3D, Location = new Point(14, py + 6), Size = new Size(680, 2), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            panel.Controls.Add(presetDivider);

            // ---- Scratch folder housekeeping ----
            var houseHeading = new Label
            {
                Text = "Scratch folder housekeeping",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(14, py + 20)
            };
            _deleteScratchAfterHandoff = new CheckBox
            {
                Text = "Delete a raw rip once it is safely encoded or sent to the encoder server",
                AutoSize = true,
                Location = new Point(14, py + 46),
                Checked = settings.DeleteScratchAfterHandoff
            };
            var houseBlurb = new Label
            {
                Text = "Recommended. Raw rips are big — several GB for a DVD and far more for a Blu-ray — " +
                       "so a machine ripping disc after disc fills its drive quickly. The file is only ever " +
                       "removed once its content is safe elsewhere: encoded and placed in your library, or " +
                       "fully transferred and verified by the encoder server. Untick to keep every raw rip " +
                       "and clean the folder out yourself.",
                AutoSize = false,
                Location = new Point(32, py + 68),
                Size = new Size(650, 62)
            };
            panel.Controls.Add(houseHeading);
            panel.Controls.Add(_deleteScratchAfterHandoff);
            panel.Controls.Add(houseBlurb);

            var houseDivider = new Label { BorderStyle = BorderStyle.Fixed3D, Location = new Point(14, py + 136), Size = new Size(680, 2), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            panel.Controls.Add(houseDivider);

            int netTop = py + 150;

            var heading = new Label
            {
                Text = "Network / mapped-drive rip source",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(14, netTop)
            };

            var blurb = new Label
            {
                Text = "For a machine with no optical drive (e.g. a headless server): point MakeMKV at a " +
                       "disc shared from another PC over your LAN — a mapped drive root (Z:\\), a mounted " +
                       "disc folder, or an ISO image. Because the drive is on another machine, AutoRipper " +
                       "cannot eject it: when a rip finishes it will prompt you to change the disc on the " +
                       "shared drive instead. Leave this off for normal local-disc ripping.",
                AutoSize = false,
                Location = new Point(14, netTop + 26),
                Size = new Size(680, 60)
            };

            _networkRipEnabled = new CheckBox
            {
                Text = "Enable network / mapped-drive rip source",
                AutoSize = true,
                Location = new Point(14, netTop + 94),
                Checked = settings.NetworkRipEnabled
            };

            var srcLabel = new Label { Text = "Source (drive, folder, or .iso):", AutoSize = true, Location = new Point(14, netTop + 128) };
            _networkRipSource = new TextBox
            {
                Location = new Point(14, netTop + 148),
                Size = new Size(560, 23),
                Text = settings.NetworkRipSource ?? "",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var browse = new Button { Text = "Browse...", Location = new Point(584, netTop + 147), Size = new Size(90, 25), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            browse.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "Pick the mapped drive or mounted disc folder" })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK) { _networkRipSource.Text = dlg.SelectedPath; }
                }
            };

            _networkRipSearchSubfolders = new CheckBox
            {
                Text = "Auto-find the disc structure (BDMV / VIDEO_TS) in subfolders",
                AutoSize = true,
                Location = new Point(14, netTop + 182),
                Checked = settings.NetworkRipSearchSubfolders
            };

            var subBlurb = new Label
            {
                Text = "Recommended. Blu-rays store the disc under a BDMV folder and DVDs under VIDEO_TS, " +
                       "so the exact target changes each time you swap disc types on the shared drive. " +
                       "With this on, point at the drive root (e.g. Y:\\) once and AutoRipper finds the " +
                       "right folder automatically at scan time.",
                AutoSize = false,
                Location = new Point(32, netTop + 204),
                Size = new Size(650, 48)
            };

            panel.Controls.Add(heading);
            panel.Controls.Add(blurb);
            panel.Controls.Add(_networkRipEnabled);
            panel.Controls.Add(srcLabel);
            panel.Controls.Add(_networkRipSource);
            panel.Controls.Add(browse);
            panel.Controls.Add(_networkRipSearchSubfolders);
            panel.Controls.Add(subBlurb);
            return panel;
        }

        private Control BuildButtonBar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 9, 12, 0),
                WrapContents = false
            };

            var saveButton = new Button
            {
                Text = "Save",
                Size = new Size(110, 32)
            };
            saveButton.Click += OnSaveClicked;

            var cancelButton = new Button
            {
                Text = "Cancel",
                Size = new Size(90, 32),
                DialogResult = DialogResult.Cancel
            };

            bar.Controls.Add(saveButton);
            bar.Controls.Add(cancelButton);

            CancelButton = cancelButton;
            return bar;
        }

        private void OnSaveClicked(object sender, EventArgs e)
        {
            _editor.ApplyTo(_settings);

            // Advanced tab fields aren't part of the shared editor — apply them here.
            _presetPanel.ApplyTo(_settings);
            _settings.DeleteScratchAfterHandoff = _deleteScratchAfterHandoff.Checked;
            _settings.NetworkRipEnabled = _networkRipEnabled.Checked;
            _settings.NetworkRipSource = (_networkRipSource.Text ?? "").Trim();
            _settings.NetworkRipSearchSubfolders = _networkRipSearchSubfolders.Checked;

            _settings.Theme = (ThemePreference)_themeCombo.SelectedIndex;

            var musicFormat = _musicFormatCombo.SelectedItem as Services.Music.MusicFormat;
            _settings.MusicFormatId = musicFormat != null ? musicFormat.FormatId : "flac";

            // Node role/host/port/secret are deliberately NOT touched here — Edit > ES Connection
            // owns them now, so this dialog can never clobber what was configured there.

            bool ok = _editor.RunFullValidation();
            if (!ok)
            {
                DialogResult answer = MessageBox.Show(this,
                    "Some items didn't pass validation (see the status lines above). You can " +
                    "save anyway and fix them later, but ripping/encoding won't work correctly " +
                    "until every tool validates.\r\n\r\nSave anyway?",
                    "Validation warnings",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (answer != DialogResult.Yes)
                {
                    return;
                }
            }

            try
            {
                SettingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Couldn't save your settings:\r\n\r\n" + ex.Message,
                    "Save failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Theme applies live to every open window — the visible confirmation the save took.
            ThemeManager.Initialize(_settings.Theme);
            ThemeManager.ApplyToAllOpenForms();

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
