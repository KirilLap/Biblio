using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BibClient
{
    public static class PolicyEngine
    {
        public static string ActiveSessionType { get; private set; } = "";
        public static int ActiveElapsedSeconds { get; private set; } = 0;

        public static bool LockOnOffline { get; private set; } = false;

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

        // Фаза 4: дрейф системных часов
        public static event Action<double>? ClockMismatchDetected;

        // Дебаунс gpupdate: накапливаем несколько команд политик, применяем одним запуском
        private static System.Threading.Timer? _gpUpdateTimer;

        private static void ScheduleGpUpdate()
        {
            _gpUpdateTimer?.Dispose();
            _gpUpdateTimer = new System.Threading.Timer(
                _ => GroupPolicyEngine.RunGpUpdate(), null, 3000, Timeout.Infinite);
        }

        public static async Task HandleCommand(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("Type", out var typeProp)) return;
                var type = typeProp.GetString();
                var value = root.TryGetProperty("Value", out var valProp) ? valProp.GetString() : "";
                Logger.Info($"📥 Команда: {type} = {value}");

                switch (type?.ToUpper())
                {
                    // ======================================================
                    // Управление сессией
                    // ======================================================
                    case "REMOTE_UNLOCK": RemoteUnlockRequested?.Invoke(); break;
                    case "REMOTE_LOCK": RemoteLockRequested?.Invoke(); break;
                    case "SETTINGS_CHANGED": SettingsChanged?.Invoke(); break;
                    case "RECONNECT": if (ReconnectRequested != null) await ReconnectRequested(); break;

                    case "START_SESSION":
                        var limit = root.TryGetProperty("LimitSeconds", out var lp) ? lp.GetInt32() : 0;
                        var paid = root.TryGetProperty("PaidAmount", out var pap) ? pap.GetInt32() : 0;
                        var sType = root.TryGetProperty("SessionType", out var stp) ? stp.GetString() ?? value : value;
                        var initialElapsed = root.TryGetProperty("ElapsedSeconds", out var ep) ? ep.GetInt32() : 0;
                        StartSessionRequested?.Invoke(sType, limit, paid, initialElapsed);
                        break;

                    case "PAUSE_SESSION": SessionPaused?.Invoke(true); break;
                    case "RESUME_SESSION": SessionPaused?.Invoke(false); break;
                    case "EXTEND_SESSION": ExtendSessionRequested?.Invoke(int.Parse(value ?? "0")); break;
                    case "END_SESSION": EndSessionRequested?.Invoke(); break;

                    case "SESSION_TIME_SYNC":
                        if (int.TryParse(value, out int serverTime))
                        {
                            UpdateSessionElapsedTime?.Invoke(serverTime);
                            ActiveElapsedSeconds = serverTime;
                        }
                        break;

                    case "LOCK_ON_OFFLINE":
                        LockOnOffline = value?.ToLower() == "true";
                        break;

                    case "SET_TIME_OFFSET":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double offset))
                        {
                            SessionManager.SetTimeOffset(offset);
                            Logger.Info($"🕐 Смещение часов применено: {offset:F1}с");
                        }
                        break;

                    case "CLOCK_MISMATCH":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double drift))
                        {
                            SessionManager.SetTimeOffset(drift);
                            ClockMismatchDetected?.Invoke(drift);
                            Logger.Warn($"⚠️ Расхождение часов с сервером: {drift:F1}с");
                        }
                        break;

                    // ======================================================
                    // Настройки внешнего вида экрана блокировки
                    // ======================================================
                    case "SHOW_PC_NUMBER":
                        SettingsManager.Current.ShowPcNumber = value?.ToLower() == "true";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "SET_BACKGROUND_OPACITY":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double opacity))
                        {
                            SettingsManager.Current.BackgroundOpacity = opacity;
                            SettingsManager.Save();
                            SettingsChanged?.Invoke();
                        }
                        break;

                    case "SET_PC_NUMBER_POSITION":
                        SettingsManager.Current.PcNumberPosition = value ?? "MiddleCenter";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "SET_PC_NUMBER_FONT_SIZE":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pcFont))
                        {
                            SettingsManager.Current.PcNumberFontSize = pcFont;
                            SettingsManager.Save();
                            SettingsChanged?.Invoke();
                        }
                        break;

                    case "SHOW_LOCKED_TEXT":
                        SettingsManager.Current.ShowLockedText = value?.ToLower() == "true";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "SET_LOCKED_TEXT_POSITION":
                        SettingsManager.Current.LockedTextPosition = value ?? "MiddleCenter";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "SET_LOCKED_TEXT_FONT_SIZE":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ltFont))
                        {
                            SettingsManager.Current.LockedTextFontSize = ltFont;
                            SettingsManager.Save();
                            SettingsChanged?.Invoke();
                        }
                        break;

                    case "SET_TIME_POSITION":
                        SettingsManager.Current.TimePosition = value ?? "BottomCenter";
                        SettingsManager.Save();
                        SettingsChanged?.Invoke();
                        break;

                    case "SET_TIME_FONT_SIZE":
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double timeFont))
                        {
                            SettingsManager.Current.TimeFontSize = timeFont;
                            SettingsManager.Save();
                            SettingsChanged?.Invoke();
                        }
                        break;

                    case "SET_BACKGROUND":
                        await ApplyBackgroundAsync(value ?? "");
                        break;

                    case "ADMIN_PASSWORD":
                        if (!string.IsNullOrEmpty(value))
                        {
                            SettingsManager.Current.AdminPassword = value;
                            SettingsManager.Save();
                        }
                        break;

                    case "SET_TARIFF":
                        if (int.TryParse(value, out int tariff))
                        {
                            SettingsManager.Current.Tariff = tariff;
                            SettingsManager.Save();
                        }
                        break;

                    // ======================================================
                    // Ограничения системы
                    // ======================================================
                    case "USB_BLOCK":
                        bool blockUsb = value?.ToLower() == "true";
                        SettingsManager.Current.UsbBlocked = blockUsb;
                        SettingsManager.Save();
                        GroupPolicyEngine.SetUsbBlock(blockUsb);
                        break;

                    case "TASKMGR_DISABLE":
                        bool disableTaskMgr = value?.ToLower() == "true";
                        SettingsManager.Current.TaskMgrDisabled = disableTaskMgr;
                        SettingsManager.Save();
                        GroupPolicyEngine.SetCtrlAltDelBlock(disableTaskMgr);
                        ScheduleGpUpdate();
                        break;

                    case "BLOCK_REGEDIT":
                        bool blockRegedit = value?.ToLower() == "true";
                        SettingsManager.Current.RegeditBlocked = blockRegedit;
                        SettingsManager.Save();
                        GroupPolicyEngine.SetRegeditBlock(blockRegedit);
                        ScheduleGpUpdate();
                        break;

                    case "BLOCK_CMD":
                        bool blockCmd = value?.ToLower() == "true";
                        SettingsManager.Current.CmdBlocked = blockCmd;
                        SettingsManager.Save();
                        GroupPolicyEngine.SetCmdBlock(blockCmd);
                        ScheduleGpUpdate();
                        break;

                    case "BLOCK_POWERSHELL":
                        bool blockPs = value?.ToLower() == "true";
                        SettingsManager.Current.PowerShellBlocked = blockPs;
                        SettingsManager.Save();
                        GroupPolicyEngine.SetPowerShellBlock(blockPs);
                        ScheduleGpUpdate();
                        break;

                    case "HIDE_DRIVE_C":
                        bool hideDriveC = value?.ToLower() == "true";
                        SettingsManager.Current.DriveCHidden = hideDriveC;
                        SettingsManager.Save();
                        GroupPolicyEngine.SetHideDriveC(hideDriveC);
                        break;

                    case "BLOCK_INSTALL_UNINSTALL":
                        bool blockInstall = value?.ToLower() == "true";
                        SettingsManager.Current.InstallBlocked = blockInstall;
                        SettingsManager.Save();
                        GroupPolicyEngine.SetInstallBlock(blockInstall);
                        ScheduleGpUpdate();
                        break;

                    default: SettingsChanged?.Invoke(); break;
                }
            }
            catch (Exception ex) { Logger.Error($"Ошибка команды: {ex.Message}"); }
        }

        private static async Task ApplyBackgroundAsync(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return;
            try
            {
                var s = SettingsManager.Current;
                string url = $"http://{s.ServerIp}:{s.ServerPort}/files/{Uri.EscapeDataString(fileName)}";
                string bgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backgrounds");
                Directory.CreateDirectory(bgDir);
                string localPath = Path.Combine(bgDir, fileName);

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var bytes = await http.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(localPath, bytes);

                SettingsManager.Current.BackgroundImagePath = localPath;
                SettingsManager.Save();
                SettingsChanged?.Invoke();
                Logger.Info($"Фон загружен: {localPath}");
            }
            catch (Exception ex) { Logger.Error($"Ошибка загрузки фона: {ex.Message}"); }
        }
    }
}
