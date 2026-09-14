using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static QDTool.Utility;

namespace QDTool
{
    internal static class SharpLegacyQdCodec
    {
        public const int ImageSize = 0xF00F;
        private const int LogicalHeaderSize = 8;
        private const int FormattingTerminatorSize = 4;
        private const int MinimumFormattingTailSize = 8;

        public static IReadOnlyList<TapeRecord> Read(ReadOnlySpan<byte> image)
        {
            if (image.Length < LogicalHeaderSize || QdFormatDetector.Detect(image) != QdImageFormat.SharpLegacyLogical)
            {
                throw new InvalidDataException("Invalid Sharp/MZ legacy QuickDisk header.");
            }

            int blockCount = image[4];
            if ((blockCount & 1) != 0)
            {
                throw new InvalidDataException("FNBLK/header/body inconsistency: legacy QD block count is odd.");
            }

            int position = LogicalHeaderSize;
            var records = new List<TapeRecord>(blockCount / 2);
            for (int index = 0; index < blockCount / 2; index++)
            {
                MZQFileHeader header = ReadHeader(image, ref position);
                MZQFileBody body = ReadBody(image, ref position);
                if (header.MzfSize != body.DataSize)
                {
                    throw new InvalidDataException(
                        $"FNBLK/header/body inconsistency: header declares {header.MzfSize} bytes, body declares {body.DataSize} bytes.");
                }
                records.Add(TapeRecord.FromLegacy(header, body));
            }
            return records;
        }

        public static byte[] Write(IReadOnlyList<TapeRecord> records)
        {
            if (records.Count > byte.MaxValue / 2)
            {
                throw new InvalidDataException("QuickDisk supports at most 127 files.");
            }

            int logicalLength = LogicalHeaderSize + records.Sum(record => 84 + record.Body.DataSize);
            if (logicalLength + MinimumFormattingTailSize > ImageSize)
            {
                throw new InvalidDataException("Cannot save: QuickDisk capacity exceeded.");
            }

            byte[] image = new byte[ImageSize];
            ExpectedStartSign.CopyTo(image, 0);
            image[4] = checked((byte)(records.Count * 2));
            ExpectedCrc.CopyTo(image, 5);
            int position = LogicalHeaderSize;
            foreach (TapeRecord record in records)
            {
                WriteHeader(image, ref position, record.Header);
                WriteBody(image, ref position, record.Body);
            }

            image[position++] = 0x00;
            image[position++] = 0x16;
            image[position++] = 0x16;
            image[position++] = 0xA5;
            int patternEnd = ImageSize - FormattingTerminatorSize;
            bool write55 = true;
            while (position < patternEnd)
            {
                image[position++] = write55 ? (byte)0x55 : (byte)0xAA;
                write55 = !write55;
            }
            ExpectedCrc.CopyTo(image, position);
            image[^1] = 0x00;
            return image;
        }

        public static bool TryValidateCapacity(IReadOnlyList<TapeRecord> records, out string error)
        {
            int required = LogicalHeaderSize + records.Sum(record => 84 + record.Body.DataSize) + MinimumFormattingTailSize;
            if (required > ImageSize)
            {
                error = $"Cannot save: QuickDisk capacity exceeded ({required} bytes required, {ImageSize} available).";
                return false;
            }
            error = string.Empty;
            return true;
        }

        private static MZQFileHeader ReadHeader(ReadOnlySpan<byte> image, ref int position)
        {
            EnsureAvailable(image, position, 74, "legacy QD file header");
            if (!image.Slice(position, 4).SequenceEqual(ExpectedStartSign) ||
                !image.Slice(position + 71, 3).SequenceEqual(ExpectedCrc))
            {
                throw new InvalidDataException("Corrupt Sharp legacy QD file header marker.");
            }
            if (image[position + 4] != 0x00 || BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(position + 5, 2)) != 64)
            {
                throw new InvalidDataException("Corrupt Sharp legacy QD file header block.");
            }

