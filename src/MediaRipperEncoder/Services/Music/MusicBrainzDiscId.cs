using System;
using System.Security.Cryptography;
using System.Text;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services.Music
{
    /// <summary>
    /// Computes the MusicBrainz Disc ID from a CD's table of contents. The ID is how a physical
    /// disc is identified precisely (down to the pressing) without any typing.
    ///
    /// Algorithm (per musicbrainz.org/doc/Disc_ID_Calculation): SHA-1 over the ASCII string of
    ///   first track  as %02X
    ///   last track   as %02X
    ///   100 offsets  as %08X each — lead-out at index 0, tracks 1..99, unused entries 0
    /// then base64 with the URL-unfriendly characters swapped: '+'→'.', '/'→'_', '='→'-'.
    /// Always 28 characters.
    /// </summary>
    public static class MusicBrainzDiscId
    {
        public static string Compute(AudioCdToc toc)
        {
            if (toc == null) { throw new ArgumentNullException("toc"); }
            return Compute(toc.FirstTrack, toc.LastTrack, toc.LeadOutOffset, toc.TrackOffsets.ToArray());
        }

        public static string Compute(int firstTrack, int lastTrack, int leadOutOffset, int[] trackOffsets)
        {
            var sb = new StringBuilder();
            sb.Append(firstTrack.ToString("X2"));
            sb.Append(lastTrack.ToString("X2"));

            // Index 0 is the lead-out; 1..99 are the track start offsets; the rest stay zero.
            var offsets = new int[100];
            offsets[0] = leadOutOffset;
            for (int i = 0; i < trackOffsets.Length && i < 99; i++)
            {
                offsets[i + 1] = trackOffsets[i];
            }
            for (int i = 0; i < 100; i++)
            {
                sb.Append(offsets[i].ToString("X8"));
            }

            byte[] hash;
            using (var sha1 = SHA1.Create())
            {
                hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(sb.ToString()));
            }

            return Convert.ToBase64String(hash)
                .Replace('+', '.')
                .Replace('/', '_')
                .Replace('=', '-');
        }
    }
}
