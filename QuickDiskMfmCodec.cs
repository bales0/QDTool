using System;
using System.Collections.Generic;

namespace QDTool
{
    internal static class QuickDiskMfmCodec
    {
        public static byte[] Encode(ReadOnlySpan<byte> data, bool previousDataBit = false)
        {
            byte[] encoded = new byte[data.Length * 2];
            int outputBit = 0;
            bool previous = previousDataBit;
            foreach (byte value in data)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    bool current = ((value >> bit) & 1) != 0;
                    bool clock = !previous && !current;
                    SetLsbFirstBit(encoded, outputBit++, clock);
                    SetLsbFirstBit(encoded, outputBit++, current);
                    previous = current;
                }
            }
            return encoded;
        }

        public static byte[] Decode(ReadOnlySpan<byte> track, int dataCellPhase)
        {
            if (dataCellPhase is < 0 or > 15)
            {
                throw new ArgumentOutOfRangeException(nameof(dataCellPhase));
            }

            int bitCount = track.Length * 8;
            int byteCount = Math.Max(0, (bitCount - dataCellPhase + 1) / 16);
            byte[] decoded = new byte[byteCount];
            for (int index = 0; index < byteCount; index++)
            {
                byte value = 0;
                int firstCell = dataCellPhase + index * 16;
                for (int bit = 0; bit < 8; bit++)
                {
                    if (GetLsbFirstBit(track, firstCell + bit * 2))
                    {
                        value |= (byte)(1 << bit);
                    }
                }
                decoded[index] = value;
            }
            return decoded;
        }

        public static IReadOnlyList<SharpQdFrame> FindSharpFrames(ReadOnlySpan<byte> track)
        {
            var result = new List<SharpQdFrame>();
            for (int phase = 0; phase < 16; phase++)
            {
                byte[] decoded = Decode(track, phase);
                result.AddRange(SharpQdFrameCodec.FindFrames(decoded, phase));
            }
            return result;
        }

        public static bool ContainsPlausibleSharpFrame(ReadOnlySpan<byte> track)
        {
            for (int phase = 0; phase < 16; phase++)
            {
                if (SharpQdFrameCodec.ContainsPlausibleFrame(Decode(track, phase)))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool GetLsbFirstBit(ReadOnlySpan<byte> bytes, int position) =>
            (bytes[position >> 3] & (1 << (position & 7))) != 0;

        private static void SetLsbFirstBit(Span<byte> bytes, int position, bool value)
        {
            if (value)
            {
                bytes[position >> 3] |= (byte)(1 << (position & 7));
            }
        }
    }
}
