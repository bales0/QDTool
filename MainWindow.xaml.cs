using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using static QDTool.Utility;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MZQHeader
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public byte[] StartSign; // 4 bytes, 0x00, 0x16, 0x16, 0xa5
    public byte FileBlocksCount;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public byte[] Crc; // 3 bytes, C, R, C
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MZQFileHeader
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public byte[] StartSign; // 4 bytes, 0x00, 0x16, 0x16, 0xa5
    public byte MzfHeaderSign; // 0x00
    public ushort DataSize; // 0x40, 0x00 = 64 bytes
    public byte MzfFtype;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] MzfFname; // 16 bytes
    public byte MzfFnameEnd; // 0x0d
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public byte[] Unused1; // 2 bytes, 0x00
    public ushort MzfSize;
    public ushort MzfStart;
    public ushort MzfExec;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 104)]
    public byte[] MzfHeaderDescription; // MZQ has 38 bytes only, first 38 bytes from mzf description
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public byte[] Crc; // 3 bytes, C, R, C
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MZQFileBody
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public byte[] StartSign; // 4 bytes, 0x00, 0x16, 0x16, 0xa5
    public byte MzfBodySign; // 0x05
    public ushort DataSize; // body_size
    // Zde může být potřeba dynamické alokace pro MzfBody v závislosti na skutečné velikosti
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 65535)]
    public byte[] MzfBody; // body [body_size], maximum size 65535 bytes
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public byte[] Crc; // 3 bytes, C, R, C
    public byte[] TrailingData; // optional MZF/MZT block-alignment bytes beyond the declared body size
}

public class MzfDisplayData
{
    public string MzfFtypeName { get; set; } = string.Empty;
    public string MzfFname { get; set; } = string.Empty;
    public ushort MzfSize { get; set; }
    public string MzfSizeHex
    {
        get { return $"0x{MzfSize:X4}"; }
    }
    public string MzfStartHex { get; set; } = string.Empty;
    public string MzfExecHex { get; set; } = string.Empty;
    public string MzfHeaderDescription { get; set; } = string.Empty;
    public string TrailingData { get; set; } = string.Empty;
    public string Profile { get; set; } = string.Empty;
    public string MetadataOrigin { get; set; } = string.Empty;
}

