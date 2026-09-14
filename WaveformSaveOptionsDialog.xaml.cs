using System.Windows;

namespace QDTool
{
    public partial class WaveformSaveOptionsDialog : Window
    {
        internal WaveformSaveOptionsDialog(string extension, int recordCount)
        {
            InitializeComponent();
            headingTextBlock.Text = $"{extension.TrimStart('.').ToUpperInvariant()} save options";
            Visibility layoutVisibility = recordCount > 1 ? Visibility.Visible : Visibility.Collapsed;
            layoutOptionsPanel.Visibility = layoutVisibility;
            separateHintTextBlock.Visibility = layoutVisibility;
        }

        internal SharpTapeMachine SelectedMachine => machineComboBox.SelectedIndex == 1
            ? SharpTapeMachine.Mz700
            : SharpTapeMachine.Mz800;

        internal bool SeparateFiles => separateRadioButton.IsChecked == true;

        private void SaveButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
