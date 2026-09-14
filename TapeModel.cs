using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QDTool
{
    internal enum TapeProfile
    {
        Normal1_1,
        Normal1_2,
        Normal1_3,
        Normal1_4,
        Mz700_1_1,
        Mz700_1_3,
        Ic1_2,
        Ic1_3,
        Ic1_4,
        Tc1_2,
        Tc1_3,
        Tc1_4,
        Ultra,
        UltraMz800,
        UltraMz700
    }

    internal enum MetadataOrigin
    {
        Implicit,
        LoadedFromMfi,
        LoadedFromMti,
        CreatedOrModifiedInAdvanced
    }

    internal enum TapeDocumentFormat
    {
        None,
        Mzf,
        Mzt,
        Mzq,
        Qdf
    }

    internal sealed class TapeRecord
    {
        public const int HeaderLength = 128;

        public TapeRecord(
            byte[] rawHeader,
            MZQFileHeader header,
            MZQFileBody body,
            TapeProfile profile = TapeProfile.Normal1_1,
            MetadataOrigin metadataOrigin = MetadataOrigin.Implicit)
        {
            if (rawHeader.Length != HeaderLength)
            {
                throw new ArgumentException("An MZF raw header must contain exactly 128 bytes.", nameof(rawHeader));
            }

            RawHeader = (byte[])rawHeader.Clone();
            Header = header;
            Body = body;
            Profile = profile;
            MetadataOrigin = metadataOrigin;
        }

        public byte[] RawHeader { get; }

        public MZQFileHeader Header { get; set; }

        public MZQFileBody Body { get; set; }

        public TapeProfile Profile { get; set; }

        public MetadataOrigin MetadataOrigin { get; set; }

        public byte[] GetSerializedHeader()
        {
            byte[] result = (byte[])RawHeader.Clone();
            MZQFileHeader header = Header;
            result[0] = header.MzfFtype;
            CopyFixed(header.MzfFname, result.AsSpan(1, 16));
            result[17] = header.MzfFnameEnd;
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(18, 2), header.MzfSize);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20, 2), header.MzfStart);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22, 2), header.MzfExec);
            return result;
        }

        public byte[] DescriptionRaw => GetSerializedHeader()[24..128];

        public void RemoveTrailingData()
        {
            MZQFileBody body = Body;
            body.TrailingData = Array.Empty<byte>();
            Body = body;
        }

        public void ResetMetadataToImplicit()
        {
            Profile = TapeProfile.Normal1_1;
            MetadataOrigin = MetadataOrigin.Implicit;
        }

        public TapeRecord DeepClone()
        {
            MZQFileHeader header = Header;
            header.StartSign = CloneArray(header.StartSign);
            header.MzfFname = CloneArray(header.MzfFname);
            header.Unused1 = CloneArray(header.Unused1);
            header.MzfHeaderDescription = CloneArray(header.MzfHeaderDescription);
            header.Crc = CloneArray(header.Crc);
            MZQFileBody body = Body;
            body.StartSign = CloneArray(body.StartSign);
            body.MzfBody = CloneArray(body.MzfBody);
            body.Crc = CloneArray(body.Crc);
            body.TrailingData = CloneArray(body.TrailingData);
            return new TapeRecord(RawHeader, header, body, Profile, MetadataOrigin);
        }

        public static TapeRecord FromLegacy(MZQFileHeader header, MZQFileBody body)
        {
            byte[] rawHeader = new byte[HeaderLength];
            rawHeader[0] = header.MzfFtype;
            CopyFixed(header.MzfFname, rawHeader.AsSpan(1, 16));
            rawHeader[17] = header.MzfFnameEnd;
            BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(18, 2), header.MzfSize);
            BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(20, 2), header.MzfStart);
            BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(22, 2), header.MzfExec);
            CopyFixed(header.MzfHeaderDescription, rawHeader.AsSpan(24, 104));
            return new TapeRecord(rawHeader, header, body);
        }

        private static void CopyFixed(byte[]? source, Span<byte> destination)
        {
            destination.Clear();
            if (source != null)
            {
                source.AsSpan(0, Math.Min(source.Length, destination.Length)).CopyTo(destination);
            }
        }

        private static byte[] CloneArray(byte[]? source) =>
            source == null ? Array.Empty<byte>() : (byte[])source.Clone();
    }

    internal sealed class TapeDocument
    {
        public List<TapeRecord> Records { get; } = new();

        public string? FilePath { get; set; }

        public TapeDocumentFormat Format { get; set; }

        public byte[] ContainerTrailingData { get; set; } = Array.Empty<byte>();

        public string? SidecarPath { get; set; }

        public bool HasSidecarBinding => SidecarPath != null;

        public void Clear()
        {
            Records.Clear();
            FilePath = null;
            Format = TapeDocumentFormat.None;
            ContainerTrailingData = Array.Empty<byte>();
            SidecarPath = null;
        }
    }

    internal static class TapeProfileNames
    {
        public static string ToDisplayName(TapeProfile profile) => profile switch
        {
            TapeProfile.Normal1_1 => "NORMAL 1:1",
            TapeProfile.Normal1_2 => "NORMAL 1:2",
            TapeProfile.Normal1_3 => "NORMAL 1:3",
            TapeProfile.Normal1_4 => "NORMAL 1:4",
            TapeProfile.Mz700_1_1 => "MZ700 1:1",
            TapeProfile.Mz700_1_3 => "MZ700 1:3",
            TapeProfile.Ic1_2 => "IC 1:2",
            TapeProfile.Ic1_3 => "IC 1:3",
            TapeProfile.Ic1_4 => "IC 1:4",
            TapeProfile.Tc1_2 => "TC 1:2",
            TapeProfile.Tc1_3 => "TC 1:3",
            TapeProfile.Tc1_4 => "TC 1:4",
            TapeProfile.Ultra => "UL",
            TapeProfile.UltraMz800 => "UL_MZ800",
            TapeProfile.UltraMz700 => "UL_MZ700",
            _ => profile.ToString()
        };
    }

    internal static class TapeProfileComponents
    {
        private static readonly string[] AllLoaders =
        [
            "NORMAL", "MZ700", "IC", "TC", "UL", "UL_MZ800", "UL_MZ700"
        ];

        private static readonly string[] NormalSpeeds = ["1:1", "1:2", "1:3", "1:4"];
        private static readonly string[] Mz700Speeds = ["1:1", "1:3"];
        private static readonly string[] AcceleratedSpeeds = ["1:2", "1:3", "1:4"];
        private static readonly string[] NoSpeed = [""];

        public static IReadOnlyList<string> LoaderTypes => AllLoaders;

        public static IReadOnlyList<string> GetAvailableSpeeds(string loaderType) => loaderType switch
        {
            "NORMAL" => NormalSpeeds,
            "MZ700" => Mz700Speeds,
            "IC" or "TC" => AcceleratedSpeeds,
            "UL" or "UL_MZ800" or "UL_MZ700" => NoSpeed,
            _ => NormalSpeeds
        };

        public static string NormalizeSpeed(string loaderType, string? speed)
        {
            IReadOnlyList<string> available = GetAvailableSpeeds(loaderType);
            return speed != null && available.Contains(speed, StringComparer.Ordinal)
                ? speed
                : available[0];
        }

        public static (string LoaderType, string Speed) Split(TapeProfile profile) => profile switch
        {
            TapeProfile.Normal1_1 => ("NORMAL", "1:1"),
            TapeProfile.Normal1_2 => ("NORMAL", "1:2"),
            TapeProfile.Normal1_3 => ("NORMAL", "1:3"),
            TapeProfile.Normal1_4 => ("NORMAL", "1:4"),
            TapeProfile.Mz700_1_1 => ("MZ700", "1:1"),
            TapeProfile.Mz700_1_3 => ("MZ700", "1:3"),
            TapeProfile.Ic1_2 => ("IC", "1:2"),
            TapeProfile.Ic1_3 => ("IC", "1:3"),
            TapeProfile.Ic1_4 => ("IC", "1:4"),
            TapeProfile.Tc1_2 => ("TC", "1:2"),
            TapeProfile.Tc1_3 => ("TC", "1:3"),
            TapeProfile.Tc1_4 => ("TC", "1:4"),
            TapeProfile.Ultra => ("UL", ""),
            TapeProfile.UltraMz800 => ("UL_MZ800", ""),
            TapeProfile.UltraMz700 => ("UL_MZ700", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };

        public static TapeProfile Combine(string loaderType, string? speed)
        {
            string normalizedSpeed = NormalizeSpeed(loaderType, speed);
            return (loaderType, normalizedSpeed) switch
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
                ("UL", "") => TapeProfile.Ultra,
                ("UL_MZ800", "") => TapeProfile.UltraMz800,
                ("UL_MZ700", "") => TapeProfile.UltraMz700,
                _ => throw new ArgumentException($"Unsupported tape profile: {loaderType} {speed}".Trim())
            };
        }
    }
}
