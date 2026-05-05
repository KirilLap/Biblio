using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.AspNetCore.SignalR.Client;

namespace BibAdmin
{
    public partial class ComputersPage : Page
    {
        private ClientState? _selected = null;
        private DispatcherTimer _timer = new();
        private HubConnection? _hub;
        public static int Revenue { get; set; } = 0;
        public static int Tariff { get; set; } = 3000;
        
        // Текущий режим сортировки
        private string _currentSortMode = "ByNumber";

        // Дедупликация: не более одного окна оффлайн-уведомления на ПК
        private readonly Dictionary<string, OfflineAlertWindow> _activeOfflineWindows = new();

        public ComputersPage()
        {
            InitializeComponent();

            AdminHub.ClientUpdated += OnClientUpdated;
            AdminHub.ClientsChanged += OnClientsChanged;
            AdminHub.ClientOfflineWithSession += OnClientOfflineWithSession;
            AdminHub.ClientTimeMismatch += OnClientTimeMismatch;
            AdminHub.ClientTimeDrift += OnClientTimeDrift;

            // Загружаем сохранённый режим сортировки
            LoadSortMode();
            
            BuildGrid();
            UpdateStats();

            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            ConnectToHub();
        }
        
        private void LoadSortMode()
        {
            var settings = GlobalSettings.Load();
            _currentSortMode = settings.ClientSortMode ?? "ByNumber";
            
            // Устанавливаем значение в ComboBox
            if (CmbSortMode != null)
            {
                foreach (var item in CmbSortMode.Items)
                {
                    if (item is ComboBoxItem cmbItem && cmbItem.Tag?.ToString() == _currentSortMode)
                    {
                        CmbSortMode.SelectedItem = cmbItem;
                        break;
                    }
                }
            }
        }
        
        private void SaveSortMode(string mode)
        {
            _currentSortMode = mode;
            var settings = GlobalSettings.Load();
            settings.ClientSortMode = mode;
            settings.Save();
        }
        
        private void CmbSortMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbSortMode.SelectedItem is ComboBoxItem selected)
            {
                var mode = selected.Tag?.ToString();
                if (!string.IsNullOrEmpty(mode))
                {
                    SaveSortMode(mode);
                    BuildGrid(); // Перестраиваем сетку с новой сортировкой
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
                Logger.Error($"Ошибка подключения к хабу: {ex.Message}");
            }
        }

        private void OnClientUpdated(ClientState client)
        {
            Dispatcher.Invoke(() =>
            {
                // Обновляем ссылку на _selected — RegisterClient создаёт новый объект
                // в KnownClients, старая ссылка устаревает и команды (пауза и т.д.) теряются
                // Используем PcNumberValue для сравнения, так как оно не меняется при переименовании
                if (_selected?.PcNumberValue == client.PcNumberValue)
                    RefreshSelected();

                UpdateStats();
                if (_selected?.PcNumberValue == client.PcNumberValue)
                    UpdateBottomPanel(_selected!);
            });
        }

        /// <summary>
        /// Обновляет _selected до актуального объекта из KnownClients.
        /// RegisterClient заменяет объект в словаре — без этого клик «Пауза» и другие
        /// команды будут изменять мёртвую копию и не влиять на реальное состояние.
        /// </summary>
        private void RefreshSelected()
        {
            if (_selected == null) return;
            // Ищем по PcNumberValue, так как PcNumber может измениться при переименовании
            var fresh = AdminHub.KnownClients.Values.FirstOrDefault(c => c.PcNumberValue == _selected.PcNumberValue);
            if (fresh != null)
                _selected = fresh;
        }

        private void OnClientsChanged()
        {
            Dispatcher.Invoke(() =>
            {
                BuildGrid();
                UpdateStats();
            });
        }

        private void OnClientOfflineWithSession(ClientState client)
        {
            Dispatcher.Invoke(() =>
            {
                // Если окно для этого ПК уже открыто — не создаём дублирующее
                if (_activeOfflineWindows.TryGetValue(client.PcNumber, out var existing) && existing.IsVisible)
                    return;

                var win = new OfflineAlertWindow(
                    client,
                    onPause: () => OnPauseDecision(client.PcNumber),
                    onContinue: () => OnContinueDecision(client.PcNumber)
                );
                _activeOfflineWindows[client.PcNumber] = win;
                win.Closed += (s, e) => _activeOfflineWindows.Remove(client.PcNumber);
                win.Show();
            });
        }

        private async Task OnPauseDecision(string pcNumber)
        {
            AdminHub.SetOfflineDecision(pcNumber, OfflineDecision.Pause);

            if (AdminHub.KnownClients.TryGetValue(pcNumber, out var client) &&
                client.IsOnline &&
                _hub?.State == HubConnectionState.Connected)
            {
                int frozenElapsed = client.ElapsedAtDisconnect;
                await _hub.InvokeAsync("SendCommand", pcNumber,
                    JsonSerializer.Serialize(new { Type = "SESSION_TIME_SYNC", Value = frozenElapsed.ToString() }));
                await _hub.InvokeAsync("SendCommand", pcNumber,
                    JsonSerializer.Serialize(new { Type = "PAUSE_SESSION", Value = "" }));
            }
        }

        private void OnContinueDecision(string pcNumber)
        {
            AdminHub.SetOfflineDecision(pcNumber, OfflineDecision.Continue);
        }

        private void OnClientTimeMismatch(string pcNumber, int clientSecs, int serverSecs)
        {
            Dispatcher.Invoke(() =>
            {
                var win = new TimeMismatchAlertWindow(pcNumber, clientSecs, serverSecs);
                win.Show();
            });
        }

