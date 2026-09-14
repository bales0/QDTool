using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static QDTool.Utility;

namespace QDTool
{
    internal sealed class MztReadResult
    {
        public List<TapeRecord> Records { get; } = new();
        public byte[] ContainerTrailingData { get; set; } = Array.Empty<byte>();
    }

    internal class MZTFileReader
    {
        public long currentPosition;
        public long totalLength;
        public long bytesRemaining;

        public TapeRecord ReadStandaloneMzf(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            TapeRecord record = ReadMzfRecord(reader);
            int trailingLength = checked((int)(stream.Length - stream.Position));
            if (trailingLength > 0)
            {
                MZQFileBody body = record.Body;
                body.TrailingData = ReadBytesExact(reader, trailingLength, "MZF trailing data");
                record.Body = body;
            }
            SetPosition(stream);
            return record;
        }

        public MztReadResult ReadMzt(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            var result = new MztReadResult();

            while (stream.Position < stream.Length)
            {
                long recordStart = stream.Position;
                if (!TryReadMzfRecord(reader, out TapeRecord? record))
                {
                    stream.Position = recordStart;
                    int trailingLength = checked((int)(stream.Length - stream.Position));
                    result.ContainerTrailingData = ReadBytesExact(reader, trailingLength, "MZT container trailing data");
                    break;
                }
                result.Records.Add(record!);
            }

            if (result.Records.Count == 0)
            {
                throw new InvalidDataException("The MZT does not contain a complete, structurally valid MZF record.");
            }
            SetPosition(stream);
            return result;
        }

        public bool TryReadMzfRecord(BinaryReader reader, out TapeRecord? record)
        {
            record = null;
            Stream stream = reader.BaseStream;
            long start = stream.Position;
            if (stream.Length - start < TapeRecord.HeaderLength)
            {
                return false;
            }

            byte[] rawHeader = reader.ReadBytes(TapeRecord.HeaderLength);
            ushort bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(rawHeader.AsSpan(18, 2));
            bool acceptableHeader = IsStructurallyAcceptableHeader(rawHeader);
            if (!acceptableHeader || stream.Length - stream.Position < bodyLength)
            {
                stream.Position = start;
                return false;
            }

            byte[] bodyBytes = reader.ReadBytes(bodyLength);
            record = CreateRecord(rawHeader, bodyBytes);
            return true;
        }

        private static bool IsStructurallyAcceptableHeader(ReadOnlySpan<byte> rawHeader)
        {
            // MZF has no magic signature. A short name normally ends with CR in
            // byte 17, while several real tools write NUL when all 16 filename
            // bytes are occupied. Requiring CR rejected otherwise valid records.
            // File type zero is not a defined MZF type and keeps zero-filled or
            // alignment tails from being mistaken for another record.
            return rawHeader[0] != 0 && (rawHeader[17] == 0x0D || rawHeader[17] == 0x00);
        }

        public TapeRecord ReadMzfRecord(BinaryReader reader)
        {
            long available = reader.BaseStream.Length - reader.BaseStream.Position;
            if (available < TapeRecord.HeaderLength)
            {
                throw new InvalidDataException(
                    $"Incomplete MZF header: expected {TapeRecord.HeaderLength} bytes, only {available} remain.");
            }

            byte[] rawHeader = ReadBytesExact(reader, TapeRecord.HeaderLength, "MZF header");
            ushort bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(rawHeader.AsSpan(18, 2));
            byte[] body = ReadBytesExact(reader, bodyLength, "MZF file body");
            return CreateRecord(rawHeader, body);
        }

        // Compatibility entry points retained for QD/MZQ code and older tests.
        public (MZQFileHeader, MZQFileBody) ReadMzfFile(BinaryReader reader)
        {
            TapeRecord record = ReadMzfRecord(reader);
            return (record.Header, record.Body);
        }

        public List<(MZQFileHeader, MZQFileBody)> ReadMztFile(string filePath) =>
            ReadMzt(filePath).Records.Select(record => (record.Header, record.Body)).ToList();

        public void WriteMZFFileHeaderToFile(FileStream fileStream, MZQFileHeader mzfHeader) =>
            fileStream.Write(TapeRecord.FromLegacy(mzfHeader, default).GetSerializedHeader());

        public void WriteMZFFileBodyToFile(FileStream fileStream, MZQFileBody mzfBody) =>
            fileStream.Write(mzfBody.MzfBody, 0, mzfBody.DataSize);

        public void WriteMZFTrailingDataToFile(FileStream fileStream, MZQFileBody mzfBody)
        {
            if (mzfBody.TrailingData is { Length: > 0 })
            {
                fileStream.Write(mzfBody.TrailingData);
            }
        }

        private static TapeRecord CreateRecord(byte[] rawHeader, byte[] bodyBytes)
        {
            var header = new MZQFileHeader
            {
                StartSign = ExpectedStartSign.ToArray(),
                MzfHeaderSign = 0,
                DataSize = 0x0040,
                MzfFtype = rawHeader[0],
                MzfFname = rawHeader[1..17],
                MzfFnameEnd = rawHeader[17],
                Unused1 = ExpectedUnused.ToArray(),
                MzfSize = BinaryPrimitives.ReadUInt16LittleEndian(rawHeader.AsSpan(18, 2)),
                MzfStart = BinaryPrimitives.ReadUInt16LittleEndian(rawHeader.AsSpan(20, 2)),
                MzfExec = BinaryPrimitives.ReadUInt16LittleEndian(rawHeader.AsSpan(22, 2)),
                MzfHeaderDescription = rawHeader[24..128],
                Crc = ExpectedCrc.ToArray()
            };
            var body = new MZQFileBody
            {
                StartSign = ExpectedStartSign.ToArray(),
                MzfBodySign = 0x05,
                DataSize = header.MzfSize,
                MzfBody = bodyBytes,
                Crc = ExpectedCrc.ToArray(),
                TrailingData = Array.Empty<byte>()
            };
            return new TapeRecord(rawHeader, header, body);
        }

        private void SetPosition(Stream stream)
        {
            currentPosition = stream.Position;
            totalLength = stream.Length;
            bytesRemaining = totalLength - currentPosition;
        }
    }
}
