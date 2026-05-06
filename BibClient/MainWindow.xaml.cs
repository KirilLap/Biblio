using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// ✅ Алиасы для устранения конфликтов WPF vs WinForms
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfButton = System.Windows.Controls.Button;
using WpfCursors = System.Windows.Input.Cursors;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;

namespace BibClient
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _clockTimer = new();
        private KeyboardHook? _lockHook;
        private KeyboardHook? _alwaysHook;
        private NetworkManager? _networkManager;
        private bool _isUnlocked = false;
        private bool _isReady = false;
        private UIElement? _originalContent;

        private SessionManager? _sessionManager = null;
        private TrayIcon? _trayIcon = null;

        // true — экран заблокирован из-за потери сети, сессия при этом жива
        private bool _isOfflineLocked = false;

        // true — экран заблокирован из-за паузы (сессия жива, таймер стоит)
        private bool _isPauseLocked = false;

        private ClientSettings _settings => SettingsManager.Current;

        public MainWindow()
        {
            InitializeComponent();
            _originalContent = (UIElement?)this.Content;

            _trayIcon = new TrayIcon();
            _lockHook = new KeyboardHook(KeyboardHookMode.LockScreen);
            _alwaysHook = new KeyboardHook(KeyboardHookMode.Always);

            SettingsManager.Load();
            ApplySettings();
            StartClock();
            StartNetwork();

            // Подписки на удалённые команды
            PolicyEngine.RemoteUnlockRequested += () => Dispatcher.Invoke(Unlock);
            PolicyEngine.RemoteLockRequested += () => Dispatcher.Invoke(Lock);
            PolicyEngine.SettingsChanged += () => Dispatcher.Invoke(ApplySettings);

            // Фаза 4: дрейф системных часов
            PolicyEngine.ClockMismatchDetected += (offset) => Dispatcher.Invoke(() => ShowClockWarning(offset));

            PolicyEngine.ReconnectRequested += () => Dispatcher.Invoke(async () =>
            {
                if (_networkManager != null)
                {
                    Logger.Info("Переподключение к серверу...");
                    await _networkManager.SendRegistrationAsync();
                }
            });

            // Подписка на запуск сессии (initialElapsed > 0 при восстановлении после реконнекта)
            PolicyEngine.StartSessionRequested += (type, limit, paid, initialElapsed) =>
                Dispatcher.Invoke(() => StartSession(type, limit, paid, initialElapsed));

            // Блокировка экрана при паузе: пользователь не может работать пока сессия на паузе
            PolicyEngine.SessionPaused += (paused) => Dispatcher.Invoke(() =>
            {
                if (paused) PauseLock();
                else PauseUnlock();
            });

            _isReady = true;
        }

        private void StartNetwork()
        {
            // Проверяем наличие сохранённой сессии ДО подключения к сети
            var restoredSession = SessionManager.TryRestoreSession();
            if (restoredSession != null)
            {
                Logger.Info($"🔄 Найдена сохранённая сессия: {restoredSession.SessionType}, {restoredSession.ElapsedSeconds}с");
            }

            string serverUrl = $"http://{_settings.ServerIp}:{_settings.ServerPort}";
            _networkManager = new NetworkManager(serverUrl);

            // Если восстанавливаем сессию — не посылаем "Заблокирован" при регистрации
            if (restoredSession != null)
                _networkManager.IsRestoring = true;

            // После успешной регистрации — сервер сам отправит START_SESSION с откорректированным
            // временем (smart offline protection). Локально ничего не запускаем.
            _networkManager.OnRegistered += () =>
            {
                if (restoredSession != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        Logger.Info($"🔄 Восстановление: ждем START_SESSION от сервера...");
                        _networkManager!.IsRestoring = false;
                    });
                    restoredSession = null;
                }
            };

            _networkManager.ConnectionStateChanged += (isConnected) =>
            {
                Dispatcher.Invoke(() =>
                {
                    NetDot.Fill = isConnected ? WpfBrushes.Green : WpfBrushes.Red;
                    NetStatusText.Text = isConnected ? "Онлайн" : "Оффлайн";

                    if (!isConnected && _sessionManager != null && PolicyEngine.LockOnOffline && !_isOfflineLocked)
                    {
                        // Потеря сети во время сессии + режим блокировки включён
                        OfflineSoftLock();
                    }
                    else if (isConnected && _isOfflineLocked)
                    {
                        // Сеть восстановлена — снимаем offline-блокировку
                        OfflineSoftUnlock();
                    }
                });
            };

            _ = _networkManager.StartAsync();
        }

        // Визуальная блокировка при потере сети: показывает экран, но НЕ завершает сессию
        private void OfflineSoftLock()
        {
            if (_isOfflineLocked) return;
            _isOfflineLocked = true;
            Logger.Info("📵 Оффлайн-блокировка (сессия продолжается)");

            _lockHook?.Dispose();
            _lockHook = new KeyboardHook(KeyboardHookMode.LockScreen);

            this.WindowStyle = WindowStyle.None;
            this.WindowState = WindowState.Maximized;
            this.Topmost = true;
            this.ResizeMode = ResizeMode.NoResize;
            this.Width = double.NaN;
            this.Height = double.NaN;
            this.Content = _originalContent;

            this.Show();
            this.Activate();
            StartClock();
            ApplySettings();
        }

        // Снятие визуальной блокировки при восстановлении сети
        private void OfflineSoftUnlock()
        {
            if (!_isOfflineLocked) return;
            _isOfflineLocked = false;
            Logger.Info("📶 Сеть восстановлена — снимаем оффлайн-блокировку");

            // Если пауза ещё активна — экран остаётся заблокированным
            if (_isPauseLocked) return;

            _lockHook?.Dispose();
            _lockHook = null;
            _clockTimer.Stop();

            this.WindowStyle = WindowStyle.None;
            this.WindowState = WindowState.Minimized;
            this.Topmost = false;
            this.Hide();

            // Показываем попап сессии
            _sessionManager?.ShowPopup();
        }

        // ── Блокировка экрана при паузе ──────────────────────────────────────
        private void PauseLock()
        {
            if (_isPauseLocked) return;
            _isPauseLocked = true;
            Logger.Info("⏸ Блокировка экрана по паузе");

            // Если экран уже заблокирован (оффлайн) — просто обновляем флаг
            if (_isOfflineLocked) return;

            _lockHook?.Dispose();
            _lockHook = new KeyboardHook(KeyboardHookMode.LockScreen);

            this.WindowStyle = WindowStyle.None;
            this.WindowState = WindowState.Maximized;
            this.Topmost = true;
            this.ResizeMode = ResizeMode.NoResize;
            this.Width = double.NaN;
            this.Height = double.NaN;
            this.Content = _originalContent;

            this.Show();
            this.Activate();
            StartClock();
            ApplySettings();
        }

        // ── Снятие блокировки при продолжении сессии ─────────────────────────
        private void PauseUnlock()
        {
            if (!_isPauseLocked) return;
            _isPauseLocked = false;
            Logger.Info("▶ Снятие блокировки паузы — сессия продолжается");

            // Если оффлайн-блокировка ещё активна — оставляем экран заблокированным
            if (_isOfflineLocked) return;

            _lockHook?.Dispose();
            _lockHook = null;
            _clockTimer.Stop();

            this.WindowStyle = WindowStyle.None;
            this.WindowState = WindowState.Minimized;
            this.Topmost = false;
            this.Hide();

            // Показываем попап с текущим состоянием сессии
            _sessionManager?.ShowPopup();
        }

        public void ApplySettings()
        {
            // Перечитываем с диска — PolicyEngine уже сохранил новые значения перед вызовом
            SettingsManager.Load();

            // ── Номер ПК ─────────────────────────────────────────────────────────
            // Формируем отображаемое имя на основе настроек ShowPcName и ShowPcNumber
            string displayName = "";
            if (_settings.ShowPcName && _settings.ShowPcNumber)
            {
                displayName = _settings.PcNumber;  // Полное имя: "ПК 1" или "Комп 1"
            }
            else if (_settings.ShowPcName)
            {
                // Только имя без номера
                displayName = string.IsNullOrEmpty(_settings.CustomName) ? "ПК" : _settings.CustomName;
            }
            else if (_settings.ShowPcNumber)
            {
                // Только номер
                displayName = _settings.PcNumberValue.ToString();
            }
            
            TxtPcNumber.Visibility = (_settings.ShowPcName || _settings.ShowPcNumber) ? Visibility.Visible : Visibility.Collapsed;
            TxtPcNumber.Text = displayName;
            TxtPcNumber.FontSize = _settings.PcNumberFontSize;
            this.Title = $"BibClient - {displayName}";

            // ── Текст "Компьютер заблокирован" ───────────────────────────────────
            TxtLocked.Visibility = _settings.ShowLockedText ? Visibility.Visible : Visibility.Collapsed;
            TxtLocked.FontSize = _settings.LockedTextFontSize;

            // ── Время ────────────────────────────────────────────────────────────
            TxtTime.FontSize = _settings.TimeFontSize;

            // ── Фон ──────────────────────────────────────────────────────────────
            BgImage.Opacity = _settings.BackgroundOpacity;

            if (!string.IsNullOrEmpty(_settings.BackgroundImagePath) && File.Exists(_settings.BackgroundImagePath))
            {
                try
                {
                    BgImage.Source = new BitmapImage(new Uri(_settings.BackgroundImagePath, UriKind.Absolute));
                    Logger.Info($"Фон обновлён: {_settings.BackgroundImagePath}");
                }
                catch (Exception ex) { Logger.Error($"Ошибка загрузки фона: {ex.Message}"); }
            }

            // ── Позиции ──────────────────────────────────────────────────────────
            ApplyPosition(PanelCenter, _settings.PcNumberPosition);
            ApplyPosition(PanelTime, _settings.TimePosition);
        }

        // Переводит строку-позицию ("TopLeft", "MiddleCenter", …) в выравнивание WPF
        private static void ApplyPosition(FrameworkElement? element, string position)
        {
            if (element == null) return;

            VerticalAlignment va;
            WpfHorizontalAlignment ha;

            switch (position)
            {
                case "TopLeft":      va = VerticalAlignment.Top;    ha = WpfHorizontalAlignment.Left;   break;
                case "TopCenter":    va = VerticalAlignment.Top;    ha = WpfHorizontalAlignment.Center; break;
                case "TopRight":     va = VerticalAlignment.Top;    ha = WpfHorizontalAlignment.Right;  break;
                case "MiddleLeft":   va = VerticalAlignment.Center; ha = WpfHorizontalAlignment.Left;   break;
                case "MiddleCenter": va = VerticalAlignment.Center; ha = WpfHorizontalAlignment.Center; break;
                case "MiddleRight":  va = VerticalAlignment.Center; ha = WpfHorizontalAlignment.Right;  break;
                case "BottomLeft":   va = VerticalAlignment.Bottom; ha = WpfHorizontalAlignment.Left;   break;
                case "BottomCenter": va = VerticalAlignment.Bottom; ha = WpfHorizontalAlignment.Center; break;
                case "BottomRight":  va = VerticalAlignment.Bottom; ha = WpfHorizontalAlignment.Right;  break;
                default:             va = VerticalAlignment.Center; ha = WpfHorizontalAlignment.Center; break;
            }

            element.VerticalAlignment = va;
            element.HorizontalAlignment = ha;
        }

        private void StartClock()
        {
            _clockTimer.Stop();
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += (s, e) => UpdateClock();
            _clockTimer.Start();
            UpdateClock();
        }

        private void UpdateClock()
        {
            TxtTime.Text = DateTime.Now.ToString("HH:mm:ss");
            TxtDate.Text = DateTime.Now.ToString("dddd, d MMMM yyyy", new CultureInfo("ru-RU"));
        }

        private void ShowClockWarning(double offsetSeconds)
        {
            if (TxtClockWarning == null) return;
            double abs = Math.Abs(offsetSeconds);
            string dir = offsetSeconds > 0 ? "отстают" : "опережают";
            TxtClockWarning.Text = $"⚠ Системные часы {dir} на {abs:F0}с — синхронизируйте время";
            TxtClockWarning.Visibility = Visibility.Visible;
        }

        // =====================
        // Сессия
        // =====================
        private void StartSession(string sessionType, int limitSeconds, int paidAmount, int initialElapsedSeconds = 0)
        {
            Logger.Info($"Запуск сессии: {sessionType}, лимит: {limitSeconds}с, начальное: {initialElapsedSeconds}с");

            _isOfflineLocked = false;
            _isPauseLocked = false;  // новая сессия — снимаем все блокировки

            // 1. Останавливаем старую сессию если есть
            _sessionManager?.Dispose();
            _sessionManager = null;

            // 2. Скрываем экран блокировки
            this.Hide();

            // 3. Создаём SessionManager (с восстановленным временем если нужно)
            _sessionManager = new SessionManager(
                sessionType,
                limitSeconds,
                _settings.Tariff,
                _trayIcon!,
                paidAmount,
                initialElapsedSeconds);

            // Подписки на события сессии
            _sessionManager.SessionExpired += OnSessionExpired;
            _sessionManager.ElapsedUpdated += (elapsed) =>
                _ = _networkManager?.SendStatusUpdateAsync(sessionType, elapsed);

            // 4. Отправляем статус на сервер
            _ = _networkManager?.SendStatusAsync(sessionType);

            Logger.Info("Сессия запущена (попап отображается через TrayIcon)");
        }

        private void OnSessionExpired()
        {
            Dispatcher.Invoke(() =>
            {
                Logger.Info("Сессия завершена — блокируем ПК");

                // 1. Очищаем менеджер сессии
                _sessionManager?.Dispose();
                _sessionManager = null;

                // 2. Сбрасываем состояние
                _isUnlocked = false;

                // 3. Восстанавливаем хук блокировки
                _lockHook?.Dispose();
                _lockHook = new KeyboardHook(KeyboardHookMode.LockScreen);

                // 4. Возвращаем оригинальный контент (экран блокировки)
                this.Content = _originalContent;

                // 5. Возвращаем свойства окна блокировки
                this.WindowStyle = WindowStyle.None;
                this.WindowState = WindowState.Maximized;
                this.Topmost = true;
                this.ResizeMode = ResizeMode.NoResize;
                this.Width = double.NaN;
                this.Height = double.NaN;

                // 6. Показываем экран блокировки
                this.Show();
                this.Activate();
                this.Focus();

                StartClock();
                ApplySettings();

                // 7. Очищаем поле пароля
                if (PbPassword != null) PbPassword.Clear();
                if (PanelPassword != null) PanelPassword.Visibility = Visibility.Collapsed;

                _ = _networkManager?.SendStatusAsync("Заблокирован");

                Logger.Info("Экран блокировки восстановлен");
            });
        }

        // =====================
        // Клавиши
        // =====================
        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.F4 && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; return; }
            if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; return; }
            if (e.Key == Key.Escape) { e.Handled = true; return; }

            if (e.Key == Key.A && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt))
            {
                TogglePasswordPanel();
                e.Handled = true;
            }
        }

        private void TogglePasswordPanel()
        {
            if (PanelPassword.Visibility == Visibility.Collapsed)
            {
                PanelPassword.Visibility = Visibility.Visible;
                PbPassword.Clear();
                TxtError.Visibility = Visibility.Collapsed;
                PbPassword.Focus();
            }
            else
            {
                PanelPassword.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnHidePassword_Click(object sender, RoutedEventArgs e)
        {
            PanelPassword.Visibility = Visibility.Collapsed;
            PbPassword.Clear();
            TxtError.Visibility = Visibility.Collapsed;
        }

        private void PbPassword_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter) TryUnlock();
            if (e.Key == Key.Escape) BtnHidePassword_Click(sender, e);
        }

        private void BtnUnlock_Click(object sender, RoutedEventArgs e) => TryUnlock();

        private void TryUnlock()
        {
            // 🚑 ЭКСТРЕННЫЙ ПАРОЛЬ — всегда работает
            if (PbPassword.Password == "9999")
            {
                Unlock();
                return;
            }

            if (ClientSettings.VerifyPassword(PbPassword.Password, _settings.AdminPasswordHash))
            {
                Unlock();
            }
            else
            {
                TxtError.Visibility = Visibility.Visible;
                PbPassword.Clear();
                PbPassword.Focus();
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                t.Tick += (s, e) => { TxtError.Visibility = Visibility.Collapsed; t.Stop(); };
                t.Start();
            }
        }

        // =====================
        // Блокировка / Разблокировка
        // =====================
        public void Unlock()
        {
            if (!_isReady || _isUnlocked) return;

            _isUnlocked = true;
            Logger.Info("ПК разблокирован локально");

            _lockHook?.Dispose();
            _lockHook = null;
            _clockTimer.Stop();

            this.WindowStyle = WindowStyle.SingleBorderWindow;
            this.WindowState = WindowState.Normal;
            this.Topmost = false;
            this.ResizeMode = ResizeMode.CanResize;
            this.Width = 340;
            this.Height = 220;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            this.Content = CreateUnlockedPanel();
            this.Show();

            _ = _networkManager?.SendStatusAsync("Свободный");
        }

        private Border CreateUnlockedPanel()
        {
            var stack = new StackPanel
            {
                // ✅ Используем алиасы для устранения неоднозначности
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(new TextBlock
            {
                Text = _settings.PcNumber,
                Foreground = WpfBrushes.White,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            });

            stack.Children.Add(new TextBlock
            {
                Text = "Свободный доступ",
                // ✅ Используем WpfColor
                Foreground = new SolidColorBrush(WpfColor.FromRgb(29, 158, 117)),
                FontSize = 13,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            });

            // ✅ Используем WpfButton
            var btnLock = new WpfButton
            {
                Content = "Заблокировать снова",
                Padding = new Thickness(16, 10, 16, 10),
                // ✅ Используем WpfCursors
                Cursor = WpfCursors.Hand,
                Background = new SolidColorBrush(WpfColor.FromRgb(61, 61, 107)),
                Foreground = WpfBrushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 13
            };
            btnLock.Click += (s, e) => Lock();
            stack.Children.Add(btnLock);

            return new Border
            {
                Background = new SolidColorBrush(WpfColor.FromRgb(30, 30, 46)),
                Child = stack
            };
        }

        public void Lock()
        {
            if (!_isReady) return;

            _isUnlocked = false;
            _isPauseLocked = false;  // REMOTE_LOCK сбрасывает состояние паузы
            Logger.Info("ПК блокируется...");

            // 1. Останавливаем сессию если активна
            _sessionManager?.Dispose();
            _sessionManager = null;
            SessionManager.ClearStateFile();

            // 2. Очищаем UI
            if (PbPassword != null) PbPassword.Clear();
            if (PanelPassword != null) PanelPassword.Visibility = Visibility.Collapsed;
            if (TxtError != null) TxtError.Visibility = Visibility.Collapsed;

            // 3. Возвращаем свойства окна блокировки
            this.WindowStyle = WindowStyle.None;
            this.WindowState = WindowState.Maximized;
            this.Topmost = true;
            this.ResizeMode = ResizeMode.NoResize;
            this.Width = double.NaN;
            this.Height = double.NaN;
            this.WindowStartupLocation = WindowStartupLocation.Manual;

            this.Content = _originalContent;

            _lockHook?.Dispose();
            _lockHook = new KeyboardHook(KeyboardHookMode.LockScreen);

            // 4. Показываем окно блокировки
            this.Show();
            this.Activate();

            StartClock();
            ApplySettings();

            _ = _networkManager?.SendStatusAsync("Заблокирован");

            Logger.Info("ПК заблокирован");
        }

        public void ApplyNewSettings(ClientSettings settings)
        {
            SettingsManager.Current = settings;
            SettingsManager.Save();
            ApplySettings();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
#if DEBUG
            _lockHook?.Dispose();
            _alwaysHook?.Dispose();
            _trayIcon?.Dispose();
            _sessionManager?.Dispose();
            e.Cancel = false;
#else
            if (true) e.Cancel = true;
#endif
        }
    }
}