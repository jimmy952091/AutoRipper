// AutoRipper — automated disc ripping/encoding for Plex, Jellyfin, and other media servers.
// Copyright (C) 2026 James Spurgeon (heto.black@gmail.com)
//
// This program is free software: you can redistribute it and/or modify it under the terms of
// the GNU Affero General Public License as published by the Free Software Foundation, either
// version 3 of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License along with this
// program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Threading;
using MediaRipperEncoder.Models;
using Newtonsoft.Json.Linq;

namespace MediaRipperEncoder.Services.Dashboard
{
    /// <summary>
    /// What the remote-setup agent needs from the rest of the app, abstracted so the agent's
    /// guard logic is testable without discs, subprocesses, or API keys. The live implementation
    /// (LiveSetupBackend) wires these to MakeMKV, the metadata provider, and the pipeline.
    /// </summary>
    public interface IRemoteSetupBackend
    {
        /// <summary>True while a rip is running — scanning/processing would fight over the drive.</summary>
        bool IsBusyRipping();

        /// <summary>Scan the disc (local drive or configured network source). An audio CD comes back
        /// as IsAudioCd + Toc rather than titles. Throws with a user-readable message on failure
        /// (no disc, drive not chosen yet…).</summary>
        RemoteScanData Scan();

        List<MetadataCandidate> SearchMovies(string title, string year);
        List<MetadataCandidate> SearchSeries(string name);
        List<EpisodeInfo> GetEpisodes(string seriesId, int season, EpisodeOrder order);

        /// <summary>The series' name in the preferred metadata language ("" keeps what's shown).</summary>
        string GetSeriesName(string seriesId);

        /// <summary>MusicBrainz releases for the CD's table of contents (Disc ID, then fuzzy TOC).</summary>
        List<MusicRelease> LookupMusic(AudioCdToc toc);

        /// <summary>Typed fallback when the pressing isn't in MusicBrainz by TOC.</summary>
        List<MusicRelease> SearchMusic(string artist, string album);

        /// <summary>Queue the confirmed disc. Returns the human disc label for the browser.</summary>
        string StartJob(MediaMetadata meta, RemoteScanData scan);

        /// <summary>Queue the confirmed audio CD (Selected flags already set on the release's tracks).
        /// conflictPolicy: "keepBoth" (default) / "overwrite" / "skip" — decided up front in the
        /// browser because nobody is at the machine to answer a conflict prompt.</summary>
        string StartMusicJob(MusicRelease release, AudioCdToc toc, string driveLetter, string formatId,
            string conflictPolicy);

        // Quick controls, mirroring the desktop buttons. Each returns a short status message
        // for the browser; failures throw with a user-readable reason.
        string StopRip();
        string RetryFailed();
        string Eject();
    }

    /// <summary>Everything one scan produced, cached by the agent between Scan and Process.</summary>
    public class RemoteScanData
    {
        public string ScanId = "";
        public string DiscName = "";
        public List<DiscTitle> Titles = new List<DiscTitle>();
        public int DiscIndex;
        public string DriveLetter = "";
        public string SourceSpec = "";       // network/mapped source, "" for a local drive
        public bool ManualDiscChange;        // true for a network source (no remote eject)
        public List<PresetChoice> Presets = new List<PresetChoice>();
        public bool ProviderIsLive;          // false = mock provider (no API keys configured)

        // Audio CD variant: the wizard goes down the music path instead of titles/lookup.
        public bool IsAudioCd;
        public AudioCdToc Toc;
        public List<MusicFormatChoice> Formats = new List<MusicFormatChoice>();
        public string DefaultFormatId = "";
    }

    /// <summary>One selectable music output format (id persisted, label shown).</summary>
    public class MusicFormatChoice
    {
        public string Id = "";
        public string Label = "";
    }

    /// <summary>One HandBrake preset the instance can encode with (name + a friendly label).</summary>
    public class PresetChoice
    {
        public string Name = "";
        public string Label = "";
    }

