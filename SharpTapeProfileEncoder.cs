using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace QDTool
{
    internal readonly record struct SharpPulseProfile(
        double ShortHighMicroseconds,
        double ShortLowMicroseconds,
        double LongHighMicroseconds,
        double LongLowMicroseconds);

    internal readonly record struct SharpTapeStage(
        byte[] Data,
        SharpPulseProfile Pulses,
        int LeaderShortPulses,
        int MarkLongPulses,
        int MarkShortPulses,
        int FinalMarkLongPulses,
        int TrailingPulses,
        bool TrailingPulseIsLong,
        bool InvertSignal,
        int DelayBeforeMilliseconds = 0);

    internal static class SharpTapeProfileEncoder
    {
        private static readonly SharpPulseProfile Mz800Normal = new(250, 250, 500, 500);
        private static readonly SharpPulseProfile Mz800Normal2 = new(136.125, 136.125, 276.5625, 276.5625);
        private static readonly SharpPulseProfile Mz800Normal3 = new(113.1875, 113.1875, 204.0625, 204.0625);
        private static readonly SharpPulseProfile Mz800Normal4 = new(112, 80, 176, 160);
        private static readonly SharpPulseProfile Mz700Normal = new(240, 264, 464, 494);
        private static readonly SharpPulseProfile Mz700Fast3 = new(80, 80, 160, 160);
        private static readonly SharpPulseProfile Ic2 = new(144, 112, 256, 224);
        private static readonly SharpPulseProfile Ic3 = new(112, 96, 224, 192);
        private static readonly SharpPulseProfile Ic4 = new(112, 80, 176, 160);
        private static readonly SharpPulseProfile Tc2 = new(144, 144, 288, 288);
        private static readonly SharpPulseProfile Tc3 = new(112, 112, 204, 204);
        private static readonly SharpPulseProfile Tc4 = Ic4;

        private static readonly byte[] IcLoader =
        [
            0x3E, 0x08, 0xD3, 0xCE, 0xCD, 0x3E, 0x07, 0x36,
            0x01, 0x97, 0x57, 0x5F, 0xCD, 0x08, 0x03, 0xCD,
            0xBE, 0x02, 0xD3, 0xE2, 0x1A, 0xD3, 0xE0, 0x12,
            0x13, 0xCB, 0x62, 0x28, 0xF5, 0x3E, 0xC3, 0x32,
            0x1F, 0x06, 0x21, 0x5C, 0x11, 0x22, 0x20, 0x06,
            0x2A, 0x08, 0x11, 0x7D, 0x32, 0x12, 0x05, 0x7C,
            0x32, 0x4B, 0x0A, 0x2A, 0x0A, 0x11, 0x22, 0x02,
            0x11, 0xCD, 0xF8, 0x04, 0x01, 0xCF, 0x06, 0xED,
            0x71, 0xD3, 0xE2, 0xDA, 0xAA, 0xE9, 0x21, 0x0A,
            0x11, 0xC3, 0x08, 0xED, 0xC5, 0x3A, 0x10, 0x11,
            0xEE, 0x0C, 0x32, 0x10, 0x11, 0x01, 0xCF, 0x06,
            0xED, 0x79, 0xC1, 0xC9, 0x31, 0x39, 0x38, 0x37
        ];

        private static readonly byte[] TcLoaderTemplate =
        [
            0x3E, 0x08, 0xD3, 0xCE, 0xE5, 0x21, 0x00, 0x00,
            0xD3, 0xE4, 0x7E, 0xD3, 0xE0, 0x77, 0x23, 0x7C,
            0xFE, 0x10, 0x20, 0xF4, 0x3A, 0x4B, 0xD4, 0x32,
            0x4B, 0x0A, 0x3A, 0x4C, 0xD4, 0x32, 0x12, 0x05,
            0x21, 0x4D, 0xD4, 0x11, 0x02, 0x11, 0x01, 0x0D,
            0x00, 0xED, 0xB0, 0xE1, 0x7C, 0xFE, 0xD4, 0x28,
            0x12, 0x2A, 0x04, 0x11, 0xD9, 0x21, 0x00, 0x12,
            0x22, 0x04, 0x11, 0xCD, 0x2A, 0x00, 0xD3, 0xE4,
            0xC3, 0x9A, 0xE9, 0xCD, 0x2A, 0x00, 0xD3, 0xE4,
            0xC3, 0x24, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00
        ];

        private static readonly byte[] TcTag = [0x5B, 0x96, 0xA5, 0x9D, 0x9A, 0xB7, 0x5D, 0x00];

        // Exact 1Z-009A QADCN table from MZ-SD2CMT2-Reborn.
        private static readonly byte[] Mz700Qadcn =
        [
            0xF0,0xF0,0xF0,0xF3,0xF0,0xF5,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,
            0xF0,0xC1,0xC2,0xC3,0xC4,0xC5,0xC6,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,
            0x00,0x61,0x62,0x63,0x64,0x65,0x66,0x67,0x68,0x69,0x6B,0x6A,0x2F,0x2A,0x2E,0x2D,
            0x20,0x21,0x22,0x23,0x24,0x25,0x26,0x27,0x28,0x29,0x4F,0x2C,0x51,0x2B,0x57,0x49,
            0x55,0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0A,0x0B,0x0C,0x0D,0x0E,0x0F,
            0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,0x1A,0x52,0x59,0x54,0x50,0x45,
            0xC7,0xC8,0xC9,0xCA,0xCB,0xCC,0xCD,0xCE,0xCF,0xDF,0xE7,0xE8,0xE9,0xEA,0xEC,0xED,
            0xD0,0xD1,0xD2,0xD3,0xD4,0xD5,0xD6,0xD7,0xD8,0xD9,0xDA,0xDB,0xDC,0xDD,0xDE,0xC0,
            0x80,0xBD,0x9D,0xB1,0xB5,0xB9,0xB4,0x9E,0xB2,0xB6,0xBA,0xBE,0x9F,0xB3,0xB7,0xBB,
            0xBF,0xA3,0x85,0xA4,0xA5,0xA6,0x94,0x87,0x88,0x9C,0x82,0x98,0x84,0x92,0x90,0x83,
            0x91,0x81,0x9A,0x97,0x93,0x95,0x89,0xA1,0xAF,0x8B,0x86,0x96,0xA2,0xAB,0xAA,0x8A,
            0x8E,0xB0,0xAD,0x8D,0xA7,0xA8,0xA9,0x8F,0x8C,0xAE,0xAC,0x9B,0xA0,0x99,0xBC,0xB8,
            0x40,0x3B,0x3A,0x70,0x3C,0x71,0x5A,0x3D,0x43,0x56,0x3F,0x1E,0x4A,0x1C,0x5D,0x3E,
            0x5C,0x1F,0x5F,0x5E,0x37,0x7B,0x7F,0x36,0x7A,0x7E,0x33,0x4B,0x4C,0x1D,0x6C,0x5B,
            0x78,0x41,0x35,0x34,0x74,0x30,0x38,0x75,0x39,0x4D,0x6F,0x6E,0x32,0x77,0x76,0x72,
            0x73,0x47,0x7C,0x53,0x31,0x4E,0x6D,0x48,0x46,0x7D,0x44,0x1B,0x58,0x79,0x42,0x60
        ];

        public static IReadOnlyList<SharpTapeStage> Build(TapeRecord record, SharpTapeMachine implicitMachine)
        {
            ArgumentNullException.ThrowIfNull(record);
            byte[] header = record.GetSerializedHeader();
            byte[] body = record.Body.MzfBody;
            TapeProfile profile = record.Profile;
            if (profile == TapeProfile.Normal1_1 &&
                record.MetadataOrigin == MetadataOrigin.Implicit &&
                implicitMachine == SharpTapeMachine.Mz700)
            {
                profile = TapeProfile.Mz700_1_1;
            }

            return profile switch
            {
                TapeProfile.Normal1_1 => BuildConventional(header, body, Mz800Normal, 6344, 6344),
                TapeProfile.Normal1_2 => BuildConventional(header, body, Mz800Normal2, 11239, 11239),
                TapeProfile.Normal1_3 => BuildConventional(header, body, Mz800Normal3, 15130, 15130),
                TapeProfile.Normal1_4 => BuildConventional(header, body, Mz800Normal4, 11000, 5500),
                TapeProfile.Mz700_1_1 => BuildConventional(header, body, Mz700Normal, 22000, 11000),
                TapeProfile.Mz700_1_3 => BuildMz700Fast3(header, body),
                TapeProfile.Ic1_2 => BuildIc(header, body, Ic2, 0x20),
                TapeProfile.Ic1_3 => BuildIc(header, body, Ic3, 0x16),
                TapeProfile.Ic1_4 => BuildIc(header, body, Ic4, 0x11),
                TapeProfile.Tc1_2 => BuildTc(header, body, Tc2, 0x29, 11239),
                TapeProfile.Tc1_3 => BuildTc(header, body, Tc3, 0x1B, 15130),
                TapeProfile.Tc1_4 => BuildTc(header, body, Tc4, 0x16, 5500),
                TapeProfile.Ultra or TapeProfile.UltraMz800 or TapeProfile.UltraMz700 =>
                    throw new InvalidOperationException(
                        $"{TapeProfileNames.ToDisplayName(profile)} uses a live WRITE/SENSE handshake and cannot be exported as a static WAV/LEP/L16 waveform."),
                _ => throw new ArgumentOutOfRangeException(nameof(record.Profile))
            };
        }

        private static IReadOnlyList<SharpTapeStage> BuildConventional(
            byte[] header, byte[] body, SharpPulseProfile pulses, int headerLeader, int dataLeader) =>
        [
            HeaderStage(header, pulses, headerLeader),
            DataStage(body, pulses, dataLeader)
        ];

        private static IReadOnlyList<SharpTapeStage> BuildIc(
            byte[] originalHeader, byte[] body, SharpPulseProfile turboPulses, byte speedByte)
        {
            ValidateLoaderInput(originalHeader, body, requireMachineCode: true);
            if (RangesOverlap(0x1110, IcLoader.Length, ReadU16(originalHeader, 20), body.Length))
            {
                throw new InvalidOperationException("The IC loader at $1110 overlaps the payload destination range.");
            }

            byte[] header = (byte[])originalHeader.Clone();
            header[0] = 0xBB;
            WriteU16(header, 18, 0);
            WriteU16(header, 20, 0x1200);
            WriteU16(header, 22, 0x1110);
            header[24] = 0x01;
            header[25] = speedByte;
            Array.Copy(originalHeader, 18, header, 26, 6);
            Array.Copy(IcLoader, 0, header, 32, IcLoader.Length);
            return
            [
                HeaderStage(header, Mz800Normal, 6344),
                DataStage(body, turboPulses, 5500, invertSignal: true, delayBeforeMilliseconds: 345)
            ];
        }

        private static IReadOnlyList<SharpTapeStage> BuildTc(
            byte[] originalHeader,
            byte[] body,
            SharpPulseProfile turboPulses,
            byte speedByte,
            int turboLeader)
        {
            ValidateLoaderInput(originalHeader, body, requireMachineCode: false);
            if (RangesOverlap(0xD400, TcLoaderTemplate.Length, ReadU16(originalHeader, 20), body.Length))
            {
                throw new InvalidOperationException("The TC loader at $D400 overlaps the payload destination range.");
            }

            byte[] header = (byte[])originalHeader.Clone();
            WriteU16(header, 18, (ushort)TcLoaderTemplate.Length);
            WriteU16(header, 20, 0xD400);
            WriteU16(header, 22, 0xD400);
            Array.Copy(TcTag, 0, header, 24, TcTag.Length);

            byte[] loader = (byte[])TcLoaderTemplate.Clone();
            loader[0x4B] = speedByte;
            loader[0x4C] = originalHeader[0];
            Array.Copy(originalHeader, 18, loader, 0x4D, 13);
            return
            [
                HeaderStage(header, Mz800Normal, 6344, invertSignal: true),
                DataStage(loader, Mz800Normal, 6344, invertSignal: true, trailingPulses: 98, trailingLong: false),
                DataStage(body, turboPulses, turboLeader, invertSignal: true, trailingPulses: 98, trailingLong: false, delayBeforeMilliseconds: 110)
            ];
        }

        private static IReadOnlyList<SharpTapeStage> BuildMz700Fast3(byte[] originalHeader, byte[] body)
        {
            ValidateLoaderInput(originalHeader, body, requireMachineCode: false);
            byte[] name = originalHeader[1..18];
            int runtimeSize = 73 + DisplayedNameLength(name);
            ushort loadAddress = ReadU16(originalHeader, 20);
            ushort runtimeAddress;
            if (!RangesOverlap(0x1108, runtimeSize, loadAddress, body.Length) && StageIsEncodable(0x1108, runtimeSize))
            {
                runtimeAddress = 0x1108;
            }
            else if (!RangesOverlap(0xC000, runtimeSize, loadAddress, body.Length) && StageIsEncodable(0xC000, runtimeSize))
            {
                runtimeAddress = 0xC000;
            }
            else
            {
                throw new InvalidOperationException("No safe RAM location is available for the MZ700 FAST3 loader.");
            }

            byte[] header = BuildMz700Fast3Header(originalHeader, runtimeAddress, runtimeSize);
            return
            [
                HeaderStage(header, Mz800Normal, 6344),
                DataStage(body, Mz700Fast3, 5500, delayBeforeMilliseconds: 400)
            ];
        }

        private static byte[] BuildMz700Fast3Header(byte[] originalHeader, ushort runtimeAddress, int runtimeSize)
        {
            byte[] stage = BuildMz700Stage(runtimeAddress, runtimeSize);
            byte[] header = new byte[128];
            header[0] = 0x01;
            for (int index = 0; index < stage.Length; index++)
            {
                header[index + 1] = EncodeQadcn(stage[index]);
            }
            header[15] = 0x0D;
            WriteU16(header, 22, 0xD080);

            int offset = 24;
            header[offset++] = 0xAF;
            header[offset++] = 0x21; WriteU16(header, offset, 0xD000); offset += 2;
            header[offset++] = 0x77;
            header[offset++] = 0x11; WriteU16(header, offset, 0xD001); offset += 2;
            header[offset++] = 0x01; WriteU16(header, offset, 0x03E7); offset += 2;
            header[offset++] = 0xED; header[offset++] = 0xB0;

            int statusLength = DisplayedNameLength(originalHeader.AsSpan(1, 17));
            ushort statusAddress = checked((ushort)(runtimeAddress + 73));
            header[offset++] = 0x21; WriteU16(header, offset, statusAddress); offset += 2;
            header[offset++] = 0x11; WriteU16(header, offset, 0xD000); offset += 2;
            header[offset++] = 0x01; WriteU16(header, offset, (ushort)statusLength); offset += 2;
            header[offset++] = 0xED; header[offset++] = 0xB0;

            header[offset++] = 0x16; header[offset++] = 0xE4;
            header[offset++] = 0x1E; header[offset++] = 0xE0;
            header[offset++] = 0x21; WriteU16(header, offset, 0); offset += 2;
            header[offset++] = 0x4A; header[offset++] = 0xED; header[offset++] = 0x79;
            header[offset++] = 0x7E; header[offset++] = 0x4B; header[offset++] = 0xED; header[offset++] = 0x79;
            header[offset++] = 0x77; header[offset++] = 0x23; header[offset++] = 0xCB; header[offset++] = 0x64;
            header[offset++] = 0x28; header[offset++] = 0xF3;

            header[offset++] = 0x3E; header[offset++] = 0x15;
            header[offset++] = 0x32; WriteU16(header, offset, 0x0A4B); offset += 2;
            header[offset++] = 0x21; WriteU16(header, offset, ReadU16(originalHeader, 18)); offset += 2;
            header[offset++] = 0x22; WriteU16(header, offset, 0x1102); offset += 2;
            header[offset++] = 0x21; WriteU16(header, offset, ReadU16(originalHeader, 20)); offset += 2;
            header[offset++] = 0x22; WriteU16(header, offset, 0x1104); offset += 2;
            header[offset++] = 0xCD; WriteU16(header, offset, 0x002A); offset += 2;
            header[offset++] = 0xDA; WriteU16(header, offset, 0x00FE); offset += 2;
            header[offset++] = 0xAF; header[offset++] = 0xD3; header[offset++] = 0xE4;
            header[offset++] = 0xC3; WriteU16(header, offset, ReadU16(originalHeader, 22)); offset += 2;

            ReadOnlySpan<byte> name = originalHeader.AsSpan(1, 17);
            int trimmedLength = TrimmedNameLength(name);
            if (trimmedLength == 0)
            {
                header[offset++] = Mz700Qadcn[0x20];
            }
            else
            {
                for (int index = 0; index < trimmedLength; index++)
                {
                    header[offset++] = Mz700Qadcn[name[index]];
                }
            }

            if (offset - 24 != runtimeSize)
            {
                throw new InvalidOperationException("Internal MZ700 FAST3 runtime size mismatch.");
            }
            return header;
        }

        private static byte[] BuildMz700Stage(ushort runtimeAddress, int runtimeSize)
        {
            byte[] stage = new byte[14];
            if (runtimeAddress == 0x1108)
            {
                stage[0] = 0xC3;
                WriteU16(stage, 1, 0x1108);
                return stage;
            }
            stage[0] = 0x21; WriteU16(stage, 1, 0x1108);
            stage[3] = 0x11; WriteU16(stage, 4, runtimeAddress);
            stage[6] = 0x01; WriteU16(stage, 7, (ushort)runtimeSize);
            stage[9] = 0xED; stage[10] = 0xB0;
            stage[11] = 0xC3; WriteU16(stage, 12, runtimeAddress);
            return stage;
        }

        private static bool StageIsEncodable(ushort runtimeAddress, int runtimeSize)
        {
            foreach (byte value in BuildMz700Stage(runtimeAddress, runtimeSize))
            {
                try { _ = EncodeQadcn(value); }
                catch (InvalidOperationException) { return false; }
            }
            return true;
        }

        private static byte EncodeQadcn(byte displayByte)
        {
            for (int source = 0; source < Mz700Qadcn.Length; source++)
            {
                if (source != 0x0D && Mz700Qadcn[source] == displayByte)
                {
                    return (byte)source;
                }
            }
            throw new InvalidOperationException($"MZ700 FAST3 stage byte ${displayByte:X2} is not QADCN-encodable.");
        }

        private static int TrimmedNameLength(ReadOnlySpan<byte> name)
        {
            int length = 0;
            while (length < name.Length && name[length] is not 0x00 and not 0x0D) length++;
            while (length > 0 && name[length - 1] == 0x20) length--;
            return length;
        }

        private static int DisplayedNameLength(ReadOnlySpan<byte> name) => Math.Max(1, TrimmedNameLength(name));

        private static void ValidateLoaderInput(byte[] header, byte[] body, bool requireMachineCode)
        {
            if (header[0] is not 0x01 and not 0x76)
            {
                throw new InvalidOperationException($"Loader profile does not support MZF file type ${header[0]:X2}.");
            }
            if (requireMachineCode && header[0] != 0x01)
            {
                throw new InvalidOperationException("The IC loader can be generated only for an OBJ (machine-code) record.");
            }
            if (body.Length == 0 || ReadU16(header, 18) != body.Length)
            {
                throw new InvalidOperationException("The loader requires a non-empty body matching the MZF header size.");
            }
            if ((uint)ReadU16(header, 20) + body.Length > 0x10000)
            {
                throw new InvalidOperationException("The payload destination range exceeds the 16-bit address space.");
            }
        }

        private static bool RangesOverlap(int leftStart, int leftLength, int rightStart, int rightLength) =>
            leftStart < rightStart + rightLength && rightStart < leftStart + leftLength;

        private static SharpTapeStage HeaderStage(
            byte[] data, SharpPulseProfile pulses, int leader, bool invertSignal = false) =>
            new(data, pulses, leader, 40, 40, 2, 2, true, invertSignal);

        private static SharpTapeStage DataStage(
            byte[] data,
            SharpPulseProfile pulses,
            int leader,
            bool invertSignal = false,
            int trailingPulses = 2,
            bool trailingLong = true,
            int delayBeforeMilliseconds = 0) =>
            new(data, pulses, leader, 20, 20, 2, trailingPulses, trailingLong, invertSignal, delayBeforeMilliseconds);

        private static ushort ReadU16(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

        private static void WriteU16(byte[] data, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
    }
}
