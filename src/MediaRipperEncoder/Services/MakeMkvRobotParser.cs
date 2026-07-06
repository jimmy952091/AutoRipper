using System;
using System.Collections.Generic;
using System.Globalization;
using MediaRipperEncoder.Models;

namespace MediaRipperEncoder.Services
{
    /// <summary>
    /// Parses MakeMKV's "-r" (robot) output. Every line looks like TYPE:field0,field1,...
    /// where string fields are double-quoted and may contain commas; backslash escapes the
    /// next character. This class is pure (no I/O) so it can be unit-tested against captured
    /// sample output — which matters because this format is the brittle seam between our app
    /// and an external tool we don't control.
    ///
    /// Line types we use:
    ///   DRV : drive slot           -> map Windows drive letter to MakeMKV disc index
    ///   CINFO/TINFO : disc/title info -> titles list for the scan
    ///   PRGV/PRGC/PRGT : progress   -> live rip progress
    ///   MSG : status/error message  -> logging + error detection
    /// </summary>
    public static class MakeMkvRobotParser
    {
        // MakeMKV item attribute IDs (from its apdefs.h) that we read out of TINFO/CINFO.
        private const int AttrName = 2;
        private const int AttrChapterCount = 8;
        private const int AttrDuration = 9;
        private const int AttrDiskSizeBytes = 11;
        private const int AttrOutputFileName = 27;

        /// <summary>Splits a robot line into its TYPE and its list of fields (quotes/escapes handled).</summary>
        public static bool TryParseLine(string line, out string type, out string[] fields)
        {
            type = null;
            fields = null;
            if (string.IsNullOrEmpty(line)) { return false; }

            int colon = line.IndexOf(':');
            if (colon <= 0) { return false; }

            type = line.Substring(0, colon);
            fields = ParseFields(line.Substring(colon + 1));
            return true;
        }

        /// <summary>Comma-splits a robot line body, respecting double-quoted fields and \ escapes.</summary>
        public static string[] ParseFields(string body)
        {
            var result = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (inQuotes)
                {
                    if (c == '\\' && i + 1 < body.Length)
                    {
                        sb.Append(body[i + 1]); // literal next char (handles \" and \\)
                        i++;
                    }
                    else if (c == '"')
                    {
                        inQuotes = false;
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    if (c == '"') { inQuotes = true; }
                    else if (c == ',') { result.Add(sb.ToString()); sb.Length = 0; }
                    else { sb.Append(c); }
                }
            }
            result.Add(sb.ToString());
            return result.ToArray();
        }

        // --- DRV: drives ---

        /// <summary>Parses all present drives from robot output lines.</summary>
        public static List<MakeMkvDrive> ParseDrives(IEnumerable<string> lines)
        {
            var drives = new List<MakeMkvDrive>();
            foreach (string line in lines)
            {
                string type;
                string[] f;
                if (!TryParseLine(line, out type, out f)) { continue; }
                if (type != "DRV" || f.Length < 7) { continue; }

                var drive = new MakeMkvDrive
                {
                    Index = GetInt(f, 0, -1),
                    Name = f[4],
                    DiscName = f[5],
                    DeviceName = f[6]
                };
                if (drive.IsPresent) { drives.Add(drive); }
            }
            return drives;
        }

        // --- TINFO/CINFO: disc scan ---

        /// <summary>
        /// Builds the title list from a disc-info scan's robot output. Also returns the disc
        /// volume name if one was reported (CINFO Name attribute).
        /// </summary>
        public static List<DiscTitle> ParseTitles(IEnumerable<string> lines, out string discName)
        {
            discName = "";
            // Keep titles keyed by index so TINFO lines (which arrive attribute by attribute)
            // accumulate onto the right title regardless of order.
            var byIndex = new Dictionary<int, DiscTitle>();

            foreach (string line in lines)
            {
                string type;
                string[] f;
                if (!TryParseLine(line, out type, out f)) { continue; }

                if (type == "CINFO" && f.Length >= 3)
                {
                    int attr = GetInt(f, 0, -1);
                    if (attr == AttrName && string.IsNullOrEmpty(discName))
                    {
                        discName = f[2];
                    }
                }
                else if (type == "TINFO" && f.Length >= 4)
                {
                    int titleIndex = GetInt(f, 0, -1);
                    int attr = GetInt(f, 1, -1);
                    string value = f[3];
                    if (titleIndex < 0) { continue; }

                    DiscTitle title;
                    if (!byIndex.TryGetValue(titleIndex, out title))
                    {
                        title = new DiscTitle { Index = titleIndex };
                        byIndex[titleIndex] = title;
                    }

                    switch (attr)
                    {
                        case AttrName: title.Name = value; break;
                        case AttrDuration: title.Duration = value; break;
                        case AttrChapterCount: title.ChapterCount = ParseIntSafe(value); break;
                        case AttrDiskSizeBytes: title.SizeBytes = ParseLongSafe(value); break;
                        case AttrOutputFileName: title.OutputFileName = value; break;
                    }
                }
            }

            var titles = new List<DiscTitle>(byIndex.Values);
            titles.Sort((a, b) => a.Index.CompareTo(b.Index));
            return titles;
        }

        // --- PRGV/PRGC: progress ---

        /// <summary>
        /// Reads a progress line. PRGV:current,total,max -> overall percent = total/max.
        /// PRGC:code,id,"name" -> the name of the current operation. Returns false for
        /// non-progress lines.
        /// </summary>
        public static bool TryParseProgress(string line, out int overallPercent, out string operationName)
        {
            overallPercent = -1;
            operationName = null;

            string type;
            string[] f;
            if (!TryParseLine(line, out type, out f)) { return false; }

            if (type == "PRGV" && f.Length >= 3)
            {
                long total = ParseLongSafe(f[1]);
                long max = ParseLongSafe(f[2]);
                if (max > 0)
                {
                    overallPercent = (int)Math.Round(total * 100.0 / max);
                    if (overallPercent < 0) { overallPercent = 0; }
                    if (overallPercent > 100) { overallPercent = 100; }
                }
                return true;
            }

            if ((type == "PRGC" || type == "PRGT") && f.Length >= 3)
            {
                operationName = f[2];
                return true;
            }

            return false;
        }

        // --- MSG: messages ---

        /// <summary>Reads a MSG line's numeric code and human text. False for non-MSG lines.</summary>
        public static bool TryParseMessage(string line, out int code, out string text)
        {
            code = -1;
            text = null;

            string type;
            string[] f;
            if (!TryParseLine(line, out type, out f)) { return false; }
            if (type != "MSG" || f.Length < 4) { return false; }

            code = GetInt(f, 0, -1);
            text = f[3];
            return true;
        }

        // --- helpers ---

        private static int GetInt(string[] fields, int index, int fallback)
        {
            if (fields == null || index < 0 || index >= fields.Length) { return fallback; }
            return ParseIntSafe(fields[index], fallback);
        }

        private static int ParseIntSafe(string s, int fallback = 0)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static long ParseLongSafe(string s, long fallback = 0)
        {
            long v;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }
    }
}