namespace QDTool
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int MaxQuickDiskFiles = byte.MaxValue / 2;
        private const long QdfImageSize = 81936;
        private const long QdfHeaderSize = 7655;
        private const long QdfFileOverhead = 620;

        private static readonly HashSet<string> ReservedWindowsFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        private readonly TapeDocument document = new();
        private List<TapeRecord> mzfBlocks => document.Records;
        private string actFileName = string.Empty;
        private bool AdvancedFeaturesEnabled => advancedFeaturesCheckBox.IsChecked == true;

        public ObservableCollection<MzfDisplayData> MzfDisplayDataCollection { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            MzfDisplayDataCollection = new ObservableCollection<MzfDisplayData>();
            MzfDataGrid.ItemsSource = MzfDisplayDataCollection;
            this.Title = "QDTool";
            viewButton.IsEnabled = false; // Zakážem některá tlačítka při spuštění
            moveUpButton.IsEnabled = false;
            moveDownButton.IsEnabled = false;
            exportButton.IsEnabled = false;
            exportAllButton.IsEnabled = false;
            deleteButton.IsEnabled = false;
            clearAllButton.IsEnabled = false;
            saveButton.IsEnabled = false;
            editProfileButton.IsEnabled = false;
            ApplyFeatureMode();
        }

        private static bool TryValidateBlock(
            TapeRecord block,
            int index,
            out string error)
        {
            MZQFileHeader header = block.Header;
            MZQFileBody body = block.Body;

            if (header.StartSign is null || header.StartSign.Length != 4 ||
                header.MzfFname is null || header.MzfFname.Length != 16 ||
                header.Unused1 is null || header.Unused1.Length != 2 ||
                header.MzfHeaderDescription is null || header.MzfHeaderDescription.Length != 104 ||
                header.Crc is null || header.Crc.Length != 3 ||
                body.StartSign is null || body.StartSign.Length != 4 ||
                body.MzfBody is null || body.Crc is null || body.Crc.Length != 3)
            {
                error = $"File {index + 1} has an invalid or incomplete structure.";
                return false;
            }

            if (body.MzfBody.Length != body.DataSize)
            {
                error = $"File {index + 1} declares {body.DataSize} data bytes but contains {body.MzfBody.Length}.";
                return false;
            }

            if (header.MzfSize != body.DataSize)
            {
                error = $"File {index + 1} has inconsistent header ({header.MzfSize}) and body ({body.DataSize}) sizes.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private bool TryValidateAllBlocks(out string error)
        {
            if (mzfBlocks.Count == 0)
            {
                error = "There are no files to save.";
                return false;
            }

            for (int i = 0; i < mzfBlocks.Count; i++)
            {
                if (!TryValidateBlock(mzfBlocks[i], i, out error))
                {
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private bool TryValidateOutputFormat(string extension, out string error)
        {
            if ((extension == ".mzq" || extension == ".qdf") && mzfBlocks.Count > MaxQuickDiskFiles)
            {
                error = $"The {extension.ToUpperInvariant()} format supports at most {MaxQuickDiskFiles} files.";
                return false;
            }

            if (extension == ".qdf")
            {
                long requiredSize = QdfHeaderSize + mzfBlocks.Sum(block => QdfFileOverhead + block.Body.DataSize);
                if (requiredSize > QdfImageSize)
                {
                    error = $"The selected files need {requiredSize} bytes, but a QDF image can contain only {QdfImageSize} bytes.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private static string SanitizeExportFileName(string fileName)
        {
            char[] invalidCharacters = System.IO.Path.GetInvalidFileNameChars();
            string sanitized = new string(fileName
                .Select(character => invalidCharacters.Contains(character) ? '_' : character)
                .ToArray())
                .Trim()
                .TrimEnd('.');

            if (string.IsNullOrWhiteSpace(sanitized) || sanitized == "." || sanitized == "..")
            {
                sanitized = "unnamed";
            }

            string firstNameSegment = sanitized.Split('.')[0];
            if (ReservedWindowsFileNames.Contains(firstNameSegment))
            {
                sanitized = "_" + sanitized;
            }

            return sanitized;
        }

        private static string GetAvailableExportPath(
            string exportPath,
            string fileName,
            HashSet<string> reservedPaths)
        {
            string safeName = SanitizeExportFileName(fileName);

            for (int suffix = 1; ; suffix++)
            {
                string candidateName = suffix == 1
                    ? $"{safeName}.mzf"
                    : $"{safeName}_{suffix}.mzf";
                string candidatePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(exportPath, candidateName));

                if (!File.Exists(candidatePath) && reservedPaths.Add(candidatePath))
                {
                    return candidatePath;
                }
            }
        }

        private SharpTapeMachine GetSelectedTapeMachine()
        {
            return tapeProfileComboBox.SelectedIndex == 1
                ? SharpTapeMachine.Mz700
                : SharpTapeMachine.Mz800;
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy; // Změňte ukazatel, aby uživatel věděl, že soubor může být zde upuštěn
            else
                e.Effects = DragDropEffects.None; // Jinak neumožněte drop
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);

                if (files != null && files.Length > 0)
                {
                    bool bindAsCurrent = mzfBlocks.Count == 0;
                    foreach (var file in files)
                    {
                        if (AddFile(file, bindAsCurrent))
                        {
                            bindAsCurrent = false;
                        }
                    }
                    saveButton.IsEnabled = true;
                    exportAllButton.IsEnabled = true;
                    clearAllButton.IsEnabled = true;
                    UpdateStatus();
                }
            }
        }
        private void LoadDataToGrid(IEnumerable<TapeRecord> records)
        {
            foreach (TapeRecord record in records)
            {
                MZQFileHeader header = record.Header;
                var displayData = new MzfDisplayData
                {
                    MzfFtypeName = ConvertFtypeToDescription(header.MzfFtype),
                    MzfFname = ConvertMzfNameToASCIIString(header.MzfFname),
                    MzfSize = header.MzfSize,
                    MzfStartHex = $"0x{header.MzfStart:X4}",
                    MzfExecHex = $"0x{header.MzfExec:X4}",
                    MzfHeaderDescription = ConvertMzfNameToASCIIString(record.DescriptionRaw),
                    TrailingData = $"{record.Body.TrailingData?.Length ?? 0} B",
                    Profile = TapeProfileNames.ToDisplayName(record.Profile),
                    MetadataOrigin = record.MetadataOrigin.ToString()
                };
                MzfDisplayDataCollection.Add(displayData);
            }
        }

        private void UpdateStatus()
        {
            long declaredSize = 0;
            long trailingSize = 0;
            long containerTrailingSize = document.ContainerTrailingData.Length;
            long sizeOnQDF = QdfHeaderSize;
            long sizeOnMZQ = 8;

            foreach (TapeRecord record in mzfBlocks)
            {
                MZQFileBody body = record.Body;
                declaredSize += body.DataSize;
                trailingSize += body.TrailingData?.Length ?? 0;
                sizeOnQDF += QdfFileOverhead + body.DataSize;
                sizeOnMZQ += 84 + body.DataSize;
            }

            long occupiedSize = declaredSize + (AdvancedFeaturesEnabled ? trailingSize + containerTrailingSize : 0);
            string trailingInfo = AdvancedFeaturesEnabled && (trailingSize > 0 || containerTrailingSize > 0)
                ? $" ({declaredSize} declared + {trailingSize} record trailing + {containerTrailingSize} container trailing)"
                : string.Empty;
            infoText.Content = $"Total {mzfBlocks.Count} files occupy {occupiedSize} bytes{trailingInfo}, est. {(float)sizeOnQDF / 819.36:F0}% of QDF or {(float)sizeOnMZQ / 614.71:F0}% of MZQ.";
        }

        private void AdvancedFeaturesCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
            {
                ApplyFeatureMode();
                UpdateStatus();
            }
        }

        private void ApplyFeatureMode()
        {
            Visibility advancedVisibility = AdvancedFeaturesEnabled ? Visibility.Visible : Visibility.Collapsed;
            tapeProfileLabel.Visibility = advancedVisibility;
            tapeProfileComboBox.Visibility = advancedVisibility;
            trailingColumn.Visibility = advancedVisibility;
            profileColumn.Visibility = advancedVisibility;
            metadataOriginColumn.Visibility = advancedVisibility;
            editProfileButton.Visibility = advancedVisibility;
            UpdateAdvancedActionState();
        }

        private void UpdateAdvancedActionState()
        {
            int selectedIndex = MzfDataGrid.SelectedIndex;
            editProfileButton.IsEnabled = AdvancedFeaturesEnabled &&
                selectedIndex >= 0 && selectedIndex < mzfBlocks.Count;

        }

        private bool TryChooseTapeSaveOptions(
            string mainPath,
            TapeDocumentFormat format,
            int trailingBytes,
            out bool preserveTrailing,
            out bool generateSidecar)
        {
            preserveTrailing = false;
            generateSidecar = false;
            if (!AdvancedFeaturesEnabled)
            {
                return true;
            }

            string sidecarPath = SidecarService.GetSidecarPath(mainPath);
            var dialog = new TapeSaveOptionsDialog(
                format,
                trailingBytes,
                sidecarAlreadyExists: File.Exists(sidecarPath))
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true)
            {
                return false;
            }

            preserveTrailing = dialog.PreserveTrailing;
            generateSidecar = dialog.GenerateSidecar;
            return true;
        }

        private string GetOpenFilter() => FeatureModePolicy.OpenFilter;

        private string GetSaveFilter() => FeatureModePolicy.GetSaveFilter(AdvancedFeaturesEnabled);

        private string GetExportFilter() => FeatureModePolicy.GetExportFilter(AdvancedFeaturesEnabled);

        private bool TryChoosePreserveTrailing(
            IEnumerable<TapeRecord> records,
            out bool preserveTrailing)
        {
            preserveTrailing = false;
            int trailingBytes = records.Sum(record => record.Body.TrailingData?.Length ?? 0);
            if (!AdvancedFeaturesEnabled || trailingBytes == 0)
            {
                return true;
            }

            MessageBoxResult result = MessageBox.Show(
                $"Preserve {trailingBytes} trailing bytes in the standalone MZF output?",
                "Preserve trailing data",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            preserveTrailing = result == MessageBoxResult.Yes;
            return result != MessageBoxResult.Cancel;
        }

        private void button_Click_Open(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = GetOpenFilter();

            if (openFileDialog.ShowDialog() == true)
            {
                string filePath = openFileDialog.FileName;
                if (!AddFile(filePath, bindAsCurrent: true))
                {
                    return;
                }

                exportAllButton.IsEnabled = true;
                clearAllButton.IsEnabled = true;
                saveButton.IsEnabled = true;

                UpdateStatus();
            }
        }

        private void button_Click_Save(object sender, RoutedEventArgs e)
        {
            if (!TryValidateAllBlocks(out string validationError))
            {
                MessageBox.Show(validationError, "Cannot save", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = GetSaveFilter();
            string filenameWithoutExtension = System.IO.Path.GetFileNameWithoutExtension(actFileName);
            saveFileDialog.FileName = filenameWithoutExtension;
            string extension = System.IO.Path.GetExtension(actFileName).ToLower();
            int filterIndex = 1; // Default to the first filter
            if (extension == ".mzq")
            {
                filterIndex = 2;
            }
            else if (extension == ".mzt")
            {
                filterIndex = 3;
            }
            else if (extension == ".mzf")
            {
                filterIndex = 4;
            }
            saveFileDialog.FilterIndex = filterIndex;

            if (saveFileDialog.ShowDialog() == true)
            {
                string filePath = saveFileDialog.FileName;
                string fileExtension = System.IO.Path.GetExtension(filePath).ToLower();

                if (!TryValidateOutputFormat(fileExtension, out validationError))
                {
                    MessageBox.Show(validationError, "Cannot save", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                try
                {
                    if (fileExtension == ".mzq")
                    {
                        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            MZQFileReader mzqf = new MZQFileReader();
                            mzqf.WriteMZQHeaderToFile(fileStream, checked((byte)(mzfBlocks.Count * 2)));
                            foreach (TapeRecord record in mzfBlocks)
                            {
                                mzqf.WriteMZQFileHeaderToFile(fileStream, record.Header);
                                mzqf.WriteMZQFileBodyToFile(fileStream, record.Body);
                            }
                        }
                        DiscardMetadataNotStoredByCurrentFormat();
                        SetCurrentDocumentAfterSave(filePath, TapeDocumentFormat.Mzq, sidecarPath: null);
                    }
                    else if (fileExtension == ".qdf")
                    {
                        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                        {
                            QDFFileReader qdfr = new QDFFileReader();
                            qdfr.WriteQDFHeaderToFile(fileStream, checked((byte)(mzfBlocks.Count * 2)));
                            foreach (TapeRecord record in mzfBlocks)
                            {
                                qdfr.WriteQDFFileHeaderToFile(fileStream, record.Header);
                                qdfr.WriteQDFFileBodyToFile(fileStream, record.Body);
                            }
                            long currentSize = fileStream.Length;
                            long bytesToWrite = QdfImageSize - currentSize;
                            qdfr.WriteBytesToStream(fileStream, 0x00, bytesToWrite);
                        }
                        DiscardMetadataNotStoredByCurrentFormat();
                        SetCurrentDocumentAfterSave(filePath, TapeDocumentFormat.Qdf, sidecarPath: null);
                    }
                    else if (fileExtension == ".mzf")
                    {
                        if (mzfBlocks.Count > 1)
                        {
                            MessageBox.Show("MZF file should contain only one tape file. Please use Export button or Save as MZT file.", "MZF file limitation", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }
                        int trailingBytes = mzfBlocks[0].Body.TrailingData?.Length ?? 0;
                        if (!TryChooseTapeSaveOptions(
                            filePath,
                            TapeDocumentFormat.Mzf,
                            trailingBytes,
                            out bool preserve,
                            out bool generateSidecar))
                        {
                            return;
                        }
                        TapeDocumentWriter.SaveMzf(filePath, mzfBlocks[0], preserve, generateSidecar);
                        SetCurrentDocumentAfterSave(filePath, TapeDocumentFormat.Mzf, GetExistingSidecarPath(filePath));
                    }
                    else if (fileExtension == ".mzt")
                    {
                        if (!TryChooseTapeSaveOptions(
                            filePath,
                            TapeDocumentFormat.Mzt,
                            trailingBytes: 0,
                            out _,
                            out bool generateSidecar))
                        {
                            return;
                        }
                        TapeDocumentWriter.SaveMzt(filePath, mzfBlocks, generateSidecar);
                        document.ContainerTrailingData = Array.Empty<byte>();
                        SetCurrentDocumentAfterSave(filePath, TapeDocumentFormat.Mzt, GetExistingSidecarPath(filePath));
                    }
                    else if (AdvancedFeaturesEnabled && (fileExtension == ".lep" || fileExtension == ".l16" || fileExtension == ".wav"))
                    {
                        SharpTapeExporter.Export(
                            filePath,
                            mzfBlocks,
                            SharpTapeExporter.GetFormat(fileExtension),
                            GetSelectedTapeMachine());
                    }
                    else
                    {
                        MessageBox.Show($"I do not know how to save file with extension {fileExtension} (yet).", "Unknown file extension", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Error saving file", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private static string? GetExistingSidecarPath(string filePath)
        {
            string sidecar = SidecarService.GetSidecarPath(filePath);
            return File.Exists(sidecar) ? sidecar : null;
        }

        private void SetCurrentDocumentAfterSave(
            string filePath,
            TapeDocumentFormat format,
            string? sidecarPath)
        {
            document.FilePath = System.IO.Path.GetFullPath(filePath);
            document.Format = format;
            document.SidecarPath = sidecarPath;
            actFileName = System.IO.Path.GetFileName(filePath);
            Title = $"QDTool - {actFileName}";
            RefreshGrid();
        }

        private void DiscardMetadataNotStoredByCurrentFormat()
        {
            foreach (TapeRecord record in mzfBlocks)
            {
                record.RemoveTrailingData();
                record.ResetMetadataToImplicit();
            }
            document.ContainerTrailingData = Array.Empty<byte>();
        }

        private void button_Click_View(object sender, RoutedEventArgs e)
        {
            HexBrowser hexBrowserWindow = new HexBrowser();

            int selectedIndex = MzfDataGrid.SelectedIndex; // Získání indexu vybraného řádku

            if (selectedIndex >= 0 && selectedIndex < mzfBlocks.Count)
            {
                TapeRecord selectedRecord = mzfBlocks[selectedIndex];
                hexBrowserWindow.ShowHexDump(selectedRecord, AdvancedFeaturesEnabled);
            }

            hexBrowserWindow.ShowDialog(); // Zobrazí HexBrowser jako modální dialogové okno
        }

        private void MzfDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Tlačítko "View" bude aktivní pouze pokud je vybrán nějaký řádek
            viewButton.IsEnabled = MzfDataGrid.SelectedItem != null;
            exportButton.IsEnabled = MzfDataGrid.SelectedItem != null;
            deleteButton.IsEnabled = MzfDataGrid.SelectedItem != null;

            int selectedIndex = MzfDataGrid.SelectedIndex;
            moveUpButton.IsEnabled = selectedIndex > 0 && mzfBlocks.Count > 1;
            moveDownButton.IsEnabled = selectedIndex < mzfBlocks.Count - 1 && selectedIndex >= 0;
            UpdateAdvancedActionState();
        }

        private void button_Click_EditProfile(object sender, RoutedEventArgs e)
        {
            int selectedIndex = MzfDataGrid.SelectedIndex;
            if (!AdvancedFeaturesEnabled || selectedIndex < 0 || selectedIndex >= mzfBlocks.Count)
            {
                return;
            }

            TapeRecord selectedRecord = mzfBlocks[selectedIndex];
            string recordName = ConvertMzfNameToASCIIString(selectedRecord.Header.MzfFname);
            var dialog = new ProfileEditorDialog(recordName, selectedRecord.Profile, mzfBlocks.Count > 1)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            IEnumerable<TapeRecord> records = dialog.ApplyToAll
                ? mzfBlocks
                : new[] { selectedRecord };
            foreach (TapeRecord record in records)
            {
                record.Profile = dialog.SelectedProfile;
                record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
            }

            RefreshGrid();
            MzfDataGrid.SelectedIndex = selectedIndex;
        }

        private void button_Click_Up(object sender, RoutedEventArgs e)
        {
            int selectedIndex = MzfDataGrid.SelectedIndex;
            if (selectedIndex > 0)
            {
                var itemToMoveUp = mzfBlocks[selectedIndex];
                mzfBlocks.RemoveAt(selectedIndex);
                mzfBlocks.Insert(selectedIndex - 1, itemToMoveUp);

                //MzfDataGrid.ItemsSource = null;
                //MzfDataGrid.ItemsSource = mzfBlocks;

                RefreshGrid();

                MzfDataGrid.SelectedIndex = selectedIndex - 1;
                MzfDataGrid.Focus();
            }
        }

        private void button_Click_Down(object sender, RoutedEventArgs e)
        {
            int selectedIndex = MzfDataGrid.SelectedIndex;
            if (selectedIndex < mzfBlocks.Count - 1 && selectedIndex >= 0)
            {
                var itemToMoveDown = mzfBlocks[selectedIndex];
                mzfBlocks.RemoveAt(selectedIndex);
                mzfBlocks.Insert(selectedIndex + 1, itemToMoveDown);

                //MzfDataGrid.ItemsSource = null;
                //MzfDataGrid.ItemsSource = mzfBlocks;

                RefreshGrid();

                //var currentSource = MzfDataGrid.ItemsSource;
                //MzfDataGrid.ItemsSource = null;
                //MzfDataGrid.ItemsSource = currentSource;

                MzfDataGrid.SelectedIndex = selectedIndex + 1;
                MzfDataGrid.Focus();
            }
        }

        private bool AddFile(string filePath, bool bindAsCurrent = false)
        {
            string fileExtension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            var recordsToAdd = new List<TapeRecord>();
            byte[] containerTrailing = Array.Empty<byte>();
            string? loadedSidecar = null;
            TapeDocumentFormat format;

            try
            {
                if (fileExtension == ".mzq")
                {
                    format = TapeDocumentFormat.Mzq;
                    recordsToAdd.AddRange(new MZQFileReader().ReadFile(filePath)
                        .Select(block => TapeRecord.FromLegacy(block.Item1, block.Item2)));
                }
                else if (fileExtension == ".qdf")
                {
                    format = TapeDocumentFormat.Qdf;
                    recordsToAdd.AddRange(new QDFFileReader().ReadFile(filePath)
                        .Select(block => TapeRecord.FromLegacy(block.Item1, block.Item2)));
                }
                else if (fileExtension == ".mzf")
                {
                    format = TapeDocumentFormat.Mzf;
                    TapeRecord record = new MZTFileReader().ReadStandaloneMzf(filePath);
                    loadedSidecar = SidecarService.LoadForMzf(filePath, record);
                    recordsToAdd.Add(record);
                }
                else if (fileExtension == ".mzt")
                {
                    format = TapeDocumentFormat.Mzt;
                    MztReadResult result = new MZTFileReader().ReadMzt(filePath);
                    recordsToAdd.AddRange(result.Records);
                    containerTrailing = result.ContainerTrailingData;
                    loadedSidecar = SidecarService.LoadForMzt(filePath, recordsToAdd);
                }
                else
                {
                    MessageBox.Show($"I do not know how to process file with extension {fileExtension} (yet).", "Unknown file extension", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                if (bindAsCurrent)
                {
                    document.Clear();
                    document.FilePath = System.IO.Path.GetFullPath(filePath);
                    document.Format = format;
                    document.ContainerTrailingData = containerTrailing;
                    document.SidecarPath = loadedSidecar;
                    actFileName = System.IO.Path.GetFileName(filePath);
                    Title = $"QDTool - {actFileName}";
                }
                mzfBlocks.AddRange(recordsToAdd);
                RefreshGrid();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error reading file", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void RefreshGrid()
        {
            MzfDisplayDataCollection.Clear();
            LoadDataToGrid(mzfBlocks);
            UpdateStatus();
            UpdateAdvancedActionState();
        }

        private void button_Click_Add(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = GetOpenFilter();

            if (openFileDialog.ShowDialog() == true)
            {
                // string currentDirectory = Directory.GetCurrentDirectory();
                string filePath = openFileDialog.FileName;
                AddFile(filePath, bindAsCurrent: mzfBlocks.Count == 0);

                saveButton.IsEnabled = true;
                exportAllButton.IsEnabled = true;
                clearAllButton.IsEnabled = true;
                UpdateStatus();
            }
        }

        private void button_Click_Export(object sender, RoutedEventArgs e)
        {
            int selectedIndex = MzfDataGrid.SelectedIndex; // Získání indexu vybraného řádku

            if (selectedIndex >= 0 && selectedIndex < mzfBlocks.Count)
            {
                TapeRecord item = mzfBlocks[selectedIndex];

                if (!TryValidateBlock(item, selectedIndex, out string validationError))
                {
                    MessageBox.Show(validationError, "Cannot export", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                SaveFileDialog saveFileDialog = new SaveFileDialog();
                saveFileDialog.Filter = GetExportFilter();
                saveFileDialog.AddExtension = true;
                saveFileDialog.DefaultExt = ".mzf";
                MZQFileHeader header = item.Header;
                MZQFileBody body = item.Body;
                saveFileDialog.FileName = ConvertMzfNameToASCIIString(header.MzfFname);

                if (saveFileDialog.ShowDialog() == true)
                {
                    string filePath = saveFileDialog.FileName;

                    string fileExtension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                    if (fileExtension == ".mzf")
                    {
                        TapeRecord exportRecord = item.DeepClone();
                        int trailingBytes = item.Body.TrailingData?.Length ?? 0;
                        if (!TryChooseTapeSaveOptions(
                            filePath,
                            TapeDocumentFormat.Mzf,
                            trailingBytes,
                            out bool preserve,
                            out bool generateSidecar))
                        {
                            return;
                        }
                        TapeDocumentWriter.SaveMzf(filePath, exportRecord, preserve, generateSidecar);
                    }
                    else if (AdvancedFeaturesEnabled && (fileExtension == ".lep" || fileExtension == ".l16" || fileExtension == ".wav"))
                    {
                        SharpTapeExporter.Export(
                            filePath,
                            new[] { (header, body) },
                            SharpTapeExporter.GetFormat(fileExtension),
                            GetSelectedTapeMachine());
                    }
                    else
                    {
                        MessageBox.Show(
                            $"I do not know how to export a file with extension {fileExtension}.",
                            "Unknown file extension",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
            }
        }

        private void check_QDF_Files()
        {
            QDFFileReader qdfr = new QDFFileReader();

            string directoryPath = @"H:\Sharp\QDC\";
            string searchPattern = "*.qdf"; // Hledá všechny soubory s příponou .qdf
            string outFilePath = "output.txt";
            if (File.Exists(outFilePath))
            {
                File.Delete(outFilePath);
            }

            try
            {
                // Search for all .qdf files in subfolders
                string[] files = Directory.GetFiles(directoryPath, searchPattern, SearchOption.AllDirectories);

                foreach (string filePath in files)
                {
                    mzfBlocks.Clear();
                    qdfr.positions.Clear();
                    int cnt = 1;
                    long sumDta = 0;
                    long lastPos = 0;
                    bool firstPass = true;
                    int blocks = -1;

                    mzfBlocks.AddRange(qdfr.ReadFile(filePath)
                        .Select(block => TapeRecord.FromLegacy(block.Item1, block.Item2)));

                    using (StreamWriter writer = new StreamWriter(outFilePath, append: true))
                    {
                        writer.WriteLine(filePath);

                        foreach (long position in qdfr.positions)
                        {
                            if (cnt++ % 2 == 1)
                            {
                                writer.Write($"{position:X5} ");
                            }
                            else
                            {
                                writer.Write($"({position}) ");
                            }

                            if (firstPass)
                            {
                                firstPass = false;
                                using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                                {
                                    fileStream.Seek(position, SeekOrigin.Begin);
                                    blocks = fileStream.ReadByte();
                                }
                                writer.Write($"[{blocks}] ");
                                if (blocks != mzfBlocks.Count * 2)
                                {
                                    writer.Write($"BAD BLOCK COUNT ");
                                }
                            }

                            if (cnt % 2 == 0)
                            {
                                using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                                {
                                    fileStream.Seek(position - 1, SeekOrigin.Begin);
                                    byte blockStart = (byte)fileStream.ReadByte();
                                    if (blockStart != 0xA5) // this should never happen
                                        writer.Write($"BLOCKSTART=0x{blockStart:X2} ");
                                }
                            }
                        }
                        writer.WriteLine();

                        foreach (long position in qdfr.positions)
                        {
                            if (cnt++ % 2 == 1)
                            {
                                writer.Write(position - sumDta - lastPos);
                                lastPos = position - sumDta;
                                writer.Write(" ");
                            }
                            else
                            {
                                sumDta += position;
                            }
                        }
                        writer.WriteLine();
                    }
                }
                MessageBox.Show("Done");
            }
            catch (Exception ex)
            {
                // Zachytávání a zpracování výjimek
                Console.WriteLine("Došlo k chybě: " + ex.Message);
            }
        }

        private void button_Click_Delete(object sender, RoutedEventArgs e)
        {
            int selectedIndex = MzfDataGrid.SelectedIndex; // Získání indexu vybraného řádku

            if (selectedIndex >= 0 && selectedIndex < mzfBlocks.Count)
            {
                mzfBlocks.RemoveAt(selectedIndex);

                RefreshGrid();

                //MzfDataGrid.SelectedIndex = selectedIndex - 1;
                //MzfDataGrid.Focus();
            }
        }

        private void button_Click_ExportAll(object sender, RoutedEventArgs e)
        {
            if (!TryValidateAllBlocks(out string validationError))
            {
                MessageBox.Show(validationError, "Cannot export", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            OpenFileDialog dialog = new OpenFileDialog
            {
                ValidateNames = false,
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "Select folder",
                Filter = "Folders|*.this.directory",
                DereferenceLinks = true // odkazy na složky budou následovány
            };

            if (dialog.ShowDialog() == true)
            {
                string? exportPath = System.IO.Path.GetDirectoryName(dialog.FileName);

                if (exportPath == null)
                {
                    MessageBox.Show("Export path cannot be empty", "Invalid path", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    HashSet<string> reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!TryChoosePreserveTrailing(mzfBlocks, out bool preserve))
                    {
                        return;
                    }
                    foreach (TapeRecord record in mzfBlocks)
                    {
                        string fileName = ConvertMzfNameToASCIIString(record.Header.MzfFname);
                        string filePath = GetAvailableExportPath(exportPath, fileName, reservedPaths);
                        TapeDocumentWriter.SaveMzf(filePath, record.DeepClone(), preserve);
                    }
                }
            }
        }

        private void button_Click_ClearAll(object sender, RoutedEventArgs e)
        {
            document.Clear();
            MzfDisplayDataCollection.Clear();
            exportAllButton.IsEnabled = false;
            Title = "QDTool";
            actFileName = string.Empty;
            UpdateStatus();
            UpdateAdvancedActionState();
        }

        private void button_Click_About(object sender, RoutedEventArgs e)
        {
            AboutDialog aboutDialog = new AboutDialog();
            aboutDialog.ShowDialog();
        }
    }
}
