using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;

namespace BibClient
{
    public class NetworkManager : IDisposable
    {
        private HubConnection? _hub;
        private readonly string _serverUrl;
        private bool _isConnected = false;
        private string _pcNumber = "";
        public bool IsRestoring { get; set; } = false;
        public event Action<bool>? ConnectionStateChanged;

        // Срабатывает после успешной регистрации на сервере
        public event Action? OnRegistered;

        public NetworkManager(string serverUrl)
        {
            _serverUrl = serverUrl;
            UpdateChecker.UpdateFailed += async reason =>
            {
                Logger.Info($"⬆️ Обновление не состоялось ({reason}) — сообщаем серверу");
                await ReportUpdateResultAsync(reason);
            };
            PolicyEngine.LogsReady += async logContent =>
            {
                if (!_isConnected || _hub == null || string.IsNullOrEmpty(_pcNumber)) return;
                try { await _hub.InvokeAsync("ReportLogs", _pcNumber, logContent); }
                catch (Exception ex) { Logger.Error($"❌ Ошибка отправки логов: {ex.Message}"); }
            };
        }

        // Бесконечная политика реконнекта: 2s, 5s, 10s, 30s, 30s, ...
        private sealed class InfiniteRetryPolicy : IRetryPolicy
        {
            private static readonly TimeSpan[] _delays =
            {
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            };

            public TimeSpan? NextRetryDelay(RetryContext ctx)
            {
                int idx = (int)Math.Min(ctx.PreviousRetryCount, _delays.Length - 1);
                return _delays[idx];
            }
        }

        public async Task StartAsync()
        {
            try
            {
                _hub = new HubConnectionBuilder()
                    .WithUrl(_serverUrl + "/hub")
                    .WithAutomaticReconnect(new InfiniteRetryPolicy())
                    .Build();

                _hub.On<string>("ReceiveCommand", async (json) => await PolicyEngine.HandleCommand(json));

                _hub.Reconnecting += _ =>
                {
                    _isConnected = false;
                    ConnectionStateChanged?.Invoke(false);
                    Logger.Warn("⚠️ Переподключение...");
                    return Task.CompletedTask;
                };

                _hub.Reconnected += async _ =>
                {
                    _isConnected = true;
                    ConnectionStateChanged?.Invoke(true);
                    Logger.Info("✅ Переподключение восстановлено");
                    await SendRegistrationAsync();
                };

                // Closed срабатывает только если InfiniteRetryPolicy вернёт null
                // (в нашем случае — никогда). Оставляем на всякий случай.
                _hub.Closed += async _ =>
                {
                    _isConnected = false;
                    ConnectionStateChanged?.Invoke(false);
                    Logger.Warn("⚠️ Соединение закрыто, повторная попытка через 5с");
                    await Task.Delay(5000);
                    await StartAsync();
                };

                await _hub.StartAsync();
                _isConnected = true;
                ConnectionStateChanged?.Invoke(true);
                Logger.Info("✅ Соединение установлено");
                await SendRegistrationAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"❌ Ошибка подключения: {ex.Message}");
                _isConnected = false;
                ConnectionStateChanged?.Invoke(false);
                // Повторная попытка через 5 секунд
                await Task.Delay(5000);
                await StartAsync();
            }
        }

