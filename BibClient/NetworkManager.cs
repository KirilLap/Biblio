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

        public NetworkManager(string serverUrl) { _serverUrl = serverUrl; }

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
                var info = new { HostName = Environment.MachineName, OsVersion = Environment.OSVersion.VersionString, LocalIp = GetLocalIpAddress(), MacAddress = GetMacAddress(), DiskFreeGb = GetDiskFreeGb(), UptimeHours = GetUptimeHours(), ClientTimeUtc = DateTime.UtcNow.ToString("o") };

                // Читаем heartbeat-файл: SessionId и время последнего пульса → offline duration
                var heartbeat = SessionManager.ReadHeartbeat();
                string sessionId = heartbeat?.sessionId ?? "";
                int offlineSeconds = 0;
                if (heartbeat.HasValue && IsRestoring)
                {
                    offlineSeconds = Math.Max(0, (int)(DateTime.UtcNow - heartbeat.Value.lastSync).TotalSeconds);
                    Logger.Info($"🕐 Heartbeat: lastSync={heartbeat.Value.lastSync:HH:mm:ss}, offline={offlineSeconds}с, sessionId={sessionId[..Math.Min(8, sessionId.Length)]}…");
                }

                _pcNumber = await _hub.InvokeAsync<string>("RegisterClient", SettingsManager.Current.PcNumber, info, info.MacAddress, IsRestoring, sessionId, offlineSeconds);

                if (_pcNumber != SettingsManager.Current.PcNumber) { SettingsManager.Current.PcNumber = _pcNumber; SettingsManager.Save(); }
                Logger.Info($"✅ Зарегистрирован как: {_pcNumber}");
                if (!IsRestoring) await SendStatusAsync("Заблокирован");
                OnRegistered?.Invoke();
            }
            catch (Exception ex) { Logger.Error($"❌ Ошибка регистрации: {ex.Message}"); }
        }

        public async Task SendStatusAsync(string status)
        {
            if (!_isConnected || _hub == null || string.IsNullOrEmpty(_pcNumber)) return;
            if (IsRestoring && status == "Заблокирован") return;
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