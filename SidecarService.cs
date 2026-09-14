using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace QDTool
{
    internal static class SidecarService
    {
        public static string GetSidecarPath(string mainPath)
        {
            string extension = Path.GetExtension(mainPath).Equals(".mzt", StringComparison.OrdinalIgnoreCase)
                ? ".mti"
                : ".mfi";
            return Path.ChangeExtension(mainPath, extension);
        }

        public static string? LoadForMzf(string mzfPath, TapeRecord record)
        {
            string path = GetSidecarPath(mzfPath);
            if (!File.Exists(path))
            {
                return null;
            }

            if (TryParseProfile(File.ReadAllLines(path), out TapeProfile profile))
            {
                record.Profile = profile;
                record.MetadataOrigin = MetadataOrigin.LoadedFromMfi;
            }

            return path;
        }

        public static string? LoadForMzt(string mztPath, IReadOnlyList<TapeRecord> records)
        {
            string path = GetSidecarPath(mztPath);
            if (!File.Exists(path))
            {
                return null;
            }

            Dictionary<int, List<string>> sections = ParseMtiSections(File.ReadAllLines(path));
            for (int index = 0; index < records.Count; index++)
            {
                if (sections.TryGetValue(index + 1, out List<string>? lines) &&
                    TryParseProfile(lines, out TapeProfile profile))
                {
                    records[index].Profile = profile;
                    records[index].MetadataOrigin = MetadataOrigin.LoadedFromMti;
                }
            }

            return path;
        }

        public static byte[] SerializeMfi(TapeRecord record) =>
            Encoding.ASCII.GetBytes(SerializeProfile(record.Profile));

        public static byte[] SerializeMti(IReadOnlyList<TapeRecord> records)
        {
            var text = new StringBuilder();
            for (int index = 0; index < records.Count; index++)
            {
                text.Append("RECORD=").Append(index + 1).Append('\n');
                text.Append(SerializeProfile(records[index].Profile));
                if (index + 1 < records.Count)
                {
                    text.Append('\n');
                }
            }
            return Encoding.ASCII.GetBytes(text.ToString());
        }

        internal static bool TryParseProfile(IEnumerable<string> sourceLines, out TapeProfile profile)
        {
            Dictionary<string, string> values = sourceLines
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('='))
                .Select(line => line.Split('=', 2))
                .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);

            values.TryGetValue("TYPE", out string? type);
            values.TryGetValue("SPEED", out string? speed);
            profile = (type?.ToUpperInvariant(), speed?.ToUpperInvariant()) switch
            {
                ("NORMAL", "1:1") => TapeProfile.Normal1_1,
                ("NORMAL", "1:2") => TapeProfile.Normal1_2,
                ("NORMAL", "1:3") => TapeProfile.Normal1_3,
                ("NORMAL", "1:4") => TapeProfile.Normal1_4,
                ("MZ700", "1:1") => TapeProfile.Mz700_1_1,
                ("MZ700", "1:3") => TapeProfile.Mz700_1_3,
                ("IC", "1:2") => TapeProfile.Ic1_2,
                ("IC", "1:3") => TapeProfile.Ic1_3,
                ("IC", "1:4") => TapeProfile.Ic1_4,
                ("TC", "1:2") => TapeProfile.Tc1_2,
                ("TC", "1:3") => TapeProfile.Tc1_3,
                ("TC", "1:4") => TapeProfile.Tc1_4,
                ("UL", null or "") => TapeProfile.Ultra,
                ("UL_MZ800", null or "") => TapeProfile.UltraMz800,
                ("UL_MZ700", null or "") => TapeProfile.UltraMz700,
                _ => (TapeProfile)(-1)
            };
            return Enum.IsDefined(profile);
        }

        private static Dictionary<int, List<string>> ParseMtiSections(IEnumerable<string> lines)
        {
            var result = new Dictionary<int, List<string>>();
            List<string>? current = null;
            foreach (string sourceLine in lines)
            {
                string line = sourceLine.Trim();
                if (line.StartsWith("RECORD=", StringComparison.OrdinalIgnoreCase))
                {
                    current = int.TryParse(line[7..].Trim(), out int number) && number > 0
                        ? result[number] = new List<string>()
                        : null;
                }
                else
                {
                    current?.Add(line);
                }
            }
            return result;
        }

        private static string SerializeProfile(TapeProfile profile) => profile switch
        {
            TapeProfile.Normal1_1 => "TYPE=NORMAL\nSPEED=1:1\n",
            TapeProfile.Normal1_2 => "TYPE=NORMAL\nSPEED=1:2\n",
            TapeProfile.Normal1_3 => "TYPE=NORMAL\nSPEED=1:3\n",
            TapeProfile.Normal1_4 => "TYPE=NORMAL\nSPEED=1:4\n",
            TapeProfile.Mz700_1_1 => "TYPE=MZ700\nSPEED=1:1\n",
            TapeProfile.Mz700_1_3 => "TYPE=MZ700\nSPEED=1:3\n",
            TapeProfile.Ic1_2 => "TYPE=IC\nSPEED=1:2\n",
            TapeProfile.Ic1_3 => "TYPE=IC\nSPEED=1:3\n",
            TapeProfile.Ic1_4 => "TYPE=IC\nSPEED=1:4\n",
            TapeProfile.Tc1_2 => "TYPE=TC\nSPEED=1:2\n",
            TapeProfile.Tc1_3 => "TYPE=TC\nSPEED=1:3\n",
            TapeProfile.Tc1_4 => "TYPE=TC\nSPEED=1:4\n",
            TapeProfile.Ultra => "TYPE=UL\n",
            TapeProfile.UltraMz800 => "TYPE=UL_MZ800\n",
            TapeProfile.UltraMz700 => "TYPE=UL_MZ700\n",
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
    }
}
