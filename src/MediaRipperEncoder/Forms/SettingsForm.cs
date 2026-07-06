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

        // Advanced tab: network / mapped-drive rip source.
        private CheckBox _networkRipEnabled;
        private TextBox _networkRipSource;
        private CheckBox _networkRipSearchSubfolders;

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

            // --- Advanced tab: network / mapped-drive rip source ---
            var advancedTab = new TabPage("Advanced");
            advancedTab.Controls.Add(BuildAdvancedPanel(settings));

            tabs.TabPages.Add(generalTab);
            tabs.TabPages.Add(advancedTab);

            layout.Controls.Add(tabs, 0, 0);
            layout.Controls.Add(BuildButtonBar(), 0, 1);

            Controls.Add(layout);
        }

        private Control BuildAdvancedPanel(AppSettings settings)
        {
            var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14) };

            var heading = new Label
            {
                Text = "Network / mapped-drive rip source",
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(14, 14)
            };

            var blurb = new Label
            {
                Text = "For a machine with no optical drive (e.g. a headless server): point MakeMKV at a " +
                       "disc shared from another PC over your LAN — a mapped drive root (Z:\\), a mounted " +
                       "disc folder, or an ISO image. Because the drive is on another machine, AutoRipper " +
                       "cannot eject it: when a rip finishes it will prompt you to change the disc on the " +
                       "shared drive instead. Leave this off for normal local-disc ripping.",
                AutoSize = false,
                Location = new Point(14, 40),
                Size = new Size(680, 60)
            };

            _networkRipEnabled = new CheckBox
            {
                Text = "Enable network / mapped-drive rip source",
                AutoSize = true,
                Location = new Point(14, 108),
                Checked = settings.NetworkRipEnabled
            };

            var srcLabel = new Label { Text = "Source (drive, folder, or .iso):", AutoSize = true, Location = new Point(14, 142) };
            _networkRipSource = new TextBox
            {
                Location = new Point(14, 162),
                Size = new Size(560, 23),
                Text = settings.NetworkRipSource ?? "",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var browse = new Button { Text = "Browse...", Location = new Point(584, 161), Size = new Size(90, 25), Anchor = AnchorStyles.Top | AnchorStyles.Right };
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
                Location = new Point(14, 196),
                Checked = settings.NetworkRipSearchSubfolders
            };

            var subBlurb = new Label
            {
                Text = "Recommended. Blu-rays store the disc under a BDMV folder and DVDs under VIDEO_TS, " +
                       "so the exact target changes each time you swap disc types on the shared drive. " +
                       "With this on, point at the drive root (e.g. Y:\\) once and AutoRipper finds the " +
                       "right folder automatically at scan time.",
                AutoSize = false,
                Location = new Point(32, 218),
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
            _settings.NetworkRipEnabled = _networkRipEnabled.Checked;
            _settings.NetworkRipSource = (_networkRipSource.Text ?? "").Trim();
            _settings.NetworkRipSearchSubfolders = _networkRipSearchSubfolders.Checked;

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

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
