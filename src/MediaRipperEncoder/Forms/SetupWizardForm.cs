using System;
using System.Drawing;
using System.Windows.Forms;
using MediaRipperEncoder.Forms.Controls;
using MediaRipperEncoder.Models;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Forms
{
    /// <summary>
    /// First-run setup wizard. Hosts the shared settings editor and gates completion behind
    /// a full validation pass: the user can't finish setup with a broken tool path, which is
    /// the whole point of the wizard — catch configuration problems here rather than 20
    /// minutes into a rip.
    ///
    /// Layout: a fixed button bar is docked to the bottom and is always visible, while the
    /// fields scroll in the region above it. This guarantees the Finish/Cancel buttons can
    /// never be hidden behind the fields when the window is made small.
    /// </summary>
    public class SetupWizardForm : BaseForm
    {
        private readonly AppSettings _settings;
        private readonly SettingsEditorControl _editor;
        private readonly PresetPathsPanel _presetPanel;

        public SetupWizardForm(AppSettings settings)
        {
            _settings = settings;

            Text = "Setup — " + AppInfo.DisplayName;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ClientSize = new Size(760, 720);
            MinimumSize = new Size(520, 340);

            // This form manages its own scroll region via the inner content panel, so it
            // doesn't need (and shouldn't have) the form-level auto-scroll from BaseForm —
            // that's what let the buttons drift over the fields before.
            AutoScroll = false;

            // Deterministic 2-row layout: row 0 scrolls, row 1 (button bar) is fixed.
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // scrolling content
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); // fixed button bar

            // --- Row 0: tabbed content (General setup + Advanced presets) ---
            var tabs = new TabControl { Dock = DockStyle.Fill };

            var generalTab = new TabPage("General");
            var content = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };

            var intro = new Label
            {
                Text = "Let's point the app at your tools and folders. Everything here can be " +
                       "changed later under Tools → Settings.",
                AutoSize = false,
                Location = new Point(15, 10),
                Size = new Size(720, 34)
            };
            content.Controls.Add(intro);

            _editor = new SettingsEditorControl
            {
                Location = new Point(0, 48)
            };
            _editor.LoadFrom(settings);
            _editor.PrefillAutoDetected();
            content.Controls.Add(_editor);
            generalTab.Controls.Add(content);

            // Advanced: the HandBrake preset files (the same shared panel as Settings → Advanced).
            // Optional at setup — every slot can be filled in later — but exporting a preset from
            // HandBrake's GUI is the one step new users most often haven't done yet, so give it a
            // visible home in the wizard instead of a note pointing somewhere else.
            var advancedTab = new TabPage("Advanced");
            var advancedContent = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var advancedIntro = new Label
            {
                Text = "Optional now, needed before your first encode: preset files exported from " +
                       "HandBrake's GUI (Presets → Export). At minimum set the general preset; the " +
                       "others can stay blank.",
                AutoSize = false,
                Location = new Point(15, 10),
                Size = new Size(700, 34)
            };
            advancedContent.Controls.Add(advancedIntro);
            _presetPanel = new PresetPathsPanel { Location = new Point(0, 52) };
            _presetPanel.LoadFrom(settings);
            advancedContent.Controls.Add(_presetPanel);
            advancedTab.Controls.Add(advancedContent);

            tabs.TabPages.Add(generalTab);
            tabs.TabPages.Add(advancedTab);
            layout.Controls.Add(tabs, 0, 0);

            // --- Row 1: fixed button bar, always visible ---
            layout.Controls.Add(BuildButtonBar(), 0, 1);

            Controls.Add(layout);
        }

        private Control BuildButtonBar()
        {
            // FlowLayoutPanel with right-to-left flow keeps buttons pinned to the right and
            // spaced correctly regardless of window width. First added = rightmost.
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 9, 12, 0),
                WrapContents = false
            };

            var finishButton = new Button
            {
                Text = "Validate && Finish",
                Size = new Size(140, 32)
            };
            finishButton.Click += OnFinishClicked;

            var cancelButton = new Button
            {
                Text = "Cancel",
                Size = new Size(90, 32),
                DialogResult = DialogResult.Cancel
            };

            bar.Controls.Add(finishButton);
            bar.Controls.Add(cancelButton);

            CancelButton = cancelButton;
            return bar;
        }

        private void OnFinishClicked(object sender, EventArgs e)
        {
            // Pull the entered values into the settings object first so validation checks
            // exactly what will be saved.
            _editor.ApplyTo(_settings);
            _presetPanel.ApplyTo(_settings);

            bool ok = _editor.RunFullValidation();
            if (!ok)
            {
                MessageBox.Show(this,
                    "Some required items didn't pass. Check the red status lines on the General " +
                    "tab — the MakeMKV and HandBrake tools must validate, and the Movies, " +
                    "TV Shows, and Temp folders must be set.\r\n\r\n" +
                    "Tip: HandBrakeCLI is a SEPARATE download from the HandBrake app.",
                    "Setup not complete yet",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                EnsureOutputFoldersExist();
                _settings.SetupCompleted = true;
                SettingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Your settings passed validation but couldn't be saved:\r\n\r\n" + ex.Message,
                    "Couldn't save settings",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>
        /// Creates the library/scratch folders now if they don't exist, so later phases can
        /// assume they're there. Idempotent — existing folders are left untouched.
        /// </summary>
        private void EnsureOutputFoldersExist()
        {
            CreateIfMissing(_settings.MoviesRoot);
            CreateIfMissing(_settings.TvShowsRoot);
            CreateIfMissing(_settings.TempFolder);
            if (!string.IsNullOrWhiteSpace(_settings.MusicRoot))
            {
                CreateIfMissing(_settings.MusicRoot);
            }
        }

        private void CreateIfMissing(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !System.IO.Directory.Exists(path))
            {
                System.IO.Directory.CreateDirectory(path);
                Logger.Info("Created folder during setup: " + path);
            }
        }
    }
}
