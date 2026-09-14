namespace QDTool
{
    internal static class FeatureModePolicy
    {
        public const string OpenFilter =
            "All supported files|*.mzt;*.mzf;*.mzq;*.qdf;*.qd|QuickDisk image (*.qd)|*.qd|Quickdisk file (*.mzq)|*.mzq|Multiple files tape (*.mzt)|*.mzt|Single tape file (*.mzf)|*.mzf|Quickdisk file (*.qdf)|*.qdf|All files (*.*)|*.*";

        public static string GetSaveFilter(bool advanced) => advanced
            ? "Quickdisk file (*.qdf)|*.qdf|Quickdisk file (*.mzq)|*.mzq|QuickDisk - HxC (*.qd)|*.qd|QuickDisk - FlashFloppy (*.qd)|*.qd|QuickDisk - Sharp/MZ legacy (*.qd)|*.qd|Multiple files tape (*.mzt)|*.mzt|Single tape file (*.mzf)|*.mzf|LEP pulse file (*.lep)|*.lep|L16 pulse file (*.l16)|*.l16|Wave audio (*.wav)|*.wav|All files (*.*)|*.*"
            : "Quickdisk file (*.qdf)|*.qdf|Quickdisk file (*.mzq)|*.mzq|QuickDisk - HxC (*.qd)|*.qd|QuickDisk - FlashFloppy (*.qd)|*.qd|QuickDisk - Sharp/MZ legacy (*.qd)|*.qd|Multiple files tape (*.mzt)|*.mzt|Single tape file (*.mzf)|*.mzf|All files (*.*)|*.*";

        public static string GetExportFilter(bool advanced) => advanced
            ? "Single tape file (*.mzf)|*.mzf|LEP pulse file (*.lep)|*.lep|L16 pulse file (*.l16)|*.l16|Wave audio (*.wav)|*.wav"
            : "Single tape file (*.mzf)|*.mzf";
    }
}
