using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MediaRipperEncoder.Forms.Controls;
using MediaRipperEncoder.Models;
using MediaRipperEncoder.Services;
using MediaRipperEncoder.Services.Metadata;

namespace MediaRipperEncoder.Forms
{
    /// <summary>
    /// Main working window — Phase 7 integration. Select a drive, scan a disc, confirm its
    /// metadata, and the disc flows through the rip queue, the encode queue (in parallel), and
    /// into the Plex/Jellyfin library. Both queues are shown live in a 50/50 split, each with a
    /// progress bar for its current job; finished encodes can be re-run from the encode list.
    /// </summary>
    public class MainForm : BaseForm
    {
        private AppSettings _settings;
        private PipelineCoordinator _pipeline;
        private ConflictResolution? _sessionResolution;

        /// <summary>Folder of the most recently placed library file — the "Open output folder" target.</summary>
        private string _lastPlacedFolder;

        // Menu
        private ToolStripMenuItem _showWelcomeMenuItem;

        // Drive panel
        private ComboBox _driveCombo;
        private Button _rescanButton;
        private Button _ejectButton;
        private Button _scanButton;
        private Label _driveStatus;

        // Queues
        private AutoColumnListView _ripList;
        private AutoColumnListView _encodeList;
        private ProgressBar _ripProgress;
        private ProgressBar _encodeProgress;
        private GroupBox _ripGroup;
        private GroupBox _encodeGroup;
        private Label _providerModeLabel;
        private Label _statusStrip;

        // EncoderServer role only: receives rips from a client and encodes them here.
        private Services.Net.EncodeServerHost _encodeServer;

        // Rip side: one group per disc, one row per title.
        private readonly Dictionary<Guid, ListViewGroup> _ripGroups = new Dictionary<Guid, ListViewGroup>();
        private readonly Dictionary<string, ListViewItem> _ripTitleRows = new Dictionary<string, ListViewItem>();
        private readonly Dictionary<Guid, RipJob> _ripJobs = new Dictionary<Guid, RipJob>();

        // Encode side.
        private readonly Dictionary<Guid, ListViewItem> _encodeRows = new Dictionary<Guid, ListViewItem>();
        private readonly Dictionary<Guid, EncodeJob> _encodeJobs = new Dictionary<Guid, EncodeJob>();

        /// <summary>Row tag linking a title row back to its job + title index (for retry).</summary>
        private class RipRowTag
        {
            public Guid JobId;
            public int TitleIndex;
        }

        /// <summary>Discs that already got their one automatic fold-on-completion (see OnRipJobUpdated).</summary>
        private readonly HashSet<Guid> _autoFoldedJobs = new HashSet<Guid>();

        public MainForm(AppSettings settings)
        {
            _settings = settings;

            Text = AppInfo.DisplayName;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(820, 560);
            ClientSize = new Size(980, 640);

            // Add the Fill root FIRST (lowest z-order) so the docked menu reserves the top and
            // the root fills the remainder.
            Controls.Add(BuildRootLayout());
            BuildMenu();

            _pipeline = new PipelineCoordinator(_settings);
            _pipeline.RipJobUpdated += OnRipJobUpdated;
            _pipeline.RipTitleUpdated += OnRipTitleUpdated;
            _pipeline.EncodeJobUpdated += OnEncodeJobUpdated;
            _pipeline.FilePlaced += OnFilePlaced;
            _pipeline.ManualDiscChangeRequested += OnManualDiscChangeRequested;
            _pipeline.ResolveConflict = ResolveConflict;

            UpdateProviderModeLabel();
            ConfigureForNodeRole();

            Load += (s, e) => RefreshDrives(selectLetter: _settings.LastUsedDrive);
            FormClosed += (s, e) =>
            {
                if (_pipeline != null) { _pipeline.Dispose(); }
                if (_encodeServer != null) { _encodeServer.Dispose(); }
            };
        }

        /// <summary>
        /// Adapts the window to the node role (Advanced settings): a RipperClient shows the live
        /// encoder-server connection state; an EncoderServer stops local ripping and instead
        /// listens for a ripper client, showing the jobs it receives in the encode list.
        /// </summary>
        private void ConfigureForNodeRole()
        {
            if (_settings.NodeRole == NodeRole.RipperClient && _pipeline.IsRemoteRipper)
            {
                _pipeline.RemoteConnectionChanged += OnRemoteConnectionChanged;
                _encodeGroup.Text = "Encode queue → remote encoder (" + _settings.NodeServerHost + ") — connecting…";
            }
            else if (_settings.NodeRole == NodeRole.RipperClient)
            {
                // RipperClient chosen but not fully configured — say so instead of silently ripping+encoding locally.
                _encodeGroup.Text = "Encode queue (local — remote encoder not configured; set host + secret in Settings)";
            }
            else if (_settings.NodeRole == NodeRole.EncoderServer)
            {
                StartEncoderServerMode();
            }
        }

