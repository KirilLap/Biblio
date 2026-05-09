using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.AspNetCore.SignalR.Client;

namespace BibAdmin
{
    public partial class SettingsPage : Page
    {
        private HubConnection? _hub;
        private GlobalSettings _global = GlobalSettings.Load();

        public SettingsPage()
        {
            InitializeComponent();
            LoadGlobalSettingsToUI();
            ConnectToHub();
        }

        // Загружаем сохранённые глобальные настройки в UI
        private void LoadGlobalSettingsToUI()
        {
            try
            {
                TxtTariff.Text = _global.Tariff.ToString();
                ComputersPage.Tariff = _global.Tariff;

                SliderOpacity.Value = _global.BackgroundOpacity * 100;
                TxtOpacityValue.Text = $"{(int)(SliderOpacity.Value)}%";

                ChkShowPcName.IsChecked = _global.ShowPcName;
                ChkShowPcNumber.IsChecked = _global.ShowPcNumber;
                SliderPcFontSize.Value = _global.PcNumberFontSize;
                TxtPcFontValue.Text = $"{(int)_global.PcNumberFontSize}";

                ChkShowLockedText.IsChecked = _global.ShowLockedText;
                SliderLockedTextFontSize.Value = _global.LockedTextFontSize;
                TxtLockedFontValue.Text = $"{(int)_global.LockedTextFontSize}";

                SliderTimeFontSize.Value = _global.TimeFontSize;
                TxtTimeFontValue.Text = $"{(int)_global.TimeFontSize}";

                ChkBlockUsb.IsChecked = _global.UsbBlocked;
                ChkDisableTaskMgr.IsChecked = _global.TaskMgrDisabled;
                ChkBlockRegedit.IsChecked = _global.BlockRegedit;
                ChkBlockCmd.IsChecked = _global.BlockCmd;
                ChkBlockPowerShell.IsChecked = _global.BlockPowerShell;
                ChkHideDriveC.IsChecked = _global.HideDriveC;
                ChkBlockInstall.IsChecked = _global.BlockInstall;
                ChkLockOnOffline.IsChecked = _global.LockOnOffline;
                ChkPreventClose.IsChecked = _global.PreventClose;
                ChkAutoStartWithUser.IsChecked = _global.AutoStartWithUser;

                SelectComboByTag(CmbPcNumberPosition, _global.PcNumberPosition);
                SelectComboByTag(CmbLockedTextPosition, _global.LockedTextPosition);
                SelectComboByTag(CmbTimePosition, _global.TimePosition);

                if (!string.IsNullOrEmpty(_global.BackgroundFileName))
                {
                    string bgPath = Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "Files", _global.BackgroundFileName);
                    if (File.Exists(bgPath))
                    {
                        TxtBgPath.Text = bgPath;
                        ImgPreview.Source = new BitmapImage(new Uri(bgPath));
                    }
                }

                RenderServicesPanel();
                Logger.Info("Настройки загружены в UI");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка загрузки: {ex.Message}");
            }
        }

        // =====================
        // Дополнительные услуги
        // =====================

        private void RenderServicesPanel()
        {
            ServicesPanel.Children.Clear();

            bool alt = false;
            foreach (var svc in _global.Services)
            {
                var row = new Border
                {
                    Background = alt
                        ? new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(250, 250, 250))
                        : System.Windows.Media.Brushes.White,
                    Padding = new Thickness(12, 8, 12, 8)
                };
                alt = !alt;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

                var captured = svc;

                var nameText = new TextBlock
                {
                    Text = svc.Name,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(30, 30, 46))
                };
                Grid.SetColumn(nameText, 0);
                grid.Children.Add(nameText);