            byte[] description = new byte[104];
            image.Slice(position + 33, 38).CopyTo(description);
            var header = new MZQFileHeader
            {
                StartSign = ExpectedStartSign.ToArray(),
                MzfHeaderSign = 0,
                DataSize = 64,
                MzfFtype = image[position + 7],
                MzfFname = image.Slice(position + 8, 16).ToArray(),
                MzfFnameEnd = image[position + 24],
                Unused1 = image.Slice(position + 25, 2).ToArray(),
                MzfSize = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(position + 27, 2)),
                MzfStart = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(position + 29, 2)),
                MzfExec = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(position + 31, 2)),
                MzfHeaderDescription = description,
                Crc = ExpectedCrc.ToArray()
            };
            position += 74;
            return header;
        }

        private static MZQFileBody ReadBody(ReadOnlySpan<byte> image, ref int position)
        {
            EnsureAvailable(image, position, 10, "legacy QD file body header");
            if (!image.Slice(position, 4).SequenceEqual(ExpectedStartSign) || image[position + 4] != 0x05)
            {
                throw new InvalidDataException("Corrupt Sharp legacy QD file body marker.");
            }
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(position + 5, 2));
            EnsureAvailable(image, position, 10 + length, "legacy QD file body");
            if (!image.Slice(position + 7 + length, 3).SequenceEqual(ExpectedCrc))
            {
                throw new InvalidDataException("Corrupt Sharp legacy QD file body CRC marker.");
            }
            var body = new MZQFileBody
            {
                StartSign = ExpectedStartSign.ToArray(),
                MzfBodySign = 0x05,
                DataSize = length,
                MzfBody = image.Slice(position + 7, length).ToArray(),
                Crc = ExpectedCrc.ToArray(),
                TrailingData = Array.Empty<byte>()
            };
            position += 10 + length;
            return body;
        }

        private static void WriteHeader(Span<byte> output, ref int position, MZQFileHeader header)
        {
            ExpectedStartSign.CopyTo(output.Slice(position, 4));
            output[position + 4] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(position + 5, 2), 64);
            output[position + 7] = header.MzfFtype;
            Copy(header.MzfFname, output.Slice(position + 8, 16));
            output[position + 24] = header.MzfFnameEnd;
            Copy(header.Unused1, output.Slice(position + 25, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(position + 27, 2), header.MzfSize);
            BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(position + 29, 2), header.MzfStart);
            BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(position + 31, 2), header.MzfExec);
            Copy(header.MzfHeaderDescription, output.Slice(position + 33, 38));
            ExpectedCrc.CopyTo(output.Slice(position + 71, 3));
            position += 74;
        }

        private static void WriteBody(Span<byte> output, ref int position, MZQFileBody body)
        {
            if (body.MzfBody is null || body.MzfBody.Length < body.DataSize)
            {
                throw new InvalidDataException("QuickDisk body is shorter than its declared size.");
            }
            ExpectedStartSign.CopyTo(output.Slice(position, 4));
            output[position + 4] = 0x05;
            BinaryPrimitives.WriteUInt16LittleEndian(output.Slice(position + 5, 2), body.DataSize);
            body.MzfBody.AsSpan(0, body.DataSize).CopyTo(output.Slice(position + 7));
            ExpectedCrc.CopyTo(output.Slice(position + 7 + body.DataSize, 3));
            position += 10 + body.DataSize;
        }

        private static void Copy(byte[]? source, Span<byte> destination)
        {
            destination.Clear();
            source?.AsSpan(0, Math.Min(source.Length, destination.Length)).CopyTo(destination);
        }

        private static void EnsureAvailable(ReadOnlySpan<byte> image, int position, int count, string field)
        {
            if (position < 0 || count < 0 || position > image.Length - count)
            {
                throw new EndOfStreamException($"Unexpected end of file while reading {field}.");
            }
        }
    }
}
