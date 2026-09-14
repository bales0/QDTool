using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static QDTool.Utility;

namespace QDTool
{
    internal sealed record SharpQdFrame(long Position, byte Type, byte[] Bytes);

    internal static class SharpQdFrameCodec
    {
        internal const int HeaderDataLength = 64;
        internal const int HeaderFrameLength = 70;
        private static readonly object CrcLock = new();

        public static IReadOnlyList<SharpQdFrame> FindFrames(ReadOnlySpan<byte> decoded, long firstBitPosition = 0)
        {
            var frames = new List<SharpQdFrame>();
            for (int index = 0; index <= decoded.Length - 4; index++)
            {
                if (decoded[index] != 0xA5 || !HasSharpSync(decoded, index))
                {
                    continue;
                }

                int frameLength;
                byte type = decoded[index + 1];
                byte[] countCandidate = decoded.Slice(index, 4).ToArray();
                if ((type & 1) == 0 && HasValidCrc(countCandidate))
                {
                    frames.Add(new SharpQdFrame(firstBitPosition + index * 16L, 0x02, countCandidate));
                    continue;
                }

                if (type is 0x00 or 0x05)
                {
                    if (index + 4 > decoded.Length)
                    {
                        continue;
                    }

                    int dataLength = BinaryPrimitives.ReadUInt16LittleEndian(decoded.Slice(index + 2, 2));
                    if (type == 0x00 && dataLength != HeaderDataLength)
                    {
                        continue;
                    }

                    frameLength = 1 + 1 + 2 + dataLength + 2;
                }
                else
                {
                    continue;
                }

                if (frameLength < 4 || index > decoded.Length - frameLength)
                {
                    continue;
                }

                byte[] bytes = decoded.Slice(index, frameLength).ToArray();
                if (HasValidCrc(bytes))
                {
                    frames.Add(new SharpQdFrame(firstBitPosition + index * 16L, type, bytes));
                }
            }

            return frames;
        }

        public static List<(MZQFileHeader, MZQFileBody)> DecodeRecords(IEnumerable<SharpQdFrame> candidates)
        {
            List<SharpQdFrame> frames = candidates
                .OrderBy(frame => frame.Position)
                .Aggregate(new List<SharpQdFrame>(), (result, frame) =>
                {
                    if (!result.Any(existing =>
                        Math.Abs(existing.Position - frame.Position) <= 15 &&
                        existing.Bytes.AsSpan().SequenceEqual(frame.Bytes)))
                    {
                        result.Add(frame);
                    }
                    return result;
                });

            bool foundCountFrame = false;
            string? lastError = null;
            foreach (SharpQdFrame countFrame in frames.Where(frame => frame.Type == 0x02))
            {
                foundCountFrame = true;
                int blockCount = countFrame.Bytes[1];
                if ((blockCount & 1) != 0)
                {
                    lastError = "FNBLK/header/body inconsistency: the Sharp QD block count is odd.";
                    continue;
                }

                if (blockCount == 0)
                {
                    return new List<(MZQFileHeader, MZQFileBody)>();
                }

                List<SharpQdFrame> following = frames
                    .Where(frame => frame.Position > countFrame.Position && frame.Type is 0x00 or 0x05)
                    .ToList();
                if (following.Count < blockCount)
                {
                    lastError = "Corrupt Sharp QD frame sequence: FNBLK declares more blocks than were found.";
                    continue;
                }

                var records = new List<(MZQFileHeader, MZQFileBody)>();
                bool consistent = true;
                for (int block = 0; block < blockCount; block += 2)
                {
                    SharpQdFrame headerFrame = following[block];
                    SharpQdFrame bodyFrame = following[block + 1];
                    if (headerFrame.Type != 0x00 || bodyFrame.Type != 0x05)
                    {
                        lastError = "FNBLK/header/body inconsistency in Sharp QD image.";
                        consistent = false;
                        break;
                    }

                    MZQFileHeader header = DecodeHeader(headerFrame.Bytes);
                    MZQFileBody body = DecodeBody(bodyFrame.Bytes);
                    if (header.MzfSize != body.DataSize)
                    {
                        lastError = $"FNBLK/header/body inconsistency: header declares {header.MzfSize} bytes, body declares {body.DataSize} bytes.";
                        consistent = false;
                        break;
                    }
                    records.Add((header, body));
                }

                if (consistent)
                {
                    return records;
                }
            }

            if (!foundCountFrame)
            {
                throw new InvalidDataException("Corrupt Sharp QD frame sequence: a CRC-valid FNBLK frame was not found.");
            }

            throw new InvalidDataException(lastError ?? "Corrupt Sharp QD frame sequence.");
        }

        public static List<(MZQFileHeader, MZQFileBody)> DecodeByteStream(ReadOnlySpan<byte> bytes)
        {
            IReadOnlyList<SharpQdFrame> frames = FindFrames(bytes);
            return DecodeRecords(frames);
        }

