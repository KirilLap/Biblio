using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

namespace BibAdminWeb
{
    public class AdminWebHub : Hub
    {
        private readonly IHubContext<AdminHub> _adminCtx;

        public AdminWebHub(IHubContext<AdminHub> adminCtx) => _adminCtx = adminCtx;

        public override async Task OnConnectedAsync()
        {
            if (!IsAuthorized()) { Context.Abort(); return; }
            var all = AdminHub.KnownClients.Values.Select(ClientDto).ToList();
            await Clients.Caller.SendAsync("stateSnapshot", all);
            await base.OnConnectedAsync();
        }

        public async Task RequestSnapshot()
        {
            if (!IsAuthorized()) return;
            var all = AdminHub.KnownClients.Values.Select(ClientDto).ToList();
            await Clients.Caller.SendAsync("stateSnapshot", all);
        }

        // ─── Session management ───────────────────────────────────────────────

        public async Task StartSession(string pcNumber, string sessionType,
            int limitSeconds, int paidAmount, string userName, string readerId)
        {
            if (!IsAuthorized()) return;
            if (!AdminHub.KnownClients.TryGetValue(pcNumber, out var client)) return;
            if (!client.IsOnline || client.IsSession) return;

            var serverStart = DateTime.UtcNow;
            client.SessionType = sessionType;
            client.Status = sessionType;
            client.LimitSeconds = limitSeconds;
            client.PaidAmount = paidAmount;
            client.ElapsedSeconds = 0;
            client.AccumulatedSeconds = 0;
            client.SessionStart = serverStart;
            client.IsPaused = false;
            client.SessionId = Guid.NewGuid().ToString("N")[..8];
            client.UserName = string.IsNullOrWhiteSpace(userName) ? null : userName;
            client.ReaderId = string.IsNullOrWhiteSpace(readerId) ? null : readerId;
            client.StartedByOperatorName = "";
            AdminHub.KnownClients[pcNumber] = client;
            AdminHub.SaveActiveSessions();

            var cmd = new { Type = "START_SESSION", SessionType = sessionType, LimitSeconds = limitSeconds, PaidAmount = paidAmount, ElapsedSeconds = 0, ServerStartTime = serverStart.ToString("o") };
            await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(cmd));
            AdminHub.RaiseClientUpdated(client);
        }

        public async Task EndSession(string pcNumber)
        {
            if (!IsAuthorized()) return;
            if (!AdminHub.KnownClients.TryGetValue(pcNumber, out var client)) return;
            if (!client.IsSession) return;

            string sessionType = string.IsNullOrEmpty(client.SessionType) ? client.Status : client.SessionType;
            int tariff = GlobalSettings.Load().Tariff;
            int earned = (int)(tariff * client.ElapsedSeconds / 3600.0);
            int paidAmount = client.PaidAmount;
            int refund = Math.Max(0, paidAmount - earned);
            int duration = client.ElapsedSeconds;
            var startTime = client.SessionStart ?? DateTime.Now;

            FinanceStore.AddSession(new SessionRecord
            {
                PcNumber = client.PcNumber, SessionType = sessionType,
                UserName = client.UserName ?? "—", ReaderId = client.ReaderId ?? "",
                DurationSeconds = duration, EarnedAmount = earned,
                PaidAmount = paidAmount, RefundAmount = refund,
                StartTime = startTime, EndTime = DateTime.Now, OperatorName = ""
            });

            if (client.IsOnline)
            {
                await _adminCtx.Clients.Client(client.ConnectionId)
                    .SendAsync("ReceiveCommand", JsonSerializer.Serialize(new { Type = "END_SESSION", Value = "" }));
                await _adminCtx.Clients.Client(client.ConnectionId)
                    .SendAsync("ReceiveCommand", JsonSerializer.Serialize(new { Type = "REMOTE_LOCK", Value = "true" }));
            }

            client.Status = "Заблокирован"; client.SessionType = ""; client.ElapsedSeconds = 0;
            client.LimitSeconds = 0; client.PaidAmount = 0; client.SessionStart = null;
            client.IsPaused = false; client.AccumulatedSeconds = 0; client.SessionId = "";
            AdminHub.KnownClients[pcNumber] = client;
            AdminHub.SaveActiveSessions();
            AdminHub.AddPendingCommand(pcNumber, "REMOTE_LOCK", "true");
            AdminHub.RaiseClientUpdated(client);

            await Clients.Caller.SendAsync("sessionSummary", new { pcNumber, sessionType, duration, earned, paidAmount, refund });
        }

