using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Markup;
using static QDTool.Utility;

namespace QDTool
{
    class QDFFileReader
    {
        public long currentPosition = 0;
        public long totalLength = 0;
        public long bytesRemaining = 0;
        public List<long> positions = new List<long>();

        private static bool ValidateStartSign(byte[] startSign)
        {
            return startSign.SequenceEqual(ExpectedStartSign);
        }

        private static bool ValidateCrc(byte[] crc)
        {
            return crc.SequenceEqual(ExpectedCrc);
        }

        private static bool ValidateQDFHeader(MZQHeader header)
        {
            return ValidateStartSign(header.StartSign) && ValidateCrc(header.Crc);
        }

        private static bool ValidateQDiskMzfHeader(MZQFileHeader header)
        {
            return ValidateStartSign(header.StartSign) && ValidateCrc(header.Crc);
        }

        private static bool ValidateQDiskMzfBody(MZQFileBody body)
        {
            return ValidateStartSign(body.StartSign) && ValidateCrc(body.Crc);
        }

        public bool IsQDFHeader(BinaryReader reader)
        {
            byte[] expectedHeaderBytes = Encoding.ASCII.GetBytes("-QD format-")
                .Concat(Enumerable.Repeat((byte)0xFF, 5)).ToArray();
            byte[] headerBytes = reader.ReadBytes(expectedHeaderBytes.Length);

            return headerBytes.SequenceEqual(expectedHeaderBytes);
        }

        public bool FindStartSequence(BinaryReader reader)
        {
            bool found00 = false; // Indikátor nalezení 0x00
            int countOf16 = 0;    // Počet postupných 0x16 bytů

            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                byte currentByte = reader.ReadByte();
                long currentPosition = reader.BaseStream.Position;

                if (currentPosition == 0x12E8)
                    currentPosition++;

                if (currentByte == 0x00)
                {
                    found00 = true; // Nalezen 0x00, čekáme na 0x16
                    countOf16 = 0;
                }
                else // found00 je true
                {
                    if (found00 && currentByte == 0x16)
                    {
                        countOf16++; // Počítání 0x16 bytů
                    }
                    else if (found00 && currentByte == 0xA5 && countOf16 >= 2)
                    {
                        return true; // Úspěšně nalezená sekvence
                    }
                    else
                    {
                        found00 = false; // Reset, pokud byte není 0x16 nebo 0xA5
                    }
                }
            }

            return false;
        }


        public MZQHeader ReadQDFHeader(BinaryReader reader)
        {
            MZQHeader header = new MZQHeader();
            header.StartSign = ZeroStartSign.ToArray();
            header.FileBlocksCount = 0;
            header.Crc = ZeroCrc.ToArray();

            if (IsQDFHeader(reader))
            {
                if (FindStartSequence(reader))
                {
                    positions.Add(reader.BaseStream.Position);
                    positions.Add(3);
                    header.StartSign = ExpectedStartSign.ToArray();
                    CRC_check(0xA5, true);
                    header.FileBlocksCount = reader.ReadByte();
                    ushort crc = CRC_check(header.FileBlocksCount);
                    ushort readCRC = reader.ReadUInt16();
                    byte crcHi = ReverseBits((byte)(crc & 0xFF));  // reverse bits and swap hi-lo bytes too
                    byte crcLo = ReverseBits((byte)(crc >> 8));
                    ushort calculatedCRC = (ushort)(crcLo + (crcHi << 8));
                    //CRC_check((byte)(readCRC & 0xFF));
                    //CRC_check((byte)(readCRC >> 8));
                    crc = CRC_check(readCRC);
                    header.Crc = ExpectedCrc.ToArray();
                    if (readCRC != calculatedCRC || crc != 0)
                    {
                        header.StartSign = ZeroStartSign.ToArray();
                        header.FileBlocksCount = 0;
                        header.Crc = ZeroCrc.ToArray();
                    }
                }
            }

            return header;
        }

