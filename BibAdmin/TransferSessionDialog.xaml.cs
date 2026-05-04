using System.Collections.Generic;
using System.Windows;

namespace BibAdmin
{
    public partial class TransferSessionDialog : Window
    {
        public string SelectedPcNumber { get; private set; } = "";

        public TransferSessionDialog(string sourcePcNumber, string sessionInfo, List<string> availablePcs)
        {
            InitializeComponent();
            Owner = Application.Current.MainWindow;
            TxtSourceInfo.Text = $"{sourcePcNumber}  ·  {sessionInfo}";

            foreach (var pc in availablePcs)
                CmbTarget.Items.Add(pc);

            if (CmbTarget.Items.Count > 0)
                CmbTarget.SelectedIndex = 0;
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (CmbTarget.SelectedItem is string selected && !string.IsNullOrEmpty(selected))
            {
                SelectedPcNumber = selected;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("Выберите целевой ПК", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