        public async Task TogglePause(string pcNumber)
        {
            if (!IsAuthorized()) return;
            if (!AdminHub.KnownClients.TryGetValue(pcNumber, out var client)) return;
            if (!client.IsSession) return;

            if (client.IsPaused)
            {
                client.IsPaused = false;
                client.SessionStart = DateTime.UtcNow;
                client.Status = string.IsNullOrEmpty(client.SessionType) ? "Лимит" : client.SessionType;
                AdminHub.KnownClients[pcNumber] = client;
                if (client.IsOnline)
                    await _adminCtx.Clients.Client(client.ConnectionId)
                        .SendAsync("ReceiveCommand", JsonSerializer.Serialize(new { Type = "RESUME_SESSION", Value = "" }));
                else
                    AdminHub.SetOfflineDecision(pcNumber, OfflineDecision.Continue);
            }
            else
            {
                int elapsed = client.AccumulatedSeconds +
                    (client.IsPaused ? 0 : (int)(DateTime.UtcNow - (client.SessionStart ?? DateTime.UtcNow)).TotalSeconds);
                elapsed = Math.Max(0, elapsed);
                client.IsPaused = true;
                client.AccumulatedSeconds = elapsed;
                client.Status = "Пауза";
                AdminHub.KnownClients[pcNumber] = client;
                if (client.IsOnline)
                {
                    await _adminCtx.Clients.Client(client.ConnectionId)
                        .SendAsync("ReceiveCommand", JsonSerializer.Serialize(new { Type = "SESSION_TIME_SYNC", Value = elapsed.ToString() }));
                    await _adminCtx.Clients.Client(client.ConnectionId)
                        .SendAsync("ReceiveCommand", JsonSerializer.Serialize(new { Type = "PAUSE_SESSION", Value = "" }));
                }
                else
                    AdminHub.SetOfflineDecision(pcNumber, OfflineDecision.Pause);
            }
            AdminHub.SaveActiveSessions();
            AdminHub.RaiseClientUpdated(client);
        }

        public Task ResolveOffline(string pcNumber, string decision)
        {
            if (!IsAuthorized()) return Task.CompletedTask;
            if (!Enum.TryParse<OfflineDecision>(decision, out var d)) return Task.CompletedTask;
            var client = AdminHub.SetOfflineDecision(pcNumber, d);
            if (client != null) AdminBroadcaster.Instance?.NotifyOfflineResolved(pcNumber, decision);
            return Task.CompletedTask;
        }

        public Task ResolveNameConflict(string mac, bool accept, int pcNumberValue, string customName)
        {
            if (!IsAuthorized()) return Task.CompletedTask;
            AdminHub.ResolveConflict(mac, accept ? pcNumberValue : null, accept ? customName : null);
            return Task.CompletedTask;
        }

        public async Task<string> TransferSession(string fromPc, string toPc)
        {
            if (!IsAuthorized()) return "Нет авторизации";
            if (!AdminHub.KnownClients.TryGetValue(fromPc, out var source)) return $"ПК {fromPc} не найден";
            if (!AdminHub.KnownClients.TryGetValue(toPc, out var target)) return $"ПК {toPc} не найден";
            if (!source.IsSession) return "На исходном ПК нет активной сессии";
            if (!target.IsOnline) return "ПК назначения не в сети";
            if (target.IsSession) return "На ПК назначения уже есть сессия";

            string sessionType = source.SessionType;
            int limitSeconds = source.LimitSeconds;
            int paidAmount = source.PaidAmount;
            int elapsed = source.IsPaused
                ? source.AccumulatedSeconds
                : source.AccumulatedSeconds + (int)(DateTime.UtcNow - (source.SessionStart ?? DateTime.UtcNow)).TotalSeconds;
            elapsed = Math.Max(0, elapsed);

            if (source.IsOnline)
                await _adminCtx.Clients.Client(source.ConnectionId)
                    .SendAsync("ReceiveCommand", JsonSerializer.Serialize(new { Type = "REMOTE_LOCK", Value = "true" }));
            else
                AdminHub.AddPendingCommand(fromPc, "REMOTE_LOCK", "true");

            source.Status = "Заблокирован"; source.SessionType = ""; source.ElapsedSeconds = 0;
            source.LimitSeconds = 0; source.PaidAmount = 0; source.SessionStart = null;
            source.IsPaused = false; source.AccumulatedSeconds = 0; source.SessionId = "";
            source.DisconnectedAt = null; source.OfflineDecision = OfflineDecision.None;

            var newStart = DateTime.UtcNow.AddSeconds(-elapsed);
            var startCmd = new { Type = "START_SESSION", SessionType = sessionType, LimitSeconds = limitSeconds, PaidAmount = paidAmount, ElapsedSeconds = elapsed, ServerStartTime = newStart.ToString("o") };
            await _adminCtx.Clients.Client(target.ConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(startCmd));

            target.SessionType = sessionType; target.Status = sessionType;
            target.LimitSeconds = limitSeconds; target.PaidAmount = paidAmount;
            target.ElapsedSeconds = elapsed; target.AccumulatedSeconds = elapsed;
            target.SessionStart = newStart; target.IsPaused = false; target.SessionId = "";

            AdminHub.KnownClients[fromPc] = source;
            AdminHub.KnownClients[toPc] = target;
            AdminHub.SaveActiveSessions();
            AdminHub.RaiseClientUpdated(source);
            AdminHub.RaiseClientUpdated(target);
            return "OK";
        }

