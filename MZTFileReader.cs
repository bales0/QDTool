using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.RightsManagement;
using System.Text;
using System.Threading.Tasks;
using static QDTool.Utility;

namespace QDTool
{
    internal class MZTFileReader
    {
        private const int MzfBlockAlignment = 128;

        public long currentPosition = 0;
        public long totalLength = 0;
        public long bytesRemaining = 0;

        public List<(MZQFileHeader, MZQFileBody)> ReadMztFile(string filePath)
        {
            List<(MZQFileHeader, MZQFileBody)> mzfBlocks = new List<(MZQFileHeader, MZQFileBody)>();

            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (BinaryReader reader = new BinaryReader(fs))
            {
                // Read each MZF File
                while (reader.BaseStream.Length - reader.BaseStream.Position >= MzfBlockAlignment)
                {
                    var mzfBlock = ReadMzfFile(reader);
                    mzfBlocks.Add(mzfBlock);
                }

                long trailingBytes = reader.BaseStream.Length - reader.BaseStream.Position;
                if (mzfBlocks.Count == 0 && trailingBytes > 0)
                {
                    throw new InvalidDataException(
                        $"Incomplete MZF header: expected {MzfBlockAlignment} bytes, only {trailingBytes} remain.");
                }

                if (trailingBytes > 0 && reader.BaseStream.Length % MzfBlockAlignment != 0)
                {
                    throw new InvalidDataException(
                        $"Unexpected {trailingBytes} trailing bytes after the last MZF file.");
                }

                if (trailingBytes > 0)
                {
                    byte[] trailingData = ReadBytesExact(reader, (int)trailingBytes, "MZF trailing data");
                    var (header, body) = mzfBlocks[^1];
                    body.TrailingData = trailingData;
                    mzfBlocks[^1] = (header, body);
                }

                currentPosition = fs.Position;
                totalLength = fs.Length;
                bytesRemaining = totalLength - currentPosition;
            }

            return mzfBlocks;
        }

        public (MZQFileHeader, MZQFileBody) ReadMzfFile(BinaryReader reader)
        {

            MZQFileHeader header = new MZQFileHeader();

            // Načtení jednotlivých členů struktury
            header.StartSign = ExpectedStartSign.ToArray(); // zadny tu neni, ale vyplnime
            header.MzfHeaderSign = 0x00;
            header.DataSize = 0x0040; // (ushort)(header.MzfSize + 128); // ma vyznam asi jen u MZQ - ??? - aaa
            header.MzfFtype = reader.ReadByte();
            header.MzfFname = ReadBytesExact(reader, 16, "MZF file name");
            header.MzfFnameEnd = reader.ReadByte();
            header.Unused1 = ExpectedUnused.ToArray(); // zadny tu neni, ale vyplnime
            header.MzfSize = reader.ReadUInt16();
            header.MzfStart = reader.ReadUInt16();
            header.MzfExec = reader.ReadUInt16();

            // Načtení celych 104 bajtů pro MzfHeaderDescription
            header.MzfHeaderDescription = new byte[104]; // Inicializace pole 104 bajty
            byte[] descriptionBytes = ReadBytesExact(reader, 104, "MZF header description");
            Array.Copy(descriptionBytes, header.MzfHeaderDescription, descriptionBytes.Length);

            // Načtení CRC
            header.Crc = ExpectedCrc.ToArray(); // zadny tu neni, ale vyplnime

            // Načtení zbytku MZF Těla
            MZQFileBody body = new MZQFileBody
            {
                StartSign = ExpectedStartSign.ToArray(),
                MzfBodySign = 0x05,
                DataSize = header.MzfSize, // ??? - aaa
                MzfBody = ReadBytesExact(reader, header.MzfSize, "MZF file body"),
                Crc = ExpectedCrc.ToArray(),
                TrailingData = Array.Empty<byte>()
            };

            return (header, body);
        }

        public void WriteMZFFileHeaderToFile(FileStream fileStream, MZQFileHeader mzfHeader)
        {
            fileStream.WriteByte(mzfHeader.MzfFtype);
            fileStream.Write(mzfHeader.MzfFname, 0, mzfHeader.MzfFname.Length);
            fileStream.WriteByte(mzfHeader.MzfFnameEnd);

            var mzfSizeBytes = BitConverter.GetBytes(mzfHeader.MzfSize);
            fileStream.Write(mzfSizeBytes, 0, mzfSizeBytes.Length);

            var mzfStartBytes = BitConverter.GetBytes(mzfHeader.MzfStart);
            fileStream.Write(mzfStartBytes, 0, mzfStartBytes.Length);

            var mzfExecBytes = BitConverter.GetBytes(mzfHeader.MzfExec);
            fileStream.Write(mzfExecBytes, 0, mzfExecBytes.Length);

            fileStream.Write(mzfHeader.MzfHeaderDescription, 0, 104);
        }

        public void WriteMZFFileBodyToFile(FileStream fileStream, MZQFileBody mzfBody)
        {
            fileStream.Write(mzfBody.MzfBody, 0, mzfBody.DataSize);
        }

        public void WriteMZFTrailingDataToFile(FileStream fileStream, MZQFileBody mzfBody)
        {
            if (mzfBody.TrailingData is { Length: > 0 })
            {
                fileStream.Write(mzfBody.TrailingData, 0, mzfBody.TrailingData.Length);
            }
        }

    }
}