    /// <summary>
    /// Executes remote disc-setup commands ON the instance that owns the drive. One command at a
    /// time on a dedicated worker thread (a scan and a lookup racing each other helps no one), and
    /// every guard the desktop flow enforces is enforced here too:
    ///  - Process requires an explicit confirmed match (MatchConfirmed is set HERE, only when the
    ///    payload carries the confirmed provider identity — never silently),
    ///  - Process must reference the CURRENT scan (a stale browser tab can't rip yesterday's disc),
    ///  - every title index in the payload must exist on the cached scan,
    ///  - nothing runs while a rip is using the drive.
    /// The metadata package is rebuilt server-side from constrained fields — the browser's JSON is
    /// input to validation, never trusted wholesale.
    /// </summary>
    public class RemoteSetupAgent : IDisposable
    {
        private readonly IRemoteSetupBackend _backend;
        private readonly Queue<DashCommand> _pending = new Queue<DashCommand>();
        private readonly object _lock = new object();
        private readonly ManualResetEvent _wake = new ManualResetEvent(false);
        private Thread _worker;
        private volatile bool _running;

        private RemoteScanData _lastScan;            // guarded by _lock
        private List<MusicRelease> _lastReleases;    // guarded by _lock; audio-CD candidates

        /// <summary>Fired when a command finishes; the reporter delivers this to the host.</summary>
        public event Action<DashCommandResult> ResultReady;

        public RemoteSetupAgent(IRemoteSetupBackend backend)
        {
            _backend = backend;
            _running = true;
            _worker = new Thread(WorkLoop) { IsBackground = true, Name = "RemoteSetupAgent" };
            _worker.Start();
        }

        /// <summary>Accepts a command from the report cycle. Returns immediately; executes async.</summary>
        public void HandleCommand(DashCommand cmd)
        {
            if (cmd == null || string.IsNullOrEmpty(cmd.Id)) { return; }
            lock (_lock) { _pending.Enqueue(cmd); }
            _wake.Set();
        }

        private void WorkLoop()
        {
            while (_running)
            {
                DashCommand cmd = null;
                lock (_lock)
                {
                    if (_pending.Count > 0) { cmd = _pending.Dequeue(); }
                    else { _wake.Reset(); }
                }
                if (cmd == null)
                {
                    _wake.WaitOne(500);
                    continue;
                }

                DashCommandResult result = Execute(cmd);
                var h = ResultReady;
                if (h != null)
                {
                    try { h(result); }
                    catch (Exception ex) { Logger.Error("Remote setup: result delivery failed.", ex); }
                }
            }
        }

        private DashCommandResult Execute(DashCommand cmd)
        {
            var result = new DashCommandResult { Id = cmd.Id };
            try
            {
                switch (cmd.Action)
                {
                    case DashAction.Scan: result.Result = DoScan(); break;
                    case DashAction.SearchMovies: result.Result = DoSearchMovies(cmd); break;
                    case DashAction.SearchSeries: result.Result = DoSearchSeries(cmd); break;
                    case DashAction.Episodes: result.Result = DoEpisodes(cmd); break;
                    case DashAction.MusicLookup: result.Result = DoMusicLookup(cmd); break;
                    case DashAction.Process: result.Result = DoProcess(cmd); break;
                    case DashAction.StopRip: result.Result = Message(_backend.StopRip()); break;
                    case DashAction.RetryFailed: result.Result = Message(_backend.RetryFailed()); break;
                    case DashAction.Eject: result.Result = Message(_backend.Eject()); break;
                    default:
                        throw new InvalidOperationException(
                            "This machine's AutoRipper doesn't understand the request '" + cmd.Action +
                            "' — it may be running an older version.");
                }
                result.Ok = true;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = ex.Message;
                Logger.Info("Remote setup command '" + cmd.Action + "' failed: " + ex.Message);
            }
            return result;
        }

        // ---- actions ----