        internal static bool ContainsPlausibleFrame(ReadOnlySpan<byte> decoded)
        {
            for (int index = 0; index <= decoded.Length - 4; index++)
            {
                if (decoded[index] != 0xA5 || !HasSharpSync(decoded, index))
                {
                    continue;
                }

                byte marker = decoded[index + 1];
                if ((marker & 1) == 0)
                {
                    return true;
                }
                if (marker == 0x05)
                {
                    int length = BinaryPrimitives.ReadUInt16LittleEndian(decoded.Slice(index + 2, 2));
                    if (index <= decoded.Length - (6 + length))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static byte[] BuildPhysicalByteStream(IReadOnlyList<TapeRecord> records)
        {
            if (records.Count > byte.MaxValue / 2)
            {
                throw new InvalidDataException("QuickDisk supports at most 127 files.");
            }

            using var stream = new MemoryStream();
            WriteFramed(stream, EncodeCountFrame(checked((byte)(records.Count * 2))), 256);
            foreach (TapeRecord record in records)
            {
                WriteFramed(stream, EncodeHeaderFrame(record.Header), 254);
                WriteFramed(stream, EncodeBodyFrame(record.Body), 256);
            }
            return stream.ToArray();
        }

        public static byte[] EncodeCountFrame(byte blockCount) => AddCrc([0xA5, blockCount]);

        public static byte[] EncodeHeaderFrame(MZQFileHeader header)
        {
            byte[] withoutCrc = new byte[HeaderFrameLength - 2];
            withoutCrc[0] = 0xA5;
            withoutCrc[1] = 0x00;
            BinaryPrimitives.WriteUInt16LittleEndian(withoutCrc.AsSpan(2, 2), HeaderDataLength);
            withoutCrc[4] = header.MzfFtype;
            CopyExactOrPad(header.MzfFname, withoutCrc.AsSpan(5, 16));
            withoutCrc[21] = header.MzfFnameEnd;
            CopyExactOrPad(header.Unused1, withoutCrc.AsSpan(22, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(withoutCrc.AsSpan(24, 2), header.MzfSize);
            BinaryPrimitives.WriteUInt16LittleEndian(withoutCrc.AsSpan(26, 2), header.MzfStart);
            BinaryPrimitives.WriteUInt16LittleEndian(withoutCrc.AsSpan(28, 2), header.MzfExec);
            CopyExactOrPad(header.MzfHeaderDescription, withoutCrc.AsSpan(30, 38));
            return AddCrc(withoutCrc);
        }

        public static byte[] EncodeBodyFrame(MZQFileBody body)
        {
            if (body.MzfBody is null || body.MzfBody.Length < body.DataSize)
            {
                throw new InvalidDataException("QuickDisk body is shorter than its declared size.");
            }

            byte[] withoutCrc = new byte[4 + body.DataSize];
            withoutCrc[0] = 0xA5;
            withoutCrc[1] = 0x05;
            BinaryPrimitives.WriteUInt16LittleEndian(withoutCrc.AsSpan(2, 2), body.DataSize);
            body.MzfBody.AsSpan(0, body.DataSize).CopyTo(withoutCrc.AsSpan(4));
            return AddCrc(withoutCrc);
        }

        internal static bool HasValidCrc(ReadOnlySpan<byte> frame)
        {
            lock (CrcLock)
            {
                ushort value = 0;
                for (int i = 0; i < frame.Length; i++)
                {
                    value = CRC_check(frame[i], i == 0);
                }
                return value == 0;
            }
        }

        private static byte[] AddCrc(ReadOnlySpan<byte> bytes)
        {
            byte[] result = new byte[bytes.Length + 2];
            bytes.CopyTo(result);
            lock (CrcLock)
            {
                ushort crc = 0;
                for (int i = 0; i < bytes.Length; i++)
                {
                    crc = CRC_check(bytes[i], i == 0);
                }
                result[^2] = ReverseBits((byte)(crc >> 8));
                result[^1] = ReverseBits((byte)(crc & 0xFF));
            }
            return result;
        }

        private static MZQFileHeader DecodeHeader(ReadOnlySpan<byte> frame)
        {
            byte[] description = new byte[104];
            frame.Slice(30, 38).CopyTo(description);
            return new MZQFileHeader
            {
                StartSign = ExpectedStartSign.ToArray(),
                MzfHeaderSign = 0x00,
                DataSize = HeaderDataLength,
                MzfFtype = frame[4],
                MzfFname = frame.Slice(5, 16).ToArray(),
                MzfFnameEnd = frame[21],
                Unused1 = frame.Slice(22, 2).ToArray(),
                MzfSize = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(24, 2)),
                MzfStart = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(26, 2)),
                MzfExec = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(28, 2)),
                MzfHeaderDescription = description,
                Crc = ExpectedCrc.ToArray()
            };
        }

        private static MZQFileBody DecodeBody(ReadOnlySpan<byte> frame)
        {
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(2, 2));
            return new MZQFileBody
            {
                StartSign = ExpectedStartSign.ToArray(),
                MzfBodySign = 0x05,
                DataSize = length,
                MzfBody = frame.Slice(4, length).ToArray(),
                Crc = ExpectedCrc.ToArray(),
                TrailingData = Array.Empty<byte>()
            };
        }

        private static bool HasSharpSync(ReadOnlySpan<byte> bytes, int frameIndex)
        {
            int index = frameIndex - 1;
            int syncCount = 0;
            while (index >= 0 && bytes[index] == 0x16)
            {
                syncCount++;
                index--;
            }
            if (syncCount < 2)
            {
                return false;
            }

            // A phase transition at the break/sync boundary can decode the first
            // sync-adjacent byte as 0x10/0x14. Require a nearby break byte while
            // allowing that single boundary artifact.
            int lowerBound = Math.Max(0, index - 16);
            for (; index >= lowerBound; index--)
            {
                if (bytes[index] == 0x00)
                {
                    return true;
                }
            }
            return false;
        }

        private static void WriteFramed(Stream stream, byte[] frame, int trailingZeroCount)
        {
            stream.WriteByte(0x00);
            for (int i = 0; i < 10; i++) stream.WriteByte(0x16);
            stream.Write(frame);
            for (int i = 0; i < 7; i++) stream.WriteByte(0x16);
            for (int i = 0; i < trailingZeroCount; i++) stream.WriteByte(0x00);
        }

        private static void CopyExactOrPad(byte[]? source, Span<byte> destination)
        {
            destination.Clear();
            source?.AsSpan(0, Math.Min(source.Length, destination.Length)).CopyTo(destination);
        }
    }
}