        public (MZQFileHeader, MZQFileBody) ReadQDFMzfBlock(BinaryReader reader)
        {

            MZQFileHeader header = new MZQFileHeader();
            MZQFileBody body = new MZQFileBody();

            if (FindStartSequence(reader))
            {
                positions.Add(reader.BaseStream.Position);
                positions.Add(64 + 5);
                // Načtení jednotlivých členů struktury
                header.StartSign = ExpectedStartSign.ToArray();
                CRC_check(0xA5, true);
                header.MzfHeaderSign = reader.ReadByte();
                CRC_check(header.MzfHeaderSign);
                header.DataSize = reader.ReadUInt16();
                CRC_check(header.DataSize);
                header.MzfFtype = reader.ReadByte();
                CRC_check(header.MzfFtype);
                header.MzfFname = ReadBytesExact(reader, 16, "QDF MZF file name");
                for (int i = 0; i < 16; i++)
                {
                    CRC_check(header.MzfFname[i]);
                }
                header.MzfFnameEnd = reader.ReadByte();
                CRC_check(header.MzfFnameEnd);
                header.Unused1 = ReadBytesExact(reader, 2, "QDF unused header bytes");
                CRC_check(header.Unused1[0]);
                CRC_check(header.Unused1[1]);
                header.MzfSize = reader.ReadUInt16();
                CRC_check(header.MzfSize);
                header.MzfStart = reader.ReadUInt16();
                CRC_check(header.MzfStart);
                header.MzfExec = reader.ReadUInt16();
                CRC_check(header.MzfExec);

                // Načtení prvních 38 bajtů pro MzfHeaderDescription
                header.MzfHeaderDescription = new byte[104]; // Inicializace pole 104 bajty
                byte[] descriptionBytes = ReadBytesExact(reader, 38, "QDF MZF header description");
                Array.Copy(descriptionBytes, header.MzfHeaderDescription, descriptionBytes.Length);
                for (int i = 0; i < 38; i++)
                {
                    CRC_check(header.MzfHeaderDescription[i]);
                }

                // Načtení CRC
                header.Crc = ExpectedCrc.ToArray();
                ushort readCRC = reader.ReadUInt16();
                ushort crc = CRC_check(readCRC);

                if (crc != 0)
                {
                    header.Crc = ZeroCrc;
                    throw new InvalidOperationException("Invalid CRC in QDF MZF Header");
                }

                if (!ValidateQDiskMzfHeader(header))
                {
                    throw new InvalidOperationException("Neplatný MZF Header");
                }

                if (FindStartSequence(reader))
                {
                    // Načtení začátku MZF Těla pro získání DataSize
                    // byte[] mzfBodyStartBytes = reader.ReadBytes(7); // Předpokládáme, že prvních 7 bajtů obsahuje StartSign (4 bajty), MzfBodySign (1 bajt) a DataSize (2 bajty)
                    // ushort bodyDataSize = // BitConverter.ToUInt16(mzfBodyStartBytes, 5);

                    positions.Add(reader.BaseStream.Position);
                    body.StartSign = ExpectedStartSign.ToArray();
                    CRC_check(0xA5, true);
                    body.MzfBodySign = reader.ReadByte();
                    CRC_check(body.MzfBodySign);
                    body.DataSize = reader.ReadUInt16();
                    positions.Add(body.DataSize + 5);
                    CRC_check(body.DataSize);
                    body.MzfBody = ReadBytesExact(reader, body.DataSize, "QDF MZF file body");
                    body.TrailingData = Array.Empty<byte>();
                    for (int i = 0; i < body.DataSize; i++)
                    {
                        CRC_check(body.MzfBody[i]);
                    }
                    body.Crc = ExpectedCrc.ToArray();
                    readCRC = reader.ReadUInt16();
                    crc = CRC_check(readCRC);

                    if (crc != 0)
                    {
                        body.Crc = ZeroCrc;
                        throw new InvalidOperationException("Invalid CRC in QDF MZF Body");
                    }

                    if (!ValidateQDiskMzfBody(body))
                    {
                        throw new InvalidOperationException("Invlid QD/MZF Header");
                    }
                }
                else
                {
                    throw new InvalidOperationException("Missing data block after header block.");
                }

                if (header.MzfSize != body.DataSize)
                {
                    throw new InvalidDataException(
                        $"MZF size mismatch: header declares {header.MzfSize} bytes, body declares {body.DataSize} bytes.");
                }
            }
            else
            {
                throw new InvalidDataException("Missing QDF MZF header block.");
            }
            return (header, body);
        }

        public List<(MZQFileHeader, MZQFileBody)> ReadFile(string filePath)
        {
            byte[] image = File.ReadAllBytes(filePath);
            byte[] expectedHeaderBytes = Encoding.ASCII.GetBytes("-QD format-")
                .Concat(Enumerable.Repeat((byte)0xFF, 5)).ToArray();
            if (image.Length < expectedHeaderBytes.Length ||
                !image.AsSpan(0, expectedHeaderBytes.Length).SequenceEqual(expectedHeaderBytes))
            {
                throw new InvalidDataException("Invalid QDF container header.");
            }

            List<(MZQFileHeader, MZQFileBody)> mzfBlocks = SharpQdFrameCodec.DecodeByteStream(image);
            currentPosition = image.Length;
            totalLength = image.Length;
            bytesRemaining = 0;
            return mzfBlocks;
        }

        public void WriteBytesToStream(FileStream fileStream, byte val, long repeat)
        {
            for (long i = 0; i < repeat; i++)
            {
                fileStream.WriteByte(val);
            }
        }

        public void WriteQDFHeaderToFile(FileStream fileStream, byte fbCount)
        {
            byte[] QDFFileSignature = Encoding.ASCII.GetBytes("-QD format-")
                .Concat(Enumerable.Repeat((byte)0xFF, 5)).ToArray();
            fileStream.Write(QDFFileSignature, 0, QDFFileSignature.Length);

            WriteBytesToStream(fileStream, 0x00, 0x12EA - 16);
            WriteBytesToStream(fileStream, 0x16, 9);
            fileStream.Write(SharpQdFrameCodec.EncodeCountFrame(fbCount));

            WriteBytesToStream(fileStream, 0x16, 6);
            WriteBytesToStream(fileStream, 0x00, 2794);

            //header.Crc = Encoding.ASCII.GetBytes("CRC");
            //fileStream.Write(header.Crc, 0, header.Crc.Length);
        }

        public void WriteQDFFileHeaderToFile(FileStream fileStream, MZQFileHeader mzfHeader)
        {
            WriteBytesToStream(fileStream, 0x16, 10);
            fileStream.Write(SharpQdFrameCodec.EncodeHeaderFrame(mzfHeader));

            WriteBytesToStream(fileStream, 0x16, 7);
            WriteBytesToStream(fileStream, 0x00, 254);
        }

        public void WriteQDFFileBodyToFile(FileStream fileStream, MZQFileBody mzfBody)
        {
            WriteBytesToStream(fileStream, 0x16, 10);
            fileStream.Write(SharpQdFrameCodec.EncodeBodyFrame(mzfBody));

            WriteBytesToStream(fileStream, 0x16, 7);
            WriteBytesToStream(fileStream, 0x00, 256);
        }
    }
}
