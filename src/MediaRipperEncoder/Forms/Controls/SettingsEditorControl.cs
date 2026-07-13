using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using MediaRipperEncoder.Models;
using MediaRipperEncoder.Services;

namespace MediaRipperEncoder.Forms.Controls
{
    /// <summary>
    /// The reusable settings-editing surface: all the tool paths, output folders, and the
    /// scratch folder, each with a Browse button, and (for the tools) a Validate button
    /// with a live pass/fail status line. It's used in two places — the first-run Setup
    /// Wizard and the re-editable Settings dialog — so the field layout and validation
    /// logic live in exactly one place.
    ///
    /// Validation runs on a background thread so pressing Validate never freezes the window
    /// while an external tool starts up (important on the weak test hardware this targets).
    /// </summary>
    public class SettingsEditorControl : UserControl
    {
        // Tool path fields
        private TextBox _makeMkvBox;
        private Label _makeMkvStatus;
        private Button _makeMkvValidate;

        private TextBox _handBrakeBox;
        private Label _handBrakeStatus;
        private Button _handBrakeValidate;

        // HandBrake presets moved to Settings > Advanced (general, animation, and the UHD
        // variants live together there). See BuildUi's note row.

        // Metadata lookup API keys (per-user; never hardcoded/shipped)
        private TextBox _omdbKeyBox;
        private TextBox _tvdbKeyBox;
        private TextBox _tvdbPinBox;
        private ComboBox _languageCombo;

        // Preferred metadata language, as TheTVDB ISO 639-3 codes. Names shown to the user,
        // codes stored in settings. _languageCodes stays index-aligned with the combo items.
        private static readonly string[] LangCodes = { "eng", "spa", "fra", "deu", "ita", "por", "nld", "jpn", "kor", "zho", "rus" };
        private static readonly string[] LangNames = { "English", "Spanish", "French", "German", "Italian", "Portuguese", "Dutch", "Japanese", "Korean", "Chinese", "Russian" };
        private readonly System.Collections.Generic.List<string> _languageCodes = new System.Collections.Generic.List<string>();

        // Folder fields
        private TextBox _moviesBox;
        private Label _moviesStatus;

        private TextBox _tvBox;
        private Label _tvStatus;

        private TextBox _musicBox;
        private Label _musicStatus;

        private TextBox _tempBox;
        private Label _tempStatus;

        private const int ControlWidth = 720;
        private const int LabelLeft = 15;
        private const int RowInnerWidth = ControlWidth - 30;

        private static readonly Color OkColor = Color.FromArgb(0, 128, 0);
        private static readonly Color WarnColor = Color.FromArgb(176, 96, 0);
        private static readonly Color FailColor = Color.FromArgb(180, 0, 0);

        public SettingsEditorControl()
        {
            Width = ControlWidth;
            BuildUi();
        }

        // --- public API used by the host forms ---

        public void LoadFrom(AppSettings settings)
        {
            _makeMkvBox.Text = settings.MakeMkvCliPath ?? "";
            _handBrakeBox.Text = settings.HandBrakeCliPath ?? "";
            _omdbKeyBox.Text = settings.OmdbApiKey ?? "";
            _tvdbKeyBox.Text = settings.TheTvdbApiKey ?? "";
            _tvdbPinBox.Text = settings.TheTvdbPin ?? "";
            SelectLanguage(settings.MetadataLanguage);
            _moviesBox.Text = settings.MoviesRoot ?? "";
            _tvBox.Text = settings.TvShowsRoot ?? "";
            _musicBox.Text = settings.MusicRoot ?? "";
            _tempBox.Text = settings.TempFolder ?? "";
        }

        public void ApplyTo(AppSettings settings)
        {
            settings.MakeMkvCliPath = _makeMkvBox.Text.Trim();
            settings.HandBrakeCliPath = _handBrakeBox.Text.Trim();
            settings.OmdbApiKey = _omdbKeyBox.Text.Trim();
            settings.TheTvdbApiKey = _tvdbKeyBox.Text.Trim();
            settings.TheTvdbPin = _tvdbPinBox.Text.Trim();
            settings.MetadataLanguage = (_languageCombo.SelectedIndex >= 0 && _languageCombo.SelectedIndex < _languageCodes.Count)
                ? _languageCodes[_languageCombo.SelectedIndex]
                : "eng";
            settings.MoviesRoot = _moviesBox.Text.Trim();
            settings.TvShowsRoot = _tvBox.Text.Trim();
            settings.MusicRoot = _musicBox.Text.Trim();
            settings.TempFolder = _tempBox.Text.Trim();
        }