        private JObject DoScan()
        {
            if (_backend.IsBusyRipping())
            {
                throw new InvalidOperationException(
                    "A rip is in progress on this machine — the drive can't be scanned until it finishes.");
            }

            RemoteScanData scan = _backend.Scan();
            scan.ScanId = Guid.NewGuid().ToString("N").Substring(0, 12);
            lock (_lock) { _lastScan = scan; _lastReleases = null; }

            // Audio CD: the wizard branches to the music flow — no titles, no video presets.
            if (scan.IsAudioCd)
            {
                var formats = new JArray();
                foreach (MusicFormatChoice f in scan.Formats)
                {
                    var jf = new JObject();
                    jf["id"] = f.Id;
                    jf["label"] = f.Label;
                    formats.Add(jf);
                }
                var cd = new JObject();
                cd["scanId"] = scan.ScanId;
                cd["kind"] = "audioCd";
                cd["discName"] = scan.DiscName ?? "";
                cd["trackCount"] = scan.Toc != null ? scan.Toc.TrackCount : 0;
                cd["formats"] = formats;
                cd["defaultFormatId"] = scan.DefaultFormatId ?? "";
                return cd;
            }

            var titles = new JArray();
            foreach (DiscTitle t in scan.Titles)
            {
                var jt = new JObject();
                jt["index"] = t.Index;
                jt["duration"] = t.Duration ?? "";
                jt["chapters"] = t.ChapterCount;
                jt["sizeGb"] = t.SizeBytes > 0
                    ? Math.Round(t.SizeBytes / (1024.0 * 1024.0 * 1024.0), 1)
                    : 0.0;
                titles.Add(jt);
            }

            var presets = new JArray();
            foreach (PresetChoice p in scan.Presets)
            {
                var jp = new JObject();
                jp["name"] = p.Name;
                jp["label"] = p.Label;
                presets.Add(jp);
            }

            var o = new JObject();
            o["scanId"] = scan.ScanId;
            o["kind"] = "video";
            o["discName"] = scan.DiscName ?? "";
            o["titles"] = titles;
            o["presets"] = presets;
            o["providerLive"] = scan.ProviderIsLive;
            o["networkSource"] = !string.IsNullOrEmpty(scan.SourceSpec);
            return o;
        }

        /// <summary>
        /// MusicBrainz candidates for the scanned audio CD: by Disc ID / fuzzy TOC normally, or by
        /// typed artist+album when the browser supplies them (the not-in-the-database fallback).
        /// The full releases are cached HERE; the browser only ever refers to them by index, so a
        /// Process can't smuggle in tampered track lists or a different release.
        /// </summary>
        private JObject DoMusicLookup(DashCommand cmd)
        {
            RemoteScanData scan = CurrentAudioCdScan(cmd.ArgString("scanId"));

            string artist = cmd.ArgString("artist").Trim();
            string album = cmd.ArgString("album").Trim();
            List<MusicRelease> releases = (artist.Length > 0 || album.Length > 0)
                ? _backend.SearchMusic(artist, album)
                : _backend.LookupMusic(scan.Toc);
            if (releases == null) { releases = new List<MusicRelease>(); }

            lock (_lock) { _lastReleases = releases; }

            var arr = new JArray();
            for (int i = 0; i < releases.Count; i++)
            {
                MusicRelease r = releases[i];
                var jr = new JObject();
                jr["index"] = i;
                jr["artist"] = r.Artist ?? "";
                jr["album"] = r.Album ?? "";
                jr["year"] = r.Year ?? "";
                jr["detail"] = r.Detail ?? "";
                jr["discNumber"] = r.DiscNumber;
                jr["discCount"] = r.DiscCount;
                var tracks = new JArray();
                if (r.Tracks != null)
                {
                    foreach (AudioTrack t in r.Tracks)
                    {
                        var jt = new JObject();
                        jt["n"] = t.Number;
                        jt["title"] = t.Title ?? "";
                        jt["len"] = t.LengthText ?? "";
                        tracks.Add(jt);
                    }
                }
                jr["tracks"] = tracks;
                arr.Add(jr);
            }
            var o = new JObject();
            o["releases"] = arr;
            return o;
        }

        /// <summary>The cached scan, required to be an audio CD matching the browser's scanId.</summary>
        private RemoteScanData CurrentAudioCdScan(string scanId)
        {
            RemoteScanData scan;
            lock (_lock) { scan = _lastScan; }
            if (scan == null || !scan.IsAudioCd ||
                !string.Equals(scanId, scan.ScanId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "This setup is based on an old disc scan — scan the disc again and redo the setup.");
            }
            return scan;
        }

