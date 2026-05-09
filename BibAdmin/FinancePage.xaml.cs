using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace BibAdmin
{
    // Модель одной сессии
    public class SessionRecord
    {
        public string PcNumber { get; set; } = "";
        public string SessionType { get; set; } = "";
        public string UserName { get; set; } = "—";
        public string ReaderId { get; set; } = "";
        public int DurationSeconds { get; set; }
        public int EarnedAmount { get; set; }
        public int PaidAmount { get; set; }
        public int RefundAmount { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    public partial class FinancePage : Page
    {
        public static List<SessionRecord> Sessions { get; } = new();

        private static readonly string HistoryFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BibAdmin",
            "finance_history.json");

        // Текущая вкладка: "Sessions" | "Services" | "All"
        private string _activeTab = "Sessions";

        public FinancePage()
        {
            InitializeComponent();
            TxtCurrentDate.Text = DateTime.Now.ToString(
                "dddd, d MMMM yyyy",
                new System.Globalization.CultureInfo("ru-RU"));

            UpdateStats();
            ApplyFilter();
        }

        public void RefreshUI()
        {
            UpdateStats();
            ApplyFilter();
        }

        // =====================
        // Переключение вкладок
        // =====================

        private void TabSessions_Click(object sender, RoutedEventArgs e) => SetTab("Sessions");
        private void TabServices_Click(object sender, RoutedEventArgs e) => SetTab("Services");
        private void TabAll_Click(object sender, RoutedEventArgs e) => SetTab("All");

        private void SetTab(string tab)
        {
            _activeTab = tab;

            BtnTabSessions.Tag = tab == "Sessions" ? "active" : null;
            BtnTabServices.Tag = tab == "Services" ? "active" : null;
            BtnTabAll.Tag = tab == "All" ? "active" : null;

            // Заголовки таблицы
            HeaderSessions.Visibility = tab == "Sessions" ? Visibility.Visible : Visibility.Collapsed;
            HeaderServices.Visibility = tab == "Services" ? Visibility.Visible : Visibility.Collapsed;
            HeaderAll.Visibility = tab == "All" ? Visibility.Visible : Visibility.Collapsed;

            // Фильтры
            PnlSessionTypeFilter.Visibility = tab == "Sessions" ? Visibility.Visible : Visibility.Collapsed;
            PnlServiceStatusFilter.Visibility = tab == "Services" ? Visibility.Visible : Visibility.Collapsed;

            // Контент
            SessionsList.Visibility = tab == "Sessions" ? Visibility.Visible : Visibility.Collapsed;
            DeletedPcsList.Visibility = tab == "Sessions" ? Visibility.Visible : Visibility.Collapsed;
            ServicesList.Visibility = tab == "Services" ? Visibility.Visible : Visibility.Collapsed;
            AllList.Visibility = tab == "All" ? Visibility.Visible : Visibility.Collapsed;

            // Метки статистики
            switch (tab)
            {
                case "Sessions":
                    TblTodayLabel.Text = "Выручка сегодня";
                    TblWeekLabel.Text = "За неделю";
                    TblMonthLabel.Text = "За месяц";
                    TblCountLabel.Text = "Всего сессий";
                    break;
                case "Services":
                    TblTodayLabel.Text = "Услуги сегодня";
                    TblWeekLabel.Text = "За неделю";
                    TblMonthLabel.Text = "За месяц";
                    TblCountLabel.Text = "Транзакций";
                    break;
                case "All":
                    TblTodayLabel.Text = "Итого сегодня";
                    TblWeekLabel.Text = "За неделю";
                    TblMonthLabel.Text = "За месяц";
                    TblCountLabel.Text = "Операций";
                    break;
            }

            UpdateStats();
            ApplyFilter();
        }

        // =====================
        // Статистика
        // =====================

        private void UpdateStats()
        {
            var today = DateTime.Today;
            var weekStart = today.AddDays(-(int)today.DayOfWeek + 1);
            var monthStart = new DateTime(today.Year, today.Month, 1);

            switch (_activeTab)
            {
                case "Sessions":
                    TxtToday.Text  = $"{Sessions.Where(s => s.EndTime.Date == today).Sum(s => s.EarnedAmount):N0} сум";
                    TxtWeek.Text   = $"{Sessions.Where(s => s.EndTime.Date >= weekStart).Sum(s => s.EarnedAmount):N0} сум";
                    TxtMonth.Text  = $"{Sessions.Where(s => s.EndTime.Date >= monthStart).Sum(s => s.EarnedAmount):N0} сум";
                    TxtTotalSessions.Text = Sessions.Count.ToString();
                    break;

                case "Services":
                    var svc = ServiceTransaction.All;
                    TxtToday.Text  = $"{svc.Where(t => t.CreatedAt.Date == today).Sum(t => t.TotalAmount):N0} сум";
                    TxtWeek.Text   = $"{svc.Where(t => t.CreatedAt.Date >= weekStart).Sum(t => t.TotalAmount):N0} сум";
                    TxtMonth.Text  = $"{svc.Where(t => t.CreatedAt.Date >= monthStart).Sum(t => t.TotalAmount):N0} сум";
                    TxtTotalSessions.Text = svc.Count.ToString();
                    break;

                case "All":
                    int todayS  = Sessions.Where(s => s.EndTime.Date == today).Sum(s => s.EarnedAmount);
                    int todaySv = ServiceTransaction.All.Where(t => t.CreatedAt.Date == today).Sum(t => t.TotalAmount);
                    int weekS   = Sessions.Where(s => s.EndTime.Date >= weekStart).Sum(s => s.EarnedAmount);
                    int weekSv  = ServiceTransaction.All.Where(t => t.CreatedAt.Date >= weekStart).Sum(t => t.TotalAmount);
                    int monS    = Sessions.Where(s => s.EndTime.Date >= monthStart).Sum(s => s.EarnedAmount);
                    int monSv   = ServiceTransaction.All.Where(t => t.CreatedAt.Date >= monthStart).Sum(t => t.TotalAmount);
                    TxtToday.Text  = $"{todayS + todaySv:N0} сум";
                    TxtWeek.Text   = $"{weekS + weekSv:N0} сум";
                    TxtMonth.Text  = $"{monS + monSv:N0} сум";
                    TxtTotalSessions.Text = (Sessions.Count + ServiceTransaction.All.Count).ToString();
                    break;
            }
        }

        // =====================
        // Фильтрация и рендер
        // =====================

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (SessionsList == null) return;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var today = DateTime.Today;

            DateTime? from = null;
            if (RbToday.IsChecked == true)
                from = today;
            else if (RbWeek.IsChecked == true)
                from = today.AddDays(-(int)today.DayOfWeek + 1);
            else if (RbMonth.IsChecked == true)
                from = new DateTime(today.Year, today.Month, 1);

            switch (_activeTab)
            {
                case "Sessions":
                    RenderSessions(FilterSessions(from));
                    break;
                case "Services":
                    RenderServices(FilterServices(from));
                    break;
                case "All":
                    RenderAll(from);
                    break;
            }

            UpdateStats();
        }

        private IEnumerable<SessionRecord> FilterSessions(DateTime? from)
        {
            var q = Sessions.AsEnumerable();
            if (from.HasValue)
                q = q.Where(s => s.EndTime.Date >= from.Value);

            var type = (CmbType.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (type != null && type != "Все типы")
                q = q.Where(s => s.SessionType == type);

            return q;
        }

        private IEnumerable<ServiceTransaction> FilterServices(DateTime? from)
        {
            var q = ServiceTransaction.All.AsEnumerable();
            if (from.HasValue)
                q = q.Where(t => t.CreatedAt.Date >= from.Value);

            var status = (CmbServiceStatus.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (status == "Оплачено")
                q = q.Where(t => t.IsPaid);
            else if (status == "Не оплачено")
                q = q.Where(t => !t.IsPaid);

            return q;
        }

        // =====================
        // Рендер: Сессии
        // =====================

        private void RenderSessions(IEnumerable<SessionRecord> sessions)
        {
            SessionsList.Children.Clear();
            var list = sessions.ToList();

            if (!list.Any())
            {
                SessionsList.Children.Add(EmptyLabel("Нет записей о сессиях"));
                TxtFilterResult.Text = "Показано: 0";
                return;
            }

            bool alt = false;
            foreach (var s in list)
            {
                var row = CreateRowBorder(alt);
                alt = !alt;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

                AddCell(grid, 0, s.PcNumber, Color.FromRgb(30, 30, 46), bold: true);

                var typeColor = s.SessionType switch
                {
                    "VIP"        => Color.FromRgb(133, 79, 11),
                    "Лимит"      => Color.FromRgb(15, 110, 86),
                    "По времени" => Color.FromRgb(15, 110, 86),
                    "По деньгам" => Color.FromRgb(24, 95, 165),
                    _            => Color.FromRgb(100, 100, 100)
                };
                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(30, typeColor.R, typeColor.G, typeColor.B)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = s.SessionType, FontSize = 11, Foreground = new SolidColorBrush(typeColor) }
                };
                Grid.SetColumn(badge, 1);
                grid.Children.Add(badge);

                AddCell(grid, 2, string.IsNullOrEmpty(s.ReaderId) ? "—" : s.ReaderId, Color.FromRgb(80, 80, 80));
                AddCell(grid, 3, s.UserName, Color.FromRgb(80, 80, 80));

                int h = s.DurationSeconds / 3600, m = (s.DurationSeconds % 3600) / 60, sec = s.DurationSeconds % 60;
                AddCell(grid, 4, $"{h:D2}:{m:D2}:{sec:D2}", Color.FromRgb(80, 80, 80));
                AddCell(grid, 5, $"{s.EarnedAmount:N0} сум", Color.FromRgb(15, 110, 86), bold: true);
                AddCell(grid, 6, s.EndTime.ToString("dd.MM HH:mm"), Color.FromRgb(150, 150, 150));

                row.Child = grid;
                SessionsList.Children.Add(row);
            }

            TxtFilterResult.Text = $"Показано: {list.Count}";
            RenderDeletedPcs();
        }

        // =====================
        // Рендер: Услуги
        // =====================

        private void RenderServices(IEnumerable<ServiceTransaction> services)
        {
            ServicesList.Children.Clear();
            var list = services.ToList();

            if (!list.Any())
            {
                ServicesList.Children.Add(EmptyLabel("Нет транзакций услуг"));
                TxtFilterResult.Text = "Показано: 0";
                return;
            }

            bool alt = false;
            foreach (var t in list)
            {
                var row = CreateRowBorder(alt);
                alt = !alt;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

                AddCell(grid, 0, t.ServiceName, Color.FromRgb(30, 30, 46), bold: true);
                AddCell(grid, 1, $"{t.Quantity} {t.Unit}", Color.FromRgb(80, 80, 80));
                AddCell(grid, 2, $"{t.TotalAmount:N0} сум", Color.FromRgb(15, 110, 86), bold: true);

                string readerDisplay = !string.IsNullOrEmpty(t.ReaderName) ? t.ReaderName
                    : !string.IsNullOrEmpty(t.ReaderId) ? t.ReaderId : "—";
                AddCell(grid, 3, readerDisplay, Color.FromRgb(80, 80, 80));

                // Бейдж статуса оплаты
                bool paid = t.IsPaid;
                var statusColor = paid ? Color.FromRgb(15, 110, 86) : Color.FromRgb(133, 79, 11);
                var statusBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(30, statusColor.R, statusColor.G, statusColor.B)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = paid ? "Оплачено" : $"Долг: {t.DebtAmount:N0}",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(statusColor)
                    }
                };
                Grid.SetColumn(statusBadge, 4);
                grid.Children.Add(statusBadge);

                AddCell(grid, 5, t.CreatedAt.ToString("dd.MM HH:mm"), Color.FromRgb(150, 150, 150));

                row.Child = grid;
                ServicesList.Children.Add(row);
            }

            TxtFilterResult.Text = $"Показано: {list.Count}";
        }

        // =====================
        // Рендер: Всё вместе
        // =====================

        private void RenderAll(DateTime? from)
        {
            AllList.Children.Clear();

            // Собираем объединённый список: (дата, тип, описание, читатель, сумма, статус)
            var items = new List<(DateTime Date, string Category, string Description, string Reader, int Amount, string Status)>();

            var sessions = Sessions.AsEnumerable();
            if (from.HasValue) sessions = sessions.Where(s => s.EndTime.Date >= from.Value);
            foreach (var s in sessions)
                items.Add((s.EndTime, "Сессия", $"{s.PcNumber} · {s.SessionType}",
                    string.IsNullOrEmpty(s.UserName) || s.UserName == "—" ? (s.ReaderId ?? "—") : s.UserName,
                    s.EarnedAmount, "Оплачено"));

            var svcList = ServiceTransaction.All.AsEnumerable();
            if (from.HasValue) svcList = svcList.Where(t => t.CreatedAt.Date >= from.Value);
            foreach (var t in svcList)
            {
                string reader = !string.IsNullOrEmpty(t.ReaderName) ? t.ReaderName
                    : !string.IsNullOrEmpty(t.ReaderId) ? t.ReaderId : "—";
                items.Add((t.CreatedAt, "Услуга", $"{t.ServiceName} ×{t.Quantity}",
                    reader, t.TotalAmount, t.IsPaid ? "Оплачено" : $"Долг: {t.DebtAmount:N0}"));
            }

            items = items.OrderByDescending(x => x.Date).ToList();

            if (!items.Any())
            {
                AllList.Children.Add(EmptyLabel("Нет операций"));
                TxtFilterResult.Text = "Показано: 0";
                return;
            }

            bool alt = false;
            foreach (var (date, category, desc, reader, amount, status) in items)
            {
                var row = CreateRowBorder(alt);
                alt = !alt;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

                bool isService = category == "Услуга";
                var catColor = isService ? Color.FromRgb(24, 95, 165) : Color.FromRgb(15, 110, 86);
                var catBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(30, catColor.R, catColor.G, catColor.B)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 2, 5, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = category, FontSize = 11, Foreground = new SolidColorBrush(catColor) }
                };
                Grid.SetColumn(catBadge, 0);
                grid.Children.Add(catBadge);

                AddCell(grid, 1, desc, Color.FromRgb(30, 30, 46), bold: true);
                AddCell(grid, 2, reader, Color.FromRgb(80, 80, 80));
                AddCell(grid, 3, $"{amount:N0} сум", Color.FromRgb(15, 110, 86), bold: true);

                bool isPaid = status == "Оплачено";
                var stColor = isPaid ? Color.FromRgb(15, 110, 86) : Color.FromRgb(133, 79, 11);
                var stBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(30, stColor.R, stColor.G, stColor.B)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 2, 5, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = status, FontSize = 11, Foreground = new SolidColorBrush(stColor) }
                };
                Grid.SetColumn(stBadge, 4);
                grid.Children.Add(stBadge);

                AddCell(grid, 5, date.ToString("dd.MM HH:mm"), Color.FromRgb(150, 150, 150));

                row.Child = grid;
                AllList.Children.Add(row);
            }

            TxtFilterResult.Text = $"Показано: {items.Count}";
        }

        // =====================
        // Удалённые ПК
        // =====================

        private void RenderDeletedPcs()
        {
            DeletedPcsList.Children.Clear();
            var list = AdminHub.DeletedPcs;
            if (list.Count == 0) return;

            DeletedPcsList.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(248, 240, 240)),
                Padding = new Thickness(16, 10, 16, 10),
                Margin = new Thickness(0, 16, 0, 0),
                Child = new TextBlock
                {
                    Text = $"История удалённых ПК ({list.Count})",
                    FontSize = 13, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(180, 60, 60))
                }
            });

            bool alt = false;
            foreach (var d in list.OrderByDescending(x => x.DeletedAt))
            {
                var row = new Border
                {
                    Background = alt
                        ? new SolidColorBrush(Color.FromRgb(255, 250, 250))
                        : Brushes.White,
                    Padding = new Thickness(16, 9, 16, 9)
                };
                alt = !alt;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });

                AddCell(grid, 0, d.PcNumber, Color.FromRgb(30, 30, 46), bold: true);
                AddCell(grid, 1, d.MacAddress, Color.FromRgb(120, 120, 120));
                AddCell(grid, 2, d.Ip, Color.FromRgb(120, 120, 120));
                AddCell(grid, 3, d.DeletedAt.ToString("dd.MM.yyyy HH:mm"), Color.FromRgb(180, 60, 60));

                row.Child = grid;
                DeletedPcsList.Children.Add(row);
            }
        }

        // =====================
        // История: загрузка / сохранение
        // =====================

        public static void LoadHistory()
        {
            try
            {
                if (File.Exists(HistoryFilePath))
                {
                    var json = File.ReadAllText(HistoryFilePath);
                    if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]")
                    {
                        Logger.Info("📂 Файл истории финансов пустой");
                        return;
                    }

                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    };
                    var records = JsonSerializer.Deserialize<List<SessionRecord>>(json, options);
                    if (records != null && records.Count > 0)
                    {
                        var existingKeys = new HashSet<(DateTime, string)>(
                            Sessions.Select(s => (s.StartTime, s.PcNumber)));

                        int added = 0;
                        foreach (var record in records)
                        {
                            if (string.IsNullOrEmpty(record.PcNumber) || record.EndTime == default) continue;
                            var key = (record.StartTime, record.PcNumber);
                            if (!existingKeys.Contains(key))
                            {
                                Sessions.Add(record);
                                existingKeys.Add(key);
                                added++;
                            }
                        }
                        Sessions.Sort((a, b) => b.EndTime.CompareTo(a.EndTime));
                        Logger.Info($"📂 Загружено {Sessions.Count} записей (добавлено {added} новых)");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка загрузки истории финансов: {ex.Message}");
            }
        }

        public static void SaveHistory()
        {
            try
            {
                var directory = Path.GetDirectoryName(HistoryFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                var valid = Sessions
                    .Where(s => !string.IsNullOrEmpty(s.PcNumber) && s.EndTime != default)
                    .ToList();

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var json = JsonSerializer.Serialize(valid, options);

                var tmp = HistoryFilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(HistoryFilePath))
                    File.Replace(tmp, HistoryFilePath, null);
                else
                    File.Move(tmp, HistoryFilePath);

                Logger.Info($"💾 Сохранено {valid.Count} записей в {HistoryFilePath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка сохранения истории финансов: {ex.Message}");
            }
        }

        public static void AddSession(SessionRecord session)
        {
            if (string.IsNullOrEmpty(session.PcNumber))
            {
                Logger.Error($"⚠️ Попытка добавить сессию с пустым PcNumber.");
                return;
            }
            Logger.Info($"💰 Сессия: ПК={session.PcNumber}, Тип={session.SessionType}, Сумма={session.EarnedAmount}");
            Sessions.Insert(0, session);
            SaveHistory();
        }

        // =====================
        // Очистка истории
        // =====================

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            string question = _activeTab switch
            {
                "Sessions" => "Очистить историю сессий?",
                "Services" => "Очистить историю услуг?",
                _ => "Очистить историю сессий и услуг?"
            };

            var r = MessageBox.Show(question, "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            if (_activeTab == "Sessions" || _activeTab == "All")
            {
                Sessions.Clear();
                ComputersPage.Revenue = 0;
                SaveHistory();
            }
            if (_activeTab == "Services" || _activeTab == "All")
            {
                ServiceTransaction.All.Clear();
                ServiceTransaction.SaveHistory();
            }

            UpdateStats();
            ApplyFilter();
        }

        // =====================
        // Экспорт в CSV
        // =====================

        private void ExportToCsv_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CSV файлы (*.csv)|*.csv|Все файлы (*.*)|*.*",
                DefaultExt = "csv",
                FileName = $"finance_export_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                Title = "Экспорт в Excel (CSV)"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                var sb = new StringBuilder();

                if (_activeTab == "Sessions" || _activeTab == "All")
                {
                    sb.AppendLine("=== СЕССИИ ===");
                    sb.AppendLine("ПК;Тип;ID читателя;Пользователь;Длительность;Сумма;Оплачено;Возврат;Начало;Конец");
                    foreach (var s in Sessions)
                    {
                        int h = s.DurationSeconds / 3600, m2 = (s.DurationSeconds % 3600) / 60, sec = s.DurationSeconds % 60;
                        sb.AppendLine($"{s.PcNumber};{s.SessionType};{s.ReaderId};{s.UserName};{h:D2}:{m2:D2}:{sec:D2};{s.EarnedAmount};{s.PaidAmount};{s.RefundAmount};{s.StartTime:dd.MM.yyyy HH:mm};{s.EndTime:dd.MM.yyyy HH:mm}");
                    }
                }

                if (_activeTab == "Services" || _activeTab == "All")
                {
                    if (_activeTab == "All") sb.AppendLine();
                    sb.AppendLine("=== УСЛУГИ ===");
                    sb.AppendLine("Услуга;Единица;Кол-во;Цена/ед;Итого;Оплачено;Читатель;Дата");
                    foreach (var t in ServiceTransaction.All)
                        sb.AppendLine($"{t.ServiceName};{t.Unit};{t.Quantity};{t.PricePerUnit};{t.TotalAmount};{t.PaidAmount};{t.ReaderName};{t.CreatedAt:dd.MM.yyyy HH:mm}");
                }

                File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);

                MessageBox.Show(
                    $"Экспорт выполнен!\n\nФайл: {dialog.FileName}",
                    "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка экспорта: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =====================
        // Вспомогательные методы
        // =====================

        private static Border CreateRowBorder(bool alt) => new()
        {
            Background = alt
                ? new SolidColorBrush(Color.FromRgb(250, 250, 250))
                : Brushes.White,
            Padding = new Thickness(16, 10, 16, 10)
        };

        private static UIElement EmptyLabel(string text) => new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 40, 0, 0)
        };

        private static void AddCell(Grid grid, int col, string text, Color color, bool bold = false)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = bold ? FontWeights.Medium : FontWeights.Normal,
                Foreground = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
    }
}
