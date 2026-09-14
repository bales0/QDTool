using System;
using System.Collections.Generic;

namespace QDTool
{
    public enum QdImageFormat
    {
        Unknown,
        SharpLegacyLogical,
        HxcPhysical,
        FlashFloppyPhysical
    }

    internal sealed record QdReadResult(QdImageFormat Format, IReadOnlyList<TapeRecord> Records)
    {
        public TapeDocumentFormat DocumentFormat => Format switch
        {
            QdImageFormat.SharpLegacyLogical => TapeDocumentFormat.QdSharpLegacy,
            QdImageFormat.HxcPhysical => TapeDocumentFormat.QdHxc,
            QdImageFormat.FlashFloppyPhysical => TapeDocumentFormat.QdFlashFloppy,
            _ => throw new InvalidOperationException("Unknown QD image format has no document format.")
        };
    }
}
