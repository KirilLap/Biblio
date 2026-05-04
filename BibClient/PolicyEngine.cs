using System;
using System.Text.Json;
using System.Threading.Tasks;
namespace BibClient
{
    public static class PolicyEngine
    {
        public static string ActiveSessionType { get; private set; } = "";
        public static int ActiveElapsedSeconds { get; private set; } = 0;

        // Блокировать ли ПК при потере связи во время сессии
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

        // Фаза 4: дрейф системных часов — offsetSeconds > 0 → клиент отстаёт
        public static event Action<double>? ClockMismatchDetected;

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
                        if (int.TryParse(value, out int serverTime)) { UpdateSessionElapsedTime?.Invoke(serverTime); ActiveElapsedSeconds = serverTime; }
                        break;
                    case "LOCK_ON_OFFLINE":
                        LockOnOffline = value?.ToLower() == "true";
                        break;
                    case "SET_TIME_OFFSET":
                        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double offset))
                        {
                            SessionManager.SetTimeOffset(offset);
                            Logger.Info($"🕐 Смещение часов применено: {offset:F1}с");
                        }
                        break;
                    case "CLOCK_MISMATCH":
                        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double drift))
                        {
                            SessionManager.SetTimeOffset(drift);
                            ClockMismatchDetected?.Invoke(drift);
                            Logger.Warn($"⚠️ Расхождение часов с сервером: {drift:F1}с");
                        }
                        break;
                    default: SettingsChanged?.Invoke(); break;
                }
            }
            catch (Exception ex) { Logger.Error($"Ошибка команды: {ex.Message}"); }
        }
    }
}