        private void OnRemoteConnectionChanged(bool connected)
        {
            UI(() =>
            {
                _encodeGroup.Text = connected
                    ? "Encode queue → remote encoder (" + _settings.NodeServerHost + ") — ● connected"
                    : "Encode queue → remote encoder (" + _settings.NodeServerHost + ") — ○ reconnecting…";
                SetStatus(connected
                    ? "Connected to the encoder server. Ripped files will be sent there to encode."
                    : "Lost the encoder server connection — ripping continues; files send automatically when it's back.",
                    !connected);
            });
        }

        /// <summary>
        /// EncoderServer role: this machine doesn't rip — it receives ripped files from a client
        /// and encodes them. Disable the disc controls, relabel the panels, and start the listener.
        /// </summary>
        private void StartEncoderServerMode()
        {
            _ripGroup.Text = "Rip queue — disabled (this machine is an Encoder Server node)";
            _driveCombo.Enabled = false;
            _rescanButton.Enabled = false;
            _ejectButton.Enabled = false;
            _scanButton.Enabled = false;
            SetDriveStatus("Encoder Server node: this machine encodes rips sent by a client; it does not rip discs itself.", false);

            if (string.IsNullOrWhiteSpace(_settings.NodeSharedSecret))
            {
                _encodeGroup.Text = "Encode queue — SERVER NOT STARTED (set a shared secret in Settings first)";
                SetStatus("Encoder Server can't start without a shared secret. Set one in Tools > Settings > Advanced.", true);
                return;
            }

            try
            {
                _encodeServer = new Services.Net.EncodeServerHost(_settings);
                _encodeServer.JobUpdated += job => OnEncodeJobUpdated(job); // reuse the encode list UI
                _encodeServer.Start();
                _encodeGroup.Text = "Encode queue (Encoder Server — listening on port " + _settings.NodePort + ")";
                SetStatus("Encoder Server node running on port " + _settings.NodePort +
                          ". LAN only — do not port-forward this port.", false);
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to start Encoder Server.", ex);
                _encodeGroup.Text = "Encode queue — SERVER FAILED TO START (see log)";
                SetStatus("Encoder Server failed to start: " + ex.Message, true);
            }
        }

        // ---------------- menu ----------------

        private void BuildMenu()
        {
            var menu = new MenuStrip();

            var fileMenu = new ToolStripMenuItem("&File");
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (s, e) => Close()));

            // Edit > ES Connection > Edit connection... — quick access to the encoder-server
            // session settings without digging into Settings > Advanced.
            var editMenu = new ToolStripMenuItem("&Edit");
            var esConnection = new ToolStripMenuItem("ES &Connection");
            esConnection.DropDownItems.Add(new ToolStripMenuItem("&Edit connection...", null, OnEditConnection));
            editMenu.DropDownItems.Add(esConnection);

            var toolsMenu = new ToolStripMenuItem("&Tools");
            toolsMenu.DropDownItems.Add(new ToolStripMenuItem("&Settings...", null, OnSettingsClicked));

            // Checkable toggle that reflects/controls whether the welcome screen appears on the
            // next launch — so a user who ticked "Don't show again" can bring it back.
            _showWelcomeMenuItem = new ToolStripMenuItem("Show &welcome screen on startup", null, OnToggleShowWelcome)
            {
                CheckOnClick = true,
                Checked = _settings.ShowWelcomeOnStartup
            };
            toolsMenu.DropDownItems.Add(_showWelcomeMenuItem);

            var helpMenu = new ToolStripMenuItem("&Help");
            helpMenu.DropDownItems.Add(new ToolStripMenuItem("&About AutoRipper...", null, OnAboutClicked));
            helpMenu.DropDownItems.Add(new ToolStripSeparator());
            helpMenu.DropDownItems.Add(new ToolStripMenuItem("&Uninstall AutoRipper...", null, OnUninstallClicked));

