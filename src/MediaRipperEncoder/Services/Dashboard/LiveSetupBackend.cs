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
using MediaRipperEncoder.Services.Metadata;

namespace MediaRipperEncoder.Services.Dashboard
{
    /// <summary>
    /// The real IRemoteSetupBackend: scans through the same MakeMkvService, looks up through the
    /// same OMDb/TheTVDB provider (with THIS machine's keys — keys never leave the machine), and
    /// queues through the same PipelineCoordinator.StartDiscJob as the desktop flow. A disc set up
    /// from the dashboard is indistinguishable downstream from one set up at the keyboard.
    /// </summary>
    public class LiveSetupBackend : IRemoteSetupBackend
    {
        private readonly AppSettings _settings;
        private readonly PipelineCoordinator _pipeline;
        private readonly Func<bool> _busyCheck;

        /// <summary>Fired after a remote Process queues a rip (UI shows it like a local one).</summary>
        public event Action<RipJob> JobQueued;

        public LiveSetupBackend(AppSettings settings, PipelineCoordinator pipeline, Func<bool> busyCheck)
        {
            _settings = settings;
            _pipeline = pipeline;
            _busyCheck = busyCheck;
        }

        public bool IsBusyRipping()
        {
            try { return _busyCheck != null && _busyCheck(); }
            catch { return false; }
        }

        public RemoteScanData Scan()
        {
            // Same source resolution as the desktop Scan button: the advanced network/mapped
            // source when configured, otherwise the last drive the user picked at the machine.
            bool network = _settings.NetworkRipEnabled && !string.IsNullOrWhiteSpace(_settings.NetworkRipSource);
            var data = new RemoteScanData { ManualDiscChange = network };

            DiscScanResult scan;
            if (network)
            {
                string resolved = MakeMkvService.ResolveDiscFolder(
                    _settings.NetworkRipSource, _settings.NetworkRipSearchSubfolders);
                data.SourceSpec = MakeMkvService.BuildFileSourceSpec(resolved);
                Logger.Info("Remote setup: network scan of '" + data.SourceSpec + "'.");
                scan = _pipeline.MakeMkv.ScanSource(data.SourceSpec, 0, CancellationToken.None);
            }
            else
            {
                string letter = (_settings.LastUsedDrive ?? "").Trim();
                if (letter.Length == 0)
                {
                    throw new InvalidOperationException(
                        "No drive has been chosen on that machine yet — select the drive once in " +
                        "AutoRipper there, and remote setup will use it from then on.");
                }
                data.DriveLetter = letter;

                // Audio CD? Same quick TOC probe as the desktop Scan button. If it is one, hand
                // the wizard the music path: MusicBrainz lookup + track checkboxes, remotely.
                AudioCdToc toc = null;
                try { toc = Services.Music.CdTocReader.Read(letter); }
                catch { /* no readable TOC — normal for DVDs/Blu-rays; carry on */ }
                if (toc != null && toc.TrackCount > 0)
                {
                    data.IsAudioCd = true;
                    data.Toc = toc;
                    data.DiscName = "Audio CD — " + toc.TrackCount + " tracks";
                    data.DefaultFormatId = string.IsNullOrWhiteSpace(_settings.MusicFormatId)
                        ? "flac" : _settings.MusicFormatId;
                    foreach (Services.Music.MusicFormat f in Services.Music.MusicFormat.All())
                    {
                        data.Formats.Add(new MusicFormatChoice { Id = f.FormatId, Label = f.DisplayName });
                    }
                    Logger.Info("Remote setup: audio CD in " + letter + " (" + toc.TrackCount + " tracks).");
                    return data;
                }

                Logger.Info("Remote setup: scanning drive " + letter + ".");
                int discIndex = _pipeline.MakeMkv.FindDiscIndexForLetter(letter);
                if (discIndex < 0)
                {
                    throw new InvalidOperationException(
                        "Couldn't match drive " + letter + " to a disc — is a disc inserted?");
                }
                scan = _pipeline.MakeMkv.ScanDisc(discIndex, CancellationToken.None);
                scan.DiscIndex = discIndex;
            }

            if (!scan.Success)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(scan.Error) ? "The disc scan failed." : scan.Error);
            }