        /// <summary>
        /// If a CLI path is blank, try to auto-detect it and fill it in. Only fills blanks
        /// so it never stomps a path the user already chose.
        /// </summary>
        public void PrefillAutoDetected()
        {
            if (string.IsNullOrWhiteSpace(_makeMkvBox.Text))
            {
                string found = ToolAutoDetect.FindMakeMkv();
                if (found != null)
                {
                    _makeMkvBox.Text = found;
                    SetStatus(_makeMkvStatus, "Auto-detected — click Validate to confirm.", WarnColor);
                }
            }
            if (string.IsNullOrWhiteSpace(_handBrakeBox.Text))
            {
                string found = ToolAutoDetect.FindHandBrakeCli();
                if (found != null)
                {
                    _handBrakeBox.Text = found;
                    SetStatus(_handBrakeStatus, "Auto-detected — click Validate to confirm.", WarnColor);
                }
            }
        }

        /// <summary>
        /// Runs every check and updates all status lines. Returns true only if the required
        /// items pass: both CLI tools, the preset, and Movies/TV/Temp folders present.
        /// The Music folder is optional (its layout is still to be decided per the spec).
        /// </summary>
        public bool RunFullValidation()
        {
            var makeMkv = ToolValidator.ValidateMakeMkv(_makeMkvBox.Text.Trim());
            ShowResult(_makeMkvStatus, makeMkv);

            var handBrake = ToolValidator.ValidateHandBrake(_handBrakeBox.Text.Trim());
            ShowResult(_handBrakeStatus, handBrake);

            var movies = FolderValidator.ValidateOutputFolder(_moviesBox.Text.Trim(), "Movies");
            ShowResult(_moviesStatus, movies);

            var tv = FolderValidator.ValidateOutputFolder(_tvBox.Text.Trim(), "TV Shows");
            ShowResult(_tvStatus, tv);

            var temp = FolderValidator.ValidateTempFolder(_tempBox.Text.Trim());
            ShowResult(_tempStatus, temp);

            // Music is optional — validate for feedback only if the user entered something.
            if (!string.IsNullOrWhiteSpace(_musicBox.Text))
            {
                var music = FolderValidator.ValidateOutputFolder(_musicBox.Text.Trim(), "Music");
                ShowResult(_musicStatus, music);
            }

            // Folders that "don't exist yet" report Success=false but are acceptable to
            // finish on, because we create them on demand. Treat only the tools as hard-required
            // and require the folder paths to be non-blank and well-formed. The HandBrake PRESET
            // is validated on the Advanced tab now, not here, so it isn't part of this gate.
            bool toolsOk = makeMkv.Success && handBrake.Success;
            bool foldersPresent =
                !string.IsNullOrWhiteSpace(_moviesBox.Text) &&
                !string.IsNullOrWhiteSpace(_tvBox.Text) &&
                !string.IsNullOrWhiteSpace(_tempBox.Text);

            return toolsOk && foldersPresent;
        }

        // --- UI construction ---

        private void BuildUi()
        {
            int y = 10;

            y = AddSectionHeader("External command-line tools (you supply these)", y);

            AddToolRow("MakeMKV console  (makemkvcon.exe / makemkvcon64.exe)", ref y,
                out _makeMkvBox, out _makeMkvValidate, out _makeMkvStatus,
                "MakeMKV console|makemkvcon*.exe|Executables (*.exe)|*.exe",
                OnValidateMakeMkv);

            AddToolRow("HandBrake CLI  (HandBrakeCLI.exe — separate download from the app)", ref y,
                out _handBrakeBox, out _handBrakeValidate, out _handBrakeStatus,
                "HandBrake CLI|HandBrakeCLI.exe|Executables (*.exe)|*.exe",
                OnValidateHandBrake);

            AddInfoLabel(
                "HandBrake presets (general, animation, and the UHD/4K variants) are configured on " +
                "the Advanced tab of Settings. Finish setup here, then set your preset there before " +
                "your first encode.",
                ref y);

            y += 8;
            y = AddSectionHeader("Metadata lookup — your own free API keys (never shared/built-in)", y);

            AddInfoLabel(
                "TheTVDB's free tier is per-user: enter YOUR key, not a shared one. Get keys at " +
                "thetvdb.com (TV) and omdbapi.com (movies). Leave blank to skip online lookups.",
                ref y);

            AddTextRow("OMDb API key  (movies — free at omdbapi.com)", ref y, out _omdbKeyBox, false);
            AddTextRow("TheTVDB API key  (TV shows — free per-user at thetvdb.com)", ref y, out _tvdbKeyBox, false);
            AddTextRow("TheTVDB PIN  (optional — only for a subscriber/supporter key)", ref y, out _tvdbPinBox, false);

            // Preferred language: forces show + episode names into one language (e.g. English for
            // anime whose original title comes back in Japanese), so folders never need renaming.
            var languageItems = new string[LangNames.Length];
            for (int i = 0; i < LangNames.Length; i++)
            {
                _languageCodes.Add(LangCodes[i]);
                languageItems[i] = LangNames[i] + " (" + LangCodes[i] + ")";
            }
            AddComboRow("Preferred metadata language  (forces show/episode names to this language)",
                ref y, out _languageCombo, languageItems);

            y += 8;
            y = AddSectionHeader("Media-server library folders", y);

            AddFolderRow("Movies root folder", ref y, out _moviesBox, out _moviesStatus);
            AddFolderRow("TV Shows root folder", ref y, out _tvBox, out _tvStatus);
            AddFolderRow("Music root folder  (optional for now)", ref y, out _musicBox, out _musicStatus);

            y += 8;
            y = AddSectionHeader("Temporary / scratch folder (raw rips land here before encoding)", y);

            AddFolderRow("Temp / scratch folder", ref y, out _tempBox, out _tempStatus);

            Height = y + 10;
        }