        private JObject DoSearchMovies(DashCommand cmd)
        {
            return CandidatesJson(_backend.SearchMovies(cmd.ArgString("title"), cmd.ArgString("year")));
        }

        private JObject DoSearchSeries(DashCommand cmd)
        {
            return CandidatesJson(_backend.SearchSeries(cmd.ArgString("name")));
        }

        private static JObject CandidatesJson(List<MetadataCandidate> found)
        {
            var arr = new JArray();
            if (found != null)
            {
                foreach (MetadataCandidate c in found)
                {
                    var jc = new JObject();
                    jc["id"] = c.ProviderId ?? "";
                    jc["title"] = c.Title ?? "";
                    jc["year"] = c.Year ?? "";
                    jc["detail"] = c.Detail ?? "";
                    arr.Add(jc);
                }
            }
            var o = new JObject();
            o["candidates"] = arr;
            return o;
        }

        private JObject DoEpisodes(DashCommand cmd)
        {
            string seriesId = cmd.ArgString("seriesId");
            int season = cmd.ArgInt("season", -1);
            if (string.IsNullOrEmpty(seriesId) || season < 0)
            {
                throw new InvalidOperationException("A confirmed series and a season number are required first.");
            }

            EpisodeOrder order;
            if (!Enum.TryParse(cmd.ArgString("order"), true, out order)) { order = EpisodeOrder.Aired; }

            List<EpisodeInfo> episodes = _backend.GetEpisodes(seriesId, season, order);
            string preferredName = "";
            try { preferredName = _backend.GetSeriesName(seriesId) ?? ""; }
            catch { /* optional nicety; the picked candidate's name still works */ }

            var arr = new JArray();
            if (episodes != null)
            {
                foreach (EpisodeInfo e in episodes)
                {
                    var je = new JObject();
                    je["season"] = e.SeasonNumber;
                    je["episode"] = e.EpisodeNumber;
                    je["name"] = e.Name ?? "";
                    arr.Add(je);
                }
            }
            var o = new JObject();
            o["episodes"] = arr;
            o["seriesName"] = preferredName;
            return o;
        }

        private JObject DoProcess(DashCommand cmd)
        {
            if (_backend.IsBusyRipping())
            {
                // The queue would technically accept it, but the scan it's based on can't be
                // re-verified while the drive is busy; keep the rule simple and safe.
                throw new InvalidOperationException(
                    "A rip is in progress on this machine — wait for it to finish, then scan again.");
            }

            RemoteScanData scan;
            lock (_lock) { scan = _lastScan; }
            if (scan == null || !string.Equals(cmd.ArgString("scanId"), scan.ScanId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "This setup is based on an old disc scan — scan the disc again and redo the setup " +
                    "(the disc may have been changed since).");
            }
            if (!cmd.ArgBool("confirmed"))
            {
                // The desktop rule, enforced remotely: nothing is processed on a guess.
                throw new InvalidOperationException("The match hasn't been confirmed — pick the correct " +
                    "match in the list and confirm it before processing.");
            }

            string label;
            if (string.Equals(cmd.ArgString("mediaType"), "music", StringComparison.OrdinalIgnoreCase))
            {
                label = ProcessMusic(cmd, scan);
            }
            else
            {
                if (scan.IsAudioCd)
                {
                    throw new InvalidOperationException(
                        "The scanned disc is an audio CD — it can only be processed through the music flow.");
                }
                MediaMetadata meta = BuildMetadata(cmd, scan);
                label = _backend.StartJob(meta, scan);
            }

            // One scan = one processed disc; a second Process must rescan. Prevents the browser's
            // Back button from queueing the same disc twice.
            lock (_lock) { _lastScan = null; _lastReleases = null; }

            var o = new JObject();
            o["queued"] = true;
            o["discLabel"] = label ?? "";
            return o;
        }

