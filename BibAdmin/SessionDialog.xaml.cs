using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BibAdmin
{
    public partial class SessionDialog : Window
    {
        public string SessionType { get; private set; } = "По времени";
        public int LimitSeconds { get; private set; }
        public int PaidAmount { get; private set; }

        private bool _isSyncing = false;
        private bool _isVip = false;
        private int Tariff =>
            ComputersPage.Tariff > 0 ? ComputersPage.Tariff : 3000;

        public SessionDialog(string pcNumber)
        {
            InitializeComponent();
            TxtPcInfo.Text = pcNumber;
            Owner = Application.Current.MainWindow;
            SelectLimited(); // По умолчанию — лимитированная
        }

        // =====================
        // Выбор типа сессии
        // =====================
        private void BtnLimited_Click(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectLimited();
        }

        private void BtnVip_Click(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            SelectVip();
        }

        private void SelectLimited()
        {
            _isVip = false;

            // Активная кнопка — лимитированная
            BtnLimited.Background =
                new SolidColorBrush(Color.FromRgb(29, 158, 117))
                { Opacity = 0.15 };
            BtnLimited.BorderBrush =
                new SolidColorBrush(Color.FromRgb(29, 158, 117));
            TxtLimitedLabel.Foreground =
                new SolidColorBrush(Color.FromRgb(29, 158, 117));

            // Неактивная — VIP
            BtnVip.Background =
                new SolidColorBrush(Color.FromRgb(30, 30, 46));
            BtnVip.BorderBrush =
                new SolidColorBrush(Color.FromRgb(61, 61, 107));
            TxtVipLabel.Foreground =
                new SolidColorBrush(Color.FromRgb(102, 102, 136));

            PanelFields.Visibility = Visibility.Visible;
            TxtVipInfo.Visibility = Visibility.Collapsed;
        }

        private void SelectVip()
        {
            _isVip = true;

            // Активная кнопка — VIP
            BtnVip.Background =
                new SolidColorBrush(Color.FromRgb(239, 159, 39))
                { Opacity = 0.15 };
            BtnVip.BorderBrush =
                new SolidColorBrush(Color.FromRgb(239, 159, 39));
            TxtVipLabel.Foreground =
                new SolidColorBrush(Color.FromRgb(239, 159, 39));

            // Неактивная — лимитированная
            BtnLimited.Background =
                new SolidColorBrush(Color.FromRgb(30, 30, 46));
            BtnLimited.BorderBrush =
                new SolidColorBrush(Color.FromRgb(61, 61, 107));
            TxtLimitedLabel.Foreground =
                new SolidColorBrush(Color.FromRgb(102, 102, 136));

            PanelFields.Visibility = Visibility.Collapsed;
            TxtVipInfo.Visibility = Visibility.Visible;

            TxtMinutes.Text = "";
            TxtMoney.Text = "";
            TxtHint.Text = "";
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

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_isVip)
            {
                SessionType = "VIP";
                LimitSeconds = 0;
                PaidAmount = 0;
                DialogResult = true;
                Close();
                return;
            }

            // Лимитированная — нужно время или сумма
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

            SessionType = "По времени";

            if (hasMinutes)
            {
                LimitSeconds = (int)Math.Round(mins * 60);
                PaidAmount = (int)Math.Round((mins / 60.0) * Tariff);
            }
            else
            {
                PaidAmount = (int)money;
                LimitSeconds = (int)Math.Round((money / Tariff) * 60);
            }

            DialogResult = true;
            Close();
        }
    }
}