            data.DiscName = scan.DiscName ?? "";
            data.Titles = scan.Titles ?? new List<DiscTitle>();
            data.DiscIndex = scan.DiscIndex;
            data.Presets = ListPresets();
            data.ProviderIsLive = HasMetadataKeys();
            return data;
        }

        public List<MetadataCandidate> SearchMovies(string title, string year)
        {
            return CreateProvider().SearchMoviesAsync(title, year).GetAwaiter().GetResult();
        }

        public List<MetadataCandidate> SearchSeries(string name)
        {
            return CreateProvider().SearchSeriesAsync(name).GetAwaiter().GetResult();
        }

        public List<EpisodeInfo> GetEpisodes(string seriesId, int season, EpisodeOrder order)
        {
            return CreateProvider().GetEpisodesAsync(seriesId, season, order).GetAwaiter().GetResult();
        }

        public string GetSeriesName(string seriesId)
        {
            return CreateProvider().GetSeriesNameAsync(seriesId).GetAwaiter().GetResult();
        }

        public List<MusicRelease> LookupMusic(AudioCdToc toc)
        {
            // The desktop chain: exact Disc ID first, fuzzy TOC when the pressing isn't known.
            var client = new Services.Music.MusicBrainzClient();
            string discId = Services.Music.MusicBrainzDiscId.Compute(toc);
            List<MusicRelease> found = client.LookupByDiscIdAsync(discId).GetAwaiter().GetResult();
            if (found == null || found.Count == 0)
            {
                found = client.LookupByTocAsync(toc).GetAwaiter().GetResult();
            }
            return found ?? new List<MusicRelease>();
        }

        public List<MusicRelease> SearchMusic(string artist, string album)
        {
            return new Services.Music.MusicBrainzClient()
                .SearchReleasesAsync(artist, album).GetAwaiter().GetResult() ?? new List<MusicRelease>();
        }

        public string StartMusicJob(MusicRelease release, AudioCdToc toc, string driveLetter, string formatId,
            string conflictPolicy)
        {
            // Conflict policy decided in the browser up front — nobody is at this machine to
            // answer a prompt when the placed track collides with an existing file.
            ConflictResolution? policy = null;
            if (string.Equals(conflictPolicy, "overwrite", StringComparison.OrdinalIgnoreCase)) { policy = ConflictResolution.Overwrite; }
            else if (string.Equals(conflictPolicy, "skip", StringComparison.OrdinalIgnoreCase)) { policy = ConflictResolution.Skip; }
            else if (!string.IsNullOrEmpty(conflictPolicy)) { policy = ConflictResolution.KeepBoth; }

            RipJob job = _pipeline.StartMusicJob(release, toc, driveLetter,
                string.IsNullOrWhiteSpace(formatId) ? _settings.MusicFormatId : formatId, policy);
            var h = JobQueued;
            if (h != null) { h(job); }
            Logger.Info("Remote setup queued '" + job.DiscLabel + "' from the dashboard.");
            return job.DiscLabel;
        }

        // ---- quick controls (mirror the desktop buttons) ----

        /// <summary>Set by the main window: runs its "Retry failed title(s)" logic on the UI thread.</summary>
        public Func<string> RetryFailedHandler;

        public string StopRip()
        {
            _pipeline.CancelCurrentRip();
            return "Stop requested — the unfinished title will be marked failed so it can be retried " +
                   "after cleaning the disc. Titles already ripped are kept.";
        }

        public string RetryFailed()
        {
            Func<string> handler = RetryFailedHandler;
            if (handler == null)
            {
                throw new InvalidOperationException("Retry isn't available on that machine right now.");
            }
            return handler();
        }

        public string Eject()
        {
            string letter = (_settings.LastUsedDrive ?? "").Trim();
            if (letter.Length == 0)
            {
                throw new InvalidOperationException("No drive has been chosen on that machine yet.");
            }
            EjectResult result = EjectService.Eject(letter);
            if (!result.Success)
            {
                // Includes the OS-aware Windows 7 run-as-admin hint where it applies.
                throw new InvalidOperationException(result.Message);
            }
            return "Ejected drive " + letter + ". (" + result.Message + ")";
        }

        public string StartJob(MediaMetadata meta, RemoteScanData scan)
        {
            RipJob job = _pipeline.StartDiscJob(meta, scan.Titles, scan.DiscIndex,
                scan.ManualDiscChange ? "" : scan.DriveLetter,
                string.IsNullOrEmpty(scan.SourceSpec) ? null : scan.SourceSpec,
                scan.ManualDiscChange);

            var h = JobQueued;
            if (h != null) { h(job); }
            Logger.Info("Remote setup queued '" + job.DiscLabel + "' from the dashboard.");
            return job.DiscLabel;
        }

        // ---- helpers (mirror MainForm's provider selection) ----

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

        /// <summary>The four configured presets, by internal name, with friendly labels.</summary>
        private List<PresetChoice> ListPresets()
        {
            var list = new List<PresetChoice>();
            AddPreset(list, _settings.HandBrakePresetPath, "General");
            AddPreset(list, _settings.HandBrakeAnimationPresetPath, "Animation");
            AddPreset(list, _settings.HandBrakeUhdPresetPath, "UHD");
            AddPreset(list, _settings.HandBrakeUhdAnimationPresetPath, "UHD Animation");
            return list;
        }

        private static void AddPreset(List<PresetChoice> list, string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path)) { return; }
            string name = PresetInfo.GetPresetName(path);
            if (string.IsNullOrEmpty(name)) { return; }
            list.Add(new PresetChoice { Name = name, Label = label + "  (" + name + ")" });
        }
    }
}