        /// <summary>
        /// Audio CD process: the browser refers to a CACHED release by index and lists the track
        /// numbers to rip — the release object itself never round-trips, so it can't be altered.
        /// </summary>
        private string ProcessMusic(DashCommand cmd, RemoteScanData scan)
        {
            if (!scan.IsAudioCd)
            {
                throw new InvalidOperationException("The scanned disc isn't an audio CD.");
            }

            List<MusicRelease> releases;
            lock (_lock) { releases = _lastReleases; }
            int idx = cmd.ArgInt("releaseIndex", -1);
            if (releases == null || idx < 0 || idx >= releases.Count)
            {
                throw new InvalidOperationException(
                    "No confirmed album match — look the CD up and pick the correct release first.");
            }
            MusicRelease release = releases[idx];

            // Selected tracks: at least one, and every number must exist on the release.
            var wanted = new HashSet<int>();
            JArray tracks = cmd.Args["tracks"] as JArray;
            if (tracks != null)
            {
                foreach (JToken t in tracks)
                {
                    try { wanted.Add((int)t); } catch { /* ignore non-numeric */ }
                }
            }
            if (wanted.Count == 0)
            {
                throw new InvalidOperationException("No tracks are selected — tick at least one track.");
            }
            var known = new HashSet<int>();
            foreach (AudioTrack t in release.Tracks) { known.Add(t.Number); }
            foreach (int n in wanted)
            {
                if (!known.Contains(n))
                {
                    throw new InvalidOperationException(
                        "Track " + n + " isn't on that release — redo the lookup and try again.");
                }
            }
            foreach (AudioTrack t in release.Tracks) { t.Selected = wanted.Contains(t.Number); }

            string formatId = cmd.ArgString("formatId");
            return _backend.StartMusicJob(release, scan.Toc, scan.DriveLetter, formatId,
                cmd.ArgString("conflict"));
        }

        /// <summary>Wraps a quick-control status text as a { message } result.</summary>
        private static JObject Message(string text)
        {
            var o = new JObject();
            o["message"] = text ?? "";
            return o;
        }

        /// <summary>
        /// Rebuilds the confirmed metadata package from the browser's constrained payload,
        /// validating every reference against the cached scan. Mirrors what MetadataEntryForm
        /// produces so downstream (rip → encode → placement) can't tell the flows apart.
        /// </summary>
        private static MediaMetadata BuildMetadata(DashCommand cmd, RemoteScanData scan)
        {
            var meta = new MediaMetadata();

            DiscType discType;
            if (!Enum.TryParse(cmd.ArgString("discType"), true, out discType)) { discType = DiscType.Dvd; }
            meta.DiscType = discType;
            meta.PresetName = cmd.ArgString("presetName");

            string mediaType = cmd.ArgString("mediaType");
            if (string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase))
            {
                meta.MediaType = MediaType.Movie;
                meta.MovieTitle = cmd.ArgString("movieTitle").Trim();
                meta.Year = cmd.ArgString("movieYear").Trim();
                meta.ImdbId = cmd.ArgString("imdbId").Trim();
                if (meta.MovieTitle.Length == 0)
                {
                    throw new InvalidOperationException("The movie needs a title before it can be processed.");
                }
                if (meta.ImdbId.Length == 0)
                {
                    throw new InvalidOperationException("No confirmed movie match — search and confirm one first.");
                }
                meta.MatchConfirmed = true;
                return meta;
            }

            // Multi-movie (double-feature) disc: each included title is its own confirmed film,
            // mirroring the desktop's per-title movie mappings ([[multi-movie-discs]] case).
            if (string.Equals(mediaType, "multimovie", StringComparison.OrdinalIgnoreCase))
            {
                meta.MediaType = MediaType.Movie;

                var knownTitles = new HashSet<int>();
                foreach (DiscTitle t in scan.Titles) { knownTitles.Add(t.Index); }

                JArray movieMaps = cmd.Args["mappings"] as JArray;
                if (movieMaps == null || movieMaps.Count == 0)
                {
                    throw new InvalidOperationException("No titles were assigned to movies — nothing to process.");
                }

                int films = 0;
                foreach (JToken tok in movieMaps)
                {
                    JObject jm = tok as JObject;
                    if (jm == null) { continue; }
                    int titleIndex = jm.Value<int?>("titleIndex") ?? -1;
                    if (!knownTitles.Contains(titleIndex))
                    {
                        throw new InvalidOperationException(
                            "Title " + titleIndex + " isn't on the scanned disc — scan again and redo the setup.");
                    }

                    var mm = new TitleMapping
                    {
                        TitleIndex = titleIndex,
                        Include = jm.Value<bool?>("include") ?? false,
                        MovieTitle = (jm.Value<string>("movieTitle") ?? "").Trim(),
                        MovieYear = (jm.Value<string>("movieYear") ?? "").Trim(),
                        MovieImdbId = (jm.Value<string>("imdbId") ?? "").Trim()
                    };

                    if (mm.Include)
                    {
                        if (mm.MovieTitle.Length == 0 || mm.MovieImdbId.Length == 0)
                        {
                            throw new InvalidOperationException(
                                "Title " + titleIndex + " is included but has no confirmed movie — " +
                                "search and confirm one for it, or exclude it.");
                        }
                        mm.Kind = TitleKind.Movie;
                        films++;
                    }
                    else
                    {
                        mm.Kind = TitleKind.Ignore;
                    }
                    meta.TitleMappings.Add(mm);
                }

                if (films == 0)
                {
                    throw new InvalidOperationException("Every title is excluded — nothing to process.");
                }
                meta.MatchConfirmed = true;
                return meta;
            }

