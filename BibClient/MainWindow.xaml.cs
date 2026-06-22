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
        private bool _sessionExpiredHandled = false;

        // true — экран заблокирован из-за потери сети, сессия при этом жива
        private bool _isOfflineLocked = false;

        // true — экран заблокирован из-за паузы (сессия жива, таймер стоит)
        private bool _isPauseLocked = false;

        private ClientSettings _settings => SettingsManager.Current;

        // Публичное свойство для проверки готовности окна
        public bool IsReady => _isReady;

        public MainWindow()
        {
            // ✅ Устанавливаем флаг готовности СРАЗУ, чтобы Lock() мог быть вызван сразу после создания объекта
            _isReady = true;

            InitializeComponent();
            _originalContent = (UIElement?)this.Content;

            _trayIcon = new TrayIcon();
            _lockHook = new KeyboardHook(KeyboardHookMode.LockScreen);
            _alwaysHook = new KeyboardHook(KeyboardHookMode.Always);

            SettingsManager.Load();
            ApplySettings();
            StartClock();
            StartNetwork();

            // Регистрируем автозапуск если включено (по умолчанию true)
            if (_settings.AutoStartWithUser)
            {
                Watchdog.RegisterAutostart();
            }

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

            // Подписка на завершение сессии - блокируем ПК
            PolicyEngine.EndSessionRequested += () => Dispatcher.Invoke(() => OnSessionExpired());

            // Блокировка экрана при паузе: пользователь не может работать пока сессия на паузе
            PolicyEngine.SessionPaused += (paused) => Dispatcher.Invoke(() =>
            {
                if (paused) PauseLock();
                else PauseUnlock();
            });

            // Текстовое сообщение от администратора/оператора — окно по центру экрана
            PolicyEngine.ShowMessageRequested += (text) => Dispatcher.Invoke(() => ShowAdminMessage(text));
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
            // BackgroundOpacity управляет затемнением поверх фото, не самим фото
            DimOverlay.Opacity = _settings.BackgroundOpacity;

            if (!string.IsNullOrEmpty(_settings.BackgroundImagePath) && File.Exists(_settings.BackgroundImagePath))
            {
                try
                {
                    BgImage.Source = new BitmapImage(new Uri(_settings.BackgroundImagePath, UriKind.Absolute));
                    Logger.Info($"Фон обновлён: {_settings.BackgroundImagePath}");
                }
                catch (Exception ex) { Logger.Error($"Ошибка загрузки фона: {ex.Message}"); }
            }

            // ── Позиции, стекинг, отступы ────────────────────────────────────────
            RebuildLayout();
        }

        // Перестраивает расположение панелей с учётом стекинга и отступов.
        // Вызывается при каждом изменении настроек позиций/порядка/отступов.
        private void RebuildLayout()
        {
            var s = _settings;

            // Иконка онлайн
            NetDot.Visibility = s.ShowStatusDot ? Visibility.Visible : Visibility.Collapsed;

            // Убираем старые обёртки, возвращая вложенные панели обратно в LayoutGrid
            var oldWrappers = LayoutGrid.Children
                .OfType<StackPanel>()
                .Where(sp => sp.Tag?.ToString() == "group-wrapper")
                .ToList();
            foreach (var w in oldWrappers)
            {
                var nested = w.Children.OfType<StackPanel>().ToList();
                w.Children.Clear();
                foreach (var child in nested)
                    LayoutGrid.Children.Add(child);
                LayoutGrid.Children.Remove(w);
            }

            // Видимость панели "Заблокировано"
            PanelLocked.Visibility = s.ShowLockedText ? Visibility.Visible : Visibility.Collapsed;

            // Группируем панели по позиции
            var groups = new Dictionary<string, List<(StackPanel panel, int order)>>();
            void Add(string pos, StackPanel panel, int order)
            {
                if (!groups.ContainsKey(pos)) groups[pos] = new();
                groups[pos].Add((panel, order));
            }
            Add(s.PcNumberPosition, PanelCenter, s.PcNumberOrder);
            if (s.ShowLockedText) Add(s.LockedTextPosition, PanelLocked, s.LockedTextOrder);
            Add(s.TimePosition, PanelTime, s.TimeOrder);

            foreach (var (pos, items) in groups)
            {
                if (items.Count == 1)
                {
                    ApplyPositionWithOffset(items[0].panel, pos, s.ScreenOffsetX, s.ScreenOffsetY);
                }
                else
                {
                    // Несколько элементов на одной позиции — оборачиваем в общий StackPanel
                    var wrapper = new StackPanel
                    {
                        Tag = "group-wrapper",
                        Orientation = System.Windows.Controls.Orientation.Vertical
                    };
                    foreach (var (panel, _) in items.OrderBy(x => x.order))
                    {
                        // Нормализуем горизонтальное выравнивание внутри обёртки
                        panel.HorizontalAlignment = WpfHorizontalAlignment.Stretch;
                        LayoutGrid.Children.Remove(panel);
                        wrapper.Children.Add(panel);
                    }
                    LayoutGrid.Children.Add(wrapper);
                    ApplyPositionWithOffset(wrapper, pos, s.ScreenOffsetX, s.ScreenOffsetY);
                }
            }
        }

        // Применяет позицию и отступ от ближайшего края монитора
        private static void ApplyPositionWithOffset(FrameworkElement element, string position, int offsetX, int offsetY)
        {
            (VerticalAlignment va, WpfHorizontalAlignment ha) = position switch
            {
                "TopLeft"      => (VerticalAlignment.Top,    WpfHorizontalAlignment.Left),
                "TopCenter"    => (VerticalAlignment.Top,    WpfHorizontalAlignment.Center),
                "TopRight"     => (VerticalAlignment.Top,    WpfHorizontalAlignment.Right),
                "MiddleLeft"   => (VerticalAlignment.Center, WpfHorizontalAlignment.Left),
                "MiddleCenter" => (VerticalAlignment.Center, WpfHorizontalAlignment.Center),
                "MiddleRight"  => (VerticalAlignment.Center, WpfHorizontalAlignment.Right),
                "BottomLeft"   => (VerticalAlignment.Bottom, WpfHorizontalAlignment.Left),
                "BottomCenter" => (VerticalAlignment.Bottom, WpfHorizontalAlignment.Center),
                "BottomRight"  => (VerticalAlignment.Bottom, WpfHorizontalAlignment.Right),
                _              => (VerticalAlignment.Center, WpfHorizontalAlignment.Center),
            };

            element.VerticalAlignment = va;
            element.HorizontalAlignment = ha;

            // Отступ применяется только к «прижатым» краям; по центру — 0
            double left   = ha == WpfHorizontalAlignment.Left   ? offsetX : 0;
            double right  = ha == WpfHorizontalAlignment.Right  ? offsetX : 0;
            double top    = va == VerticalAlignment.Top          ? offsetY : 0;
            double bottom = va == VerticalAlignment.Bottom       ? offsetY : 0;
            element.Margin = new Thickness(left, top, right, bottom);
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

        // Окно сообщения от администратора/оператора — поверх всего, по центру экрана
        private Window? _adminMsgWindow;
        private void ShowAdminMessage(string text)
        {
            try
            {
                // Не копим окна: закрываем предыдущее сообщение, если оно ещё открыто
                _adminMsgWindow?.Close();

                var win = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    AllowsTransparency = true,
                    Background = WpfBrushes.Transparent,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    ShowInTaskbar = false,
                    SizeToContent = SizeToContent.WidthAndHeight
                };

                var card = new Border
                {
                    CornerRadius = new CornerRadius(16),
                    Background = new SolidColorBrush(WpfColor.FromRgb(0x1A, 0x1A, 0x2E)),
                    BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x3D, 0x3D, 0x6B)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(30),
                    MaxWidth = 560,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 45, ShadowDepth = 0, Opacity = 0.55 }
                };

                var stack = new StackPanel();

                stack.Children.Add(new TextBlock
                {
                    Text = "Сообщение от администратора",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(0x8A, 0x8A, 0xE0)),
                    Margin = new Thickness(0, 0, 0, 14)
                });

                stack.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = 19,
                    LineHeight = 27,
                    Foreground = WpfBrushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 24)
                });

                var okBtn = new WpfButton
                {
                    Content = "OK",
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = WpfBrushes.White,
                    Background = new SolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6)),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(30, 10, 30, 10),
                    Cursor = WpfCursors.Hand,
                    HorizontalAlignment = WpfHorizontalAlignment.Right
                };
                okBtn.Click += (_, __) => win.Close();
                stack.Children.Add(okBtn);

                card.Child = stack;
                win.Content = card;
                win.Closed += (_, __) => { if (_adminMsgWindow == win) _adminMsgWindow = null; };
                _adminMsgWindow = win;
                win.Show();
                win.Activate();
                Logger.Info($"Показано сообщение администратора: {text}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка показа сообщения администратора: {ex.Message}");
            }
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
            _sessionExpiredHandled = false;
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
                _ = _networkManager?.SendStatusUpdateAsync(PolicyEngine.ActiveSessionType, elapsed);

            // 4. Отправляем статус на сервер
            _ = _networkManager?.SendStatusAsync(sessionType);

            Logger.Info("Сессия запущена (попап отображается через TrayIcon)");
        }

        private void OnSessionExpired()
        {
            // Метод может быть вызван из другого потока (событие PolicyEngine), поэтому используем Dispatcher
            if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => OnSessionExpiredInternal());
                return;
            }
            OnSessionExpiredInternal();
        }

        private void OnSessionExpiredInternal()
        {
            if (_sessionExpiredHandled) return;
            _sessionExpiredHandled = true;
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

            // 8. Обновляем видимость кнопок в трее (сессия завершена - кнопки должны появиться)
            PolicyEngine.ResetSession();

            _ = _networkManager?.SendStatusAsync("Заблокирован");

            Logger.Info("Экран блокировки восстановлен");
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

            // Сигнализируем службе что закрытие будет легальным — не перезапускать 30 мин
            Logger.Info("🔓 Администратор разблокировал ПК — сигнал легального закрытия");
            ServiceManager.SignalLegalClose();
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
            if (!_isReady) 
            {
                Logger.Warn("⚠️ Lock() вызван до _isReady, откладываем...");
                return; 
            }

            _isUnlocked = false;
            _isPauseLocked = false;
            Logger.Info("ПК блокируется...");

            // 1. Останавливаем сессию если активна
            _sessionManager?.Dispose();
            _sessionManager = null;
            SessionManager.ClearStateFile();

            // 2. Очищаем UI
            if (PbPassword != null) PbPassword.Clear();
            if (PanelPassword != null) PanelPassword.Visibility = Visibility.Collapsed;
            if (TxtError != null) TxtError.Visibility = Visibility.Collapsed;

            // ✅ 3. Сначала настраиваем свойства окна, ПОТОМ показываем
            this.WindowStyle = WindowStyle.None;
            this.WindowState = WindowState.Maximized;
            this.Topmost = true;
            this.ResizeMode = ResizeMode.NoResize;
            this.Width = double.NaN;
            this.Height = double.NaN;
            this.WindowStartupLocation = WindowStartupLocation.Manual;

            // ✅ 4. Проверяем _originalContent
            if (_originalContent != null)
            {
                this.Content = _originalContent;
            }
            else
            {
                // Фолбэк: загружаем контент из XAML явно
                this.Content = null;
                InitializeComponent(); // Перезагружаем контент
                _originalContent = (UIElement?)this.Content;
            }

            // ✅ 5. Пересоздаём хук
            _lockHook?.Dispose();
            _lockHook = new KeyboardHook(KeyboardHookMode.LockScreen);
            Logger.Info("🔐 KeyboardHook установлен в режиме LockScreen");

            // ✅ 6. Показываем и активируем
            this.Show();
            this.Activate();
            this.Focus(); // ← Добавь явный фокус

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
            // Защита от закрытия: если включена настройка PreventClose, отменяем закрытие
            // Исключение: администратор разблокировал ПК паролем (_isUnlocked = true)
            if (_settings.PreventClose && !_isUnlocked)
            {
                Logger.Info("🚫 Закрытие окна заблокировано настройкой PreventClose");
                e.Cancel = true;
            }
            else
            {
                // Легальное закрытие (администратор разблокировал или PreventClose выключен)
                Logger.Info("✅ Разрешено легальное закрытие приложения");

                // Освобождаем мьютекс одиночного экземпляра
                Watchdog.ReleaseSingleInstance();

                // Отменяем автозапуск если нужно
                if (!_settings.AutoStartWithUser)
                    Watchdog.UnregisterAutostart();

                e.Cancel = false;
            }
#endif
        }
    }
}