        private void OnClientTimeDrift(string pcNumber, double offsetSeconds)
        {
            Dispatcher.Invoke(() =>
            {
                var win = new ClockDriftAlertWindow(pcNumber, offsetSeconds);
                win.Show();
            });
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            bool needRebuild = false;
            var nowUtc = DateTime.UtcNow;

            foreach (var pc in AdminHub.KnownClients.Values)
            {
                if ((pc.IsSession || pc.Status == "Пауза") && pc.SessionStart.HasValue)
                {
                    int prev = pc.ElapsedSeconds;
                    int newElapsed;

                    if (pc.IsPaused)
                    {
                        newElapsed = pc.AccumulatedSeconds;
                    }
                    else
                    {
                        newElapsed = pc.AccumulatedSeconds + (int)(nowUtc - pc.SessionStart.Value).TotalSeconds;
                    }

                    newElapsed = Math.Max(0, newElapsed);

                    // ✅ Обновляем только если время реально изменилось
                    if (newElapsed != pc.ElapsedSeconds)
                    {
                        pc.ElapsedSeconds = newElapsed;
                        needRebuild = true;
                    }

                    // 🔄 УМНАЯ СИНХРОНИЗАЦИЯ: только при расхождении >10 сек ИЛИ раз в 30 секунд
                    if (!pc.IsPaused && _hub?.State == HubConnectionState.Connected && pc.IsOnline)
                    {
                        int serverElapsed = pc.AccumulatedSeconds + (int)(nowUtc - pc.SessionStart.Value).TotalSeconds;
                        int diff = Math.Abs(serverElapsed - pc.ElapsedSeconds);

                        // Синхронизируем только если расхождение значительное или прошло 30 секунд
                        if (diff > 10 || pc.ElapsedSeconds % 30 == 0)
                        {
                            _ = _hub.InvokeAsync("SyncSessionTime", pc.PcNumber);
                        }
                    }
                }
                // ✅ Для онлайн ПК без сессии тоже обновляем (чтобы статус отобразился)
                else if (pc.IsOnline)
                {
                    needRebuild = true;
                }
            }

            // ✅ Всегда перестраиваем если нужно + если выбран ПК
            if (needRebuild || _selected != null)
            {
                BuildGrid();
                if (_selected != null)
                    UpdateBottomPanel(_selected);
            }

            UpdateStats();
        }

        private void BuildGrid()
        {
            PcPanel.Children.Clear();

            if (AdminHub.KnownClients.IsEmpty)
            {
                PcPanel.Children.Add(new TextBlock
                {
                    Text = "Нет подключённых компьютеров",
                    Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                    FontSize = 14,
                    Margin = new Thickness(8)
                });
                return;
            }

            // Сортировка клиентов в зависимости от выбранного режима
            var sortedClients = _currentSortMode switch
            {
                "ByName" => AdminHub.KnownClients.Values
                                  .OrderBy(c => string.IsNullOrEmpty(c.CustomName) ? "zzz" : c.CustomName.ToLower())
                                  .ThenBy(c => c.PcNumberValue),
                "ByNumber" => AdminHub.KnownClients.Values
                                  .OrderBy(c => c.PcNumberValue)
                                  .ThenBy(c => c.CustomName),
                _ => AdminHub.KnownClients.Values.OrderBy(c => c.PcNumberValue)
            };

            foreach (var pc in sortedClients)
            {
                var btn = new Button { Style = (Style)FindResource("PcCard") };
                ApplyCardStyle(btn, pc);

                // Выделение выбранной карточки синей рамкой
                if (_selected?.PcNumberValue == pc.PcNumberValue)
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(55, 138, 221));

                var stack = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(10, 7, 10, 7)
                };

