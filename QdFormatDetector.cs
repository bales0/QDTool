using System;

namespace QDTool
{
    internal static class QdFormatDetector
    {
        private static ReadOnlySpan<byte> HxcSignature => "HXCQDDRV"u8;

        public static QdImageFormat Detect(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= HxcSignature.Length && bytes[..HxcSignature.Length].SequenceEqual(HxcSignature))
            {
                return QdImageFormat.HxcPhysical;
            }

            if (bytes.Length >= 5 && bytes[3] == (byte)'Q' && bytes[4] == (byte)'D')
            {
                return QdImageFormat.FlashFloppyPhysical;
            }

            if (bytes.Length >= 8 &&
                bytes[0] == 0x00 && bytes[1] == 0x16 && bytes[2] == 0x16 && bytes[3] == 0xA5 &&
                bytes[5] == (byte)'C' && bytes[6] == (byte)'R' && bytes[7] == (byte)'C')
            {
                return QdImageFormat.SharpLegacyLogical;
            }

            return QdImageFormat.Unknown;
        }
    }
}