        public async Task SendRegistrationAsync()
        {
            if (!_isConnected || _hub == null) return;
            try
            {
                var info = new SystemInfoDto
                {
                    HostName = Environment.MachineName,
                    OsVersion = Environment.OSVersion.VersionString,
                    LocalIp = GetLocalIpAddress(),
                    MacAddress = GetMacAddress(),
                    DiskFreeGb = GetDiskFreeGb(),
                    UptimeHours = GetUptimeHours(),
                    PcNumberValue = SettingsManager.Current.PcNumberValue,
                    CustomName = SettingsManager.Current.CustomName,
                    ClientVersion = UpdateChecker.CurrentVersion
                };

                // Читаем heartbeat-файл: SessionId и время последнего пульса → offline duration
                var heartbeat = SessionManager.ReadHeartbeat();
                string sessionId = heartbeat?.sessionId ?? "";
                int offlineSeconds = 0;

                // Если heartbeat существует — значит на клиенте была активная сессия.
                // Даже если IsRestoring уже сброшен (например, сервер перезапустился пока
                // клиент работал), мы всё равно восстанавливаем режим, чтобы:
                //   1) Не слать «Заблокирован» после регистрации (иначе сервер сбросит сессию)
                //   2) Корректно передать время оффлайна для верификации тайминга
                bool effectivelyRestoring = IsRestoring || heartbeat.HasValue;

                if (heartbeat.HasValue)
                {
                    offlineSeconds = Math.Max(0, (int)(DateTime.UtcNow - heartbeat.Value.lastSync).TotalSeconds);
                    Logger.Info($"🕐 Heartbeat: lastSync={heartbeat.Value.lastSync:HH:mm:ss}, offline={offlineSeconds}с, sessionId={sessionId[..Math.Min(8, sessionId.Length)]}… (effectivelyRestoring={effectivelyRestoring})");
                }

                _pcNumber = await _hub.InvokeAsync<string>("RegisterClient", info, info.MacAddress, effectivelyRestoring, sessionId, offlineSeconds);

                // При регистрации сервер возвращает полное имя (например, "Комп 10" или "ПК 10")
                // Но нам НЕ нужно парсить его через сеттер PcNumber, чтобы избежать дублирования номера
                // Вместо этого просто сохраняем для отображения, но не трогаем CustomName/PcNumberValue
                Logger.Info($"✅ Зарегистрирован как: {_pcNumber}");
                if (!effectivelyRestoring) await SendStatusAsync("Заблокирован");
                OnRegistered?.Invoke();
            }
            catch (Exception ex) { Logger.Error($"❌ Ошибка регистрации: {ex.Message}"); }
        }

        public async Task SendStatusAsync(string status)
        {
            if (!_isConnected || _hub == null || string.IsNullOrEmpty(_pcNumber)) return;
            // Защита: не посылаем «Заблокирован» пока идёт восстановление сессии.
            // Проверяем и IsRestoring (начальная загрузка), и наличие heartbeat (реконнект при перезапуске сервера).
            if (status == "Заблокирован" && (IsRestoring || SessionManager.ReadHeartbeat().HasValue)) return;
            try
            {
                var sessionType = PolicyEngine.ActiveSessionType;
                var elapsed = PolicyEngine.ActiveElapsedSeconds;

                // ✅ Здесь должен быть UpdateStatus, а не RegisterClient
                await _hub.InvokeAsync("UpdateStatus", _pcNumber, status, sessionType, elapsed);

                Logger.Info($"✅ Статус отправлен: {status}");
            }
            catch (Exception ex) { Logger.Error($"❌ Ошибка отправки статуса: {ex.Message}"); }
        }

        private async Task ReportUpdateResultAsync(string reason)
        {
            if (!_isConnected || _hub == null || string.IsNullOrEmpty(_pcNumber)) return;
            try
            {
                await _hub.InvokeAsync("ReportUpdateResult", _pcNumber, reason);
                Logger.Info($"✅ Результат обновления отправлен: {reason}");
            }
            catch (Exception ex) { Logger.Error($"❌ Ошибка отправки результата обновления: {ex.Message}"); }
        }

        public async Task SendStatusUpdateAsync(string sessionType, int elapsedSeconds)
        {
            if (!_isConnected || _hub == null || string.IsNullOrEmpty(_pcNumber)) return;
            try { if (!string.IsNullOrEmpty(sessionType) && elapsedSeconds >= 0) await _hub.InvokeAsync("UpdateStatus", _pcNumber, sessionType, sessionType, elapsedSeconds); }
            catch (Exception ex) { Logger.Error($"❌ Ошибка обновления: {ex.Message}"); }
        }

        private string GetLocalIpAddress() { try { return Dns.GetHostEntry(Dns.GetHostName()).AddressList.First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString(); } catch { return "unknown"; } }
        private string GetMacAddress() { try { return NetworkInterface.GetAllNetworkInterfaces().First(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback).GetPhysicalAddress().ToString(); } catch { return "unknown"; } }
        private double GetDiskFreeGb() { try { return Math.Round(DriveInfo.GetDrives().First(d => d.Name == "C:\\").AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0, 2); } catch { return 0; } }
        private double GetUptimeHours() { try { return Math.Round((DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalHours, 2); } catch { return 0; } }

        public void Dispose() { try { _hub?.StopAsync().GetAwaiter().GetResult(); _hub?.DisposeAsync().GetAwaiter().GetResult(); } catch { } }
    }
}