            if (!string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Unknown media type '" + mediaType + "' — this machine may be running an older version.");
            }

            meta.MediaType = MediaType.TvShow;
            meta.ShowName = cmd.ArgString("showName").Trim();
            meta.TvdbSeriesId = cmd.ArgString("seriesId").Trim();
            meta.SeasonNumber = cmd.ArgInt("season", -1);
            meta.DiscNumber = cmd.ArgInt("discNumber", 1);
            if (meta.ShowName.Length == 0 || meta.TvdbSeriesId.Length == 0)
            {
                throw new InvalidOperationException("No confirmed series match — search and confirm one first.");
            }
            if (meta.SeasonNumber < 0)
            {
                throw new InvalidOperationException("A season number is required.");
            }

            // Per-title mappings: each entry must reference a real scanned title, and episode
            // entries must be sane. Unmapped titles are recorded as excluded so the planner
            // skips them explicitly rather than by accident.
            var known = new HashSet<int>();
            foreach (DiscTitle t in scan.Titles) { known.Add(t.Index); }

            JArray mappings = cmd.Args["mappings"] as JArray;
            if (mappings == null || mappings.Count == 0)
            {
                throw new InvalidOperationException("No titles were mapped to episodes — nothing to process.");
            }

            int included = 0;
            foreach (JToken tok in mappings)
            {
                JObject jm = tok as JObject;
                if (jm == null) { continue; }
                int titleIndex = jm.Value<int?>("titleIndex") ?? -1;
                if (!known.Contains(titleIndex))
                {
                    throw new InvalidOperationException(
                        "Title " + titleIndex + " isn't on the scanned disc — scan again and redo the setup.");
                }

                var m = new TitleMapping
                {
                    TitleIndex = titleIndex,
                    Include = jm.Value<bool?>("include") ?? false,
                    SeasonNumber = meta.SeasonNumber,
                    Kind = TitleKind.Episode
                };

                JArray eps = jm["episodes"] as JArray;
                if (eps != null)
                {
                    foreach (JToken etok in eps)
                    {
                        JObject je = etok as JObject;
                        if (je == null) { continue; }
                        int epNum = je.Value<int?>("episode") ?? 0;
                        if (epNum <= 0) { continue; }
                        m.Episodes.Add(new EpisodeInfo
                        {
                            SeasonNumber = je.Value<int?>("season") ?? meta.SeasonNumber,
                            EpisodeNumber = epNum,
                            Name = je.Value<string>("name") ?? ""
                        });
                    }
                }

                if (m.Include && m.Episodes.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Title " + titleIndex + " is included but has no episode assigned.");
                }
                if (!m.Include) { m.Kind = TitleKind.Ignore; }
                else { included++; }

                meta.TitleMappings.Add(m);
            }

            if (included == 0)
            {
                throw new InvalidOperationException("Every title is excluded — nothing to process.");
            }

            meta.MatchConfirmed = true;
            return meta;
        }

        public void Dispose()
        {
            _running = false;
            _wake.Set();
            try { if (_worker != null) { _worker.Join(1000); } } catch { /* ignore */ }
        }
    }
}