            menu.Items.Add(fileMenu);
            menu.Items.Add(editMenu);
            menu.Items.Add(toolsMenu);
            menu.Items.Add(helpMenu);
            MainMenuStrip = menu;
            Controls.Add(menu);
        }

        private void OnEditConnection(object sender, EventArgs e)
        {
            using (var dialog = new ConnectionDialog(_settings))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    // Dialog saved to disk; reload the authoritative copy. The live session keeps
                    // its current wiring until restart (the dialog told the user so).
                    _settings = SettingsStore.Load();
                }
            }
        }

        private void OnAboutClicked(object sender, EventArgs e)
        {
            using (var about = new AboutForm())
            {
                about.ShowDialog(this);
            }
        }

        private void OnUninstallClicked(object sender, EventArgs e)
        {
            using (var dialog = new UninstallDialog())
            {
                dialog.ShowDialog(this);
            }
        }

        // ---------------- overall layout ----------------

        private Control BuildRootLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(10, 8, 10, 6)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));   // drive panel
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // queues
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));   // provider mode
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));   // status strip

            root.Controls.Add(BuildDriveGroup(), 0, 0);
            root.Controls.Add(BuildQueuesSplit(), 0, 1);

            _providerModeLabel = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            root.Controls.Add(_providerModeLabel, 0, 2);

            _statusStrip = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ThemeManager.Ok
            };
            root.Controls.Add(_statusStrip, 0, 3);

            return root;
        }

        private Control BuildDriveGroup()
        {
            var group = new GroupBox { Text = "Optical drive", Dock = DockStyle.Fill };

            var label = new Label { Text = "Drive:", AutoSize = true, Location = new Point(15, 28) };

            _driveCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(60, 24),
                Size = new Size(360, 23),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            _driveCombo.SelectedIndexChanged += OnDriveSelectionChanged;

            _rescanButton = new Button { Text = "Rescan", Size = new Size(90, 26), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _ejectButton = new Button { Text = "Eject", Size = new Size(90, 26), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _scanButton = new Button { Text = "Scan disc && configure...", Size = new Size(210, 26), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _rescanButton.Click += (s, e) => RefreshDrives(selectLetter: SelectedDriveLetter());
            _ejectButton.Click += OnEjectClicked;
            _scanButton.Click += OnScanClicked;

            _driveStatus = new Label
            {
                Text = "",
                AutoSize = false,
                Location = new Point(15, 58),
                Size = new Size(500, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            // Position the right-anchored buttons relative to the group's initial width; anchors
            // keep them pinned to the right edge as the window resizes.
            group.SizeChanged += (s, e) =>
            {
                int right = group.ClientSize.Width - 12;
                _scanButton.Location = new Point(right - _scanButton.Width, 24);
                _ejectButton.Location = new Point(_scanButton.Left - _ejectButton.Width - 8, 24);
                _rescanButton.Location = new Point(_ejectButton.Left - _rescanButton.Width - 8, 24);
                _driveCombo.Width = Math.Max(120, _rescanButton.Left - _driveCombo.Left - 12);
            };

            group.Controls.Add(label);
            group.Controls.Add(_driveCombo);
            group.Controls.Add(_rescanButton);
            group.Controls.Add(_ejectButton);
            group.Controls.Add(_scanButton);
            group.Controls.Add(_driveStatus);
            return group;
        }

        private Control BuildQueuesSplit()
        {
            var split = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 6, 0, 6)
            };
            // Each queue takes exactly half the width — no overlap when narrow, no gap when wide.
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            split.Controls.Add(BuildRipGroup(), 0, 0);
            split.Controls.Add(BuildEncodeGroup(), 1, 0);
            return split;
        }

        private Control BuildRipGroup()
        {
            var group = new GroupBox { Text = "Rip queue (one disc at a time)", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 5, 0) };
            _ripGroup = group;

            var inner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
            inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            // One row per title, grouped under each disc, so it's clear which titles succeeded.
            _ripList = new AutoColumnListView
            {
                View = View.Details,
                FullRowSelect = true,
                Dock = DockStyle.Fill,
                ShowGroups = true,
                ShowItemToolTips = true
            };
            _ripList.Columns.Add("Title", 230);
            _ripList.Columns.Add("Status", 80);
            _ripList.Columns.Add("Progress", 120);

            _ripProgress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Margin = new Padding(0, 3, 0, 0) };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 0) };
            var stop = new Button { Text = "Stop current rip", Size = new Size(130, 26) };
            var retry = new Button { Text = "Retry failed title(s)", Size = new Size(160, 26) };
            stop.Click += OnStopRip;
            retry.Click += OnRetryFailed;
            buttons.Controls.Add(stop);
            buttons.Controls.Add(retry);

            inner.Controls.Add(_ripList, 0, 0);
            inner.Controls.Add(_ripProgress, 0, 1);
            inner.Controls.Add(buttons, 0, 2);
            group.Controls.Add(inner);
            return group;
        }

        private Control BuildEncodeGroup()
        {
            var group = new GroupBox { Text = "Encode queue (parallel; check items to re-encode)", Dock = DockStyle.Fill, Margin = new Padding(5, 0, 0, 0) };
            _encodeGroup = group;

            var inner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
            inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            _encodeList = new AutoColumnListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill };
            _encodeList.Columns.Add("File", 200);
            _encodeList.Columns.Add("Status", 90);
            _encodeList.Columns.Add("Progress", 140);

            _encodeProgress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Margin = new Padding(0, 3, 0, 0) };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 0) };
            var selectAll = new Button { Text = "Select all", Size = new Size(85, 26) };
            var deselectAll = new Button { Text = "Deselect all", Size = new Size(90, 26) };
            var reEncode = new Button { Text = "Re-encode selected", Size = new Size(150, 26) };
            var openFolder = new Button { Text = "Open output folder", Size = new Size(150, 26) };
            selectAll.Click += (s, e) => SetAllEncodeChecks(true);
            deselectAll.Click += (s, e) => SetAllEncodeChecks(false);
            reEncode.Click += OnReEncodeSelected;
            openFolder.Click += OnOpenOutputFolder;
            buttons.Controls.Add(selectAll);
            buttons.Controls.Add(deselectAll);
            buttons.Controls.Add(reEncode);
            buttons.Controls.Add(openFolder);

            inner.Controls.Add(_encodeList, 0, 0);
            inner.Controls.Add(_encodeProgress, 0, 1);
            inner.Controls.Add(buttons, 0, 2);
            group.Controls.Add(inner);
            return group;
        }

        private void UpdateProviderModeLabel()
        {
            if (HasMetadataKeys())
            {
                _providerModeLabel.Text = "Metadata lookups: LIVE (using your saved API keys).";
                _providerModeLabel.ForeColor = ThemeManager.Ok;
            }
            else
            {
                _providerModeLabel.Text = "Metadata lookups: TEST DATA ONLY (no API keys saved — add them in Tools > Settings).";
                _providerModeLabel.ForeColor = ThemeManager.Warn;
            }
        }

        private bool HasMetadataKeys()
        {
            return !string.IsNullOrWhiteSpace(_settings.TheTvdbApiKey) ||
                   !string.IsNullOrWhiteSpace(_settings.OmdbApiKey);
        }

        private IMetadataProvider CreateProvider()
        {
            return HasMetadataKeys()
                ? (IMetadataProvider)new OnlineMetadataProvider(_settings)
                : new MockMetadataProvider();
        }

        // ---------------- scan + configure flow ----------------

        private async void OnScanClicked(object sender, EventArgs e)
        {
            // Advanced network/mapped-drive mode reads a shared source instead of a local drive.
            bool network = _settings.NetworkRipEnabled && !string.IsNullOrWhiteSpace(_settings.NetworkRipSource);
            // Resolve the actual disc folder first (BDMV/VIDEO_TS differs per disc type), so the
            // user can leave the setting on the drive root across Blu-ray/DVD swaps.
            string resolvedSource = network
                ? MakeMkvService.ResolveDiscFolder(_settings.NetworkRipSource, _settings.NetworkRipSearchSubfolders)
                : null;
            string sourceSpec = network ? MakeMkvService.BuildFileSourceSpec(resolvedSource) : null;

            // Log the full resolution chain so a failed network scan is diagnosable from the log:
            // what the user configured, what folder we resolved, and the exact MakeMKV source spec.
            if (network)
            {
                Logger.Info("Network scan: configured='" + _settings.NetworkRipSource +
                    "' (searchSubfolders=" + _settings.NetworkRipSearchSubfolders +
                    ") -> resolved='" + resolvedSource + "' -> MakeMKV source='" + sourceSpec + "'");
            }

            string letter = SelectedDriveLetter();
            if (!network && string.IsNullOrEmpty(letter))
            {
                SetStatus("Select a drive first.", true);
                return;
            }

            // Audio CD? A quick TOC probe (local drives only) answers instantly: only audio CDs
            // expose CDDA tracks — DVDs/Blu-rays are data discs and yield zero audio tracks. If it
            // IS an audio CD, the whole MakeMKV path is skipped for the music flow.
            if (!network)
            {
                AudioCdToc toc = null;
                try
                {
                    toc = Services.Music.CdTocReader.Read(letter);
                }
                catch
                {
                    // No readable TOC this way — fall through to the video scan.
                }
                if (toc != null && toc.TrackCount > 0)
                {
                    await ScanAudioCd(letter, toc);
                    return;
                }
            }

            _scanButton.Enabled = false;
            SetStatus(network
                ? "Scanning the shared source (" + resolvedSource + ") ... (this can take a minute)"
                : "Scanning the disc in " + letter + " ... (this can take a minute)", false);

            DiscScanResult scan;
            try
            {
                scan = await Task.Run(() =>
                {
                    if (network)
                    {
                        return _pipeline.MakeMkv.ScanSource(sourceSpec, 0, CancellationToken.None);
                    }

                    int discIndex = _pipeline.MakeMkv.FindDiscIndexForLetter(letter);
                    if (discIndex < 0)
                    {
                        return new DiscScanResult { Success = false, Error = "Couldn't match that drive to a MakeMKV disc. Is a disc inserted?" };
                    }
                    DiscScanResult r = _pipeline.MakeMkv.ScanDisc(discIndex, CancellationToken.None);
                    r.DiscIndex = discIndex;
                    return r;
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Disc scan threw.", ex);
                SetStatus("Scan failed: " + ex.Message, true);
                _scanButton.Enabled = true;
                return;
            }

            if (!scan.Success)
            {
                SetStatus(scan.Error, true);
                _scanButton.Enabled = true;
                return;
            }

            SetStatus("Found " + scan.Titles.Count + " title(s) on '" + scan.DiscName + "'. Confirm the details...", false);

            using (var form = new MetadataEntryForm(CreateProvider(), _settings, scan.Titles, DiscType.Dvd))
            {
                if (form.ShowDialog(this) == DialogResult.OK && form.Result != null)
                {
                    RipJob job = _pipeline.StartDiscJob(form.Result, scan.Titles, scan.DiscIndex,
                        network ? "" : letter, sourceSpec, network);
                    AddRipJob(job);
                    SetStatus("Queued '" + job.DiscLabel + "' for ripping.", false);
                }
                else
                {
                    SetStatus("Disc configuration cancelled — nothing queued.", false);
                }
            }

            _scanButton.Enabled = true;
        }

        /// <summary>
        /// Audio CD flow: identify via MusicBrainz (exact Disc ID, then fuzzy TOC), open the
        /// same configure form landing on the music panel, and queue the checked tracks
        /// through the same two queues as video.
        /// </summary>
        private async System.Threading.Tasks.Task ScanAudioCd(string letter, AudioCdToc toc)
        {
            _scanButton.Enabled = false;
            SetStatus("Audio CD detected (" + toc.TrackCount + " tracks) — identifying on MusicBrainz...", false);

            List<MusicRelease> candidates;
            try
            {
                candidates = await System.Threading.Tasks.Task.Run(() =>
                {
                    var client = new Services.Music.MusicBrainzClient();
                    string discId = Services.Music.MusicBrainzDiscId.Compute(toc);
                    var found = client.LookupByDiscIdAsync(discId).GetAwaiter().GetResult();
                    if (found.Count == 0)
                    {
                        // This exact pressing isn't in the database — fuzzy-match the TOC.
                        found = client.LookupByTocAsync(toc).GetAwaiter().GetResult();
                    }
                    return found;
                });
            }
            catch (Exception ex)
            {
                Logger.Error("MusicBrainz identification failed.", ex);
                candidates = new List<MusicRelease>(); // form offers the typed search
            }

            SetStatus(candidates.Count > 0
                ? "Found " + candidates.Count + " matching release(s). Confirm the edition and tracks..."
                : "Disc not recognized automatically — search by artist/album in the next screen.", false);

            using (var form = new MetadataEntryForm(CreateProvider(), _settings,
                new List<DiscTitle>(), DiscType.Cd, toc, candidates))
            {
                if (form.ShowDialog(this) == DialogResult.OK && form.MusicResult != null)
                {
                    RipJob job = _pipeline.StartMusicJob(form.MusicResult, toc, letter, _settings.MusicFormatId);
                    AddRipJob(job);
                    SetStatus("Queued '" + job.DiscLabel + "' — " + job.TitleResults.Count +
                        " track(s) to rip.", false);
                }
                else
                {
                    SetStatus("Disc configuration cancelled — nothing queued.", false);
                }
            }

            _scanButton.Enabled = true;
        }

        /// <summary>
        /// A network/mapped-source rip finished — we can't eject a remote drive, so prompt the
        /// user to change the disc on the machine that owns it. Non-blocking (BeginInvoke) so the
        /// rip worker thread moves straight on to the next queued job.
        /// </summary>
        private void OnManualDiscChangeRequested(RipJob job)
        {
            UI(() =>
            {
                SetStatus("Rip complete — change the disc on the shared drive.", false);
                MessageBox.Show(this,
                    "The rip of '" + job.DiscLabel + "' is complete.\r\n\r\n" +
                    "This is a network/mapped source, so AutoRipper can't eject it from here. " +
                    "Please change the disc on the shared drive before starting the next rip.",
                    "Change disc on shared drive",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
        }

        // ---------------- rip queue UI ----------------

        private void AddRipJob(RipJob job)
        {
            _ripJobs[job.Id] = job;

            var group = new ListViewGroup(job.DiscLabel) { Header = job.DiscLabel };
            _ripList.Groups.Add(group);
            _ripGroups[job.Id] = group;

            // Excel-style fold: give the disc's header row a native ▲/▼ chevron right away, so the
            // user can collapse any disc's titles into its header line at will. Finished discs are
            // auto-collapsed in OnRipJobUpdated.
            _ripList.SetGroupCollapsed(group, false);

            if (job.TitleResults == null || job.TitleResults.Count == 0)
            {
                // "All titles" jobs have no per-title breakdown; show a single aggregate row.
                AddTitleRow(job, new RipTitleResult { TitleIndex = -1, Label = "All titles", Status = RipStatus.Queued }, group);
                return;
            }

            foreach (RipTitleResult tr in job.TitleResults)
            {
                AddTitleRow(job, tr, group);
            }
        }

        private void AddTitleRow(RipJob job, RipTitleResult tr, ListViewGroup group)
        {
            var item = new ListViewItem(tr.Label, group);
            item.SubItems.Add(tr.Status.ToString());
            item.SubItems.Add("");
            item.Tag = new RipRowTag { JobId = job.Id, TitleIndex = tr.TitleIndex };
            _ripList.Items.Add(item);
            _ripTitleRows[RipRowKey(job.Id, tr.TitleIndex)] = item;
        }

        private static string RipRowKey(Guid jobId, int titleIndex)
        {
            return jobId.ToString("N") + ":" + titleIndex;
        }

        private void OnRipJobUpdated(RipJob job)
        {
            UI(() =>
            {
                ListViewGroup group;
                if (_ripGroups.TryGetValue(job.Id, out group))
                {
                    // Only rewrite the header when it actually changes. Reassigning a
                    // ListViewGroup.Header forces the grouped list to re-layout and snaps the
                    // scroll position back to the top, so doing it on every progress tick made the
                    // rip side jump and flicker. CurrentOperation changes rarely (operation names,
                    // not the percentage), so this update is now infrequent.
                    // Header updates preserve the group's fold state INCLUDING the user's own
                    // chevron clicks (which happen natively, without any event to us).
                    _ripList.SetGroupHeaderPreservingState(group, job.DiscLabel + "  —  " + job.CurrentOperation);

                    // A disc that finished with every title successful folds up into its header
                    // line (Excel-style) — but only ONCE. After that the chevron belongs to the
                    // user; re-asserting the fold on every late event was overriding their clicks
                    // ("chevron does nothing"). Discs with failures stay expanded so the red rows
                    // are visible for "Retry failed title(s)".
                    if (job.Status == RipStatus.Completed &&
                        !_autoFoldedJobs.Contains(job.Id) && AllTitleRowsCompleted(group))
                    {
                        _autoFoldedJobs.Add(job.Id);
                        _ripList.SetGroupCollapsed(group, true);
                    }
                }
                if (job.Status == RipStatus.Ripping || job.Status == RipStatus.Completed)
                {
                    _ripProgress.Value = Clamp(job.ProgressPercent);
                }
            });
        }

        /// <summary>True when every title row in the disc's group shows Completed.</summary>
        private static bool AllTitleRowsCompleted(ListViewGroup group)
        {
            foreach (ListViewItem item in group.Items)
            {
                if (item.SubItems[1].Text != RipStatus.Completed.ToString()) { return false; }
            }
            return group.Items.Count > 0;
        }

        private void OnRipTitleUpdated(RipJob job, RipTitleResult tr)
        {
            UI(() =>
            {
                ListViewItem item;
                if (!_ripTitleRows.TryGetValue(RipRowKey(job.Id, tr.TitleIndex), out item)) { return; }

                // Assign only what actually changed: rewriting a cell (even with identical text)
                // invalidates and repaints it, and doing that for every row on every progress tick
                // is what made the rip side flash. With these guards a typical tick repaints just
                // the one Progress cell that moved.
                string status = tr.Status.ToString();
                string progress = FormatProgress(tr.ProgressPercent, "");
                Color color =
                    tr.Status == RipStatus.Failed ? ThemeManager.Bad :
                    tr.Status == RipStatus.Completed ? ThemeManager.Ok :
                    ThemeManager.Text;
                string tip = tr.Status == RipStatus.Failed ? tr.Error : "";

                if (item.SubItems[1].Text != status) { item.SubItems[1].Text = status; }
                if (item.SubItems[2].Text != progress) { item.SubItems[2].Text = progress; }
                if (item.ForeColor != color) { item.ForeColor = color; }
                if (item.ToolTipText != tip) { item.ToolTipText = tip; }
            });
        }

        private void OnStopRip(object sender, EventArgs e)
        {
            // Stops the rip in progress. MakeMKV's process is killed; the stopped title (and any
            // queued titles in that disc) get marked failed so "Retry failed" can re-run them.
            // The disc is left in the drive so it can be cleaned. Safe: titles already ripped are kept.
            _pipeline.CancelCurrentRip();
            SetStatus("Stopping the current rip — the unfinished title will be marked failed so you can " +
                      "clean the disc and use 'Retry failed'.", false);
        }

        private void OnRetryFailed(object sender, EventArgs e)
        {
            // Collect failed title rows, grouped by the disc job they came from.
            var byJob = new Dictionary<Guid, List<int>>();
            foreach (ListViewItem item in _ripList.Items)
            {
                var tag = item.Tag as RipRowTag;
                if (tag == null) { continue; }
                if (item.SubItems[1].Text != RipStatus.Failed.ToString()) { continue; }

                List<int> list;
                if (!byJob.TryGetValue(tag.JobId, out list)) { list = new List<int>(); byJob[tag.JobId] = list; }
                list.Add(tag.TitleIndex);
            }

            if (byJob.Count == 0)
            {
                SetStatus("No failed titles to retry.", false);
                return;
            }

            int retried = 0;
            foreach (KeyValuePair<Guid, List<int>> kv in byJob)
            {
                RipJob original;
                if (!_ripJobs.TryGetValue(kv.Key, out original)) { continue; }
                RipJob retryJob = _pipeline.RetryTitles(original, kv.Value);
                AddRipJob(retryJob);
                retried += kv.Value.Count;
            }
            SetStatus("Re-queued " + retried + " failed title(s). Make sure the disc is still in the drive.", false);
        }

        // ---------------- encode queue UI ----------------

        private void OnEncodeJobUpdated(EncodeJob job)
        {
            UI(() =>
            {
                _encodeJobs[job.Id] = job;

                ListViewItem item;
                if (!_encodeRows.TryGetValue(job.Id, out item))
                {
                    item = new ListViewItem(string.IsNullOrEmpty(job.DisplayName) ? job.InputFile : job.DisplayName);
                    item.SubItems.Add(job.Status.ToString());
                    item.SubItems.Add("");
                    item.Tag = job.Id;
                    _encodeList.Items.Add(item);
                    _encodeRows[job.Id] = item;
                }

                item.SubItems[1].Text = job.Status.ToString();
                item.SubItems[2].Text = FormatProgress(job.ProgressPercent, job.CurrentOperation);

                if (job.Status == EncodeStatus.Encoding || job.Status == EncodeStatus.Completed)
                {
                    _encodeProgress.Value = Clamp(job.ProgressPercent);
                }
            });
        }

        private void OnFilePlaced(EncodeJob job, PlacementResult result)
        {
            UI(() =>
            {
                ListViewItem item;
                if (_encodeRows.TryGetValue(job.Id, out item))
                {
                    item.SubItems[2].Text = job.CurrentOperation;
                }

                if (result.Outcome == PlacementOutcome.Failed)
                {
                    SetStatus("A file finished encoding but couldn't be placed in the library — see the log.", true);
                }
                else
                {
                    // Remember where files are landing so "Open output folder" jumps straight there.
                    if (!string.IsNullOrEmpty(result.FinalPath))
                    {
                        try { _lastPlacedFolder = Path.GetDirectoryName(result.FinalPath); }
                        catch { /* keep whatever we had */ }
                    }
                    SetStatus(job.CurrentOperation, false);
                }
            });
        }

        private void OnOpenOutputFolder(object sender, EventArgs e)
        {
            // Prefer the folder we last placed a file in; otherwise fall back to a configured
            // library root so the button is still useful before anything has finished.
            string folder = _lastPlacedFolder;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                folder = FirstExistingFolder(_settings.TvShowsRoot, _settings.MoviesRoot, _settings.MusicRoot);
            }

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                SetStatus("No output folder to open yet — set your library roots in Tools > Settings.", true);
                return;
            }

            try
            {
                Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception ex)
            {
                Logger.Error("Couldn't open output folder " + folder, ex);
                SetStatus("Couldn't open the folder: " + ex.Message, true);
            }
        }

        private static string FirstExistingFolder(params string[] candidates)
        {
            foreach (string c in candidates)
            {
                if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c)) { return c; }
            }
            return "";
        }

        private void SetAllEncodeChecks(bool @checked)
        {
            foreach (ListViewItem item in _encodeList.Items) { item.Checked = @checked; }
        }

        private void OnReEncodeSelected(object sender, EventArgs e)
        {
            int count = 0;
            foreach (ListViewItem item in _encodeList.Items)
            {
                if (!item.Checked) { continue; }
                var id = (Guid)item.Tag;
                EncodeJob job;
                if (_encodeJobs.TryGetValue(id, out job))
                {
                    _pipeline.EncodeQueue.ReEncode(job);
                    item.Checked = false;
                    count++;
                }
            }
            SetStatus(count == 0 ? "Check one or more finished items to re-encode them." :
                "Re-queued " + count + " item(s) for encoding (added to the end of the queue).", false);
        }

        // ---------------- overwrite confirmation (from a background thread) ----------------

        private ConflictResolution ResolveConflict(PlacementPlan plan)
        {
            if (_sessionResolution.HasValue) { return _sessionResolution.Value; }
            if (IsDisposed) { return ConflictResolution.KeepBoth; }

            ConflictResolution chosen = ConflictResolution.KeepBoth;
            try
            {
                Invoke((Action)(() =>
                {
                    using (var dlg = new ConflictDialog(plan.Target.FullPath))
                    {
                        dlg.ShowDialog(this);
                        chosen = dlg.Resolution;
                        if (dlg.ApplyToAll) { _sessionResolution = chosen; }
                    }
                }));
            }
            catch (Exception ex)
            {
                Logger.Error("Conflict prompt failed; defaulting to Keep Both.", ex);
                return ConflictResolution.KeepBoth;
            }
            return chosen;
        }

        // ---------------- drive helpers (from Phase 2) ----------------

        private void RefreshDrives(string selectLetter)
        {
            List<OpticalDrive> drives = DriveDetector.GetOpticalDrives();

            _driveCombo.BeginUpdate();
            _driveCombo.Items.Clear();
            foreach (OpticalDrive d in drives) { _driveCombo.Items.Add(d); }
            _driveCombo.EndUpdate();

            bool hasDrives = drives.Count > 0;
            bool network = _settings.NetworkRipEnabled && !string.IsNullOrWhiteSpace(_settings.NetworkRipSource);

            // Eject and the drive dropdown only apply to a local optical drive. But Scan is also
            // valid in network mode (it reads the mapped/shared source), so it must NOT be gated
            // on a local drive being present — that's the driveless-server case.
            _ejectButton.Enabled = hasDrives;
            _driveCombo.Enabled = hasDrives;
            _scanButton.Enabled = hasDrives || network;

            if (!hasDrives)
            {
                SetDriveStatus(network
                    ? "No local optical drive — using the network source (" + _settings.NetworkRipSource +
                      "). Click 'Scan disc & configure'."
                    : "No optical drives detected. Connect a drive and click Rescan.", !network);
                return;
            }

            int index = IndexOfLetter(drives, selectLetter);
            _driveCombo.SelectedIndex = index >= 0 ? index : 0;
            SetDriveStatus(drives.Count == 1 ? "Found 1 optical drive." : "Found " + drives.Count + " optical drives.", false);
        }

        private int IndexOfLetter(List<OpticalDrive> drives, string letter)
        {
            if (string.IsNullOrWhiteSpace(letter)) { return -1; }
            string target = letter.Trim().TrimEnd(':', '\\').ToUpperInvariant();
            for (int i = 0; i < drives.Count; i++)
            {
                string dl = (drives[i].DriveLetter ?? "").TrimEnd(':', '\\').ToUpperInvariant();
                if (dl == target) { return i; }
            }
            return -1;
        }

        private string SelectedDriveLetter()
        {
            var drive = _driveCombo.SelectedItem as OpticalDrive;
            return drive == null ? "" : drive.DriveLetter;
        }

        private void OnDriveSelectionChanged(object sender, EventArgs e)
        {
            string letter = SelectedDriveLetter();
            if (string.IsNullOrEmpty(letter)) { return; }
            if (_settings.LastUsedDrive != letter)
            {
                _settings.LastUsedDrive = letter;
                TrySaveSettings();
            }
        }

        private async void OnEjectClicked(object sender, EventArgs e)
        {
            string letter = SelectedDriveLetter();
            if (string.IsNullOrEmpty(letter)) { SetDriveStatus("Select a drive first.", true); return; }

            _ejectButton.Enabled = false;
            SetDriveStatus("Ejecting " + letter + " ...", false);

            EjectResult result = await Task.Run(() => EjectService.Eject(letter));
            if (result.Success)
            {
                SetDriveStatus("Ejected " + letter + "  (" + result.Message + ")", false);
            }
            else
            {
                SetDriveStatus(result.Message, true);
                MessageBox.Show(this, result.Message, "Eject failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            _ejectButton.Enabled = true;
        }

        // ---------------- misc ----------------

        private static int Clamp(int percent)
        {
            if (percent < 0) { return 0; }
            if (percent > 100) { return 100; }
            return percent;
        }

        private static string FormatProgress(int percent, string operation)
        {
            string op = string.IsNullOrEmpty(operation) ? "" : "  " + operation;
            return percent + "%" + op;
        }

        private void SetDriveStatus(string text, bool isProblem)
        {
            _driveStatus.Text = text;
            _driveStatus.ForeColor = isProblem ? ThemeManager.Bad : ThemeManager.Ok;
        }

        private void SetStatus(string text, bool isProblem)
        {
            _statusStrip.Text = text;
            _statusStrip.ForeColor = isProblem ? ThemeManager.Bad : ThemeManager.Ok;
        }

        /// <summary>Runs an action on the UI thread; safe to call from background events.</summary>
        private void UI(Action action)
        {
            if (IsDisposed) { return; }
            try
            {
                if (InvokeRequired) { BeginInvoke(action); }
                else { action(); }
            }
            catch (ObjectDisposedException)
            {
                // Window closed mid-update; ignore.
            }
        }

        private void OnToggleShowWelcome(object sender, EventArgs e)
        {
            // CheckOnClick already flipped the menu item; persist that as the startup preference.
            _settings.ShowWelcomeOnStartup = _showWelcomeMenuItem.Checked;
            TrySaveSettings();
            SetStatus(_settings.ShowWelcomeOnStartup
                ? "The welcome screen will show again the next time you start the app."
                : "The welcome screen won't show on startup.", false);
        }

        private void OnSettingsClicked(object sender, EventArgs e)
        {
            using (var settingsForm = new SettingsForm(_settings))
            {
                if (settingsForm.ShowDialog(this) == DialogResult.OK)
                {
                    _settings = SettingsStore.Load();
                    UpdateProviderModeLabel();
                    // Keep the menu toggle in sync in case the setting changed elsewhere.
                    if (_showWelcomeMenuItem != null) { _showWelcomeMenuItem.Checked = _settings.ShowWelcomeOnStartup; }
                    // Re-evaluate drive/scan state: enabling network mode makes Scan available even
                    // with no local drive, so the button must refresh now rather than on next Rescan.
                    RefreshDrives(SelectedDriveLetter());
                }
            }
        }

        private void TrySaveSettings()
        {
            try { SettingsStore.Save(_settings); }
            catch (Exception ex) { Logger.Error("Couldn't save last-used drive.", ex); }
        }
    }
}