                // ── Строка 1: кружок + имя ПК (+ ★ если инд. настройки) ──
                var namePanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                namePanel.Children.Add(new TextBlock
                {
                    Text = "●",
                    FontSize = 8,
                    Foreground = GetDotColor(pc),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 1, 5, 0)
                });
                namePanel.Children.Add(new TextBlock
                {
                    Text = pc.PcNumber,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = GetTextColor(pc),
                    VerticalAlignment = VerticalAlignment.Center
                });
                if (pc.HasIndividualSettings)
                    namePanel.Children.Add(new TextBlock
                    {
                        Text = " ★",
                        FontSize = 9,
                        Foreground = new SolidColorBrush(Color.FromRgb(239, 159, 39)),
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = "Есть индивидуальные настройки"
                    });
                stack.Children.Add(namePanel);

                // ── Разделитель ───────────────────────────────────────────
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
                    Margin = new Thickness(0, 5, 0, 5),
                    Opacity = 0.6
                });

                // ── Строка 2: тип · статус сессии  ИЛИ  статус ПК ───────
                stack.Children.Add(new TextBlock
                {
                    Text = GetMainStatusText(pc),
                    FontSize = 11,
                    FontWeight = pc.IsSession ? FontWeights.Medium : FontWeights.Normal,
                    Foreground = GetMainStatusColor(pc),
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                // ── Строка 3: "📵 нет связи" — только если оффлайн + сессия ──
                if (!pc.IsOnline && pc.IsSession)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = "📵 нет связи",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(200, 80, 80)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }

                // ── Строка 4: таймер (только при наличии сессии) ─────────
                if (pc.IsSession)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = FormatTime(pc.ElapsedSeconds),
                        FontSize = 15,
                        FontWeight = FontWeights.Bold,
                        Foreground = GetTimerColor(pc),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 3, 0, 0)
                    });

                    // ── Строка 5: доп. инфо (стоимость VIP / остаток Лимит) ──
                    if (pc.SessionType == "VIP")
                    {
                        int cost = (int)(pc.ElapsedSeconds * Tariff / 3600.0);
                        stack.Children.Add(new TextBlock
                        {
                            Text = $"К оплате: {cost:N0} сум",
                            FontSize = 10,
                            Foreground = new SolidColorBrush(Color.FromRgb(133, 79, 11)),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 2, 0, 0)
                        });
                    }
                    else if (pc.LimitSeconds > 0 && !pc.IsPaused)
                    {
                        int rem = Math.Max(0, pc.LimitSeconds - pc.ElapsedSeconds);
                        stack.Children.Add(new TextBlock
                        {
                            Text = $"ост. {FormatTime(rem)}",
                            FontSize = 10,
                            Foreground = rem <= 300
                                ? new SolidColorBrush(Color.FromRgb(220, 60, 60))
                                : new SolidColorBrush(Color.FromRgb(80, 120, 90)),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 2, 0, 0)
                        });
                    }
                }

                btn.Content = stack;
                var pcRef = pc;
                btn.Click += (s, e) => SelectPc(pcRef);
                btn.ContextMenu = BuildContextMenu(pc);

                PcPanel.Children.Add(btn);
            }
        }

        private ContextMenu BuildContextMenu(ClientState pc)
        {
            var menuBg      = new SolidColorBrush(Color.FromRgb(28, 28, 40));
            var hoverBg     = new SolidColorBrush(Color.FromRgb(55, 55, 100));
            var separatorBg = new SolidColorBrush(Color.FromRgb(50, 50, 75));
            var textColor   = Brushes.White;
            var dangerColor = new SolidColorBrush(Color.FromRgb(226, 75, 74));
            var iconColor   = new SolidColorBrush(Color.FromRgb(170, 170, 204));

            var global = GlobalSettings.Load();

            // ── Root context menu template ────────────────────────────────────────
            var menuTemplate = new ControlTemplate(typeof(ContextMenu));
            var menuBorder   = new FrameworkElementFactory(typeof(Border));
            menuBorder.SetValue(Border.BackgroundProperty, menuBg);
            menuBorder.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(61, 61, 107)));
            menuBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            menuBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            menuBorder.SetValue(Border.PaddingProperty, new Thickness(4));
            menuBorder.SetValue(UIElement.EffectProperty, new DropShadowEffect
                { BlurRadius = 12, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black });
            menuBorder.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            menuTemplate.VisualTree = menuBorder;

            var menu = new ContextMenu
            {
                Template = menuTemplate,
                Background = menuBg,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0)
            };

            // ── Leaf item ─────────────────────────────────────────────────────────
            MenuItem MakeItem(string icon, string header, Action action, bool isDanger = false)
            {
                var t  = new ControlTemplate(typeof(MenuItem));
                var bd = new FrameworkElementFactory(typeof(Border));
                bd.Name = "Bd";
                bd.SetValue(Border.BackgroundProperty, menuBg);
                bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                bd.SetValue(Border.MarginProperty, new Thickness(0, 1, 0, 1));

                var sp = new FrameworkElementFactory(typeof(StackPanel));
                sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
                sp.SetValue(StackPanel.MarginProperty, new Thickness(10, 6, 14, 6));

                var ico = new FrameworkElementFactory(typeof(TextBlock));
                ico.SetValue(TextBlock.TextProperty, icon);
                ico.SetValue(TextBlock.FontSizeProperty, 12.0);
                ico.SetValue(TextBlock.WidthProperty, 20.0);
                ico.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
                ico.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
                ico.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 10, 0));
                ico.SetValue(TextBlock.ForegroundProperty, isDanger ? dangerColor : iconColor);

                var txt = new FrameworkElementFactory(typeof(TextBlock));
                txt.SetValue(TextBlock.TextProperty, header);
                txt.SetValue(TextBlock.FontSizeProperty, 13.0);
                txt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
                txt.SetValue(TextBlock.ForegroundProperty, isDanger ? dangerColor : textColor);

                sp.AppendChild(ico);
                sp.AppendChild(txt);
                bd.AppendChild(sp);
                t.VisualTree = bd;

                var tr = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
                tr.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "Bd"));
                t.Triggers.Add(tr);

                var item = new MenuItem { Template = t, Background = menuBg, BorderThickness = new Thickness(0) };
                item.Click += (s, e) => action();
                return item;
            }

            // ── Separator ─────────────────────────────────────────────────────────
            UIElement MakeSep()
            {
                var st = new ControlTemplate(typeof(Separator));
                var sb = new FrameworkElementFactory(typeof(Border));
                sb.SetValue(Border.HeightProperty, 1.0);
                sb.SetValue(Border.BackgroundProperty, separatorBg);
                sb.SetValue(Border.MarginProperty, new Thickness(10, 4, 10, 4));
                st.VisualTree = sb;
                return new Separator { Template = st, Background = menuBg };
            }

            // ── Submenu parent (dark popup matching the root menu) ────────────────
            MenuItem MakeSubMenu(string icon, string header, IEnumerable<object> children)
            {
                var t       = new ControlTemplate(typeof(MenuItem));
                var rootGrd = new FrameworkElementFactory(typeof(Grid));

                // Header row
                var bd = new FrameworkElementFactory(typeof(Border));
                bd.Name = "Bd";
                bd.SetValue(Border.BackgroundProperty, menuBg);
                bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
                bd.SetValue(Border.MarginProperty, new Thickness(0, 1, 0, 1));

                var sp = new FrameworkElementFactory(typeof(StackPanel));
                sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
                sp.SetValue(StackPanel.MarginProperty, new Thickness(10, 6, 14, 6));

                var ico = new FrameworkElementFactory(typeof(TextBlock));
                ico.SetValue(TextBlock.TextProperty, icon);
                ico.SetValue(TextBlock.FontSizeProperty, 12.0);
                ico.SetValue(TextBlock.WidthProperty, 20.0);
                ico.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
                ico.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
                ico.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 10, 0));
                ico.SetValue(TextBlock.ForegroundProperty, iconColor);

                var lbl = new FrameworkElementFactory(typeof(TextBlock));
                lbl.SetValue(TextBlock.TextProperty, header);
                lbl.SetValue(TextBlock.FontSizeProperty, 13.0);
                lbl.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
                lbl.SetValue(TextBlock.ForegroundProperty, textColor);

                var arrow = new FrameworkElementFactory(typeof(TextBlock));
                arrow.SetValue(TextBlock.TextProperty, " ›");
                arrow.SetValue(TextBlock.FontSizeProperty, 15.0);
                arrow.SetValue(TextBlock.ForegroundProperty, iconColor);
                arrow.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

                sp.AppendChild(ico);
                sp.AppendChild(lbl);
                sp.AppendChild(arrow);
                bd.AppendChild(sp);

                // Popup with same dark border as root menu
                var popup = new FrameworkElementFactory(typeof(Popup));
                popup.SetValue(Popup.PlacementProperty, PlacementMode.Right);
                popup.SetValue(Popup.AllowsTransparencyProperty, true);
                popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
                popup.SetBinding(Popup.IsOpenProperty, new Binding(nameof(MenuItem.IsSubmenuOpen))
                    { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });

                var popupBd = new FrameworkElementFactory(typeof(Border));
                popupBd.SetValue(Border.BackgroundProperty, menuBg);
                popupBd.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(61, 61, 107)));
                popupBd.SetValue(Border.BorderThicknessProperty, new Thickness(1));
                popupBd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
                popupBd.SetValue(Border.PaddingProperty, new Thickness(4));
                popupBd.SetValue(UIElement.EffectProperty, new DropShadowEffect
                    { BlurRadius = 12, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black });
                popupBd.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
                popup.AppendChild(popupBd);

                rootGrd.AppendChild(bd);
                rootGrd.AppendChild(popup);
                t.VisualTree = rootGrd;

                var tr = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
                tr.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "Bd"));
                t.Triggers.Add(tr);

                var item = new MenuItem { Template = t, Background = menuBg, BorderThickness = new Thickness(0) };
                foreach (var child in children)
                    item.Items.Add(child);
                return item;
            }

            // ── Toggle text helpers ───────────────────────────────────────────────
            string PcNumberText() => (pc.IsIndividual("SHOW_PC_NUMBER") ? pc.ShowPcNumber : global.ShowPcNumber)
                ? "Скрыть номер ПК" : "Показать номер ПК";
            string UsbText() => (pc.IsIndividual("USB_BLOCK") ? pc.UsbBlocked : global.UsbBlocked)
                ? "Разблокировать USB" : "Заблокировать USB";
            string TaskMgrText() => (pc.IsIndividual("TASKMGR_DISABLE") ? pc.TaskMgrDisabled : global.TaskMgrDisabled)
                ? "Включить диспетчер задач" : "Отключить диспетчер задач";

            // ═════════════════════════════════════════════════════════════════════
            // Построение меню
            // ═════════════════════════════════════════════════════════════════════

            menu.Items.Add(MakeItem("✎", "Переименовать", () => ShowRenameDialog(pc)));

            if (pc.IsSession)
                menu.Items.Add(MakeItem("↔", "Пересадить пользователя", () => ShowTransferDialog(pc)));

            menu.Items.Add(MakeSep());

            // ── Подменю «Настройки ПК» ───────────────────────────────────────────
            var settingsChildren = new List<object>
            {
                MakeItem("▣", "Изменить фон", () => ShowBackgroundDialog(pc)),
                MakeItem(
                    pc.IsIndividual("SHOW_PC_NUMBER") ? (pc.ShowPcNumber ? "◎" : "●") : (global.ShowPcNumber ? "◎" : "●"),
                    PcNumberText(), () => TogglePcNumber(pc))
            };
            if (pc.HasIndividualSettings)
            {
                settingsChildren.Add(MakeSep());
                settingsChildren.Add(MakeItem("🔄", "Сбросить к глобальным", () => ResetIndividualSettings(pc), isDanger: true));
            }
            menu.Items.Add(MakeSubMenu("🖥", "Настройки ПК", settingsChildren));

            // ── Подменю «Ограничения» ────────────────────────────────────────────
            var restrictChildren = new List<object>
            {
                MakeItem(
                    pc.IsIndividual("USB_BLOCK") ? (pc.UsbBlocked ? "▶" : "■") : (global.UsbBlocked ? "▶" : "■"),
                    UsbText(), () => ToggleUsb(pc)),
                MakeItem(
                    pc.IsIndividual("TASKMGR_DISABLE") ? (pc.TaskMgrDisabled ? "▶" : "■") : (global.TaskMgrDisabled ? "▶" : "■"),
                    TaskMgrText(), () => ToggleTaskMgr(pc)),
                MakeSep(),
                MakeItem("■", global.BlockRegedit   ? "Разрешить regedit"  : "Запретить regedit",  () => ToggleRegedit(pc)),
                MakeItem("■", global.BlockCmd        ? "Разрешить CMD"      : "Запретить CMD",       () => ToggleCmd(pc)),
                MakeItem("■", global.BlockPowerShell ? "Разрешить PowerShell" : "Запретить PowerShell", () => TogglePowerShell(pc)),
                MakeItem("■", global.BlockInstall    ? "Разрешить установку" : "Запретить установку программ", () => ToggleInstall(pc))
            };
            menu.Items.Add(MakeSubMenu("🛡", "Ограничения", restrictChildren));

            menu.Items.Add(MakeSep());
            menu.Items.Add(MakeItem("⟳", "Переподключить клиент", () => ReconnectPc(pc)));
            menu.Items.Add(MakeItem("↺", "Перезагрузить ПК",      () => RestartPc(pc)));
            menu.Items.Add(MakeItem("⏻", "Выключить ПК",           () => ShutdownPc(pc), isDanger: true));

            return menu;
        }

        private void SelectPc(ClientState pc)
        {
            _selected = pc;
            BuildGrid();
            TxtHint.Visibility = Visibility.Collapsed;
            PanelInfo.Visibility = Visibility.Visible;
            PanelButtons.Visibility = Visibility.Visible;
            UpdateBottomPanel(pc);
        }

        private void UpdateBottomPanel(ClientState pc)
        {
            TxtPcName.Text = pc.PcNumber;
            TxtPcStatus.Text = GetMainStatusText(pc);
            TxtPcStatus.Foreground = GetMainStatusColor(pc);

            bool isOnline = pc.IsOnline && pc.Status != "Оффлайн";
            bool isLocked = pc.Status == "Заблокирован";
            bool hasSession = pc.IsSession || pc.Status == "Пауза";

            BtnFreeAccess.Visibility = isOnline && isLocked && !hasSession ? Visibility.Visible : Visibility.Collapsed;
            BtnPaidSession.Visibility = isOnline && isLocked && !hasSession ? Visibility.Visible : Visibility.Collapsed;
            BtnForceBlock.Visibility = isOnline && !isLocked && !hasSession ? Visibility.Visible : Visibility.Collapsed;

            BtnExtend.Visibility = hasSession && !pc.IsPaused && pc.Status != "VIP" ? Visibility.Visible : Visibility.Collapsed;
            BtnEndSession.Visibility = hasSession ? Visibility.Visible : Visibility.Collapsed;
            BtnPauseSession.Visibility = hasSession ? Visibility.Visible : Visibility.Collapsed;
            BtnPauseSession.Content = pc.IsPaused ? "▶ Продолжить" : "⏸ Пауза";
        }

        private void ApplyCardStyle(Button btn, ClientState pc)
        {
            if (!pc.IsOnline)
            {
                if (pc.IsSession)
                {
                    // Оффлайн, но сессия продолжает идти — тёплый красный фон
                    btn.Background = new SolidColorBrush(Color.FromRgb(253, 236, 236));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(210, 100, 100));
                }
                else
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(235, 235, 235));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200));
                }
                return;
            }
            btn.Background = pc.Status switch
            {
                "Заблокирован" => new SolidColorBrush(Color.FromRgb(241, 239, 232)),
                "Свободный"    => new SolidColorBrush(Color.FromRgb(230, 241, 251)),
                "VIP"          => new SolidColorBrush(Color.FromRgb(250, 238, 218)),
                "Лимит"        => new SolidColorBrush(Color.FromRgb(225, 245, 238)),
                "Пауза"        => new SolidColorBrush(Color.FromRgb(255, 249, 230)),
                _              => Brushes.White
            };
            btn.BorderBrush = pc.Status switch
            {
                "Заблокирован" => new SolidColorBrush(Color.FromRgb(180, 178, 169)),
                "Свободный"    => new SolidColorBrush(Color.FromRgb(55, 138, 221)),
                "VIP"          => new SolidColorBrush(Color.FromRgb(239, 159, 39)),
                "Лимит"        => new SolidColorBrush(Color.FromRgb(29, 158, 117)),
                "Пауза"        => new SolidColorBrush(Color.FromRgb(210, 140, 30)),
                _              => new SolidColorBrush(Color.FromRgb(220, 220, 220))
            };
        }

        // ── Кружок: зелёный = онлайн, красный = оффлайн ─────────────────────────
        private SolidColorBrush GetDotColor(ClientState pc) =>
            pc.IsOnline
                ? new SolidColorBrush(Color.FromRgb(34, 197, 94))    // зелёный
                : new SolidColorBrush(Color.FromRgb(239, 68, 68));    // красный

        // ── Основной статус: тип · состояние  ИЛИ  статус ПК ────────────────
        private string GetMainStatusText(ClientState pc)
        {
            if (pc.IsSession)
            {
                string state = pc.IsPaused ? "Пауза" : "Идёт";
                return pc.SessionType switch
                {
                    "VIP"   => $"⭐ VIP · {state}",
                    "Лимит" => $"⏱ Лимит · {state}",
                    _       => $"{pc.SessionType} · {state}"
                };
            }
            if (!pc.IsOnline) return "Оффлайн";
            return pc.Status switch
            {
                "Заблокирован" => "🔒 Заблокирован",
                "Свободный"    => "🔓 Свободен",
                _              => pc.Status
            };
        }

        // ── Цвет основного статуса ────────────────────────────────────────────
        private SolidColorBrush GetMainStatusColor(ClientState pc)
        {
            if (pc.IsSession)
            {
                if (pc.IsPaused)
                    return new SolidColorBrush(Color.FromRgb(180, 120, 20));
                return pc.SessionType switch
                {
                    "VIP"   => new SolidColorBrush(Color.FromRgb(133, 79, 11)),
                    "Лимит" => new SolidColorBrush(Color.FromRgb(15, 110, 86)),
                    _       => new SolidColorBrush(Color.FromRgb(15, 110, 86))
                };
            }
            if (!pc.IsOnline) return new SolidColorBrush(Color.FromRgb(160, 160, 160));
            return pc.Status switch
            {
                "Заблокирован" => new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                "Свободный"    => new SolidColorBrush(Color.FromRgb(24, 95, 165)),
                _              => new SolidColorBrush(Color.FromRgb(100, 100, 100))
            };
        }

        // ── Цвет таймера ──────────────────────────────────────────────────────
        private SolidColorBrush GetTimerColor(ClientState pc)
        {
            if (!pc.IsOnline && pc.IsSession)
                return new SolidColorBrush(Color.FromRgb(200, 80, 80));   // красный (оффлайн)
            if (pc.IsPaused)
                return new SolidColorBrush(Color.FromRgb(180, 120, 20));  // янтарный (пауза)
            return pc.SessionType switch
            {
                "VIP"   => new SolidColorBrush(Color.FromRgb(133, 79, 11)),
                "Лимит" => new SolidColorBrush(Color.FromRgb(15, 110, 86)),
                _       => new SolidColorBrush(Color.FromRgb(15, 110, 86))
            };
        }

        // ── Цвет имени ПК ─────────────────────────────────────────────────────
        private SolidColorBrush GetStatusColor(ClientState pc) => GetMainStatusColor(pc);

        private SolidColorBrush GetTextColor(ClientState pc) =>
            !pc.IsOnline || pc.Status == "Заблокирован"
                ? new SolidColorBrush(Color.FromRgb(120, 120, 120))
                : new SolidColorBrush(Color.FromRgb(30, 30, 46));

        private void UpdateStats()
        {
            var clients = AdminHub.KnownClients.Values;
            TxtTotal.Text = AdminHub.KnownClients.Count.ToString();
            TxtOnline.Text = clients.Count(c => c.IsOnline).ToString();
            TxtSessions.Text = clients.Count(c => c.IsSession).ToString();
            TxtLocked.Text = clients.Count(c => c.IsOnline && c.IsLocked).ToString();
            TxtRevenue.Text = $"{Revenue:N0} сум";
        }

        private string FormatTime(int secs)
        {
            int h = secs / 3600, m = (secs % 3600) / 60, s = secs % 60;
            return $"{h:D2}:{m:D2}:{s:D2}";
        }

        private int CalcMoney(int secs) => (int)(Tariff * secs / 3600.0);

        // =====================
        // Обработчики кнопок
        // =====================
        private async void ForceBlock_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            RefreshSelected();
            var r = MessageBox.Show($"Заблокировать {_selected.PcNumber}?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            await SendCommand(_selected.PcNumber, "REMOTE_LOCK", "true");
            _selected.Status = "Заблокирован";
            _selected.SessionType = "";
            _selected.ElapsedSeconds = 0;
            _selected.LimitSeconds = 0;
            _selected.SessionStart = null;
            _selected.IsPaused = false;
            _selected.AccumulatedSeconds = 0;
            _selected.SessionId = "";
            AdminHub.SaveActiveSessions();
            AdminHub.AddPendingCommand(_selected.PcNumber, "REMOTE_LOCK", "true");
            SelectPc(_selected);
        }

        private void PauseSession_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            RefreshSelected(); // гарантируем работу с актуальным объектом из KnownClients

            if (_selected.IsPaused)
            {
                // ── Снять с паузы ─────────────────────────────────────────────────
                _selected.IsPaused = false;
                _selected.SessionStart = DateTime.UtcNow;
                // SessionType хранит правильный тип ("Лимит"/"VIP") — не перезаписываем
                _selected.Status = string.IsNullOrEmpty(_selected.SessionType) ? "Лимит" : _selected.SessionType;

                if (!_selected.IsOnline)
                {
                    // Оффлайн-ПК: фиксируем решение Continue в хабе — при реконнекте сессия продолжится
                    AdminHub.SetOfflineDecision(_selected.PcNumber, OfflineDecision.Continue);
                }

                _ = _hub?.InvokeAsync("SendCommand", _selected.PcNumber,
                    JsonSerializer.Serialize(new { Type = "RESUME_SESSION", Value = "" }));
            }
            else
            {
                // ── Поставить на паузу ────────────────────────────────────────────
                _selected.IsPaused = true;
                _selected.AccumulatedSeconds = _selected.ElapsedSeconds;
                // ❌ НЕ перезаписываем SessionType! Он уже правильный ("Лимит"/"VIP").
                // Старая строка "SessionType = Status" портила тип, если Status был "Оффлайн".
                _selected.Status = "Пауза";

                if (!_selected.IsOnline)
                {
                    // Оффлайн-ПК: фиксируем решение Pause в хабе — при реконнекте сессия встанет на паузу
                    AdminHub.SetOfflineDecision(_selected.PcNumber, OfflineDecision.Pause);
                }

                _ = _hub?.InvokeAsync("SendCommand", _selected.PcNumber,
                    JsonSerializer.Serialize(new { Type = "PAUSE_SESSION", Value = "" }));
            }
            BuildGrid();
            UpdateBottomPanel(_selected);
        }

        private async void FreeAccess_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            RefreshSelected();
            await SendCommand(_selected.PcNumber, "REMOTE_UNLOCK", "free");
            _selected.Status = "Свободный";
            BuildGrid();
            SelectPc(_selected);
            UpdateStats();
        }

        private void PaidSession_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            RefreshSelected();
            var dialog = new SessionDialog(_selected.PcNumber);
            if (dialog.ShowDialog() == true)
            {
                _selected.Status = dialog.SessionType;
                _selected.SessionType = dialog.SessionType;   // ← сразу синхронизируем SessionType
                _selected.SessionStart = DateTime.UtcNow;
                _selected.AccumulatedSeconds = 0;
                _selected.IsPaused = false;
                _selected.LimitSeconds = dialog.LimitSeconds;
                _selected.PaidAmount = dialog.PaidAmount;
                _selected.ElapsedSeconds = 0;

                _ = SendSessionCommandWithStartTime(
                    _selected.PcNumber, dialog.SessionType, dialog.LimitSeconds, dialog.PaidAmount, _selected.SessionStart.Value);

                BuildGrid();
                UpdateBottomPanel(_selected);
                UpdateStats();
            }
        }

        private async Task SendSessionCommandWithStartTime(string pcNumber, string sessionType, int limitSeconds, int paidAmount, DateTime serverStartTime)
        {
            try
            {
                if (_hub?.State == HubConnectionState.Connected)
                {
                    var cmd = new { Type = "START_SESSION", Value = sessionType, SessionType = sessionType, LimitSeconds = limitSeconds, PaidAmount = paidAmount, ServerStartTime = serverStartTime.ToString("o") };
                    await _hub.InvokeAsync("SendCommand", pcNumber, JsonSerializer.Serialize(cmd));
                }
            }
            catch (Exception ex) { Logger.Error($"Ошибка запуска сессии: {ex.Message}"); }
        }

        private void Extend_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            RefreshSelected();
            var dialog = new ExtendDialog(_selected.PcNumber, _selected.Status);
            if (dialog.ShowDialog() == true)
            {
                _selected.LimitSeconds += dialog.AddSeconds;
                _selected.PaidAmount += dialog.AddAmount;
                _ = SendExtendCommand(_selected.PcNumber, dialog.AddSeconds);
                SelectPc(_selected);
            }
        }

        private async Task SendExtendCommand(string pcNumber, int addSeconds)
        {
            try
            {
                if (_hub?.State == HubConnectionState.Connected)
                {
                    await _hub.InvokeAsync("SendCommand", pcNumber, JsonSerializer.Serialize(new { Type = "EXTEND_SESSION", Value = addSeconds.ToString(), LimitSeconds = addSeconds }));
                }
            }
            catch (Exception ex) { Logger.Error($"Ошибка продления: {ex.Message}"); }
        }

        private async void EndSession_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            RefreshSelected();

            int earned = CalcMoney(_selected.ElapsedSeconds);
            int refund = Math.Max(0, _selected.PaidAmount - earned);

            Revenue += earned;
            FinancePage.AddSession(new SessionRecord
            {
                PcNumber = _selected.PcNumber,
                SessionType = _selected.Status,
                UserName = _selected.UserName ?? "—",
                DurationSeconds = _selected.ElapsedSeconds,
                EarnedAmount = earned,
                PaidAmount = _selected.PaidAmount,
                RefundAmount = refund,
                StartTime = _selected.SessionStart ?? DateTime.Now,
                EndTime = DateTime.Now
            });

            string msg = $"Сессия завершена\n\nПК: {_selected.PcNumber}\nТип: {_selected.Status}\nВремя: {FormatTime(_selected.ElapsedSeconds)}\nОплачено: {_selected.PaidAmount:N0} сум\nИспользовано: {earned:N0} сум";
            if (refund > 0) msg += $"\n\n💵 Возврат пользователю: {refund:N0} сум";
            MessageBox.Show(msg, "Итог сессии", MessageBoxButton.OK, MessageBoxImage.Information);

            await SendCommand(_selected.PcNumber, "REMOTE_LOCK", "true");
            _selected.Status = "Заблокирован";
            _selected.SessionType = "";      // ← очищаем, иначе active_sessions.json восстановит сессию при рестарте
            _selected.ElapsedSeconds = 0;
            _selected.LimitSeconds = 0;
            _selected.PaidAmount = 0;
            _selected.SessionStart = null;
            _selected.IsPaused = false;
            _selected.AccumulatedSeconds = 0;
            _selected.SessionId = "";
            // Немедленно сохраняем: убираем запись из active_sessions.json до закрытия приложения
            AdminHub.SaveActiveSessions();
            // Страховка: если клиент переподключится позже — всё равно получит блокировку
            AdminHub.AddPendingCommand(_selected.PcNumber, "REMOTE_LOCK", "true");

            SelectPc(_selected);
            UpdateStats();
        }

        private async void LockAll_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("Заблокировать все ПК?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            foreach (var pc in AdminHub.KnownClients.Values.Where(pc => pc.IsOnline))
            {
                await SendCommand(pc.PcNumber, "REMOTE_LOCK", "true");
                pc.Status = "Заблокирован";
                pc.ElapsedSeconds = 0;
                pc.LimitSeconds = 0;
            }
            BuildGrid();
            UpdateStats();
        }

        private async void UnlockAll_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show("Разблокировать все ПК?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            foreach (var pc in AdminHub.KnownClients.Values.Where(pc => pc.IsOnline && pc.IsLocked))
            {
                await SendCommand(pc.PcNumber, "REMOTE_UNLOCK", "free");
                pc.Status = "Свободный";
            }
            BuildGrid();
            UpdateStats();
        }

        private async Task SendCommand(string pcNumber, string type, string value)
        {
            try
            {
                if (_hub?.State == HubConnectionState.Connected)
                {
                    await _hub.InvokeAsync("SendCommand", pcNumber, JsonSerializer.Serialize(new { Type = type, Value = value }));
                }
                else { Logger.Warn("Hub не подключён"); }
            }
            catch (Exception ex) { Logger.Error($"Ошибка команды: {ex.Message}"); }
        }

        // =====================
        // Индивидуальные настройки и утилиты
        // =====================
        private async void ShowRenameDialog(ClientState pc)
        {
            // Передаём только CustomName (без номера), так как диалог теперь принимает только имя
            var dialog = new PcSettingDialog(pc.PcNumber, "Переименовать ПК", "Новое имя (без номера):", pc.CustomName);
            if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.Result))
            {
                // Сначала отправляем команду клиенту на изменение имени (пока ключ ещё актуален)
                await SendCommand(pc.PcNumber, "SET_PC_NAME", dialog.Result);
                
                // Затем обновляем сервер (это изменит ключ в KnownClients и вызовет ClientsChanged)
                await RenameOnServer(pc.PcNumberValue, dialog.Result);
                
                // Обновляем _selected до актуального объекта после переименования
                RefreshSelected();
            }
        }

        private async Task RenameOnServer(int pcNumberValue, string newName)
        {
            try 
            { 
                if (_hub?.State == HubConnectionState.Connected) 
                    await _hub.InvokeAsync("SetClientCustomNameByValue", pcNumberValue, newName); 
            }
            catch (Exception ex) { Logger.Error($"Ошибка переименования: {ex.Message}"); }
        }

        private void ShowBackgroundDialog(ClientState pc)
        {
            var dialog = new PcBackgroundDialog(pc.PcNumber);
            if (dialog.ShowDialog() == true)
                AdminHub.MarkIndividualSetting(pc.PcNumber, "SET_BACKGROUND");
        }

        private async void TogglePcNumber(ClientState pc)
        {
            pc.ShowPcNumber = !pc.ShowPcNumber;
            AdminHub.MarkIndividualSetting(pc.PcNumber, "SHOW_PC_NUMBER");
            await SendCommand(pc.PcNumber, "SHOW_PC_NUMBER", pc.ShowPcNumber.ToString().ToLower());
        }

        private async void ToggleUsb(ClientState pc)
        {
            pc.UsbBlocked = !pc.UsbBlocked;
            AdminHub.MarkIndividualSetting(pc.PcNumber, "USB_BLOCK");
            await SendCommand(pc.PcNumber, "USB_BLOCK", pc.UsbBlocked.ToString().ToLower());
        }

        private async void ToggleTaskMgr(ClientState pc)
        {
            pc.TaskMgrDisabled = !pc.TaskMgrDisabled;
            AdminHub.MarkIndividualSetting(pc.PcNumber, "TASKMGR_DISABLE");
            await SendCommand(pc.PcNumber, "TASKMGR_DISABLE", pc.TaskMgrDisabled.ToString().ToLower());
        }

        private async void ToggleRegedit(ClientState pc)
        {
            var global = GlobalSettings.Load();
            global.BlockRegedit = !global.BlockRegedit;
            global.Save();
            AdminHub.MarkIndividualSetting(pc.PcNumber, "BLOCK_REGEDIT");
            await SendCommand(pc.PcNumber, "BLOCK_REGEDIT", global.BlockRegedit.ToString().ToLower());
            BuildGrid();
        }

        private async void ToggleCmd(ClientState pc)
        {
            var global = GlobalSettings.Load();
            global.BlockCmd = !global.BlockCmd;
            global.Save();
            AdminHub.MarkIndividualSetting(pc.PcNumber, "BLOCK_CMD");
            await SendCommand(pc.PcNumber, "BLOCK_CMD", global.BlockCmd.ToString().ToLower());
            BuildGrid();
        }

        private async void TogglePowerShell(ClientState pc)
        {
            var global = GlobalSettings.Load();
            global.BlockPowerShell = !global.BlockPowerShell;
            global.Save();
            AdminHub.MarkIndividualSetting(pc.PcNumber, "BLOCK_POWERSHELL");
            await SendCommand(pc.PcNumber, "BLOCK_POWERSHELL", global.BlockPowerShell.ToString().ToLower());
            BuildGrid();
        }

        private async void ToggleInstall(ClientState pc)
        {
            var global = GlobalSettings.Load();
            global.BlockInstall = !global.BlockInstall;
            global.Save();
            AdminHub.MarkIndividualSetting(pc.PcNumber, "BLOCK_INSTALL_UNINSTALL");
            await SendCommand(pc.PcNumber, "BLOCK_INSTALL_UNINSTALL", global.BlockInstall.ToString().ToLower());
            BuildGrid();
        }

        private async void ShowTransferDialog(ClientState pc)
        {
            var availablePcs = AdminHub.KnownClients.Values
                .Where(c => c.PcNumber != pc.PcNumber && c.IsOnline && !c.IsSession)
                .Select(c => c.PcNumber)
                .OrderBy(n => n)
                .ToList();

            if (!availablePcs.Any())
            {
                MessageBox.Show(
                    "Нет доступных ПК для пересадки.\n\nЦелевой ПК должен быть в сети и без активной сессии.",
                    "Пересадить", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string elapsed = FormatTime(pc.ElapsedSeconds);
            string remaining = pc.LimitSeconds > 0 ? $" · осталось {FormatTime(pc.RemainingSeconds)}" : "";
            string sessionInfo = $"{pc.SessionType} · прошло {elapsed}{remaining}";

            var dialog = new TransferSessionDialog(pc.PcNumber, sessionInfo, availablePcs);
            if (dialog.ShowDialog() != true) return;

            try
            {
                if (_hub?.State != HubConnectionState.Connected)
                {
                    MessageBox.Show("Нет подключения к серверу.", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var result = await _hub.InvokeAsync<string>("TransferSession", pc.PcNumber, dialog.SelectedPcNumber);
                if (result != "OK")
                    MessageBox.Show(result, "Ошибка пересадки", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                Logger.Error($"Ошибка TransferSession: {ex.Message}");
            }
        }

        private async void ReconnectPc(ClientState pc)
        {
            await SendCommand(pc.PcNumber, "RECONNECT", "true");
        }

        private async void RestartPc(ClientState pc)
        {
            var r = MessageBox.Show($"Перезагрузить {pc.PcNumber}?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) await SendCommand(pc.PcNumber, "RESTART", "true");
        }

        private async void ShutdownPc(ClientState pc)
        {
            var r = MessageBox.Show($"Выключить {pc.PcNumber}?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) await SendCommand(pc.PcNumber, "SHUTDOWN", "true");
        }

        private async void ResetIndividualSettings(ClientState pc)
        {
            var r = MessageBox.Show($"Сбросить индивидуальные настройки для {pc.PcNumber}?\n\nПК получит актуальные глобальные настройки.", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            AdminHub.ClearIndividualSettings(pc.PcNumber);

            if (_hub?.State == HubConnectionState.Connected)
            {
                var global = GlobalSettings.Load();
                foreach (var cmd in global.ToCommands())
                {
                    await SendCommand(pc.PcNumber, cmd.Type, cmd.Value);
                }
            }

            pc.ClearIndividual();
            BuildGrid();
        }
    }
}