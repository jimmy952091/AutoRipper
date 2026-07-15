using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MediaRipperEncoder.Models;
using MediaRipperEncoder.Services;
using MediaRipperEncoder.Services.Metadata;
using MediaRipperEncoder.Services.Music;

namespace MediaRipperEncoder.Forms
{
    /// <summary>
    /// The per-disc metadata screen. The user picks the disc type, media type, and which
    /// HandBrake preset to encode with; enters the movie/show details; looks them up (with an
    /// explicit confirm step); and — for TV — maps each ripped disc title to an episode,
    /// unchecking special features so they aren't mislabeled. On OK it produces a fully
    /// confirmed <see cref="MediaMetadata"/> package that will travel with the job.
    /// </summary>
    public class MetadataEntryForm : BaseForm
    {
        private readonly IMetadataProvider _provider;
        private readonly AppSettings _settings;
        private readonly List<DiscTitle> _titles;

        // Header controls
        private ComboBox _discTypeCombo;
        private ComboBox _mediaTypeCombo;
        private ComboBox _presetCombo;
        private Label _presetLabel;

        // Panels per media type
        private Panel _moviePanel;
        private Panel _tvPanel;
        private Panel _musicPanel;

        // Movie controls
        private TextBox _movieTitle;
        private TextBox _movieYear;
        private Label _movieMatch;
        private CheckBox _multiMovieCheck;
        private DataGridView _movieGrid;
        private Button _movieLookupButton;
        private Button _movieClearButton;
        private Button _movieSwapButton;

        /// <summary>Per-row state for a multi-movie disc (stored in each grid row's Tag).</summary>
        private class MovieRow
        {
            public DiscTitle Title;
            public string MovieTitle = "";
            public string MovieYear = "";
            public string MovieImdbId = "";
            public bool Assigned { get { return !string.IsNullOrEmpty(MovieImdbId); } }
        }

        // TV controls
        private TextBox _showName;
        private NumericUpDown _season;
        private NumericUpDown _discNumber;
        private Label _tvMatch;
        private ComboBox _episodeOrderCombo;
        private NumericUpDown _firstEpisode;
        private NumericUpDown _segmentsPerTitle;
        private DataGridView _grid;

        // Music controls
        private Label _musicStatus;

        /// <summary>One explanation dialog per window for the permanent Win7-TLS lookup failure.</summary>
        private bool _explainedLookupFailure;
        private ComboBox _musicReleaseCombo;
        private TextBox _musicArtistBox;
        private TextBox _musicAlbumBox;
        private CheckedListBox _musicTracks;
        private Label _musicFormatNote;

        // Music state
        private AudioCdToc _toc;
        private List<MusicRelease> _musicCandidates = new List<MusicRelease>();
        private readonly MusicBrainzClient _musicClient = new MusicBrainzClient();

        /// <summary>The confirmed release with track Selected flags applied. Null until OK in music mode.</summary>
        public MusicRelease MusicResult { get; private set; }

        private Button _okButton;

        // Confirmed lookup state
        private string _confirmedImdbId;
        private string _confirmedSeriesId;
        private string _confirmedShowTitle;
        private List<EpisodeInfo> _episodes = new List<EpisodeInfo>();

        /// <summary>The finished, confirmed metadata package. Null until the user clicks OK.</summary>
        public MediaMetadata Result { get; private set; }

        public MetadataEntryForm(IMetadataProvider provider, AppSettings settings,
            List<DiscTitle> titles, DiscType initialDiscType,
            AudioCdToc toc = null, List<MusicRelease> musicCandidates = null)
        {
            _provider = provider;
            _settings = settings;
            _titles = titles ?? new List<DiscTitle>();
            _toc = toc;

            Text = "Disc details — " + AppInfo.DisplayName;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(720, 560);
            ClientSize = new Size(760, 640);

            BuildHeader(initialDiscType);
            BuildMoviePanel();
            BuildTvPanel();
            BuildMusicPanel();
            BuildFooter();

            // Audio CD flow: the main window already read the TOC and did the MusicBrainz
            // lookup — land straight on the music panel with the candidates loaded.
            if (toc != null)
            {
                _mediaTypeCombo.SelectedIndex = (int)MediaType.Music;
                _musicFormatNote.Text = "Output: " +
                    MusicFormat.ById(settings.MusicFormatId).DisplayName + "  (change in Settings > Music)";
                SetMusicCandidates(musicCandidates, "matched from the disc");
            }

            UpdateVisiblePanel();
        }

        // ---------------- header ----------------

        private void BuildHeader(DiscType initialDiscType)
        {
            var discLabel = new Label { Text = "Disc type:", AutoSize = true, Location = new Point(15, 18) };
            _discTypeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(110, 14),
                Size = new Size(150, 23)
            };
            _discTypeCombo.Items.AddRange(new object[] { "DVD", "Blu-ray", "UHD Blu-ray", "CD (audio)" });
            _discTypeCombo.SelectedIndex = (int)initialDiscType;
            // UHD discs use the UHD presets, other disc types the standard ones — repopulate on change.
            _discTypeCombo.SelectedIndexChanged += (s, e) => PopulatePresetCombo();

            var mediaLabel = new Label { Text = "Media type:", AutoSize = true, Location = new Point(290, 18) };
            _mediaTypeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(380, 14),
                Size = new Size(150, 23)
            };
            _mediaTypeCombo.Items.AddRange(new object[] { "Movie", "TV Show", "Music" });
            _mediaTypeCombo.SelectedIndex = 0;
            _mediaTypeCombo.SelectedIndexChanged += (s, e) => UpdateVisiblePanel();

