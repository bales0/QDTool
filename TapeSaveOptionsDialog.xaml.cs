using System.Windows;

namespace QDTool
{
    public partial class TapeSaveOptionsDialog : Window
    {
        internal TapeSaveOptionsDialog(
            TapeDocumentFormat format,
            int trailingBytes,
            bool sidecarAlreadyExists)
        {
            InitializeComponent();
            bool isMzf = format == TapeDocumentFormat.Mzf;
            string sidecarName = isMzf ? "MFI" : "MTI";
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
                existingSidecarTextBlock.Text = $"The existing {sidecarName} will be regenerated to stay synchronized.";
                existingSidecarTextBlock.Visibility = Visibility.Visible;
            }
        }

        public bool PreserveTrailing => preserveTrailingCheckBox.IsChecked == true;

        public bool GenerateSidecar => generateSidecarCheckBox.IsChecked == true;

        private void SaveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
