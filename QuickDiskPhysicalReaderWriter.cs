using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QDTool
{
    internal static class QuickDiskPhysicalReader
    {
        public static IReadOnlyList<TapeRecord> Read(ReadOnlySpan<byte> image, QdImageFormat format)
        {
            QuickDiskContainer container = HxcFlashFloppyQdContainer.Parse(image, format);
            IReadOnlyList<SharpQdFrame> frames = QuickDiskMfmCodec.FindSharpFrames(container.Track);
            if (frames.Count == 0)
            {
                if (IsBlankTrack(container.Track))
                {
                    return Array.Empty<TapeRecord>();
                }
                if (QuickDiskMfmCodec.ContainsPlausibleSharpFrame(container.Track))
                {
                    throw new InvalidDataException("CRC error or corrupt Sharp QD frame.");
                }
                throw new InvalidDataException(
                    "The QD container is valid, but it does not contain a supported Sharp MZ QuickDisk image.");
            }

            return SharpQdFrameCodec.DecodeRecords(frames)
                .Select(block => TapeRecord.FromLegacy(block.Item1, block.Item2))
                .ToList();
        }

        internal static bool IsBlankTrack(ReadOnlySpan<byte> track)
        {
            if (track.IsEmpty) return false;
            Span<int> histogram = stackalloc int[256];
            foreach (byte value in track) histogram[value]++;
            int dominant = 0;
            for (int i = 0; i < histogram.Length; i++) dominant = Math.Max(dominant, histogram[i]);
            return dominant >= track.Length * 98L / 100L;
        }
    }

    internal static class QuickDiskPhysicalWriter
    {
        public static byte[] Write(IReadOnlyList<TapeRecord> records, QdImageFormat format)
        {
            QuickDiskPhysicalProfile profile = QuickDiskPhysicalProfile.For(format);
            byte[] byteStream = SharpQdFrameCodec.BuildPhysicalByteStream(records);
            byte[] encoded = QuickDiskMfmCodec.Encode(byteStream);
            int capacity = profile.WindowEnd - profile.WindowStart;
            if (encoded.Length > capacity)
            {
                throw new InvalidDataException("Cannot save: QuickDisk physical data window capacity exceeded.");
            }

            byte[] track = Enumerable.Repeat(profile.BlankFiller, profile.StoredTrackLength).ToArray();
            encoded.CopyTo(track, profile.WindowStart);
            return HxcFlashFloppyQdContainer.Write(track, profile);
        }

        public static bool TryValidateCapacity(IReadOnlyList<TapeRecord> records, QdImageFormat format, out string error)
        {
            QuickDiskPhysicalProfile profile = QuickDiskPhysicalProfile.For(format);
            long logicalBytes = 1 + 10 + 4 + 7 + 256;
            foreach (TapeRecord record in records)
            {
                logicalBytes += 1 + 10 + SharpQdFrameCodec.HeaderFrameLength + 7 + 254;
                logicalBytes += 1 + 10 + 6 + record.Body.DataSize + 7 + 256;
            }
            long encodedBytes = logicalBytes * 2;
            long capacity = profile.WindowEnd - profile.WindowStart;
            if (encodedBytes > capacity)
            {
                error = $"Cannot save: QuickDisk physical data window capacity exceeded ({encodedBytes} encoded bytes required, {capacity} available).";
                return false;
            }
            error = string.Empty;
            return true;
        }
    }

    internal static class QdImageReaderWriter
    {
        public static QdReadResult ReadFile(string filePath)
        {
            byte[] image = File.ReadAllBytes(filePath);
            return Read(image);
        }

        public static QdReadResult Read(ReadOnlySpan<byte> image)
        {
            QdImageFormat format = QdFormatDetector.Detect(image);
            IReadOnlyList<TapeRecord> records = format switch
            {
                QdImageFormat.SharpLegacyLogical => SharpLegacyQdCodec.Read(image),
                QdImageFormat.HxcPhysical or QdImageFormat.FlashFloppyPhysical => QuickDiskPhysicalReader.Read(image, format),
                _ => throw new InvalidDataException("Unknown .QD format.")
            };
            return new QdReadResult(format, records);
        }

        public static byte[] Write(IReadOnlyList<TapeRecord> records, QdImageFormat format) => format switch
        {
            QdImageFormat.SharpLegacyLogical => SharpLegacyQdCodec.Write(records),
            QdImageFormat.HxcPhysical or QdImageFormat.FlashFloppyPhysical => QuickDiskPhysicalWriter.Write(records, format),
            _ => throw new ArgumentOutOfRangeException(nameof(format), "A concrete QD image format is required.")
        };
    }
}
