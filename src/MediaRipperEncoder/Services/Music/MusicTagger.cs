using System;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services.Music
{
    /// <summary>
    /// Writes the tags INSIDE each ripped audio file (ID3/Vorbis comments via TagLib#), plus the
    /// embedded front-cover image. Media servers read music metadata from embedded tags — unlike
    /// video, where the folder/filename carries the meaning — so this is what makes the album
    /// show up correctly in Plex/Jellyfin.
    ///
    /// We tag deliberately ourselves (not via fre:ac's own lookup) so the file contents always
    /// match the confirmed on-screen track list, original punctuation included (tags keep
    /// "AC/DC" even though the folder had to become "AC-DC").
    ///
    /// Never throws: a tagging failure is logged and the audio file is left playable as-is.
    /// </summary>
    public static class MusicTagger
    {
        public static bool Tag(string filePath, MusicRelease release, AudioTrack track, byte[] coverArt)
        {
            try
            {
                using (TagLib.File file = TagLib.File.Create(filePath))
                {
                    file.Tag.Title = track.Title;
                    file.Tag.Performers = new[] { release.Artist };
                    file.Tag.AlbumArtists = new[] { release.Artist };
                    file.Tag.Album = release.Album;
                    file.Tag.Track = (uint)track.Number;
                    file.Tag.TrackCount = (uint)release.Tracks.Count;
                    file.Tag.Disc = (uint)release.DiscNumber;
                    file.Tag.DiscCount = (uint)release.DiscCount;

                    uint year;
                    if (uint.TryParse(release.Year, out year)) { file.Tag.Year = year; }

                    if (coverArt != null && coverArt.Length > 0)
                    {
                        file.Tag.Pictures = new TagLib.IPicture[]
                        {
                            new TagLib.Picture(new TagLib.ByteVector(coverArt))
                            {
                                Type = TagLib.PictureType.FrontCover,
                                Description = "Front cover"
                            }
                        };
                    }

                    file.Save();
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Couldn't tag " + filePath + " (file left untagged but playable).", ex);
                return false;
            }
        }
    }
}
