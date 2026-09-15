using System.Windows;

namespace QDTool
{
    public partial class SaveOptionsDialog : Window
    {
        internal SaveOptionsDialog(
            TapeDocumentFormat format,
            int trailingBytes,
            bool sidecarAlreadyExists)
        {
            InitializeComponent();
            waveformOptionsPanel.Visibility = Visibility.Collapsed;

            bool isMzf = format == TapeDocumentFormat.Mzf;
            string sidecarName = isMzf ? "MFI" : "MTI";
            Title = "Tape save options";
            headingTextBlock.Text = isMzf ? "MZF save options" : "MZT save options";

            preserveTrailingCheckBox.Content = $"Preserve trailing data ({trailingBytes} B)";
            preserveTrailingCheckBox.Visibility = isMzf && trailingBytes > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            generateSidecarCheckBox.Content = $"Generate {sidecarName}";
            generateSidecarCheckBox.IsChecked = sidecarAlreadyExists;
            generateSidecarCheckBox.IsEnabled = !sidecarAlreadyExists;
            if (sidecarAlreadyExists)
            {
                existingSidecarTextBlock.Text =
                    $"The existing {sidecarName} will be regenerated to stay synchronized.";
                existingSidecarTextBlock.Visibility = Visibility.Visible;
            }
        }

        internal SaveOptionsDialog(string extension, int recordCount)
        {
            InitializeComponent();
            tapeOptionsPanel.Visibility = Visibility.Collapsed;
            Title = "Tape export options";
            headingTextBlock.Text = $"{extension.TrimStart('.').ToUpperInvariant()} save options";

            Visibility layoutVisibility = recordCount > 1
                ? Visibility.Visible
                : Visibility.Collapsed;
            layoutOptionsPanel.Visibility = layoutVisibility;
            separateHintTextBlock.Visibility = layoutVisibility;
        }

        public bool PreserveTrailing => preserveTrailingCheckBox.IsChecked == true;

        public bool GenerateSidecar => generateSidecarCheckBox.IsChecked == true;

        internal SharpTapeMachine SelectedMachine => machineComboBox.SelectedIndex == 1
            ? SharpTapeMachine.Mz700
            : SharpTapeMachine.Mz800;

        internal bool SeparateFiles => separateRadioButton.IsChecked == true;

        private void SaveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
