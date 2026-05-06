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
        // Статическое хранилище сессий — доступно из ComputersPage
        public static List<SessionRecord> Sessions { get; } = new();
        
        // Путь к файлу истории: %APPDATA%\BibAdmin\finance_history.json
        private static readonly string HistoryFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BibAdmin",
            "finance_history.json");

        public FinancePage()
        {
            InitializeComponent();
            TxtCurrentDate.Text = DateTime.Now.ToString(
                "dddd, d MMMM yyyy",
                new System.Globalization.CultureInfo("ru-RU"));

            // ❌ УБРАНА загрузка истории здесь - она загружается только из MainWindow
            // LoadHistory() больше не вызывается в конструкторе!
            
            UpdateStats();
            RenderSessions(Sessions);
        }
        
        // Загрузка истории из JSON-файла
        public static void LoadHistory()
        {
            try
            {
                if (File.Exists(HistoryFilePath))
                {
                    var json = File.ReadAllText(HistoryFilePath);
                    var records = JsonSerializer.Deserialize<List<SessionRecord>>(json);
                    if (records != null)
                    {
                        // 🔥 КРИТИЧНО: Не очищаем Sessions.Clear() - это удаляет ранее добавленные сессии!
                        // Вместо этого добавляем только отсутствующие записи
                        
                        // Создаём HashSet ключей существующих записей (StartTime + PcNumber)
                        var existingKeys = new HashSet<(DateTime, string)>(
                            Sessions.Select(s => (s.StartTime, s.PcNumber)));
                        
                        int addedCount = 0;
                        foreach (var record in records)
                        {
                            // Пропускаем пустые записи
                            if (string.IsNullOrEmpty(record.PcNumber) || record.EndTime == default)
                                continue;
                                
                            // Добавляем только если запись ещё не существует
                            var key = (record.StartTime, record.PcNumber);
                            if (!existingKeys.Contains(key))
                            {
                                Sessions.Add(record);
                                existingKeys.Add(key);
                                addedCount++;
                            }
                        }
                        
                        // Сортируем по времени окончания (новые сверху)
                        Sessions.Sort((a, b) => b.EndTime.CompareTo(a.EndTime));
                        
                        Logger.Info($"📂 Загружено {Sessions.Count} записей (добавлено {addedCount} новых)");
                    }
                }
                else
                {
                    Logger.Info("📂 Файл истории финансов не найден");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка загрузки истории финансов: {ex.Message}");
            }
        }
        
        // Сохранение истории в JSON-файл
        public static void SaveHistory()
        {
            try
            {
                var directory = Path.GetDirectoryName(HistoryFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                
                // 🔥 Фильтруем только валидные записи перед сохранением
                var validSessions = Sessions
                    .Where(s => !string.IsNullOrEmpty(s.PcNumber) && s.EndTime != default)
                    .ToList();
                
                var options = new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var json = JsonSerializer.Serialize(validSessions, options);
                File.WriteAllText(HistoryFilePath, json);
                Logger.Info($"💾 Сохранено {validSessions.Count} записей в {HistoryFilePath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка сохранения истории финансов: {ex.Message}");
            }
        }

        // Добавить сессию (вызывается из ComputersPage)
        public static void AddSession(SessionRecord session)
        {
            // 🔥 КРИТИЧНО: Проверяем что данные не пустые перед добавлением
            if (string.IsNullOrEmpty(session.PcNumber))
            {
                Logger.Error($"⚠️ Попытка добавить сессию с пустым PcNumber. SessionType={session.SessionType}, ReaderId={session.ReaderId}");
                return;
            }
            
            Logger.Info($"💰 Добавлена сессия: ПК={session.PcNumber}, Тип={session.SessionType}, Пользователь={session.UserName}, Длительность={session.DurationSeconds}с, Сумма={session.EarnedAmount} сум");
            
            Sessions.Insert(0, session);
            SaveHistory(); // Автосохранение после добавления сессии
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
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); // ID читателя
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
                    "VIP"          => Color.FromRgb(133, 79, 11),
                    "Лимит"        => Color.FromRgb(15, 110, 86),
                    // Совместимость со старыми записями в истории
                    "По времени"   => Color.FromRgb(15, 110, 86),
                    "По деньгам"   => Color.FromRgb(24, 95, 165),
                    _              => Color.FromRgb(100, 100, 100)
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

                // ID читателя
                var readerIdText = new TextBlock
                {
                    Text = string.IsNullOrEmpty(s.ReaderId) ? "—" : s.ReaderId,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(readerIdText, 2);
                grid.Children.Add(readerIdText);

                // Пользователь
                var userText = new TextBlock
                {
                    Text = s.UserName,
                    FontSize = 13,
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(userText, 3);
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
                Grid.SetColumn(durationText, 4);
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
                Grid.SetColumn(amountText, 5);
                grid.Children.Add(amountText);

                // Дата и время
                var dateText = new TextBlock
                {
                    Text = s.EndTime.ToString("dd.MM HH:mm"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(dateText, 6);
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
            // RbAll не требует фильтрации - оставляем все записи

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
                SaveHistory(); // Сохраняем очистку на диск
                UpdateStats();
                RenderSessions(Sessions);
            }
        }
        
        // Экспорт в CSV (Excel)
        private void ExportToCsv_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CSV файлы (*.csv)|*.csv|Все файлы (*.*)|*.*",
                DefaultExt = "csv",
                FileName = $"finance_export_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                Title = "Экспорт истории финансов в Excel (CSV)"
            };
            
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var sb = new StringBuilder();
                    // Заголовок CSV
                    sb.AppendLine("ПК;Тип сессии;ID читателя;Пользователь;Длительность (сек);Сумма (сум);Оплачено;Возврат;Начало;Конец");
                    
                    foreach (var s in Sessions)
                    {
                        int h = s.DurationSeconds / 3600;
                        int m = (s.DurationSeconds % 3600) / 60;
                        int sec = s.DurationSeconds % 60;
                        string duration = $"{h:D2}:{m:D2}:{sec:D2}";
                        
                        sb.AppendLine($"{s.PcNumber};{s.SessionType};{s.ReaderId};{s.UserName};{duration};{s.EarnedAmount};{s.PaidAmount};{s.RefundAmount};{s.StartTime:dd.MM.yyyy HH:mm};{s.EndTime:dd.MM.yyyy HH:mm}");
                    }
                    
                    // Пишем с BOM для корректного отображения кириллицы в Excel
                    File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                    
                    MessageBox.Show(
                        $"Экспорт выполнен успешно!\n\nФайл: {dialog.FileName}\nЗаписей: {Sessions.Count}",
                        "Экспорт завершён",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Ошибка экспорта: {ex.Message}",
                        "Ошибка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }
    }
}