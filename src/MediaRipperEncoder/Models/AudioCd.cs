using System.Collections.Generic;

namespace MediaRipperEncoder.Models
{
    /// <summary>
    /// The table of contents of an audio CD, in MusicBrainz's terms: track numbers plus each
    /// track's start offset in CD frames (75ths of a second, INCLUDING the standard 150-frame
    /// / 2-second lead-in). This is all that's needed to compute the MusicBrainz Disc ID.
    /// </summary>
    public class AudioCdToc
    {
        public int FirstTrack { get; set; }
        public int LastTrack { get; set; }

        /// <summary>Offset of the lead-out (end of the last track), in frames.</summary>
        public int LeadOutOffset { get; set; }

        /// <summary>Start offset per audio track, in track order, in frames.</summary>
        public List<int> TrackOffsets { get; set; }

        public AudioCdToc()
        {
            TrackOffsets = new List<int>();
        }

        public int TrackCount
        {
            get { return TrackOffsets.Count; }
        }

        /// <summary>Length of one track in whole seconds, derived from the offsets.</summary>
        public int TrackLengthSeconds(int trackIndex)
        {
            if (trackIndex < 0 || trackIndex >= TrackOffsets.Count) { return 0; }
            int end = trackIndex + 1 < TrackOffsets.Count ? TrackOffsets[trackIndex + 1] : LeadOutOffset;
            return (end - TrackOffsets[trackIndex]) / 75;
        }
    }

    /// <summary>One track of a matched release, as shown in the checkbox list.</summary>
    public class AudioTrack
    {
        public int Number { get; set; }
        public string Title { get; set; }
        public int LengthSeconds { get; set; }

        /// <summary>Checkbox state — all tracks start checked; only checked tracks are ripped.</summary>
        public bool Selected { get; set; }

        public AudioTrack()
        {
            Title = "";
            Selected = true;
        }

        public string LengthText
        {
            get { return (LengthSeconds / 60) + ":" + (LengthSeconds % 60).ToString("00"); }
        }
    }

    /// <summary>
    /// One candidate release from MusicBrainz (an album as released — different pressings /
    /// reissues are separate releases with possibly different track lists, which is why the
    /// user confirms the right one instead of us auto-matching).
    /// </summary>
    public class MusicRelease
    {
        /// <summary>MusicBrainz release MBID — also the key for Cover Art Archive lookups.</summary>
        public string ReleaseId { get; set; }

        public string Artist { get; set; }
        public string Album { get; set; }
        public string Year { get; set; }

        /// <summary>Country/format/label detail line for the confirm dialog.</summary>
        public string Detail { get; set; }

        /// <summary>Total discs in this release (drives the 1-01 multi-disc naming).</summary>
        public int DiscCount { get; set; }

        /// <summary>Which disc of the release THIS CD is (1-based).</summary>
        public int DiscNumber { get; set; }

        public List<AudioTrack> Tracks { get; set; }

        public MusicRelease()
        {
            ReleaseId = "";
            Artist = "";
            Album = "";
            Year = "";
            Detail = "";
            DiscCount = 1;
            DiscNumber = 1;
            Tracks = new List<AudioTrack>();
        }
    }
}