        public Task<object[]> GetTransferTargets(string fromPcNumber)
        {
            if (!IsAuthorized()) return Task.FromResult(Array.Empty<object>());
            var targets = AdminHub.KnownClients.Values
                .Where(c => c.PcNumber != fromPcNumber && c.IsOnline && !c.IsSession)
                .Select(c => (object)new { pcNumber = c.PcNumber, pcNumberValue = c.PcNumberValue })
                .ToArray();
            return Task.FromResult(targets);
        }

        public async Task SendCommandToPc(string pcNumber, string type, string value)
        {
            if (!IsAuthorized()) return;
            if (!AdminHub.KnownClients.TryGetValue(pcNumber, out var client)) return;
            var json = JsonSerializer.Serialize(new { Type = type, Value = value });
            if (client.IsOnline)
                await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
            else
                AdminHub.AddPendingCommand(pcNumber, type, value);
        }

        public async Task SendCommandToAll(string type, string value)
        {
            if (!IsAuthorized()) return;
            var json = JsonSerializer.Serialize(new { Type = type, Value = value });
            foreach (var client in AdminHub.KnownClients.Values)
            {
                if (client.IsOnline)
                    await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                else
                    AdminHub.AddPendingCommand(client.PcNumber, type, value);
            }
        }

        public async Task RenameClient(int pcNumberValue, string customName)
        {
            if (!IsAuthorized()) return;
            var client = AdminHub.KnownClients.Values.FirstOrDefault(c => c.PcNumberValue == pcNumberValue);
            if (client == null) return;
            var oldName = client.PcNumber;
            client.CustomName = customName;
            var newName = string.IsNullOrEmpty(customName) ? $"ПК {pcNumberValue}" : $"{customName} {pcNumberValue}";
            if (oldName != newName)
            {
                AdminHub.KnownClients.TryRemove(oldName, out _);
                AdminHub.KnownClients[newName] = client;
            }
            if (!string.IsNullOrEmpty(client.ConnectionId))
            {
                var cmd = new { Type = "SET_PC_NAME", Value = customName ?? "" };
                await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(cmd));
            }
            AdminHub.RaiseClientUpdated(client);
        }

        public Task<bool> DeletePc(string pcNumber)
        {
            if (!IsAuthorized()) return Task.FromResult(false);
            return Task.FromResult(AdminHub.DeleteClientStatic(pcNumber));
        }

        public async Task CreateService(string serviceTypeId, int quantity, string readerId, string readerName, bool payNow)
        {
            if (!IsAuthorized()) return;
            var settings = GlobalSettings.Load();
            var svc = settings.Services.FirstOrDefault(s => s.Id == serviceTypeId && s.IsActive);
            if (svc == null) return;
            int total = svc.Price * quantity;
            var tx = new ServiceTransaction
            {
                ServiceTypeId = svc.Id, ServiceName = svc.Name, Unit = svc.Unit,
                Quantity = quantity, PricePerUnit = svc.Price, TotalAmount = total,
                ReaderId = string.IsNullOrWhiteSpace(readerId) ? "" : readerId,
                ReaderName = string.IsNullOrWhiteSpace(readerName) ? "" : readerName
            };
            ServiceTransaction.Add(tx);
            if (payNow) ServiceTransaction.MarkAsPaid(tx.Id);
            await Clients.Caller.SendAsync("serviceCreated", new { total, isPaid = payNow, serviceName = svc.Name });
        }

        // ─── Utilities ────────────────────────────────────────────────────────

        internal static object ClientDto(ClientState c) => new
        {
            pcNumber = c.PcNumber, pcNumberValue = c.PcNumberValue, customName = c.CustomName,
            status = c.Status, isOnline = c.IsOnline, isSession = c.IsSession,
            isFree = c.IsFree, isLocked = c.IsLocked, isPaused = c.IsPaused,
            sessionType = c.SessionType, elapsedSeconds = c.ElapsedSeconds,
            limitSeconds = c.LimitSeconds, paidAmount = c.PaidAmount,
            userName = c.UserName, readerId = c.ReaderId,
            sessionStart = c.SessionStart?.ToString("o"),
            accumulatedSeconds = c.AccumulatedSeconds, ip = c.Ip,
            osVersion = c.OsVersion, diskFreeGb = c.DiskFreeGb, uptimeHours = c.UptimeHours,
            lastSeen = c.LastSeen.ToString("o"),
            disconnectedAt = c.DisconnectedAt?.ToString("o"),
            offlineDecision = c.OfflineDecision.ToString(),
            hasIndividualSettings = c.HasIndividualSettings,
            startedByOperatorName = c.StartedByOperatorName,
            backgroundFileName = c.BackgroundFileName
        };

        private bool IsAuthorized()
        {
            var token = Context.GetHttpContext()?.Request.Cookies["bib_admin"];
            return token != null && AdminAuth.IsValidToken(token);
        }
    }
}
