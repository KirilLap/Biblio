using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BibAdmin
{
    public partial class ServiceOrderDialog : Window
    {
        private GlobalSettings _settings;
        private ServiceType? _selectedService;

        public ServiceOrderDialog()
        {
            InitializeComponent();
            Owner = Application.Current.MainWindow;
            _settings = GlobalSettings.Load();
            LoadServices();
            TxtReader.TextChanged += TxtReader_Changed;
            RbDeferred.Checked += PaymentMode_Changed;
            RbPaidNow.Checked += PaymentMode_Changed;
        }

        private void LoadServices()
        {
            CmbService.Items.Clear();
            var active = _settings.Services.Where(s => s.IsActive).ToList();
            foreach (var s in active)
                CmbService.Items.Add(s);

            CmbService.DisplayMemberPath = "Name";

            if (active.Count > 0)
                CmbService.SelectedIndex = 0;
        }

        private void CmbService_Changed(object sender, SelectionChangedEventArgs e)
        {
            _selectedService = CmbService.SelectedItem as ServiceType;
            UpdateTotal();
        }

        private void TxtQty_Changed(object sender, TextChangedEventArgs e) => UpdateTotal();

        private void UpdateTotal()
        {
            if (_selectedService == null) return;

            if (!int.TryParse(TxtQty.Text, out int qty) || qty < 1)
            {
                TxtTotal.Text = "—";
                TxtServiceInfo.Text = "Укажите корректное количество";
                return;
            }

            int total = _selectedService.Price * qty;
            TxtTotal.Text = $"{total:N0} сум";
            TxtServiceInfo.Text = $"{_selectedService.Price:N0} сум × {qty} {_selectedService.Unit}";
        }

        private void TxtReader_Changed(object sender, TextChangedEventArgs e)
        {
            string reader = TxtReader.Text.Trim();
            bool hasReader = !string.IsNullOrEmpty(reader);

            // Проверяем активные сессии по ID или имени
            var activePc = AdminHub.KnownClients.Values
                .FirstOrDefault(c => c.SessionStart.HasValue &&
                    (!string.IsNullOrEmpty(c.ReaderId) && c.ReaderId.Equals(reader, StringComparison.OrdinalIgnoreCase) ||
                     !string.IsNullOrEmpty(c.UserName) && c.UserName.Contains(reader, StringComparison.OrdinalIgnoreCase)));

            if (activePc != null)
            {
                TxtActiveSession.Text = $"✓ Активная сессия на {activePc.PcNumber}";
                TxtActiveSession.Visibility = Visibility.Visible;
            }
            else
            {
                TxtActiveSession.Visibility = Visibility.Collapsed;
            }

            // Без читателя нельзя отложить
            if (!hasReader && RbDeferred.IsChecked == true)
                RbPaidNow.IsChecked = true;

            TxtDeferNote.Visibility = !hasReader ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PaymentMode_Changed(object sender, RoutedEventArgs e)
        {
            if (TxtDeferNote == null) return;
            bool hasReader = !string.IsNullOrEmpty(TxtReader.Text.Trim());
            TxtDeferNote.Visibility = (!hasReader && RbDeferred.IsChecked == true)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedService == null)
            {
                MessageBox.Show("Выберите услугу.", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtQty.Text, out int qty) || qty < 1)
            {
                MessageBox.Show("Укажите корректное количество (целое число ≥ 1).", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string reader = TxtReader.Text.Trim();
            bool deferred = RbDeferred.IsChecked == true && !string.IsNullOrEmpty(reader);

            int total = _selectedService.Price * qty;
            int paid = deferred ? 0 : total;

            // Определяем ID и имя читателя
            string readerId = "";
            string readerName = reader;

            // Пробуем найти по активным сессиям для привязки ID
            var activePc = AdminHub.KnownClients.Values
                .FirstOrDefault(c => c.SessionStart.HasValue &&
                    (!string.IsNullOrEmpty(c.ReaderId) && c.ReaderId.Equals(reader, StringComparison.OrdinalIgnoreCase) ||
                     !string.IsNullOrEmpty(c.UserName) && c.UserName.Contains(reader, StringComparison.OrdinalIgnoreCase)));

            if (activePc != null)
            {
                readerId = activePc.ReaderId ?? reader;
                readerName = activePc.UserName ?? reader;
            }
            else if (!string.IsNullOrEmpty(reader))
            {
                // Текст может быть ID или именем — сохраняем как есть
                readerId = reader;
            }

            var transaction = new ServiceTransaction
            {
                ServiceTypeId = _selectedService.Id,
                ServiceName = _selectedService.Name,
                Unit = _selectedService.Unit,
                Quantity = qty,
                PricePerUnit = _selectedService.Price,
                TotalAmount = total,
                PaidAmount = paid,
                ReaderId = readerId,
                ReaderName = readerName,
                CreatedAt = DateTime.Now,
                PaidAt = deferred ? null : DateTime.Now
            };

            ServiceTransaction.Add(transaction);

            Logger.Info($"🛎 Услуга: {_selectedService.Name} ×{qty} = {total} сум, читатель: {readerName}, оплачено: {paid}");

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