                var unitText = new TextBlock
                {
                    Text = svc.Unit,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(100, 100, 100))
                };
                Grid.SetColumn(unitText, 1);
                grid.Children.Add(unitText);

                var priceText = new TextBlock
                {
                    Text = $"{svc.Price:N0} сум",
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(15, 110, 86))
                };
                Grid.SetColumn(priceText, 2);
                grid.Children.Add(priceText);

                var chk = new CheckBox
                {
                    IsChecked = svc.IsActive,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                chk.Checked += (s, e) => { captured.IsActive = true; SaveServices(); };
                chk.Unchecked += (s, e) => { captured.IsActive = false; SaveServices(); };
                Grid.SetColumn(chk, 3);
                grid.Children.Add(chk);

                var delBtn = new Button
                {
                    Content = "🗑",
                    FontSize = 12,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    ToolTip = "Удалить услугу"
                };
                delBtn.Click += (s, e) =>
                {
                    _global.Services.Remove(captured);
                    SaveServices();
                    RenderServicesPanel();
                };
                Grid.SetColumn(delBtn, 4);
                grid.Children.Add(delBtn);

                row.Child = grid;
                ServicesPanel.Children.Add(row);
            }
        }

        private void AddService_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtNewServiceName.Text.Trim();
            string unit = (CmbNewServiceUnit.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "лист";
            string priceStr = TxtNewServicePrice.Text.Trim();

            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Введите название услуги.", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(priceStr, out int price) || price < 0)
            {
                MessageBox.Show("Введите корректную цену (целое число ≥ 0).", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _global.Services.Add(new ServiceType
            {
                Name = name,
                Unit = unit,
                Price = price,
                IsActive = true
            });

            SaveServices();
            RenderServicesPanel();

            TxtNewServiceName.Text = "";
            TxtNewServicePrice.Text = "";
        }

        private void SaveServices()
        {
            _global.Save();
            ShowSaved(TxtServicesSaved);
        }

        private void SelectComboByTag(ComboBox cmb, string tag)
        {
            foreach (ComboBoxItem item in cmb.Items)
            {
                if (item.Tag?.ToString() == tag)
                {
                    cmb.SelectedItem = item;
                    return;
                }
            }
        }

        private async void ConnectToHub()
        {
            try
            {
                _hub = new HubConnectionBuilder()
                    .WithUrl("http://localhost:8080/hub")
                    .WithAutomaticReconnect()
                    .Build();
                await _hub.StartAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"Settings hub error: {ex.Message}");
            }
        }

        // =====================
        // Тариф
        // =====================
        private void SaveTariff_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(TxtTariff.Text, out int tariff) && tariff > 0)
            {
                ComputersPage.Tariff = tariff;
                _global.Tariff = tariff;
                _global.Save();
                ShowSaved(TxtTariffSaved);
            }
            else
            {
                MessageBox.Show("Введите корректный тариф", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // =====================
        // Пароль
        // =====================
        private async void SendPasswordAll_Click(object sender, RoutedEventArgs e)
        {
            string password = TxtNewPassword.Text.Trim();
            if (string.IsNullOrEmpty(password))
            {
                MessageBox.Show("Введите новый пароль", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var r = MessageBox.Show("Отправить новый пароль всем ПК?",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            _global.AdminPassword = password;
            _global.Save();

            await ApplyCommandToAll("ADMIN_PASSWORD", password);
            ShowSaved(TxtPasswordSaved);
            TxtNewPassword.Clear();
        }

        // =====================
        // Ограничения
        // =====================
        private async void ApplyRestrictions_Click(object sender, RoutedEventArgs e)
        {
            bool blockUsb = ChkBlockUsb.IsChecked == true;
            bool disableTaskMgr = ChkDisableTaskMgr.IsChecked == true;
            bool blockRegedit = ChkBlockRegedit.IsChecked == true;
            bool blockCmd = ChkBlockCmd.IsChecked == true;
            bool blockPowerShell = ChkBlockPowerShell.IsChecked == true;
            bool hideDriveC = ChkHideDriveC.IsChecked == true;
            bool blockInstall = ChkBlockInstall.IsChecked == true;
            bool preventClose = ChkPreventClose.IsChecked == true;
            bool autoStartWithUser = ChkAutoStartWithUser.IsChecked == true;

            var r = MessageBox.Show(
                $"Применить ограничения ко всем ПК?\n\n" +
                $"USB: {(blockUsb ? "Заблокировать" : "Разблокировать")}\n" +
                $"Диспетчер задач: {(disableTaskMgr ? "Отключить" : "Включить")}\n" +
                $"Реестр: {(blockRegedit ? "Заблокировать" : "Разрешить")}\n" +
                $"CMD: {(blockCmd ? "Заблокировать" : "Разрешить")}\n" +
                $"PowerShell: {(blockPowerShell ? "Заблокировать" : "Разрешить")}\n" +
                $"Диск C: {(hideDriveC ? "Скрыть" : "Показать")}\n" +
                $"Установка программ: {(blockInstall ? "Запретить" : "Разрешить")}\n" +
                $"Защита от закрытия: {(preventClose ? "Включить" : "Выключить")}\n" +
                $"Автозапуск с пользователем: {(autoStartWithUser ? "Включить" : "Выключить")}",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (r != MessageBoxResult.Yes) return;

            _global.UsbBlocked = blockUsb;
            _global.TaskMgrDisabled = disableTaskMgr;
            _global.BlockRegedit = blockRegedit;
            _global.BlockCmd = blockCmd;
            _global.BlockPowerShell = blockPowerShell;
            _global.HideDriveC = hideDriveC;
            _global.BlockInstall = blockInstall;
            _global.PreventClose = preventClose;
            _global.AutoStartWithUser = autoStartWithUser;
            _global.Save();

            await ApplyCommandToAll("USB_BLOCK", blockUsb.ToString().ToLower());
            await ApplyCommandToAll("TASKMGR_DISABLE", disableTaskMgr.ToString().ToLower());
            await ApplyCommandToAll("BLOCK_REGEDIT", blockRegedit.ToString().ToLower());
            await ApplyCommandToAll("BLOCK_CMD", blockCmd.ToString().ToLower());
            await ApplyCommandToAll("BLOCK_POWERSHELL", blockPowerShell.ToString().ToLower());
            await ApplyCommandToAll("HIDE_DRIVE_C", hideDriveC.ToString().ToLower());
            await ApplyCommandToAll("BLOCK_INSTALL_UNINSTALL", blockInstall.ToString().ToLower());
            await ApplyCommandToAll("PREVENT_CLOSE", preventClose.ToString().ToLower());
            await ApplyCommandToAll("AUTOSTART_WITH_USER", autoStartWithUser.ToString().ToLower());

            foreach (var pc in AdminHub.KnownClients.Values)
            {
                pc.UsbBlocked = blockUsb;
                pc.TaskMgrDisabled = disableTaskMgr;
                pc.PreventClose = preventClose;
                pc.AutoStartWithUser = autoStartWithUser;
            }

            ShowSaved(TxtRestrictionsSaved);
        }

        // =====================
        // Оффлайн-поведение
        // =====================
        private async void ApplyOfflineBehavior_Click(object sender, RoutedEventArgs e)
        {
            bool lockOnOffline = ChkLockOnOffline.IsChecked == true;
            _global.LockOnOffline = lockOnOffline;
            _global.Save();
            await ApplyCommandToAll("LOCK_ON_OFFLINE", lockOnOffline.ToString().ToLower());
            ShowSaved(TxtOfflineSaved);
        }

        // =====================
        // Питание
        // =====================
        private async void ShutdownAll_Click(object sender, RoutedEventArgs e)
        {
            int count = AdminHub.KnownClients.Values.Count(pc => pc.IsOnline);
            if (count == 0)
            {
                MessageBox.Show("Нет онлайн ПК", "Внимание", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var r = MessageBox.Show($"Выключить все ПК ({count} шт.)?\n\nЭто действие нельзя отменить!",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            foreach (var pc in AdminHub.KnownClients.Values.Where(pc => pc.IsOnline))
                await SendCommand(pc.PcNumber, "SHUTDOWN", "true");
        }

        private async void RestartAll_Click(object sender, RoutedEventArgs e)
        {
            int count = AdminHub.KnownClients.Values.Count(pc => pc.IsOnline);
            if (count == 0)
            {
                MessageBox.Show("Нет онлайн ПК", "Внимание", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var r = MessageBox.Show($"Перезагрузить все ПК ({count} шт.)?",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            foreach (var pc in AdminHub.KnownClients.Values.Where(pc => pc.IsOnline))
                await SendCommand(pc.PcNumber, "RESTART", "true");
        }

        // =====================
        // Фон
        // =====================
        private void BrowseBg_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp|Все файлы|*.*",
                Title = "Выберите фоновое изображение"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtBgPath.Text = dialog.FileName;
                try { ImgPreview.Source = new BitmapImage(new Uri(dialog.FileName)); } catch { }
            }
        }

        private async void SendBgAll_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TxtBgPath.Text))
            {
                MessageBox.Show("Выберите файл изображения", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_hub?.State != HubConnectionState.Connected)
            {
                MessageBox.Show("Нет соединения с сервером", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Проверяем есть ли ПК с индивидуальным фоном
            var pcsWithIndividualBg = AdminHub.KnownClients.Values
                .Where(pc => pc.IsIndividual("SET_BACKGROUND"))
                .ToList();

            bool replaceIndividual = true;

            if (pcsWithIndividualBg.Any())
            {
                var names = string.Join(", ", pcsWithIndividualBg.Select(p => p.PcNumber));
                var result = MessageBox.Show(
                    $"Следующие ПК имеют индивидуальный фон:\n\n{names}\n\n" +
                    "[Да] — Заменить фон на всех ПК\n" +
                    "[Нет] — Оставить индивидуальные фоны без изменений\n" +
                    "[Отмена] — Не применять",
                    "Индивидуальные фоны",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel) return;
                replaceIndividual = result == MessageBoxResult.Yes;
            }
            else
            {
                var r = MessageBox.Show("Отправить фон всем ПК?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }

            try
            {
                var bytes = await File.ReadAllBytesAsync(TxtBgPath.Text);
                var fileName = Path.GetFileName(TxtBgPath.Text);

                // GlobalSettings и pending-команды теперь обновляются внутри UploadFile
                await _hub.InvokeAsync("UploadFile", fileName, bytes, "*", replaceIndividual);

                // Обновляем локальную копию чтобы превью осталось актуальным
                _global = GlobalSettings.Load();
                ShowSaved(TxtBgSaved);
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка отправки фона: {ex.Message}");
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // =====================
        // Экран блокировки
        // =====================
        private void SliderOpacity_Changed(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtOpacityValue == null) return;
            TxtOpacityValue.Text = $"{(int)e.NewValue}%";
        }

        private void SliderFont_Changed(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtPcFontValue == null || TxtTimeFontValue == null) return;
            TxtPcFontValue.Text = $"{(int)SliderPcFontSize.Value}";
            TxtTimeFontValue.Text = $"{(int)SliderTimeFontSize.Value}";
        }

        private void SliderLockedFont_Changed(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtLockedFontValue != null)
                TxtLockedFontValue.Text = $"{(int)e.NewValue}";
        }

        private void CmbPosition_Changed(object sender, SelectionChangedEventArgs e) { }

        private async void ApplyScreenSettingsAll_Click(object sender, RoutedEventArgs e)
        {
            bool showPcName = ChkShowPcName.IsChecked == true;
            bool showPcNumber = ChkShowPcNumber.IsChecked == true;
            double opacity = SliderOpacity.Value / 100.0;
            string pcPos = GetSelectedTag(CmbPcNumberPosition);
            string timePos = GetSelectedTag(CmbTimePosition);
            double pcFont = SliderPcFontSize.Value;
            double timeFont = SliderTimeFontSize.Value;
            bool showLockedText = ChkShowLockedText?.IsChecked ?? true;
            string lockedPos = GetSelectedTag(CmbLockedTextPosition);
            double lockedFont = SliderLockedTextFontSize?.Value ?? 16;

            var pcsWithIndividual = AdminHub.KnownClients.Values
                .Where(pc => pc.HasIndividualSettings)
                .ToList();

            bool keepIndividual = false;
            if (pcsWithIndividual.Any())
            {
                var pcNames = string.Join(", ", pcsWithIndividual.Select(p => p.PcNumber));
                var result = MessageBox.Show(
                    $"Следующие ПК имеют индивидуальные настройки:\n\n{pcNames}\n\n" +
                    $"Сбросить их и применить глобальные настройки?\n\n" +
                    $"[Да] — Сбросить все индивидуальные настройки\n" +
                    $"[Нет] — Оставить индивидуальные, применить остальные",
                    "Индивидуальные настройки", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel) return;
                keepIndividual = result == MessageBoxResult.No;
                if (!keepIndividual)
                    foreach (var pc in pcsWithIndividual)
                        AdminHub.ClearIndividualSettings(pc.PcNumber);
            }

            _global.ShowPcName = showPcName;
            _global.ShowPcNumber = showPcNumber;
            _global.BackgroundOpacity = opacity;
            _global.PcNumberPosition = pcPos;
            _global.PcNumberFontSize = pcFont;
            _global.ShowLockedText = showLockedText;
            _global.LockedTextPosition = lockedPos;
            _global.LockedTextFontSize = lockedFont;
            _global.TimePosition = timePos;
            _global.TimeFontSize = timeFont;
            _global.LastApplied = DateTime.UtcNow;
            _global.Save();

            var commands = new Dictionary<string, string>
            {
                ["SHOW_PC_NAME"] = showPcName.ToString().ToLower(),
                ["SHOW_PC_NUMBER"] = showPcNumber.ToString().ToLower(),
                ["SET_BACKGROUND_OPACITY"] = opacity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                ["SET_PC_NUMBER_POSITION"] = pcPos,
                ["SET_PC_NUMBER_FONT_SIZE"] = pcFont.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["SHOW_LOCKED_TEXT"] = showLockedText.ToString().ToLower(),
                ["SET_LOCKED_TEXT_POSITION"] = lockedPos,
                ["SET_LOCKED_TEXT_FONT_SIZE"] = lockedFont.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["SET_TIME_POSITION"] = timePos,
                ["SET_TIME_FONT_SIZE"] = timeFont.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };

            foreach (var pc in AdminHub.KnownClients.Values)
            {
                foreach (var kv in commands)
                {
                    if (keepIndividual && pc.IsIndividual(kv.Key)) continue;
                    await SendCommand(pc.PcNumber, kv.Key, kv.Value);
                }
            }

            ShowSaved(TxtScreenSaved);
        }

        // =====================
        // Вспомогательные методы
        // =====================
        private async Task ApplyCommandToAll(string type, string value)
        {
            foreach (var pc in AdminHub.KnownClients.Values)
            {
                if (pc.IsOnline)
                    await SendCommand(pc.PcNumber, type, value);
                else
                    AdminHub.AddPendingCommand(pc.PcNumber, type, value);
            }
        }

        private string GetSelectedTag(ComboBox cmb)
        {
            if (cmb.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag;
            return "MiddleCenter";
        }

        private async Task SendCommand(string pcNumber, string type, string value)
        {
            try
            {
                if (_hub?.State != HubConnectionState.Connected)
                {
                    AdminHub.AddPendingCommand(pcNumber, type, value);
                    return;
                }

                var cmd = new { Type = type, Value = value };
                var json = JsonSerializer.Serialize(cmd);
                await _hub.InvokeAsync("SendCommand", pcNumber, json);

                if (AdminHub.KnownClients.TryGetValue(pcNumber, out var pc) && !pc.IsOnline)
                    AdminHub.AddPendingCommand(pcNumber, type, value);

                Logger.Info($"Команда: {type}={value} → {pcNumber}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка команды: {ex.Message}");
                AdminHub.AddPendingCommand(pcNumber, type, value);
            }
        }

        private void ShowSaved(TextBlock txt)
        {
            txt.Visibility = Visibility.Visible;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (s, _) => { txt.Visibility = Visibility.Collapsed; t.Stop(); };
            t.Start();
        }
    }
}