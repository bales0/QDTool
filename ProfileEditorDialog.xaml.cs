using System;
using System.Linq;
using System.Windows;

namespace QDTool
{
    public partial class ProfileEditorDialog : Window
    {
        private sealed record ProfileChoice(TapeProfile Value, string Name);

        internal ProfileEditorDialog(string recordName, TapeProfile currentProfile, bool allowApplyToAll)
        {
            InitializeComponent();
            recordNameTextBlock.Text = $"Record: {recordName}";
            ProfileChoice[] choices = Enum.GetValues<TapeProfile>()
                .Select(value => new ProfileChoice(value, TapeProfileNames.ToDisplayName(value)))
                .ToArray();
            profileComboBox.ItemsSource = choices;
            profileComboBox.SelectedItem = choices.Single(choice => choice.Value == currentProfile);
            applyToAllCheckBox.Visibility = allowApplyToAll ? Visibility.Visible : Visibility.Collapsed;
        }

        internal TapeProfile SelectedProfile { get; private set; }

        public bool ApplyToAll => applyToAllCheckBox.IsChecked == true;

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (profileComboBox.SelectedItem is not ProfileChoice choice)
            {
                MessageBox.Show(this, "Select a tape profile.", "Tape profile", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedProfile = choice.Value;
            DialogResult = true;
        }
    }
}
