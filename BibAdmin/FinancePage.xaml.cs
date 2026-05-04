using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BibAdmin
{
    // Модель одной сессии
    public class SessionRecord
    {
        public string PcNumber { get; set; } = "";
        public string SessionType { get; set; } = "";
        public string UserName { get; set; } = "—";
        public int DurationSeconds { get; set; }
        public int EarnedAmount { get; set; }
        public int PaidAmount { get; set; }
        public int RefundAmount { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    public partial class FinancePage : Page
    {
        // Статическое хранилище сессий — доступно из ComputersPage
        public static List<SessionRecord> Sessions { get; } = new();

        public FinancePage()
        {
            InitializeComponent();
            TxtCurrentDate.Text = DateTime.Now.ToString(
                "dddd, d MMMM yyyy",
                new System.Globalization.CultureInfo("ru-RU"));

            UpdateStats();
            RenderSessions(Sessions);
        }

        // Добавить сессию (вызывается из ComputersPage)
        public static void AddSession(SessionRecord session)
        {
            Sessions.Insert(0, session);
        }

        private void UpdateStats()
        {
            var today = DateTime.Today;
            var weekStart = today.AddDays(-(int)today.DayOfWeek + 1);
            var monthStart = new DateTime(today.Year, today.Month, 1);

            int todayTotal = Sessions
                .Where(s => s.EndTime.Date == today)
                .Sum(s => s.EarnedAmount);

            int weekTotal = Sessions
                .Where(s => s.EndTime.Date >= weekStart)
                .Sum(s => s.EarnedAmount);

            int monthTotal = Sessions
                .Where(s => s.EndTime.Date >= monthStart)
                .Sum(s => s.EarnedAmount);

            TxtToday.Text = $"{todayTotal:N0} сум";
            TxtWeek.Text = $"{weekTotal:N0} сум";
            TxtMonth.Text = $"{monthTotal:N0} сум";
            TxtTotalSessions.Text = Sessions.Count.ToString();
        }

        private void RenderSessions(IEnumerable<SessionRecord> sessions)
        {
            SessionsList.Children.Clear();
            var list = sessions.ToList();

            if (!list.Any())
            {
                SessionsList.Children.Add(new TextBlock
                {
                    Text = "Нет записей о сессиях",
                    Foreground = new SolidColorBrush(
                        Color.FromRgb(170, 170, 170)),
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0)
                });
                return;
            }

            bool alternate = false;
            foreach (var s in list)
            {
                var row = new Border
                {
                    Background = alternate
                        ? new SolidColorBrush(Color.FromRgb(250, 250, 250))
                        : Brushes.White,
                    Padding = new Thickness(16, 10, 16, 10)
                };
                alternate = !alternate;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

                // Компьютер
                var pcText = new TextBlock
                {
                    Text = s.PcNumber,
                    FontSize = 13,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 46)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(pcText, 0);
                grid.Children.Add(pcText);

                // Тип сессии с цветом
                var typeColor = s.SessionType switch
                {
                    "VIP" => Color.FromRgb(133, 79, 11),
                    "По времени" => Color.FromRgb(15, 110, 86),
                    "По деньгам" => Color.FromRgb(24, 95, 165),
                    _ => Color.FromRgb(100, 100, 100)
                };

                var typeBadge = new Border
                {
                    Background = new SolidColorBrush(
                        Color.FromArgb(30, typeColor.R, typeColor.G, typeColor.B)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = s.SessionType,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(typeColor)
                    }
                };
                Grid.SetColumn(typeBadge, 1);
                grid.Children.Add(typeBadge);

                // Пользователь
                var userText = new TextBlock
                {
                    Text = s.UserName,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(userText, 2);
                grid.Children.Add(userText);

                // Длительность
                int h = s.DurationSeconds / 3600;
                int m = (s.DurationSeconds % 3600) / 60;
                int sec = s.DurationSeconds % 60;
                var durationText = new TextBlock
                {
                    Text = $"{h:D2}:{m:D2}:{sec:D2}",
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(durationText, 3);
                grid.Children.Add(durationText);

                // Сумма
                var amountText = new TextBlock
                {
                    Text = $"{s.EarnedAmount:N0} сум",
                    FontSize = 13,
                    FontWeight = FontWeights.Medium,
                    Foreground = new SolidColorBrush(Color.FromRgb(15, 110, 86)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(amountText, 4);
                grid.Children.Add(amountText);

                // Дата и время
                var dateText = new TextBlock
                {
                    Text = s.EndTime.ToString("dd.MM HH:mm"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(dateText, 5);
                grid.Children.Add(dateText);

                row.Child = grid;
                SessionsList.Children.Add(row);
            }

            TxtFilterResult.Text = $"Показано: {list.Count}";
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (SessionsList == null) return;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var filtered = Sessions.AsEnumerable();
            var today = DateTime.Today;

            // Фильтр по дате
            if (RbToday.IsChecked == true)
                filtered = filtered.Where(s => s.EndTime.Date == today);
            else if (RbWeek.IsChecked == true)
                filtered = filtered.Where(s => s.EndTime.Date >= today.AddDays(-(int)today.DayOfWeek + 1));
            else if (RbMonth.IsChecked == true)
                filtered = filtered.Where(s => s.EndTime.Date >= new DateTime(today.Year, today.Month, 1));

            // Фильтр по типу
            var selectedType = (CmbType.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (selectedType != null && selectedType != "Все типы")
                filtered = filtered.Where(s => s.SessionType == selectedType);

            RenderSessions(filtered);
            UpdateStats();
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show(
                "Очистить всю историю сессий?",
                "Подтверждение",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (r == MessageBoxResult.Yes)
            {
                Sessions.Clear();
                ComputersPage.Revenue = 0;
                UpdateStats();
                RenderSessions(Sessions);
            }
        }
    }
}