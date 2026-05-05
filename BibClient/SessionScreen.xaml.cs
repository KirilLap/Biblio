using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace BibClient
{
    public class SessionState
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString();
        public string SessionType { get; set; } = "";
        public int LimitSeconds { get; set; }
        public int ElapsedSeconds { get; set; }
        public int Tariff { get; set; }
        public int PaidAmount { get; set; }
        public string ServerStartTimeUtc { get; set; } = "";
        public string SavedAtUtc { get; set; } = "";
        public bool IsPaused { get; set; }
        public int AccumulatedSeconds { get; set; }
        // ✅ Храним только числовой идентификатор для проверки соответствия ПК
        public int PcNumberValue { get; set; }
        public string ServerIp { get; set; } = "";
    }

    public class SessionManager : IDisposable
    {
        private string _sessionType = "";
        private int _limitSeconds = 0;
        private int _elapsedSeconds = 0;
        private int _tariff = 3000;
        private int _paidAmount = 0;
        private bool _isPaused = false;
        private string _sessionId = Guid.NewGuid().ToString();
        private bool _isFinished = false;

        private static double _timeOffset = 0;
        private static DateTime _serverStartTime = DateTime.MinValue;

        private DispatcherTimer _timer = new();
        private TrayIcon? _trayIcon;
        private SessionPopup? _popup;

        private static readonly string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BibClient");
        private static readonly string StateFilePath = Path.Combine(StateDir, "session_state.json");
        private static readonly string HeartbeatPath = Path.Combine(StateDir, "sync_heartbeat.json");

        private bool _warningShown5 = false, _warningShown4 = false, _warningShown3 = false, _warningShown2 = false, _warningShown1 = false;

        public event Action? SessionExpired;
        public event Action<int>? ElapsedUpdated;

        public static void SetTimeOffset(double offset) => _timeOffset = offset;
        public static void SetServerStartTime(DateTime startTime) => _serverStartTime = startTime;

        public static string GetStateFilePath() { try { Directory.CreateDirectory(StateDir); } catch { } return StateFilePath; }

        public static void ClearStateFile()
        {
            try { var path = GetStateFilePath(); if (File.Exists(path)) { File.Delete(path); Logger.Info($"🗑️ Файл состояния удалён"); } }
            catch (Exception ex) { Logger.Error($"Ошибка удаления файла: {ex.Message}"); }
            ClearHeartbeat();
        }

        // =====================
        // Heartbeat (пульс клиента — каждые 5 сек во время сессии)
        // =====================
        private void SaveHeartbeat()
        {
            try
            {
                Directory.CreateDirectory(StateDir);
                var data = new
                {
                    LastSyncTime = DateTime.UtcNow.ToString("o"),
                    SessionId = _sessionId,
                    ElapsedSeconds = _elapsedSeconds
                };
                File.WriteAllText(HeartbeatPath, JsonSerializer.Serialize(data));
            }
            catch { }
        }

        public static void ClearHeartbeat()
        {
            try { if (File.Exists(HeartbeatPath)) File.Delete(HeartbeatPath); }
            catch { }
        }

        public static (DateTime lastSync, string sessionId, int elapsed)? ReadHeartbeat()
        {
            try
            {
                if (!File.Exists(HeartbeatPath)) return null;
                var json = File.ReadAllText(HeartbeatPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var lastSync = DateTime.Parse(
                    root.GetProperty("LastSyncTime").GetString()!,
                    null, System.Globalization.DateTimeStyles.RoundtripKind);
                var sessionId = root.GetProperty("SessionId").GetString() ?? "";
                var elapsed = root.GetProperty("ElapsedSeconds").GetInt32();
                return (lastSync, sessionId, elapsed);
            }
            catch { return null; }
        }

        private void SaveSessionState()
        {
            if (_isFinished) { ClearStateFile(); return; }// ✅ Запрещаем сохранять завершенную сессию

            try
            {
                var state = new SessionState
                {
                    SessionId = _sessionId, SessionType = _sessionType, LimitSeconds = _limitSeconds,
                    ElapsedSeconds = _elapsedSeconds, Tariff = _tariff, PaidAmount = _paidAmount,
                    ServerStartTimeUtc = _serverStartTime != DateTime.MinValue ? _serverStartTime.ToString("o") : DateTime.UtcNow.AddSeconds(-_elapsedSeconds).ToString("o"),
                    SavedAtUtc = DateTime.UtcNow.ToString("o"), IsPaused = _isPaused,
                    AccumulatedSeconds = _isPaused ? _elapsedSeconds : 0,
                    PcNumberValue = SettingsManager.Current.PcNumberValue, ServerIp = SettingsManager.Current.ServerIp
                };
                var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                var tempPath = GetStateFilePath() + ".tmp";
                File.WriteAllText(tempPath, json);
                if (File.Exists(GetStateFilePath())) File.Replace(tempPath, GetStateFilePath(), null);
                else File.Move(tempPath, GetStateFilePath());
            }
            catch (Exception ex) { Logger.Error($"❌ Ошибка сохранения состояния: {ex.Message}"); }
        }

        public static SessionState? TryRestoreSession()
        {
            try
            {
                var path = GetStateFilePath();
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize<SessionState>(json);
                if (state == null) return null;

                if (!string.IsNullOrEmpty(state.SavedAtUtc) && DateTime.TryParse(state.SavedAtUtc, out var savedTime) && DateTime.UtcNow - savedTime > TimeSpan.FromHours(24))
                { File.Delete(path); return null; }
                // ✅ Сравниваем только числовой идентификатор (не зависит от переименования)
                if (state.PcNumberValue != SettingsManager.Current.PcNumberValue || state.ServerIp != SettingsManager.Current.ServerIp)
                { File.Delete(path); return null; }

                DateTime serverStart = DateTime.MinValue;
                bool isServerTimeValid = false;
                if (!string.IsNullOrEmpty(state.ServerStartTimeUtc) && DateTime.TryParse(state.ServerStartTimeUtc, null, DateTimeStyles.RoundtripKind, out var parsedStart) && parsedStart != DateTime.MinValue)
                { serverStart = parsedStart; isServerTimeValid = true; }

                if (isServerTimeValid)
                {
                    state.ServerStartTimeUtc = serverStart.ToString("o");
                }
                else
                {
                    // На случай, если время с сервера не пришло, восстанавливаем локально
                    serverStart = DateTime.UtcNow.AddSeconds(-state.ElapsedSeconds);
                    state.ServerStartTimeUtc = serverStart.ToString("o");
                }

                Logger.Info($"✅ Состояние сессии восстановлено из файла: {state.SessionType}, {state.ElapsedSeconds}с");
                return state;
            }
            catch (Exception ex) { Logger.Error($"❌ Ошибка загрузки состояния: {ex.Message}"); return null; }
        }

        public void SetPaused(bool paused)
        {
            _isPaused = paused;
            if (paused) _timer.Stop(); else _timer.Start();
            System.Windows.Application.Current.Dispatcher.Invoke(() => { if (_popup?.IsVisible == true) _popup.UpdateSession(_sessionType, _elapsedSeconds, _limitSeconds, _tariff, _isPaused); UpdateTrayTooltip(); });
            SaveSessionState();
        }

        public void UpdateElapsedTimeFromServer(int serverElapsedSeconds)
        {
            if (_isPaused) return;
            int diff = serverElapsedSeconds - _elapsedSeconds;
            if (Math.Abs(diff) < 5) return;
            if (Math.Abs(diff) > 30) _elapsedSeconds = serverElapsedSeconds;
            else if (diff != 0) _elapsedSeconds += Math.Sign(diff);
            System.Windows.Application.Current.Dispatcher.Invoke(() => { if (_popup?.IsVisible == true) _popup.UpdateSession(_sessionType, _elapsedSeconds, _limitSeconds, _tariff, _isPaused); });
        }

        public SessionManager(string sessionType, int limitSeconds, int tariff, TrayIcon trayIcon, int paidAmount = 0, int initialElapsedSeconds = 0)
        {
            _sessionType = sessionType; _limitSeconds = limitSeconds; _tariff = tariff; _paidAmount = paidAmount;
            _trayIcon = trayIcon; _elapsedSeconds = initialElapsedSeconds; _sessionId = Guid.NewGuid().ToString();
            _popup = new SessionPopup(); _popup.UpdateSession(sessionType, initialElapsedSeconds, limitSeconds, tariff, false);
            _trayIcon.ShowPopupRequested += ShowPopup;
            PolicyEngine.ExtendSessionRequested += OnExtend; PolicyEngine.EndSessionRequested += OnEnd;
            PolicyEngine.UpdateSessionElapsedTime += UpdateElapsedTimeFromServer; PolicyEngine.SessionPaused += SetPaused;
            _timer.Interval = TimeSpan.FromSeconds(1); _timer.Tick += Timer_Tick; _timer.Start();
            ShowPopup(); UpdateTrayTooltip(); SaveSessionState();
        }

        public void ShowPopup() { if (_popup == null) return; System.Windows.Application.Current.Dispatcher.Invoke(() => { _popup.UpdateSession(_sessionType, _elapsedSeconds, _limitSeconds, _tariff, _isPaused); _popup.Show(); _popup.Activate(); }); }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_isPaused) return;
            if (_serverStartTime != DateTime.MinValue) { var correctedNow = DateTime.UtcNow.AddSeconds(_timeOffset); _elapsedSeconds = (int)(correctedNow - _serverStartTime).TotalSeconds; _elapsedSeconds = Math.Max(0, _elapsedSeconds); }
            else _elapsedSeconds++;

            System.Windows.Application.Current.Dispatcher.Invoke(() => { if (_popup?.IsVisible == true) _popup.UpdateSession(_sessionType, _elapsedSeconds, _limitSeconds, _tariff, _isPaused); });
            if (_elapsedSeconds % 5 == 0) { UpdateTrayTooltip(); SaveHeartbeat(); }
            ElapsedUpdated?.Invoke(_elapsedSeconds); // ✅ Отправляем каждую секунду
            CheckWarnings();
            if (_limitSeconds > 0 && _elapsedSeconds >= _limitSeconds) { _isFinished = true; _timer.Stop(); ClearStateFile(); SessionExpired?.Invoke(); } // ✅ Ставим _isFinished = true
        }

        private void UpdateTrayTooltip()
        {
            string pauseText = _isPaused ? " ⏸ ПАУЗА" : "";
            string remaining = _limitSeconds > 0 && !_isPaused ? $" | Осталось: {FormatTime(Math.Max(0, _limitSeconds - _elapsedSeconds))}" : "";
            _trayIcon?.UpdateTooltip($"{SettingsManager.Current.PcNumber} | {_sessionType}{pauseText} | {FormatTime(_elapsedSeconds)}{remaining}");
        }

        private void CheckWarnings()
        {
            if (_limitSeconds <= 0 || _isPaused) return;
            int remaining = _limitSeconds - _elapsedSeconds;
            if (remaining == 300 && !_warningShown5) { _warningShown5 = true; ShowWarning("⚠️ Осталось 5 минут!"); }
            else if (remaining == 240 && !_warningShown4) { _warningShown4 = true; ShowWarning("⚠️ Осталось 4 минуты!"); }
            else if (remaining == 180 && !_warningShown3) { _warningShown3 = true; ShowWarning("⚠️ Осталось 3 минуты!"); }
            else if (remaining == 120 && !_warningShown2) { _warningShown2 = true; ShowWarning("⚠️ Осталось 2 минуты!"); }
            else if (remaining == 60 && !_warningShown1) { _warningShown1 = true; ShowWarning("⚠️ Осталось 1 минута!"); }
        }
        private void ShowWarning(string message) { _trayIcon?.ShowNotification("Внимание!", message, 5000); ShowPopup(); }

        private void OnExtend(int addSeconds)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => { _limitSeconds += addSeconds; int remaining = _limitSeconds - _elapsedSeconds; if (remaining > 300) { _warningShown5 = _warningShown4 = _warningShown3 = _warningShown2 = _warningShown1 = false; } _trayIcon?.ShowNotification("Сессия продлена", $"Добавлено {addSeconds / 60} мин"); UpdateTrayTooltip(); SaveSessionState(); });
        }

        private void OnEnd() { System.Windows.Application.Current.Dispatcher.Invoke(() => { _isFinished = true; _timer.Stop(); ClearStateFile(); SessionExpired?.Invoke(); }); } // ✅ Ставим _isFinished = true

        public int GetElapsedSeconds() => _elapsedSeconds;
        public string GetSessionType() => _sessionType;
        private string FormatTime(int secs) => $"{secs / 3600:D2}:{(secs % 3600) / 60:D2}:{secs % 60:D2}";

        public void Dispose()
        {
            _timer.Stop(); SaveSessionState();
            PolicyEngine.ExtendSessionRequested -= OnExtend; PolicyEngine.EndSessionRequested -= OnEnd;
            PolicyEngine.UpdateSessionElapsedTime -= UpdateElapsedTimeFromServer; PolicyEngine.SessionPaused -= SetPaused;
            if (_trayIcon != null) _trayIcon.ShowPopupRequested -= ShowPopup;
            System.Windows.Application.Current.Dispatcher.Invoke(() => { _popup?.Close(); _popup = null; });
        }
    }
}