            _presetLabel = new Label { Text = "Encode preset:", AutoSize = true, Location = new Point(15, 52) };
            _presetCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(110, 48),
                Size = new Size(300, 23)
            };
            PopulatePresetCombo();

            Controls.Add(discLabel);
            Controls.Add(_discTypeCombo);
            Controls.Add(mediaLabel);
            Controls.Add(_mediaTypeCombo);
            Controls.Add(_presetLabel);
            Controls.Add(_presetCombo);
        }

        /// <summary>
        /// Fills the preset dropdown from the configured preset files' INTERNAL names (the -Z
        /// value HandBrakeCLI needs). Falls back to a placeholder if nothing is configured yet,
        /// so the screen is still usable while testing.
        /// </summary>
        private void PopulatePresetCombo()
        {
            var names = new List<string>();
            bool uhd = SelectedDiscType == DiscType.UhdBluRay;

            // UHD discs offer the UHD presets; other disc types the standard ones. If a UHD disc
            // has no UHD preset configured, fall back to the standard presets so the user isn't
            // stuck (with a clear label reminding them to set a proper UHD preset).
            string generalPath = uhd ? _settings.HandBrakeUhdPresetPath : _settings.HandBrakePresetPath;
            string animPath = uhd ? _settings.HandBrakeUhdAnimationPresetPath : _settings.HandBrakeAnimationPresetPath;

            string general = PresetInfo.GetPresetName(generalPath);
            string animation = PresetInfo.GetPresetName(animPath);

            if (uhd && string.IsNullOrEmpty(general) && string.IsNullOrEmpty(animation))
            {
                // No UHD preset set — fall back to standard presets.
                general = PresetInfo.GetPresetName(_settings.HandBrakePresetPath);
                animation = PresetInfo.GetPresetName(_settings.HandBrakeAnimationPresetPath);
            }

            if (!string.IsNullOrEmpty(general)) { names.Add(general); }
            if (!string.IsNullOrEmpty(animation)) { names.Add(animation); }
            if (names.Count == 0) { names.Add("(default preset)"); }

            _presetCombo.Items.Clear();
            _presetCombo.Items.AddRange(names.Cast<object>().ToArray());
            _presetCombo.SelectedIndex = 0;
        }

        // ---------------- movie panel ----------------

        private void BuildMoviePanel()
        {
            _moviePanel = new Panel { Location = new Point(15, 90), Size = new Size(725, 480) };

            _multiMovieCheck = new CheckBox
            {
                Text = "This disc has multiple movies (double feature) — map each one below",
                AutoSize = true,
                Location = new Point(0, 6)
            };
            _multiMovieCheck.CheckedChanged += (s, e) => UpdateMovieMode();

            var titleLabel = new Label { Text = "Title:", AutoSize = true, Location = new Point(0, 40) };
            _movieTitle = new TextBox { Location = new Point(90, 36), Size = new Size(300, 23) };

            var yearLabel = new Label { Text = "Year:", AutoSize = true, Location = new Point(410, 40) };
            _movieYear = new TextBox { Location = new Point(455, 36), Size = new Size(70, 23) };

            _movieLookupButton = new Button { Text = "Look up...", Location = new Point(545, 35), Size = new Size(160, 26) };
            _movieLookupButton.Click += OnLookupMovie;

            _movieMatch = new Label
            {
                Text = "No match confirmed yet.",
                ForeColor = Color.Gray,
                AutoSize = false,
                Location = new Point(0, 70),
                Size = new Size(700, 22)
            };

            // Multi-movie grid (hidden unless the checkbox is on): one row per disc title.
            _movieGrid = new DataGridView
            {
                Location = new Point(0, 100),
                Size = new Size(705, 300),
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                Visible = false
            };
            var includeCol = new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Rip", Width = 40 };
            var titleCol = new DataGridViewTextBoxColumn { Name = "TitleText", HeaderText = "Disc title", Width = 250, ReadOnly = true };
            var movieCol = new DataGridViewTextBoxColumn { Name = "AssignedMovie", HeaderText = "Assigned movie", Width = 400, ReadOnly = true };
            _movieGrid.Columns.Add(includeCol);
            _movieGrid.Columns.Add(titleCol);
            _movieGrid.Columns.Add(movieCol);

            _movieClearButton = new Button { Text = "Clear selected title", Location = new Point(0, 406), Size = new Size(140, 26), Visible = false };
            _movieClearButton.Click += OnClearMovieRow;
            _movieSwapButton = new Button { Text = "Swap the two movies", Location = new Point(150, 406), Size = new Size(150, 26), Visible = false };
            _movieSwapButton.Click += OnSwapMovies;

            _moviePanel.Controls.Add(_movieClearButton);
            _moviePanel.Controls.Add(_movieSwapButton);
            _moviePanel.Controls.Add(_multiMovieCheck);
            _moviePanel.Controls.Add(titleLabel);
            _moviePanel.Controls.Add(_movieTitle);
            _moviePanel.Controls.Add(yearLabel);
            _moviePanel.Controls.Add(_movieYear);
            _moviePanel.Controls.Add(_movieLookupButton);
            _moviePanel.Controls.Add(_movieMatch);
            _moviePanel.Controls.Add(_movieGrid);
            Controls.Add(_moviePanel);
        }

        /// <summary>Switches the movie panel between single-movie and multi-movie (grid) layouts.</summary>
        private void UpdateMovieMode()
        {
            bool multi = _multiMovieCheck.Checked;
            _movieGrid.Visible = multi;
            _movieClearButton.Visible = multi;
            _movieSwapButton.Visible = multi;
            _movieLookupButton.Text = multi ? "Look up movie" : "Look up...";
            _movieLookupButton.Width = 160;

            if (multi && _movieGrid.Rows.Count == 0) { PopulateMovieGrid(); }

            _movieMatch.Text = multi
                ? "Check the titles that are movies, select a row, type the movie name above, and Look up to assign it."
                : "No match confirmed yet.";
            _movieMatch.ForeColor = Color.Gray;
        }

        /// <summary>Fills the multi-movie grid from the disc's titles; feature-length ones start checked.</summary>
        private void PopulateMovieGrid()
        {
            _movieGrid.Rows.Clear();
            foreach (DiscTitle t in _titles)
            {
                int idx = _movieGrid.Rows.Add();
                DataGridViewRow row = _movieGrid.Rows[idx];
                // Titles ~60+ min are almost certainly a feature; pre-check them.
                bool likelyFeature = ParseMinutes(t.Duration) >= 60;
                row.Cells["Include"].Value = likelyFeature;
                row.Cells["TitleText"].Value = "Title " + t.Index +
                    (string.IsNullOrEmpty(t.Duration) ? "" : "  (" + t.Duration + ")");
                row.Cells["AssignedMovie"].Value = "(select this row, then Look up)";
                row.Tag = new MovieRow { Title = t };
            }
        }

        private static int ParseMinutes(string duration)
        {
            // "H:MM:SS" or "MM:SS" -> whole minutes.
            if (string.IsNullOrWhiteSpace(duration)) { return 0; }
            string[] p = duration.Split(':');
            try
            {
                if (p.Length == 3) { return int.Parse(p[0]) * 60 + int.Parse(p[1]); }
                if (p.Length == 2) { return int.Parse(p[0]); }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Assigns a confirmed film to the best-fitting disc title: the UNASSIGNED title whose
        /// duration is closest to the movie's runtime. If the runtime is unknown (0), it uses the
        /// selected row (or the first unassigned). If this film was already on another title, it's
        /// MOVED, not duplicated.
        /// </summary>
        private void AssignMovieAutoMatched(MetadataCandidate chosen, int runtimeMinutes)
        {
            // A film only lives on one title — clear any prior assignment of the same IMDb id.
            foreach (DataGridViewRow r in _movieGrid.Rows)
            {
                var existing = (MovieRow)r.Tag;
                if (existing.MovieImdbId == chosen.ProviderId) { ClearRow(r); }
            }

            DataGridViewRow best = null;
            if (runtimeMinutes > 0)
            {
                int bestDelta = int.MaxValue;
                foreach (DataGridViewRow r in _movieGrid.Rows)
                {
                    var mr = (MovieRow)r.Tag;
                    if (mr.Assigned) { continue; } // don't steal a title already given to another film
                    int delta = Math.Abs(ParseMinutes(mr.Title.Duration) - runtimeMinutes);
                    if (delta < bestDelta) { bestDelta = delta; best = r; }
                }
            }

            bool autoMatched = best != null;
            if (best == null)
            {
                // No runtime (or every title already assigned): fall back to selection / first free.
                best = _movieGrid.CurrentRow;
                if (best == null || ((MovieRow)best.Tag).Assigned)
                {
                    foreach (DataGridViewRow r in _movieGrid.Rows)
                    {
                        if (!((MovieRow)r.Tag).Assigned) { best = r; break; }
                    }
                }
            }
            if (best == null) { best = _movieGrid.CurrentRow; } // last resort: whatever's selected

            AssignRow(best, chosen);

            string where = best.Cells["TitleText"].Value.ToString();
            SetMatchLabel(_movieMatch, autoMatched
                ? "Auto-matched " + chosen.Title + " (" + runtimeMinutes + " min) to " + where +
                  " by runtime. If that's the wrong title, use \"Swap the two movies\" or Clear and redo."
                : "Assigned " + chosen.Title + " to " + where +
                  " (no runtime available to auto-match — check it's the right title).", OkColor);
        }

        private void AssignRow(DataGridViewRow row, MetadataCandidate chosen)
        {
            var mr = (MovieRow)row.Tag;
            mr.MovieTitle = chosen.Title;
            mr.MovieYear = chosen.Year ?? "";
            mr.MovieImdbId = chosen.ProviderId;
            row.Cells["Include"].Value = true; // assigning a film implies "rip this title"
            row.Cells["AssignedMovie"].Value = chosen.Title +
                (string.IsNullOrEmpty(chosen.Year) ? "" : " (" + chosen.Year + ")") +
                "   [IMDb " + chosen.ProviderId + "]";
        }

        private void ClearRow(DataGridViewRow row)
        {
            var mr = (MovieRow)row.Tag;
            mr.MovieTitle = ""; mr.MovieYear = ""; mr.MovieImdbId = "";
            row.Cells["AssignedMovie"].Value = "(select this row, then Look up)";
        }

        private void OnClearMovieRow(object sender, EventArgs e)
        {
            if (_movieGrid.CurrentRow == null) { return; }
            ClearRow(_movieGrid.CurrentRow);
            SetMatchLabel(_movieMatch, "Cleared. Look up a movie to reassign this title.", Color.Gray);
        }

        /// <summary>Swaps the film assignments of the two assigned titles — the one-click fix for a
        /// backwards auto-match (Babe and Beethoven landing on each other's title).</summary>
        private void OnSwapMovies(object sender, EventArgs e)
        {
            var assigned = new List<DataGridViewRow>();
            foreach (DataGridViewRow r in _movieGrid.Rows)
            {
                if (((MovieRow)r.Tag).Assigned) { assigned.Add(r); }
            }
            if (assigned.Count != 2)
            {
                MessageBox.Show(this, "Swap works when exactly two titles have movies assigned.",
                    "Swap", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var a = (MovieRow)assigned[0].Tag;
            var b = (MovieRow)assigned[1].Tag;
            var tmp = new MetadataCandidate { Title = a.MovieTitle, Year = a.MovieYear, ProviderId = a.MovieImdbId };
            AssignRow(assigned[0], new MetadataCandidate { Title = b.MovieTitle, Year = b.MovieYear, ProviderId = b.MovieImdbId });
            AssignRow(assigned[1], tmp);
            SetMatchLabel(_movieMatch, "Swapped the two movies.", OkColor);
        }

        // ---------------- TV panel ----------------

        private void BuildTvPanel()
        {
            _tvPanel = new Panel { Location = new Point(15, 90), Size = new Size(725, 500) };

            var showLabel = new Label { Text = "Show name:", AutoSize = true, Location = new Point(0, 8) };
            _showName = new TextBox { Location = new Point(90, 4), Size = new Size(300, 23) };

            var seasonLabel = new Label { Text = "Season:", AutoSize = true, Location = new Point(410, 8) };
            _season = new NumericUpDown
            {
                Location = new Point(465, 4),
                Size = new Size(50, 23),
                Minimum = 0,
                Maximum = 99,
                Value = 1
            };

            var discLabel = new Label { Text = "Disc #:", AutoSize = true, Location = new Point(530, 8) };
            _discNumber = new NumericUpDown
            {
                Location = new Point(580, 4),
                Size = new Size(50, 23),
                Minimum = 1,
                Maximum = 99,
                Value = 1
            };
            // Changing the disc number suggests a starting episode (disc 2 of a 6-per-disc
            // season -> episode 7), so the user isn't stuck re-numbering from episode 1 by hand.
            _discNumber.ValueChanged += (s, e) => SuggestFirstEpisodeForDisc();

            // Changing the season re-pulls that season's episode list (a confirmed series only —
            // before that there's nothing to reload). Without this, picking "Season 7" after the
            // lookup left season 1's episodes in the grid.
            _season.ValueChanged += async (s, e) =>
            {
                if (!string.IsNullOrEmpty(_confirmedSeriesId)) { await ReloadEpisodesAsync(); }
            };

            var lookup = new Button { Text = "Look up...", Location = new Point(0, 36), Size = new Size(110, 26) };
            lookup.Click += OnLookupSeries;

            _tvMatch = new Label
            {
                Text = "No series confirmed yet.",
                ForeColor = Color.Gray,
                AutoSize = false,
                Location = new Point(120, 40),
                Size = new Size(360, 22)
            };

            var orderLabel = new Label { Text = "Episode order:", AutoSize = true, Location = new Point(490, 40) };
            _episodeOrderCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(580, 36),
                Size = new Size(140, 23)
            };
            _episodeOrderCombo.Items.AddRange(new object[] { "Aired order", "DVD order", "Absolute order" });
            // Default to DVD order: this app rips physical discs, whose title order usually
            // follows the DVD ordering / printed booklet rather than broadcast order.
            _episodeOrderCombo.SelectedIndex = 1;
            _episodeOrderCombo.SelectedIndexChanged += OnEpisodeOrderChanged;

            var mapLabel = new Label
            {
                Text = "Map each disc title to its episode(s). Uncheck special features / duplicate " +
                       "\"play all\" titles. For segmented cartoons (two segments per title), set " +
                       "\"Segments/title\" to 2 — each title becomes a multi-episode file (S01E01-E02).",
                AutoSize = false,
                Location = new Point(0, 72),
                Size = new Size(720, 34)
            };

            var firstLabel = new Label { Text = "First episode #:", AutoSize = true, Location = new Point(0, 114) };
            _firstEpisode = new NumericUpDown
            {
                Location = new Point(95, 110),
                Size = new Size(50, 23),
                Minimum = 1,
                Maximum = 999,
                Value = 1
            };
            // Re-number the grid the moment the user edits this — previously the edit did nothing
            // until the Auto-number button was clicked, which read as "it ignored my change".
            _firstEpisode.ValueChanged += (s, e) => AutoNumberEpisodes();

            var segLabel = new Label { Text = "Segments/title:", AutoSize = true, Location = new Point(160, 114) };
            _segmentsPerTitle = new NumericUpDown
            {
                Location = new Point(255, 110),
                Size = new Size(45, 23),
                Minimum = 1,
                Maximum = 5,
                Value = 1
            };
            _segmentsPerTitle.ValueChanged += (s, e) => AutoNumberEpisodes();

            var autoNumber = new Button { Text = "Auto-number", Location = new Point(315, 109), Size = new Size(100, 26) };
            autoNumber.Click += (s, e) => AutoNumberEpisodes();
            var selectAll = new Button { Text = "Select all", Location = new Point(420, 109), Size = new Size(80, 26) };
            selectAll.Click += (s, e) => SetAllIncluded(true);
            var deselectAll = new Button { Text = "Deselect all", Location = new Point(505, 109), Size = new Size(90, 26) };
            deselectAll.Click += (s, e) => SetAllIncluded(false);

            _grid = new DataGridView
            {
                Location = new Point(0, 140),
                Size = new Size(720, 350),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.CellSelect
            };
            BuildGridColumns();
            PopulateGridTitles();

            _tvPanel.Controls.Add(showLabel);
            _tvPanel.Controls.Add(_showName);
            _tvPanel.Controls.Add(seasonLabel);
            _tvPanel.Controls.Add(_season);
            _tvPanel.Controls.Add(discLabel);
            _tvPanel.Controls.Add(_discNumber);
            _tvPanel.Controls.Add(lookup);
            _tvPanel.Controls.Add(_tvMatch);
            _tvPanel.Controls.Add(orderLabel);
            _tvPanel.Controls.Add(_episodeOrderCombo);
            _tvPanel.Controls.Add(mapLabel);
            _tvPanel.Controls.Add(firstLabel);
            _tvPanel.Controls.Add(_firstEpisode);
            _tvPanel.Controls.Add(segLabel);
            _tvPanel.Controls.Add(_segmentsPerTitle);
            _tvPanel.Controls.Add(autoNumber);
            _tvPanel.Controls.Add(selectAll);
            _tvPanel.Controls.Add(deselectAll);
            _tvPanel.Controls.Add(_grid);
            Controls.Add(_tvPanel);
        }

        private void BuildGridColumns()
        {
            var includeCol = new DataGridViewCheckBoxColumn
            {
                Name = "Include",
                HeaderText = "Use",
                FillWeight = 8
            };
            var titleCol = new DataGridViewTextBoxColumn
            {
                Name = "TitleText",
                HeaderText = "Disc title",
                ReadOnly = true,
                FillWeight = 30
            };
            var startCol = new DataGridViewComboBoxColumn
            {
                Name = "StartEpisode",
                HeaderText = "Episode (start)",
                FillWeight = 31,
                FlatStyle = FlatStyle.Flat
            };
            // "End" is only needed for multi-segment titles; blank = single episode.
            var endCol = new DataGridViewComboBoxColumn
            {
                Name = "EndEpisode",
                HeaderText = "…through (multi-ep)",
                FillWeight = 31,
                FlatStyle = FlatStyle.Flat
            };
            _grid.Columns.Add(includeCol);
            _grid.Columns.Add(titleCol);
            _grid.Columns.Add(startCol);
            _grid.Columns.Add(endCol);
        }

        private void PopulateGridTitles()
        {
            _grid.Rows.Clear();
            foreach (DiscTitle t in _titles)
            {
                int rowIndex = _grid.Rows.Add();
                DataGridViewRow row = _grid.Rows[rowIndex];
                row.Cells["Include"].Value = t.Selected;
                row.Cells["TitleText"].Value = "Title " + t.Index +
                    (string.IsNullOrEmpty(t.Duration) ? "" : "  (" + t.Duration + ")");
                row.Tag = t; // keep the source title for building mappings later
            }
        }

        // ---------------- music panel ----------------

        private void BuildMusicPanel()
        {
            _musicPanel = new Panel
            {
                Location = new Point(15, 90),
                Size = new Size(725, 470),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };

            _musicStatus = new Label
            {
                Text = "Insert an audio CD and use \"Scan disc && configure\" — the disc is identified " +
                       "automatically from its table of contents (MusicBrainz).",
                AutoSize = false,
                Location = new Point(0, 4),
                Size = new Size(720, 34)
            };

            var releaseLabel = new Label { Text = "Matched release:", AutoSize = true, Location = new Point(0, 44) };
            _musicReleaseCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(110, 40),
                Size = new Size(610, 23),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _musicReleaseCombo.SelectedIndexChanged += (s, e) => LoadMusicTracks();

            // Manual fallback for discs MusicBrainz can't identify automatically.
            var artistLabel = new Label { Text = "Artist:", AutoSize = true, Location = new Point(0, 76) };
            _musicArtistBox = new TextBox { Location = new Point(110, 72), Size = new Size(180, 23) };
            var albumLabel = new Label { Text = "Album:", AutoSize = true, Location = new Point(305, 76) };
            _musicAlbumBox = new TextBox { Location = new Point(355, 72), Size = new Size(200, 23) };
            var searchButton = new Button { Text = "Search", Location = new Point(565, 71), Size = new Size(90, 25) };
            searchButton.Click += OnMusicSearch;

            var tracksLabel = new Label
            {
                Text = "Tracks — only checked tracks are ripped:",
                AutoSize = true,
                Location = new Point(0, 108)
            };

            _musicTracks = new CheckedListBox
            {
                Location = new Point(0, 128),
                Size = new Size(720, 260),
                CheckOnClick = true,
                IntegralHeight = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };

            var selectAll = new Button { Text = "Select all", Size = new Size(85, 26), Location = new Point(0, 396), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var deselectAll = new Button { Text = "Deselect all", Size = new Size(90, 26), Location = new Point(92, 396), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            selectAll.Click += (s, e) => SetAllMusicTracks(true);
            deselectAll.Click += (s, e) => SetAllMusicTracks(false);

            _musicFormatNote = new Label
            {
                AutoSize = true,
                Location = new Point(200, 401),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };

            _musicPanel.Controls.Add(_musicStatus);
            _musicPanel.Controls.Add(releaseLabel);
            _musicPanel.Controls.Add(_musicReleaseCombo);
            _musicPanel.Controls.Add(artistLabel);
            _musicPanel.Controls.Add(_musicArtistBox);
            _musicPanel.Controls.Add(albumLabel);
            _musicPanel.Controls.Add(_musicAlbumBox);
            _musicPanel.Controls.Add(searchButton);
            _musicPanel.Controls.Add(tracksLabel);
            _musicPanel.Controls.Add(_musicTracks);
            _musicPanel.Controls.Add(selectAll);
            _musicPanel.Controls.Add(deselectAll);
            _musicPanel.Controls.Add(_musicFormatNote);
            Controls.Add(_musicPanel);
        }

        // ---------------- music mode logic ----------------

        /// <summary>Populates the release dropdown with candidates (from disc lookup or search).</summary>
        private void SetMusicCandidates(List<MusicRelease> candidates, string sourceDescription)
        {
            _musicCandidates = candidates ?? new List<MusicRelease>();
            _musicReleaseCombo.BeginUpdate();
            _musicReleaseCombo.Items.Clear();
            foreach (MusicRelease r in _musicCandidates)
            {
                _musicReleaseCombo.Items.Add(
                    r.Artist + " — " + r.Album + (string.IsNullOrEmpty(r.Year) ? "" : " (" + r.Year + ")") +
                    (string.IsNullOrEmpty(r.Detail) ? "" : "   [" + r.Detail + "]"));
            }
            _musicReleaseCombo.EndUpdate();

            if (_musicCandidates.Count > 0)
            {
                SetMatchLabel(_musicStatus, _musicCandidates.Count + " release(s) " + sourceDescription +
                    " — pick the edition that matches your case/booklet, then confirm the tracks.", OkColor);
                _musicReleaseCombo.SelectedIndex = 0; // triggers LoadMusicTracks
            }
            else
            {
                SetMatchLabel(_musicStatus, "No match " + sourceDescription +
                    " — type the artist and album and press Search.", WarnColor);
            }
        }

        /// <summary>Fills the checkbox list from the chosen release (all tracks checked, per spec).</summary>
        private async void LoadMusicTracks()
        {
            int index = _musicReleaseCombo.SelectedIndex;
            if (index < 0 || index >= _musicCandidates.Count) { return; }
            MusicRelease release = _musicCandidates[index];

            // Search results are shallow (no track list) — fetch detail on first selection.
            if (release.Tracks.Count == 0 && !string.IsNullOrEmpty(release.ReleaseId))
            {
                try
                {
                    SetMatchLabel(_musicStatus, "Loading track list...", OkColor);
                    MusicRelease detail = await _musicClient.GetReleaseDetailAsync(release.ReleaseId, null);
                    if (detail != null && detail.Tracks.Count > 0)
                    {
                        release.Tracks = detail.Tracks;
                        release.DiscCount = detail.DiscCount;
                        release.DiscNumber = detail.DiscNumber;
                    }
                }
                catch (Exception ex)
                {
                    SetMatchLabel(_musicStatus, "Couldn't load the track list: " + ex.Message, FailColor);
                    return;
                }
            }

            _musicTracks.BeginUpdate();
            _musicTracks.Items.Clear();
            foreach (AudioTrack t in release.Tracks)
            {
                _musicTracks.Items.Add(t.Number.ToString("00") + "  " + t.Title + "   (" + t.LengthText + ")",
                    t.Selected);
            }
            _musicTracks.EndUpdate();

            SetMatchLabel(_musicStatus, release.Artist + " — " + release.Album +
                (string.IsNullOrEmpty(release.Year) ? "" : " (" + release.Year + ")") +
                ": " + release.Tracks.Count + " tracks" +
                (release.DiscCount > 1 ? " (disc " + release.DiscNumber + " of " + release.DiscCount + ")" : "") +
                ". Uncheck anything you don't want.", OkColor);
        }

        private void SetAllMusicTracks(bool selected)
        {
            for (int i = 0; i < _musicTracks.Items.Count; i++) { _musicTracks.SetItemChecked(i, selected); }
        }

        private async void OnMusicSearch(object sender, EventArgs e)
        {
            string artist = _musicArtistBox.Text.Trim();
            string album = _musicAlbumBox.Text.Trim();
            if (album.Length == 0)
            {
                MessageBox.Show(this, "Enter at least the album name.", "Search",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var button = sender as Button;
            if (button != null) { button.Enabled = false; }
            try
            {
                SetMatchLabel(_musicStatus, "Searching MusicBrainz...", OkColor);
                List<MusicRelease> found = await _musicClient.SearchReleasesAsync(artist, album);
                SetMusicCandidates(found, "found by search");
            }
            catch (Exception ex)
            {
                string friendly = MusicBrainzClient.FriendlyLookupError(ex);
                SetMatchLabel(_musicStatus, "Search failed: " + friendly, FailColor);
                // The Windows 7 TLS case is permanent, not retryable — explain it once in a
                // dialog the user can actually read instead of a truncated status line.
                if (!_explainedLookupFailure && friendly.StartsWith("Windows 7"))
                {
                    _explainedLookupFailure = true;
                    MessageBox.Show(this, friendly, "MusicBrainz unavailable on Windows 7",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                if (button != null) { button.Enabled = true; }
            }
        }

        // ---------------- footer ----------------

        private void BuildFooter()
        {
            _okButton = new Button
            {
                Text = "Confirm details",
                Size = new Size(140, 32),
                Location = new Point(ClientSize.Width - 290, ClientSize.Height - 44),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _okButton.Click += OnConfirm;

            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Size = new Size(120, 32),
                Location = new Point(ClientSize.Width - 140, ClientSize.Height - 44),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            AcceptButton = null; // don't let Enter accidentally confirm mid-entry
            CancelButton = cancel;
            Controls.Add(_okButton);
            Controls.Add(cancel);
        }

        // ---------------- panel visibility ----------------

        private MediaType SelectedMediaType
        {
            get { return (MediaType)_mediaTypeCombo.SelectedIndex; }
        }

        private DiscType SelectedDiscType
        {
            get { return (DiscType)_discTypeCombo.SelectedIndex; }
        }

        private void UpdateVisiblePanel()
        {
            MediaType type = SelectedMediaType;
            _moviePanel.Visible = type == MediaType.Movie;
            _tvPanel.Visible = type == MediaType.TvShow;
            _musicPanel.Visible = type == MediaType.Music;

            // HandBrake presets are a video concept — music has its own format in Settings > Music.
            _presetCombo.Visible = type != MediaType.Music;
            _presetLabel.Visible = type != MediaType.Music;
        }

        // ---------------- lookups ----------------

        private async void OnLookupMovie(object sender, EventArgs e)
        {
            string title = _movieTitle.Text.Trim();
            if (string.IsNullOrEmpty(title))
            {
                MessageBox.Show(this, "Enter a movie title first.", "Look up",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool multi = _multiMovieCheck.Checked;

            var button = sender as Button;
            if (button != null) { button.Enabled = false; }
            try
            {
                List<MetadataCandidate> candidates =
                    await _provider.SearchMoviesAsync(title, _movieYear.Text.Trim());

                if (candidates == null || candidates.Count == 0)
                {
                    SetMatchLabel(_movieMatch, "No results. Check the spelling or try a year.", FailColor);
                    return;
                }

                using (var dialog = new ConfirmMatchDialog(title, candidates))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) { return; }
                    MetadataCandidate chosen = dialog.SelectedCandidate;
                    _movieTitle.Text = chosen.Title;
                    if (!string.IsNullOrEmpty(chosen.Year)) { _movieYear.Text = chosen.Year; }

                    if (!multi)
                    {
                        _confirmedImdbId = chosen.ProviderId;
                        SetMatchLabel(_movieMatch,
                            "Confirmed: " + chosen.Title +
                            (string.IsNullOrEmpty(chosen.Year) ? "" : " (" + chosen.Year + ")") +
                            "   [IMDb " + chosen.ProviderId + "]", OkColor);
                        return;
                    }

                    // Multi-movie: auto-match this film to the disc title whose duration is closest
                    // to its runtime (among titles not already assigned). Falls back to the selected
                    // row if the runtime is unknown.
                    int runtime = await _provider.GetMovieRuntimeMinutesAsync(chosen.ProviderId);
                    AssignMovieAutoMatched(chosen, runtime);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Movie lookup failed.", ex);
                SetMatchLabel(_movieMatch, ex.Message, FailColor);
            }
            finally
            {
                if (button != null) { button.Enabled = true; }
            }
        }

        private async void OnLookupSeries(object sender, EventArgs e)
        {
            string name = _showName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show(this, "Enter a show name first.", "Look up",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var button = sender as Button;
            if (button != null) { button.Enabled = false; }
            try
            {
                List<MetadataCandidate> candidates = await _provider.SearchSeriesAsync(name);
                if (candidates == null || candidates.Count == 0)
                {
                    SetMatchLabel(_tvMatch, "No results. Check the spelling.", FailColor);
                    return;
                }

                using (var dialog = new ConfirmMatchDialog(name, candidates))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) { return; }

                    MetadataCandidate chosen = dialog.SelectedCandidate;
                    _confirmedSeriesId = chosen.ProviderId;
                    _confirmedShowTitle = chosen.Title;
                    _showName.Text = chosen.Title;

                    // Force the show name to the preferred metadata language (e.g. the English
                    // title of an anime) so the library folder isn't in the original language.
                    string localizedName = await _provider.GetSeriesNameAsync(chosen.ProviderId);
                    if (!string.IsNullOrWhiteSpace(localizedName))
                    {
                        _confirmedShowTitle = localizedName;
                        _showName.Text = localizedName;
                    }

                    await ReloadEpisodesAsync();
                    // Apply the disc-number-based starting episode once on first load. (Order/season
                    // changes re-run only AutoNumber, so they don't clobber a manual first-episode.)
                    SuggestFirstEpisodeForDisc();
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Series lookup failed.", ex);
                SetMatchLabel(_tvMatch, ex.Message, FailColor);
            }
            finally
            {
                if (button != null) { button.Enabled = true; }
            }
        }

        private EpisodeOrder SelectedOrder
        {
            get { return (EpisodeOrder)_episodeOrderCombo.SelectedIndex; }
        }

        private async void OnEpisodeOrderChanged(object sender, EventArgs e)
        {
            // Only meaningful once a series is confirmed; re-pull the episode list in the new order.
            if (string.IsNullOrEmpty(_confirmedSeriesId)) { return; }
            await ReloadEpisodesAsync();
        }

        /// <summary>
        /// Pulls the episode list for the confirmed series in the currently-selected order and
        /// season, then repopulates the mapping grid. Shared by the initial lookup and the
        /// order/season change handlers.
        /// </summary>
        private async Task ReloadEpisodesAsync()
        {
            int season = (int)_season.Value;
            SetMatchLabel(_tvMatch, "Confirmed: " + _confirmedShowTitle +
                "   [TheTVDB " + _confirmedSeriesId + "] — loading " +
                _episodeOrderCombo.SelectedItem + "...", OkColor);

            _episodes = await _provider.GetEpisodesAsync(_confirmedSeriesId, season, SelectedOrder);
            LoadEpisodesIntoGrid();

            SetMatchLabel(_tvMatch, "Confirmed: " + _confirmedShowTitle +
                "   [" + _episodeOrderCombo.SelectedItem + "] — " + _episodes.Count +
                " episodes in season " + season + ".", OkColor);
        }

        // ---------------- episode mapping ----------------

        private void LoadEpisodesIntoGrid()
        {
            var startCol = (DataGridViewComboBoxColumn)_grid.Columns["StartEpisode"];
            var endCol = (DataGridViewComboBoxColumn)_grid.Columns["EndEpisode"];
            startCol.Items.Clear();
            endCol.Items.Clear();
            startCol.Items.Add(NoneChoice);
            endCol.Items.Add(NoneChoice);
            foreach (EpisodeInfo ep in _episodes)
            {
                startCol.Items.Add(ep.ToString());
                endCol.Items.Add(ep.ToString());
            }

            AutoNumberEpisodes();
        }

        /// <summary>
        /// Assigns episode ranges to the *included* titles in order, starting from the "First
        /// episode #" and advancing by "Segments/title". With 1 segment each title gets a
        /// single episode; with 2 each title spans a pair (S01E01-E02). Unchecked titles are
        /// skipped, so excluding a play-all title shifts everything after it correctly.
        /// </summary>
        /// <summary>
        /// Sets the "First episode #" from the disc number, assuming each disc holds the same number
        /// of episodes as this one: first = (discNumber - 1) × (included titles × segments) + 1.
        /// So disc 2 of a season whose discs each hold 6 episodes suggests episode 7. This is only a
        /// starting suggestion — the per-title grid (and the First episode # box) remain overridable,
        /// which matters for the odd season where discs hold unequal episode counts.
        /// </summary>
        private void SuggestFirstEpisodeForDisc()
        {
            int episodesPerDisc = IncludedEpisodeTitleCount() * (int)_segmentsPerTitle.Value;
            if (episodesPerDisc <= 0) { return; } // no titles yet — nothing to base it on

            int first = ((int)_discNumber.Value - 1) * episodesPerDisc + 1;
            if (first < _firstEpisode.Minimum) { first = (int)_firstEpisode.Minimum; }
            if (first > _firstEpisode.Maximum) { first = (int)_firstEpisode.Maximum; }

            _firstEpisode.Value = first;
            AutoNumberEpisodes();
        }

        /// <summary>Counts the currently-included (checked) title rows — i.e. how many titles on this disc are episodes.</summary>
        private int IncludedEpisodeTitleCount()
        {
            int count = 0;
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (Convert.ToBoolean(row.Cells["Include"].Value ?? false)) { count++; }
            }
            return count;
        }

        private void AutoNumberEpisodes()
        {
            if (_episodes.Count == 0) { return; }

            int startNumber = (int)_firstEpisode.Value;
            int segments = (int)_segmentsPerTitle.Value;
            int cursor = _episodes.FindIndex(ep => ep.EpisodeNumber == startNumber);
            if (cursor < 0) { cursor = 0; }

            foreach (DataGridViewRow row in _grid.Rows)
            {
                bool include = Convert.ToBoolean(row.Cells["Include"].Value ?? false);
                var startCell = (DataGridViewComboBoxCell)row.Cells["StartEpisode"];
                var endCell = (DataGridViewComboBoxCell)row.Cells["EndEpisode"];

                if (include && cursor < _episodes.Count)
                {
                    startCell.Value = _episodes[cursor].ToString();

                    int endIndex = cursor + segments - 1;
                    if (endIndex >= _episodes.Count) { endIndex = _episodes.Count - 1; }

                    // Only fill the "through" column when the title actually spans >1 episode.
                    endCell.Value = (segments > 1 && endIndex > cursor)
                        ? _episodes[endIndex].ToString()
                        : NoneChoice;

                    cursor = endIndex + 1;
                }
                else
                {
                    startCell.Value = NoneChoice;
                    endCell.Value = NoneChoice;
                }
            }
        }

        private void SetAllIncluded(bool included)
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                row.Cells["Include"].Value = included;
            }
            _grid.RefreshEdit();
            AutoNumberEpisodes();
        }

        // ---------------- confirm / build result ----------------

        private void OnConfirm(object sender, EventArgs e)
        {
            MediaType type = SelectedMediaType;

            if (type == MediaType.Music)
            {
                ConfirmMusic();
                return;
            }

            var meta = new MediaMetadata
            {
                DiscType = SelectedDiscType,
                MediaType = type,
                PresetName = _presetCombo.SelectedItem as string ?? ""
            };

            if (type == MediaType.Movie && _multiMovieCheck.Checked)
            {
                // Multi-movie disc: build one Movie-kind mapping per checked, assigned title.
                var mappings = new List<TitleMapping>();
                bool anyCheckedUnassigned = false;
                foreach (DataGridViewRow row in _movieGrid.Rows)
                {
                    bool include = Convert.ToBoolean(row.Cells["Include"].Value ?? false);
                    var mr = (MovieRow)row.Tag;
                    if (!include) { continue; }
                    if (!mr.Assigned) { anyCheckedUnassigned = true; continue; }
                    mappings.Add(new TitleMapping
                    {
                        TitleIndex = mr.Title.Index,
                        Duration = mr.Title.Duration,
                        Include = true,
                        Kind = TitleKind.Movie,
                        MovieTitle = mr.MovieTitle,
                        MovieYear = mr.MovieYear,
                        MovieImdbId = mr.MovieImdbId
                    });
                }

                if (mappings.Count == 0)
                {
                    MessageBox.Show(this, "Assign a movie to at least one checked disc title before continuing.",
                        "Confirm needed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (anyCheckedUnassigned)
                {
                    DialogResult ans = MessageBox.Show(this,
                        "Some checked titles have no movie assigned — those will be SKIPPED. Continue with just the assigned ones?",
                        "Unassigned titles", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (ans != DialogResult.Yes) { return; }
                }

                meta.TitleMappings = mappings;
                meta.MatchConfirmed = true;
            }
            else if (type == MediaType.Movie)
            {
                if (string.IsNullOrEmpty(_confirmedImdbId))
                {
                    MessageBox.Show(this, "Look up and confirm the movie match before continuing.",
                        "Confirm needed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                meta.MovieTitle = _movieTitle.Text.Trim();
                meta.Year = _movieYear.Text.Trim();
                meta.ImdbId = _confirmedImdbId;
                meta.MatchConfirmed = true;
            }
            else // TvShow
            {
                if (string.IsNullOrEmpty(_confirmedSeriesId))
                {
                    MessageBox.Show(this, "Look up and confirm the series before continuing.",
                        "Confirm needed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                meta.ShowName = _showName.Text.Trim();
                meta.SeasonNumber = (int)_season.Value;
                meta.DiscNumber = (int)_discNumber.Value;
                meta.TvdbSeriesId = _confirmedSeriesId;
                meta.TitleMappings = BuildTitleMappings();
                meta.MatchConfirmed = true;

                if (!meta.TitleMappings.Any(m => m.Include && m.Kind == TitleKind.Episode))
                {
                    MessageBox.Show(this,
                        "No titles are mapped to an episode. Check at least one title and assign it an episode.",
                        "Nothing to process", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            Result = meta;
            DialogResult = DialogResult.OK;
            Close();
        }

        private List<TitleMapping> BuildTitleMappings()
        {
            var mappings = new List<TitleMapping>();
            foreach (DataGridViewRow row in _grid.Rows)
            {
                var title = row.Tag as DiscTitle;
                if (title == null) { continue; }

                bool include = Convert.ToBoolean(row.Cells["Include"].Value ?? false);
                var mapping = new TitleMapping
                {
                    TitleIndex = title.Index,
                    Duration = title.Duration,
                    Include = include
                };

                EpisodeInfo start = EpisodeFromCell(row.Cells["StartEpisode"].Value as string);
                EpisodeInfo end = EpisodeFromCell(row.Cells["EndEpisode"].Value as string);

                if (include && start != null)
                {
                    mapping.Kind = TitleKind.Episode;
                    mapping.SeasonNumber = start.SeasonNumber;
                    mapping.Episodes = BuildEpisodeRange(start, end);
                }
                else if (include)
                {
                    // Included but not tied to an episode = a kept extra.
                    mapping.Kind = TitleKind.Extra;
                }
                else
                {
                    mapping.Kind = TitleKind.Ignore;
                }

                mappings.Add(mapping);
            }
            return mappings;
        }

        private EpisodeInfo EpisodeFromCell(string text)
        {
            if (string.IsNullOrEmpty(text) || text == NoneChoice) { return null; }
            return _episodes.FirstOrDefault(ep => ep.ToString() == text);
        }

        /// <summary>
        /// Returns the inclusive run of episodes from <paramref name="start"/> to
        /// <paramref name="end"/> (by their position in the season list). If end is null or
        /// before start, it's just the single start episode.
        /// </summary>
        private List<EpisodeInfo> BuildEpisodeRange(EpisodeInfo start, EpisodeInfo end)
        {
            var run = new List<EpisodeInfo>();
            int startIndex = _episodes.IndexOf(start);
            if (startIndex < 0) { return run; }

            int endIndex = end == null ? startIndex : _episodes.IndexOf(end);
            if (endIndex < startIndex) { endIndex = startIndex; }

            for (int i = startIndex; i <= endIndex && i < _episodes.Count; i++)
            {
                run.Add(_episodes[i]);
            }
            return run;
        }

        // ---------------- helpers ----------------

        private const string NoneChoice = "(skip / not an episode)";

        private static readonly Color OkColor = Color.FromArgb(0, 128, 0);
        private static readonly Color FailColor = Color.FromArgb(180, 0, 0);
        private static readonly Color WarnColor = Color.FromArgb(176, 96, 0);

        private void SetMatchLabel(Label label, string text, Color color)
        {
            label.Text = text;
            label.ForeColor = color;
        }

        /// <summary>
        /// Music OK: validates the confirmed release + checked tracks, applies the checkbox
        /// states onto the release, and closes with MusicResult set. Same explicit-confirm
        /// rule as video: nothing proceeds on a guess.
        /// </summary>
        private void ConfirmMusic()
        {
            if (_toc == null)
            {
                MessageBox.Show(this,
                    "To rip an audio CD, insert it and use \"Scan disc && configure\" from the main " +
                    "window — the disc is identified from its table of contents.",
                    "Music", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int index = _musicReleaseCombo.SelectedIndex;
            if (index < 0 || index >= _musicCandidates.Count)
            {
                MessageBox.Show(this, "Pick (or search for) the release that matches your disc first.",
                    "Music", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            MusicRelease release = _musicCandidates[index];
            if (release.Tracks.Count == 0)
            {
                MessageBox.Show(this, "This release has no track list yet — pick it again or search.",
                    "Music", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // The checkbox list is authoritative: copy its state onto the release.
            for (int i = 0; i < release.Tracks.Count && i < _musicTracks.Items.Count; i++)
            {
                release.Tracks[i].Selected = _musicTracks.GetItemChecked(i);
            }

            int selected = 0;
            foreach (AudioTrack t in release.Tracks) { if (t.Selected) { selected++; } }
            if (selected == 0)
            {
                MessageBox.Show(this, "Every track is unchecked — check at least one track to rip.",
                    "Music", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Sanity: warn (don't block) when the picked edition's track count differs from the
            // physical disc — the wrong edition means wrong titles on the files.
            if (release.Tracks.Count != _toc.TrackCount)
            {
                DialogResult answer = MessageBox.Show(this,
                    "The disc has " + _toc.TrackCount + " tracks but the selected edition lists " +
                    release.Tracks.Count + ". This is usually the WRONG edition — titles may not " +
                    "line up.\r\n\r\nUse it anyway?",
                    "Track count mismatch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) { return; }
            }

            MusicResult = release;
            Result = new MediaMetadata
            {
                DiscType = DiscType.Cd,
                MediaType = MediaType.Music,
                MatchConfirmed = true,
                MovieTitle = release.Artist + " — " + release.Album
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
