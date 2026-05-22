using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BibAdmin
{
    public partial class SessionSummaryDialog : Window
    {
        private readonly List<ServiceTransaction> _debts;
        private readonly string _pcNumber;
        private readonly string _readerId;

        public SessionSummaryDialog(ClientState pc, int earned, List<ServiceTransaction> debts)
        {
            InitializeComponent();
            Owner = Application.Current.MainWindow;
            _debts = debts;
            _pcNumber = pc.PcNumber;
            _readerId = pc.ReaderId ?? "";

            int h = pc.ElapsedSeconds / 3600;
            int m = (pc.ElapsedSeconds % 3600) / 60;
            int s = pc.ElapsedSeconds % 60;
            int refund = System.Math.Max(0, pc.PaidAmount - earned);

            TxtTitle.Text = $"Итог сессии — {pc.PcNumber}";
            string userName = !string.IsNullOrEmpty(pc.UserName) && pc.UserName != "—"
                ? pc.UserName : (!string.IsNullOrEmpty(pc.ReaderId) ? $"ID: {pc.ReaderId}" : "Анонимный");
            TxtSubtitle.Text = $"Пользователь: {userName}";

            TxtType.Text = pc.SessionType;
            TxtDuration.Text = $"{h:D2}:{m:D2}:{s:D2}";
            TxtPaid.Text = $"{pc.PaidAmount:N0} сум";
            TxtEarned.Text = $"{earned:N0} сум";

            if (refund > 0)
            {
                TxtRefund.Text = $"{refund:N0} сум";
                RefundPanel.Visibility = Visibility.Visible;
            }

            if (debts.Count > 0)
            {
                RenderDebts();
                PnlDebts.Visibility = Visibility.Visible;
                BtnPayDebts.Visibility = Visibility.Visible;
            }
        }

        private void RenderDebts()
        {
            DebtsList.Children.Clear();
            bool alt = false;
            foreach (var t in _debts)
            {
                var row = new Border
                {
                    Background = alt
                        ? new SolidColorBrush(Color.FromRgb(255, 248, 235))
                        : new SolidColorBrush(Color.FromRgb(255, 252, 245)),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 3)
                };
                alt = !alt;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel();
                info.Children.Add(new TextBlock
                {
                    Text = t.ServiceName,
                    FontSize = 12,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 46))
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"{t.Quantity} {t.Unit} × {t.PricePerUnit:N0} сум",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                    Margin = new Thickness(0, 1, 0, 0)
                });
                Grid.SetColumn(info, 0);
                grid.Children.Add(info);

                var amount = new TextBlock
                {
                    Text = $"{t.DebtAmount:N0} сум",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(133, 79, 11)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(amount, 1);
                grid.Children.Add(amount);

                row.Child = grid;
                DebtsList.Children.Add(row);
            }

            int total = _debts.Sum(t => t.DebtAmount);
            TxtDebtTotal.Text = $"{total:N0} сум";
        }

        private void PayDebts_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_readerId))
                ServiceTransaction.MarkAllPaidForReader(_readerId);
            if (!string.IsNullOrEmpty(_pcNumber))
                ServiceTransaction.MarkAllPaidForPc(_pcNumber);

            PnlDebts.Visibility = Visibility.Collapsed;
            BtnPayDebts.Visibility = Visibility.Collapsed;
            TxtSubtitle.Text = TxtSubtitle.Text.TrimEnd() + " · долги оплачены";
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
