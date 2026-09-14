using System;
using System.Buffers.Binary;
using System.IO;

namespace QDTool
{
    internal sealed record QuickDiskTrackDescriptor(uint Offset, uint Length, uint WindowStart, uint WindowEnd);

    internal sealed record QuickDiskContainer(
        QdImageFormat Format,
        QuickDiskTrackDescriptor Descriptor,
        byte[] Track,
        uint BitRate);

    internal static class HxcFlashFloppyQdContainer
    {
        private const int DescriptorLength = 16;

        public static QuickDiskContainer Parse(ReadOnlySpan<byte> image, QdImageFormat format)
        {
            if (format is not (QdImageFormat.HxcPhysical or QdImageFormat.FlashFloppyPhysical))
            {
                throw new ArgumentException("A physical HxC or FlashFloppy QD format is required.", nameof(format));
            }

            uint descriptorOffset;
            uint bitRate = 0;
            if (format == QdImageFormat.HxcPhysical)
            {
                if (image.Length < 40 || !image[..8].SequenceEqual("HXCQDDRV"u8))
                {
                    throw new InvalidDataException("Invalid HxC QuickDisk container header.");
                }

                uint tracks = ReadUInt32(image, 12);
                uint sides = ReadUInt32(image, 16);
                if (tracks != 1 || sides != 1)
                {
                    throw new NotSupportedException($"Unsupported QuickDisk geometry: {tracks} track(s), {sides} side(s); Sharp MZ requires 1x1.");
                }
                uint trackEncoding = ReadUInt32(image, 20);
                if (trackEncoding != 0)
                {
                    throw new NotSupportedException($"Unsupported QuickDisk track encoding: {trackEncoding}.");
                }
                bitRate = ReadUInt32(image, 28);
                descriptorOffset = ReadUInt32(image, 36);
                ulong tableSize = (ulong)tracks * sides * DescriptorLength;
                if (descriptorOffset > image.Length || (ulong)descriptorOffset + tableSize > (ulong)image.Length)
                {
                    throw new InvalidDataException("Invalid HxC track descriptor table offset.");
                }
            }
            else
            {
                if (image.Length < 5 || image[3] != (byte)'Q' || image[4] != (byte)'D')
                {
                    throw new InvalidDataException("Invalid FlashFloppy QuickDisk container header.");
                }
                descriptorOffset = QuickDiskPhysicalProfile.TrackListOffset;
                if ((ulong)descriptorOffset + DescriptorLength > (ulong)image.Length)
                {
                    throw new InvalidDataException("Invalid FlashFloppy track descriptor table offset.");
                }
            }

            int descriptorIndex = checked((int)descriptorOffset);
            var descriptor = new QuickDiskTrackDescriptor(
                ReadUInt32(image, descriptorIndex),
                ReadUInt32(image, descriptorIndex + 4),
                ReadUInt32(image, descriptorIndex + 8),
                ReadUInt32(image, descriptorIndex + 12));
            ValidateDescriptor(descriptor, image.Length);

            byte[] track = image.Slice(checked((int)descriptor.Offset), checked((int)descriptor.Length)).ToArray();
            return new QuickDiskContainer(format, descriptor, track, bitRate);
        }

        public static byte[] Write(ReadOnlySpan<byte> track, QuickDiskPhysicalProfile profile)
        {
            if (track.Length != profile.StoredTrackLength)
            {
                throw new ArgumentException("Physical track storage length does not match its QD profile.", nameof(track));
            }

            byte[] image = new byte[QuickDiskPhysicalProfile.FileSize];
            if (profile.Format == QdImageFormat.HxcPhysical)
            {
                "HXCQDDRV"u8.CopyTo(image);
                WriteUInt32(image, 8, 0);
                WriteUInt32(image, 12, 1);
                WriteUInt32(image, 16, 1);
                WriteUInt32(image, 20, 0);
                WriteUInt32(image, 24, 0);
                WriteUInt32(image, 28, profile.BitRate);
                WriteUInt32(image, 32, 0);
                WriteUInt32(image, 36, QuickDiskPhysicalProfile.TrackListOffset);
            }
            else if (profile.Format == QdImageFormat.FlashFloppyPhysical)
            {
                image[3] = (byte)'Q';
                image[4] = (byte)'D';
            }
            else
            {
                throw new ArgumentException("A physical QD output profile is required.", nameof(profile));
            }

            int descriptor = QuickDiskPhysicalProfile.TrackListOffset;
            WriteUInt32(image, descriptor, QuickDiskPhysicalProfile.TrackDataOffset);
            WriteUInt32(image, descriptor + 4, checked((uint)profile.TrackLength));
            WriteUInt32(image, descriptor + 8, checked((uint)profile.WindowStart));
            WriteUInt32(image, descriptor + 12, checked((uint)profile.WindowEnd));
            track.CopyTo(image.AsSpan(QuickDiskPhysicalProfile.TrackDataOffset));
            return image;
        }

        private static void ValidateDescriptor(QuickDiskTrackDescriptor descriptor, int imageLength)
        {
            if (descriptor.Offset < QuickDiskPhysicalProfile.TrackDataOffset)
            {
                throw new InvalidDataException("Invalid track descriptor: track data offset must be at least 0x400.");
            }
            if (descriptor.Length == 0)
            {
                throw new InvalidDataException("Invalid track descriptor: track length is zero.");
            }
            if ((ulong)descriptor.Offset + descriptor.Length > (ulong)imageLength)
            {
                throw new InvalidDataException("Invalid track descriptor: track data extends beyond the file.");
            }
            if (descriptor.WindowStart > descriptor.WindowEnd || descriptor.WindowEnd > descriptor.Length)
            {
                throw new InvalidDataException("Invalid track descriptor: the QuickDisk data window is outside the track.");
            }
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));

        private static void WriteUInt32(Span<byte> bytes, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(offset, 4), value);
    }
}
