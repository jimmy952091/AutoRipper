using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace MediaRipperEncoder.Services
{
    /// <summary>Reads metadata out of a HandBrake preset .json file.</summary>
    public static class PresetInfo
    {
        /// <summary>
        /// Returns the preset's name (PresetList[0].PresetName), which HandBrakeCLI needs
        /// via -Z to actually select the imported preset. Returns null if it can't be read.
        /// </summary>
        public static string GetPresetName(string presetPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(presetPath) || !File.Exists(presetPath))
                {
                    return null;
                }

                JObject root = JObject.Parse(File.ReadAllText(presetPath));
                JToken list = root["PresetList"];
                if (list != null && list.Type == JTokenType.Array && list.HasValues)
                {
                    JToken first = list.First;
                    if (first != null && first["PresetName"] != null)
                    {
                        return first["PresetName"].ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Couldn't read preset name from " + presetPath, ex);
            }
            return null;
        }

        /// <summary>
        /// Returns the output file extension implied by the preset's container (e.g. "mp4" or
        /// "mkv"), without the leading dot. Falls back to "mp4" if it can't be read — mp4 is the
        /// project's default container. Used only for previewing the target filename; the actual
        /// pipeline takes the extension from the encoded file itself.
        /// </summary>
        public static string GetContainerExtension(string presetPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(presetPath) && File.Exists(presetPath))
                {
                    JObject root = JObject.Parse(File.ReadAllText(presetPath));
                    JToken list = root["PresetList"];
                    if (list != null && list.Type == JTokenType.Array && list.HasValues)
                    {
                        JToken fileFormat = list.First != null ? list.First["FileFormat"] : null;
                        if (fileFormat != null)
                        {
                            // HandBrake stores containers as "av_mp4" / "av_mkv".
                            string format = fileFormat.ToString();
                            if (format.IndexOf("mkv", StringComparison.OrdinalIgnoreCase) >= 0) { return "mkv"; }
                            if (format.IndexOf("mp4", StringComparison.OrdinalIgnoreCase) >= 0) { return "mp4"; }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Couldn't read container from " + presetPath, ex);
            }
            return "mp4";
        }
    }
}
