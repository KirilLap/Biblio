using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

namespace BibAdminWeb
{
    public class OperatorHub : Hub
    {
        private readonly IHubContext<AdminHub> _adminCtx;

        public OperatorHub(IHubContext<AdminHub> adminCtx) => _adminCtx = adminCtx;

        public override async Task OnConnectedAsync()
        {
            if (!IsAuthorized()) { Context.Abort(); return; }
            var all = AdminHub.KnownClients.Values.Select(OperatorBroadcaster.ClientDto).ToList();
            await Clients.Caller.SendAsync("stateSnapshot", all);
            var settings = GlobalSettings.Load();
            var services = settings.Services.Where(s => s.IsActive)
                .Select(s => new { id = s.Id, name = s.Name, unit = s.Unit, price = s.Price }).ToList();
            await Clients.Caller.SendAsync("serviceTypes", services);
            await Clients.Caller.SendAsync("tariff", settings.Tariff);
            await base.OnConnectedAsync();
        }

        public async Task RequestSnapshot()
        {
            if (!IsAuthorized()) return;
            var all = AdminHub.KnownClients.Values.Select(OperatorBroadcaster.ClientDto).ToList();
            await Clients.Caller.SendAsync("stateSnapshot", all);
            var settings = GlobalSettings.Load();
            var services = settings.Services.Where(s => s.IsActive)
                .Select(s => new { id = s.Id, name = s.Name, unit = s.Unit, price = s.Price }).ToList();
            await Clients.Caller.SendAsync("serviceTypes", services);
            await Clients.Caller.SendAsync("tariff", settings.Tariff);
        }

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
            client.StartedByOperatorName = GetCurrentOperatorName();
            AdminHub.KnownClients[pcNumber] = client;
            AdminHub.SaveActiveSessions();

            var cmd = new { Type = "START_SESSION", SessionType = sessionType, LimitSeconds = limitSeconds, PaidAmount = paidAmount, ElapsedSeconds = 0, ServerStartTime = serverStart.ToString("o") };
            await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(cmd));
            AdminHub.RaiseClientUpdated(client);

            // Уведомляем о долге читателя, если он есть
            if (!string.IsNullOrEmpty(readerId))
            {
                int debtAmount = ReaderDebtStore.GetDebtAmount(readerId);
                if (debtAmount > 0)
                    await Clients.Caller.SendAsync("debtAlert", new { readerId, amount = debtAmount });
            }
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

            string operatorName = sessionType == "VIP" ? GetCurrentOperatorName() : client.StartedByOperatorName;

            FinanceStore.AddSession(new SessionRecord
            {
                PcNumber = client.PcNumber,
                SessionType = sessionType,
                UserName = client.UserName ?? "—",
                ReaderId = client.ReaderId ?? "",
                DurationSeconds = duration,
                EarnedAmount = earned,
                PaidAmount = paidAmount,
                RefundAmount = refund,
                StartTime = startTime,
                EndTime = DateTime.Now,
                OperatorName = operatorName
            });

            if (client.IsOnline)
            {
                await _adminCtx.Clients.Client(client.ConnectionId)
                    .SendAsync("ReceiveCommand", JsonSerializer.Serialize(new { Type = "END_SESSION", Value = "" }));
                await _adminCtx.Clients.Client(client.ConnectionId)
                    .SendAsync("ReceiveCommand", JsonSerializer.Serialize(new { Type = "REMOTE_LOCK", Value = "true" }));
            }

            client.Status = "Заблокирован";
            client.SessionType = "";
            client.ElapsedSeconds = 0;
            client.LimitSeconds = 0;
            client.PaidAmount = 0;
            client.SessionStart = null;
            client.IsPaused = false;
            client.AccumulatedSeconds = 0;
            client.SessionId = "";
            AdminHub.KnownClients[pcNumber] = client;
            AdminHub.SaveActiveSessions();
            AdminHub.AddPendingCommand(pcNumber, "REMOTE_LOCK", "true");
            AdminHub.RaiseClientUpdated(client);

            // Собираем отложенные услуги для этого ПК и закрываем их
            var deferredServices = ServiceTransaction.GetPendingForPc(pcNumber);
            int servicesTotal = deferredServices.Sum(s => s.TotalAmount);
            if (servicesTotal > 0) ServiceTransaction.MarkAllPaidForPc(pcNumber);

            // Для VIP — вся сумма к оплате сейчас; для Лимит — только услуги дополнительно
            int additionalPayment = sessionType == "VIP" ? earned + servicesTotal : servicesTotal;

            var serviceItems = deferredServices.Select(s => new
            {
                name = s.ServiceName, quantity = s.Quantity, unit = s.Unit,
                pricePerUnit = s.PricePerUnit, total = s.TotalAmount
            }).ToList();