        private int AddSectionHeader(string text, int y)
        {
            var header = new Label
            {
                Text = text,
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(LabelLeft, y)
            };
            Controls.Add(header);

            var rule = new Label
            {
                BorderStyle = BorderStyle.Fixed3D,
                Location = new Point(LabelLeft, y + 20),
                Size = new Size(RowInnerWidth, 2)
            };
            Controls.Add(rule);

            return y + 30;
        }

        /// <summary>Adds a labeled path row with Browse + Validate buttons and a status line.</summary>
        private void AddToolRow(string labelText, ref int y, out TextBox box, out Button validate,
            out Label status, string fileFilter, EventHandler validateHandler)
        {
            const int validateWidth = 90;
            const int browseWidth = 90;
            const int gap = 6;

            var label = new Label
            {
                Text = labelText,
                AutoSize = true,
                Location = new Point(LabelLeft, y)
            };
            Controls.Add(label);

            int boxWidth = RowInnerWidth - validateWidth - browseWidth - (gap * 2);
            box = new TextBox
            {
                Location = new Point(LabelLeft, y + 20),
                Size = new Size(boxWidth, 23),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(box);

            var browse = new Button
            {
                Text = "Browse...",
                Size = new Size(browseWidth, 24),
                Location = new Point(LabelLeft + boxWidth + gap, y + 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            TextBox capturedBox = box;
            string capturedFilter = fileFilter;
            browse.Click += (s, e) => BrowseForFile(capturedBox, capturedFilter);
            Controls.Add(browse);

            validate = new Button
            {
                Text = "Validate",
                Size = new Size(validateWidth, 24),
                Location = new Point(LabelLeft + boxWidth + gap + browseWidth + gap, y + 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            validate.Click += validateHandler;
            Controls.Add(validate);

            status = new Label
            {
                Text = "Not yet validated.",
                ForeColor = Color.Gray,
                AutoSize = false,
                Location = new Point(LabelLeft, y + 46),
                Size = new Size(RowInnerWidth, 32)
            };
            Controls.Add(status);

            y += 82;
        }

        /// <summary>Adds a labeled read-only dropdown row.</summary>
        private void AddComboRow(string labelText, ref int y, out ComboBox combo, string[] items)
        {
            var label = new Label
            {
                Text = labelText,
                AutoSize = true,
                Location = new Point(LabelLeft, y)
            };
            Controls.Add(label);

            combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(LabelLeft, y + 20),
                Size = new Size(300, 23)
            };
            combo.Items.AddRange(items);
            Controls.Add(combo);

            y += 50;
        }

        /// <summary>Selects the combo entry for the given language code, preserving unknown codes.</summary>
        private void SelectLanguage(string code)
        {
            code = string.IsNullOrWhiteSpace(code) ? "eng" : code.Trim().ToLowerInvariant();
            int idx = _languageCodes.IndexOf(code);
            if (idx < 0)
            {
                // Keep an unusual saved code rather than silently changing it to English.
                _languageCodes.Add(code);
                _languageCombo.Items.Add(code + " (" + code + ")");
                idx = _languageCodes.Count - 1;
            }
            _languageCombo.SelectedIndex = idx;
        }

        /// <summary>Adds a wrapped, gray informational paragraph (no input).</summary>
        private void AddInfoLabel(string text, ref int y)
        {
            var info = new Label
            {
                Text = text,
                ForeColor = Color.Gray,
                AutoSize = false,
                Location = new Point(LabelLeft, y),
                Size = new Size(RowInnerWidth, 32)
            };
            Controls.Add(info);
            y += 38;
        }

        /// <summary>
        /// Adds a labeled plain text row (no Browse/Validate) — used for the API keys. When
        /// <paramref name="mask"/> is true the field shows dots, but keys aren't really secrets
        /// worth masking, so callers pass false and keep them readable/copy-pasteable.
        /// </summary>
        private void AddTextRow(string labelText, ref int y, out TextBox box, bool mask)
        {
            var label = new Label
            {
                Text = labelText,
                AutoSize = true,
                Location = new Point(LabelLeft, y)
            };
            Controls.Add(label);

            box = new TextBox
            {
                Location = new Point(LabelLeft, y + 20),
                Size = new Size(RowInnerWidth, 23),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                UseSystemPasswordChar = mask
            };
            Controls.Add(box);

            y += 50;
        }

        /// <summary>Adds a labeled folder row with a Browse (folder) button and status line.</summary>
        private void AddFolderRow(string labelText, ref int y, out TextBox box, out Label status)
        {
            const int browseWidth = 90;
            const int gap = 6;

            var label = new Label
            {
                Text = labelText,
                AutoSize = true,
                Location = new Point(LabelLeft, y)
            };
            Controls.Add(label);

            int boxWidth = RowInnerWidth - browseWidth - gap;
            box = new TextBox
            {
                Location = new Point(LabelLeft, y + 20),
                Size = new Size(boxWidth, 23),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(box);

            var browse = new Button
            {
                Text = "Browse...",
                Size = new Size(browseWidth, 24),
                Location = new Point(LabelLeft + boxWidth + gap, y + 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            TextBox capturedBox = box;
            browse.Click += (s, e) => BrowseForFolder(capturedBox);
            Controls.Add(browse);

            status = new Label
            {
                Text = "",
                AutoSize = false,
                Location = new Point(LabelLeft, y + 46),
                Size = new Size(RowInnerWidth, 16)
            };
            Controls.Add(status);

            y += 66;
        }

        // --- browse handlers ---

        private void BrowseForFile(TextBox target, string filter)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = filter;
                dialog.CheckFileExists = true;
                if (!string.IsNullOrWhiteSpace(target.Text))
                {
                    try
                    {
                        string dir = System.IO.Path.GetDirectoryName(target.Text);
                        if (System.IO.Directory.Exists(dir)) { dialog.InitialDirectory = dir; }
                    }
                    catch { /* ignore bad starting path */ }
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    target.Text = dialog.FileName;
                }
            }
        }

        private void BrowseForFolder(TextBox target)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select a folder";
                if (System.IO.Directory.Exists(target.Text))
                {
                    dialog.SelectedPath = target.Text;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    target.Text = dialog.SelectedPath;
                }
            }
        }

        // --- validate handlers (async so the UI stays responsive) ---

        private async void OnValidateMakeMkv(object sender, EventArgs e)
        {
            await RunToolValidationAsync(_makeMkvValidate, _makeMkvStatus, _makeMkvBox.Text.Trim(),
                path => ToolValidator.ValidateMakeMkv(path));
        }

        private async void OnValidateHandBrake(object sender, EventArgs e)
        {
            await RunToolValidationAsync(_handBrakeValidate, _handBrakeStatus, _handBrakeBox.Text.Trim(),
                path => ToolValidator.ValidateHandBrake(path));
        }

        private async Task RunToolValidationAsync(Button button, Label status, string path,
            Func<string, ValidationResult> validator)
        {
            button.Enabled = false;
            SetStatus(status, "Validating...", Color.Gray);

            // Run the (possibly process-launching) check off the UI thread.
            ValidationResult result = await Task.Run(() => validator(path));

            ShowResult(status, result);
            button.Enabled = true;
        }

        // --- status helpers ---

        private void ShowResult(Label status, ValidationResult result)
        {
            if (result.Success)
            {
                SetStatus(status, result.Message, OkColor);
            }
            else
            {
                // "Doesn't exist yet — will be created" is guidance, not a hard error, so
                // colour it as a warning rather than a failure.
                bool isWarning = result.Message.IndexOf("will be created", StringComparison.OrdinalIgnoreCase) >= 0
                    || result.Message.StartsWith("Warning", StringComparison.OrdinalIgnoreCase);
                SetStatus(status, result.Message, isWarning ? WarnColor : FailColor);
            }

            if (!string.IsNullOrEmpty(result.Detail))
            {
                // Keep the captured tool output available on hover for troubleshooting.
                var tip = new ToolTip();
                string detail = result.Detail.Length > 1000
                    ? result.Detail.Substring(0, 1000) + "..."
                    : result.Detail;
                tip.SetToolTip(status, detail);
            }
        }

        private void SetStatus(Label status, string text, Color color)
        {
            status.Text = text;
            status.ForeColor = color;
        }
    }
}
