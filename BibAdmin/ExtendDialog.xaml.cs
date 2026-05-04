using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace BibAdmin
{
    public partial class ExtendDialog : Window
    {
        public int AddSeconds { get; private set; }
        public int AddAmount { get; private set; }

        private bool _isSyncing = false;
        private int Tariff => ComputersPage.Tariff > 0 ? ComputersPage.Tariff : 3000;

        public ExtendDialog(string pcNumber, string sessionType)
        {
            InitializeComponent();
            TxtPcInfo.Text = $"{pcNumber} — {sessionType}";
            Owner = Application.Current.MainWindow;
        }

        // =====================
        // Синхронизация полей
        // =====================
        private void TxtMinutes_TextChanged(object sender,
            TextChangedEventArgs e)
        {
            if (_isSyncing) return;
            _isSyncing = true;
            try
            {
                if (double.TryParse(TxtMinutes.Text,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out double mins) && mins > 0)
                {
                    double cost = (mins / 60.0) * Tariff;
                    TxtMoney.Text = cost.ToString("F0",
                        CultureInfo.InvariantCulture);
                    TxtHint.Text =
                        $"{mins:F0} мин = {cost:F0} сум";
                }
                else
                {
                    TxtMoney.Text = "";
                    TxtHint.Text = "";
                }
            }
            finally { _isSyncing = false; }
        }

        private void TxtMoney_TextChanged(object sender,
            TextChangedEventArgs e)
        {
            if (_isSyncing) return;
            _isSyncing = true;
            try
            {
                if (double.TryParse(TxtMoney.Text,
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out double money) && money > 0)
                {
                    double mins = (money / Tariff) * 60.0;
                    TxtMinutes.Text = mins.ToString("F0",
                        CultureInfo.InvariantCulture);
                    TxtHint.Text =
                        $"{money:F0} сум = {mins:F0} мин";
                }
                else
                {
                    TxtMinutes.Text = "";
                    TxtHint.Text = "";
                }
            }
            finally { _isSyncing = false; }
        }

        // =====================
        // Кнопки
        // =====================
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnExtend_Click(object sender, RoutedEventArgs e)
        {
            bool hasMinutes = double.TryParse(TxtMinutes.Text,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out double mins) && mins > 0;
            bool hasMoney = double.TryParse(TxtMoney.Text,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out double money) && money > 0;

            if (!hasMinutes && !hasMoney)
            {
                MessageBox.Show("Введите время или сумму", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (hasMinutes)
            {
                AddSeconds = (int)Math.Round(mins * 60);
                AddAmount = (int)Math.Round((mins / 60.0) * Tariff);
            }
            else
            {
                AddAmount = (int)money;
                AddSeconds = (int)Math.Round((money / Tariff) * 60);
            }

            DialogResult = true;
            Close();
        }
    }
}