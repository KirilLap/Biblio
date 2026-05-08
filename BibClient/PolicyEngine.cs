using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BibClient
{
    public static class PolicyEngine
    {
        public static string ActiveSessionType { get; private set; } = "";
        public static int ActiveElapsedSeconds { get; private set; } = 0;

        // Метод для сброса активной сессии (вызывается из MainWindow при разблокировке)
        public static void ResetSession()
        {
            ActiveSessionType = "";
            ActiveElapsedSeconds = 0;
        }

        // Метод для уведомления о завершении сессии (вызывается из SessionManager)
        public static void NotifySessionEnded()
        {
            EndSessionRequested?.Invoke();
        }

        // Блокировать ли ПК при потере связи во время сессии.
        // Значение читается из SettingsManager, чтобы пережить перезапуск.
        public static bool LockOnOffline
        {
            get => SettingsManager.Current.LockOnOffline;
            private set
            {
                SettingsManager.Current.LockOnOffline = value;
                SettingsManager.Save();
            }
        }

        public static event Action? RemoteUnlockRequested;
        public static Action? RemoteLockRequested;
        public static event Action? SettingsChanged;
        public static event Func<Task>? ReconnectRequested;
        // (sessionType, limitSeconds, paidAmount, initialElapsedSeconds)
        public static event Action<string, int, int, int>? StartSessionRequested;
        public static event Action<bool>? SessionPaused;
        public static event Action<int>? ExtendSessionRequested;
        public static event Action? EndSessionRequested;
        public static event Action<int>? UpdateSessionElapsedTime;

        // Фаза 4: дрейф системных часов — offsetSeconds > 0 → клиент отстаёт
        public static event Action<double>? ClockMismatchDetected;

        public static async Task HandleCommand(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("Type", out var typeProp)) return;
                var type = typeProp.GetString()?.ToUpper();
                var value = root.TryGetProperty("Value", out var valProp) ? valProp.GetString() ?? "" : "";
                Logger.Info($"📥 Команда: {type} = {value}");

                switch (type)
                {
                    // ── Управление блокировкой / разблокировкой ───────────────────────────
                    case "REMOTE_UNLOCK":
                        ActiveSessionType = "";
                        ActiveElapsedSeconds = 0;
                        RemoteUnlockRequested?.Invoke();
                        break;

                    case "REMOTE_LOCK":
                        ActiveSessionType = "";
                        ActiveElapsedSeconds = 0;
                        RemoteLockRequested?.Invoke();
                        break;

                    case "SETTINGS_CHANGED":
                        SettingsChanged?.Invoke();
                        break;

                    case "RECONNECT":
                        TimeZoneInfo.ClearCachedData();
                        if (ReconnectRequested != null) await ReconnectRequested();
                        break;

                    // ── Сессии ────────────────────────────────────────────────────────────
                    case "START_SESSION":
                        var limit = root.TryGetProperty("LimitSeconds", out var lp) ? lp.GetInt32() : 0;
                        var paid = root.TryGetProperty("PaidAmount", out var pap) ? pap.GetInt32() : 0;
                        var sType = root.TryGetProperty("SessionType", out var stp) ? stp.GetString() ?? value : value;
                        var initialElapsed = root.TryGetProperty("ElapsedSeconds", out var ep) ? ep.GetInt32() : 0;
                        ActiveSessionType = sType;
                        StartSessionRequested?.Invoke(sType, limit, paid, initialElapsed);
                        break;

                    case "PAUSE_SESSION":
                        SessionPaused?.Invoke(true);
                        break;

                    case "RESUME_SESSION":
                        SessionPaused?.Invoke(false);
                        break;

                    case "EXTEND_SESSION":
                        ExtendSessionRequested?.Invoke(int.TryParse(value, out int extSecs) ? extSecs : 0);
                        break;

                    case "END_SESSION":
                        ActiveSessionType = "";
                        ActiveElapsedSeconds = 0;
                        EndSessionRequested?.Invoke();
                        break;

                    // Завершение сессии по истечению времени (вызывается из SessionManager)
                    case "SESSION_EXPIRED":
                        ActiveSessionType = "";
                        ActiveElapsedSeconds = 0;
                        EndSessionRequested?.Invoke();
                        break;

                    case "SESSION_TIME_SYNC":
                        if (int.TryParse(value, out int serverTime))
                        {
                            UpdateSessionElapsedTime?.Invoke(serverTime);
                            ActiveElapsedSeconds = serverTime;
                        }
                        break;

                    // ── Смещение / расхождение часов ─────────────────────────────────────
                    case "SET_TIME_OFFSET":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double offset))
                        {
                            TimeZoneInfo.ClearCachedData();
                            SessionManager.SetTimeOffset(offset);
                            Logger.Info($"🕐 Смещение часов применено: {offset:F1}с");
                        }
                        break;

                    case "SET_PC_NAME":
                        // Обновляем отображаемое имя клиента
                        // value может быть пустым - это нормально (сброс на "ПК N")
                        SettingsManager.Current.CustomName = value ?? "";
                        SettingsManager.Save();
                        Logger.Info($"✅ Имя ПК изменено на: '{value}'");
                        // Мгновенно уведомляем UI об изменении
                        SettingsChanged?.Invoke();
                        break;

                    case "CLOCK_MISMATCH":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double drift))
                        {
                            SessionManager.SetTimeOffset(drift);
                            ClockMismatchDetected?.Invoke(drift);
                            Logger.Warn($"⚠️ Расхождение часов с сервером: {drift:F1}с");
                        }
                        break;

                    // ── Настройки отображения экрана блокировки ───────────────────────────
                    case "SHOW_PC_NAME":
                        SettingsManager.Current.ShowPcName = value.ToLower() == "true";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "SHOW_PC_NUMBER":
                        SettingsManager.Current.ShowPcNumber = value.ToLower() == "true";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "SET_BACKGROUND_OPACITY":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double opacity))
                        {
                            SettingsManager.Current.BackgroundOpacity = Math.Clamp(opacity, 0.0, 1.0);
                            SettingsManager.Save();
                            SettingsChanged?.Invoke();
                        }
                        break;

                    case "SET_PC_NUMBER_POSITION":
                        SettingsManager.Current.PcNumberPosition = value;
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "SET_PC_NUMBER_FONT_SIZE":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pcFont))
                        {
                            SettingsManager.Current.PcNumberFontSize = Math.Clamp(pcFont, 8, 200);
                            SettingsManager.Save();
                            SettingsChanged?.Invoke();
                        }
                        break;

                    case "SHOW_LOCKED_TEXT":
                        SettingsManager.Current.ShowLockedText = value.ToLower() == "true";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "SET_LOCKED_TEXT_POSITION":
                        SettingsManager.Current.LockedTextPosition = value;
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "SET_LOCKED_TEXT_FONT_SIZE":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lockedFont))
                        {
                            SettingsManager.Current.LockedTextFontSize = Math.Clamp(lockedFont, 8, 200);
                            SettingsManager.Save();
                            SettingsChanged?.Invoke();
                        }
                        break;

                    case "SET_TIME_POSITION":
                        SettingsManager.Current.TimePosition = value;
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "SET_TIME_FONT_SIZE":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double timeFont))
                        {
                            SettingsManager.Current.TimeFontSize = Math.Clamp(timeFont, 8, 200);
                            SettingsManager.Save();
                            SettingsChanged?.Invoke();
                        }
                        break;

                    case "SET_BACKGROUND":
                        if (!string.IsNullOrEmpty(value))
                        {
                            var localDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Files");
                            var localBg = Path.Combine(localDir, value);
                            if (!File.Exists(localBg))
                            {
                                try
                                {
                                    Directory.CreateDirectory(localDir);
                                    var serverUrl = $"http://{SettingsManager.Current.ServerIp}:{SettingsManager.Current.ServerPort}/files/{value}";
                                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                                    var data = await http.GetByteArrayAsync(serverUrl);
                                    await File.WriteAllBytesAsync(localBg, data);
                                    Logger.Info($"Фон скачан с сервера: {value}");
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error($"Ошибка скачивания фона: {ex.Message}");
                                }
                            }
                            SettingsManager.Current.BackgroundImagePath = File.Exists(localBg) ? localBg : value;
                            SettingsManager.Save();
                            SettingsChanged?.Invoke();
                        }
                        break;

                    // ── Тариф и пароль ────────────────────────────────────────────────────
                    case "SET_TARIFF":
                        if (int.TryParse(value, out int tariff) && tariff > 0)
                        {
                            SettingsManager.Current.Tariff = tariff;
                            SettingsManager.Save();
                            Logger.Info($"💰 Тариф обновлён: {tariff}");
                        }
                        break;

                    case "ADMIN_PASSWORD":
                        if (!string.IsNullOrEmpty(value))
                        {
                            // Setter хеширует пароль через MD5 перед сохранением
                            SettingsManager.Current.AdminPassword = value;
                            SettingsManager.Save();
                            Logger.Info("🔑 Пароль обновлён");
                        }
                        break;

                    // ── Политика оффлайн-блокировки ───────────────────────────────────────
                    case "LOCK_ON_OFFLINE":
                        LockOnOffline = value.ToLower() == "true";
                        Logger.Info($"🔒 LockOnOffline = {LockOnOffline}");
                        break;

                    // ── Системные ограничения (сохраняем + оповещаем) ─────────────────────
                    case "USB_BLOCK":
                        SettingsManager.Current.UsbBlocked = value.ToLower() == "true";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "TASKMGR_DISABLE":
                        SettingsManager.Current.TaskMgrDisabled = value.ToLower() == "true";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "BLOCK_REGEDIT":
                        SettingsManager.Current.BlockRegedit = value.ToLower() == "true";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "BLOCK_CMD":
                        SettingsManager.Current.BlockCmd = value.ToLower() == "true";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "BLOCK_POWERSHELL":
                        SettingsManager.Current.BlockPowerShell = value.ToLower() == "true";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "HIDE_DRIVE_C":
                        SettingsManager.Current.HideDriveC = value.ToLower() == "true";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "BLOCK_INSTALL_UNINSTALL":
                        SettingsManager.Current.BlockInstall = value.ToLower() == "true";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    // ── Защита и автозапуск (включены по умолчанию) ───────────────────────────────────────
                    case "PREVENT_CLOSE":
                        SettingsManager.Current.PreventClose = value.ToLower() == "true";
                        SettingsManager.Save();
                        Logger.Info($"🔒 PreventClose = {SettingsManager.Current.PreventClose}");
                        if (SettingsManager.Current.PreventClose)
                            ServiceManager.EnsureServiceRunning();
                        SettingsChanged?.Invoke();
                        break;

                    case "AUTOSTART_WITH_USER":
                        SettingsManager.Current.AutoStartWithUser = value.ToLower() == "true";
                        SettingsManager.Save();
                        if (SettingsManager.Current.AutoStartWithUser)
                        {
                            Watchdog.RegisterAutostart();
                            Logger.Info("✅ Автозапуск с пользователем включён");
                        }
                        else
                        {
                            Watchdog.UnregisterAutostart();
                            Logger.Info("❌ Автозапуск с пользователем выключен");
                        }
                        SettingsChanged?.Invoke();
                        break;

                    case "SHUTDOWN":
                        Logger.Info("💤 Выключение ПК по команде сервера");
                        Process.Start("shutdown", "/s /f /t 0");
                        break;

                    case "RESTART":
                        Logger.Info("🔄 Перезагрузка ПК по команде сервера");
                        Process.Start("shutdown", "/r /f /t 0");
                        break;

                    default:
                        // Неизвестная команда — просто уведомляем подписчиков
                        Logger.Warn($"⚠️ Неизвестная команда: {type}");
                        SettingsChanged?.Invoke();
                        break;
                }
            }
            catch (Exception ex) { Logger.Error($"Ошибка команды: {ex.Message}"); }
        }
    }
}
