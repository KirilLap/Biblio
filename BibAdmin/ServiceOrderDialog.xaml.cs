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

        // Вспомогательный класс для элементов ComboBox сессий
        private class PcSessionItem
        {
            public string PcNumber { get; set; } = "";
            public string ReaderId { get; set; } = "";
            public string ReaderName { get; set; } = "";
            public bool HasSession { get; set; }
            public override string ToString()
            {
                if (!HasSession) return "— Без привязки —";
                string reader = !string.IsNullOrEmpty(ReaderName) ? ReaderName
                    : !string.IsNullOrEmpty(ReaderId) ? $"ID: {ReaderId}" : "(анонимная сессия)";
                return $"{PcNumber}  —  {reader}";
            }
        }

        public ServiceOrderDialog()
        {
            InitializeComponent();
            Owner = Application.Current.MainWindow;
            _settings = GlobalSettings.Load();
            LoadServices();
            LoadSessions();
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
            if (active.Count > 0) CmbService.SelectedIndex = 0;
        }

        private void LoadSessions()
        {
            CmbPcSession.Items.Clear();
            CmbPcSession.Items.Add(new PcSessionItem { HasSession = false });

            var activeSessions = AdminHub.KnownClients.Values
                .Where(c => c.SessionStart.HasValue)
                .OrderBy(c => c.PcNumberValue)
                .ToList();

            foreach (var c in activeSessions)
            {
                CmbPcSession.Items.Add(new PcSessionItem
                {
                    PcNumber = c.PcNumber,
                    ReaderId = c.ReaderId ?? "",
                    ReaderName = c.UserName ?? "",
                    HasSession = true
                });
            }

            CmbPcSession.SelectedIndex = 0;
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

        private void CmbPcSession_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbPcSession.SelectedItem is not PcSessionItem item) return;

            if (item.HasSession)
            {
                string reader = !string.IsNullOrEmpty(item.ReaderName) ? item.ReaderName
                    : !string.IsNullOrEmpty(item.ReaderId) ? $"ID: {item.ReaderId}" : "";
                TxtSessionInfo.Text = string.IsNullOrEmpty(reader)
                    ? $"✓ Активная сессия на {item.PcNumber} (анонимный пользователь)"
                    : $"✓ Активная сессия на {item.PcNumber}: {reader}";
                TxtSessionInfo.Visibility = Visibility.Visible;
            }
            else
            {
                TxtSessionInfo.Visibility = Visibility.Collapsed;
            }

            UpdateDeferNote();
        }

        private void PaymentMode_Changed(object sender, RoutedEventArgs e) => UpdateDeferNote();

        private void UpdateDeferNote()
        {
            if (TxtDeferNote == null) return;
            bool selectedSession = CmbPcSession.SelectedItem is PcSessionItem item && item.HasSession;
            bool wantDeferred = RbDeferred?.IsChecked == true;
            TxtDeferNote.Visibility = (wantDeferred && !selectedSession) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedService == null)
            {
                MessageBox.Show("Выберите услугу.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(TxtQty.Text, out int qty) || qty < 1)
            {
                MessageBox.Show("Укажите корректное количество (целое число ≥ 1).", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedItem = CmbPcSession.SelectedItem as PcSessionItem;
            bool hasSession = selectedItem?.HasSession == true;
            bool deferred = RbDeferred.IsChecked == true && hasSession;

            int total = _selectedService.Price * qty;
            int paid = deferred ? 0 : total;

            var transaction = new ServiceTransaction
            {
                ServiceTypeId = _selectedService.Id,
                ServiceName = _selectedService.Name,
                Unit = _selectedService.Unit,
                Quantity = qty,
                PricePerUnit = _selectedService.Price,
                TotalAmount = total,
                PaidAmount = paid,
                PcNumber = hasSession ? selectedItem!.PcNumber : "",
                ReaderId = hasSession ? selectedItem!.ReaderId : "",
                ReaderName = hasSession ? selectedItem!.ReaderName : "",
                CreatedAt = DateTime.Now,
                PaidAt = deferred ? null : DateTime.Now
            };

            ServiceTransaction.Add(transaction);

            string pcInfo = hasSession ? $", ПК: {selectedItem!.PcNumber}" : "";
            Logger.Info($"🛎 Услуга: {_selectedService.Name} ×{qty} = {total} сум{pcInfo}, оплачено: {paid}");

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
