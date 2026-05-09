using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BibAdmin
{
    public partial class UnpaidServicesDialog : Window
    {
        private readonly List<ServiceTransaction> _unpaid;
        private readonly string _readerId;

        public UnpaidServicesDialog(List<ServiceTransaction> unpaid, string readerLabel)
        {
            InitializeComponent();
            Owner = Application.Current.MainWindow;
            _unpaid = unpaid;
            _readerId = unpaid.FirstOrDefault()?.ReaderId ?? "";

            TxtReaderInfo.Text = $"Читатель: {readerLabel}";
            RenderList();
        }

        private void RenderList()
        {
            ServicesList.Children.Clear();

            bool alt = false;
            foreach (var t in _unpaid)
            {
                var row = new Border
                {
                    Background = alt
                        ? new SolidColorBrush(Color.FromRgb(252, 250, 245))
                        : Brushes.White,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                alt = !alt;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel();
                info.Children.Add(new TextBlock
                {
                    Text = $"{t.ServiceName}",
                    FontSize = 13,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 46))
                });
                info.Children.Add(new TextBlock
                {
                    Text = $"{t.Quantity} {t.Unit} × {t.PricePerUnit:N0} сум",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(info, 0);
                grid.Children.Add(info);

                var amount = new TextBlock
                {
                    Text = $"{t.DebtAmount:N0} сум",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(133, 79, 11)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(amount, 1);
                grid.Children.Add(amount);

                row.Child = grid;
                ServicesList.Children.Add(row);
            }

            int total = _unpaid.Sum(t => t.DebtAmount);
            TxtDebtTotal.Text = $"{total:N0} сум";
        }

        private void PayAll_Click(object sender, RoutedEventArgs e)
        {
            ServiceTransaction.MarkAllPaidForReader(_readerId);
            DialogResult = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