            await Clients.Caller.SendAsync("sessionSummary", new
            {
                pcNumber, sessionType, duration, earned, paidAmount, refund,
                readerId = client.ReaderId ?? "",
                services = serviceItems,
                servicesTotal,
                additionalPayment
            });
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
            if (client != null) OperatorBroadcaster.Instance?.NotifyOfflineResolved(pcNumber, decision);
            if (client != null) AdminHub.RaiseClientUpdated(client);
            return Task.CompletedTask;
        }

        public async Task CreateService(string serviceTypeId, int quantity, string readerId, string readerName, bool payNow, string pcNumber = "")
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
                ReaderName = string.IsNullOrWhiteSpace(readerName) ? "" : readerName,
                PcNumber = !payNow && !string.IsNullOrWhiteSpace(pcNumber) ? pcNumber : ""
            };
            ServiceTransaction.Add(tx);
            if (payNow) ServiceTransaction.MarkAsPaid(tx.Id);
            await Clients.Caller.SendAsync("serviceCreated", new { total, isPaid = payNow, serviceName = svc.Name });
        }

        /// <summary>Записывает долг читателя (вызывается из UI, если читатель не смог оплатить).</summary>
        public async Task RecordDebt(string readerId, int amount, string note)
        {
            if (!IsAuthorized()) return;
            if (string.IsNullOrEmpty(readerId) || amount <= 0) return;
            ReaderDebtStore.Add(readerId, amount, note);
            await Clients.Caller.SendAsync("debtRecorded", new { readerId, amount });
        }

        /// <summary>Погашает все долги читателя.</summary>
        public async Task ClearDebt(string readerId)
        {
            if (!IsAuthorized()) return;
            ReaderDebtStore.ClearDebts(readerId);
            await Clients.Caller.SendAsync("debtCleared", new { readerId });
        }

        public async Task<string> TransferSession(string fromPcNumber, string toPcNumber)
        {
            if (!IsAuthorized()) return "Нет авторизации";
            if (!AdminHub.KnownClients.TryGetValue(fromPcNumber, out var source)) return $"ПК {fromPcNumber} не найден";
            if (!AdminHub.KnownClients.TryGetValue(toPcNumber, out var target)) return $"ПК {toPcNumber} не найден";
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
                AdminHub.AddPendingCommand(fromPcNumber, "REMOTE_LOCK", "true");

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

            AdminHub.KnownClients[fromPcNumber] = source;
            AdminHub.KnownClients[toPcNumber] = target;
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

        public async Task ExtendSession(string pcNumber, int addSeconds, int addAmount)
        {
            if (!IsAuthorized()) return;
            if (!AdminHub.KnownClients.TryGetValue(pcNumber, out var client)) return;
            if (!client.IsSession) return;
            client.LimitSeconds += addSeconds;
            client.PaidAmount += addAmount;
            AdminHub.KnownClients[pcNumber] = client;
            AdminHub.SaveActiveSessions();
            var cmd = new { Type = "EXTEND_SESSION", Value = addSeconds.ToString(), LimitSeconds = addSeconds };
            if (client.IsOnline)
                await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(cmd));
            else
                AdminHub.AddPendingCommand(pcNumber, "EXTEND_SESSION", addSeconds.ToString());
            AdminHub.RaiseClientUpdated(client);
        }

        public async Task ShutdownAll()
        {
            if (!IsAuthorized()) return;
            var json = JsonSerializer.Serialize(new { Type = "SHUTDOWN", Value = "true" });
            foreach (var c in AdminHub.KnownClients.Values)
            {
                if (c.IsOnline)
                    await _adminCtx.Clients.Client(c.ConnectionId).SendAsync("ReceiveCommand", json);
                else
                    AdminHub.AddPendingCommand(c.PcNumber, "SHUTDOWN", "true");
            }
        }

        public async Task RestartAll()
        {
            if (!IsAuthorized()) return;
            var json = JsonSerializer.Serialize(new { Type = "RESTART", Value = "true" });
            foreach (var c in AdminHub.KnownClients.Values)
            {
                if (c.IsOnline)
                    await _adminCtx.Clients.Client(c.ConnectionId).SendAsync("ReceiveCommand", json);
                else
                    AdminHub.AddPendingCommand(c.PcNumber, "RESTART", "true");
            }
        }

        private bool IsAuthorized()
        {
            var cookie = Context.GetHttpContext()?.Request.Cookies["bib_op"];
            return cookie != null && OperatorApi.ValidateToken(cookie, out _);
        }

        private string GetCurrentOperatorName()
        {
            var cookie = Context.GetHttpContext()?.Request.Cookies["bib_op"];
            if (cookie == null || !OperatorApi.ValidateToken(cookie, out var operatorId)) return "";
            return GlobalSettings.Load().Operators.Find(o => o.Id == operatorId)?.DisplayName ?? "";
        }
    }
}
