namespace QDTool
{
    internal sealed record QuickDiskPhysicalProfile(
        QdImageFormat Format,
        int TrackLength,
        int StoredTrackLength,
        int WindowStart,
        int WindowEnd,
        byte BlankFiller,
        uint BitRate)
    {
        public const int TrackListOffset = 0x200;
        public const int TrackDataOffset = 0x400;
        public const int FileSize = 0x32000;

        public static QuickDiskPhysicalProfile Hxc { get; } = new(
            QdImageFormat.HxcPhysical, 0x31C00, 0x31C00, 0x3200, 0x25600, 0x01, 203388);

        public static QuickDiskPhysicalProfile FlashFloppy { get; } = new(
            QdImageFormat.FlashFloppyPhysical, 0x31A99, 0x31C00, 0x31A9, 0x2B745, 0x11, 0);

        public static QuickDiskPhysicalProfile For(QdImageFormat format) => format switch
        {
            QdImageFormat.HxcPhysical => Hxc,
            QdImageFormat.FlashFloppyPhysical => FlashFloppy,
            _ => throw new System.ArgumentOutOfRangeException(nameof(format), "A physical QD output format is required.")
        };
    }
}
