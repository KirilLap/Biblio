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
            await Clients.Caller.SendAsync("readerCardPrefix", settings.ReaderCardPrefix);
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
            await Clients.Caller.SendAsync("readerCardPrefix", settings.ReaderCardPrefix);
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
            client.PenaltyAmount = 0;
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
        }

        public async Task EndSession(string pcNumber)
        {
            if (!IsAuthorized()) return;
            if (!AdminHub.KnownClients.TryGetValue(pcNumber, out var client)) return;
            if (!client.IsSession) return;

            string sessionType = string.IsNullOrEmpty(client.SessionType) ? client.Status : client.SessionType;
            int tariff = GlobalSettings.Load().Tariff;
            int paidAmount = client.PaidAmount;
            int earned;
            int refund = 0;

            if (client.OriginalLimitSeconds > 0) // Лимит → VIP: оплаченные секунды как кредит
            {
                if (client.ElapsedSeconds >= client.OriginalLimitSeconds)
                {
                    // Использовали больше оплаченного времени — доплата за сверхлимит по VIP-тарифу
                    int vipExtra = (int)((client.ElapsedSeconds - client.OriginalLimitSeconds) * (double)tariff / 3600);
                    earned = paidAmount + vipExtra + client.PenaltyAmount;
                }
                else
                {
                    // Завершили раньше оплаченного лимита — начисляем по VIP-тарифу, возврат разницы
                    earned = Math.Min((int)(tariff * client.ElapsedSeconds / 3600.0) + client.PenaltyAmount, paidAmount);
                    refund = Math.Max(0, paidAmount - earned);
                }
            }
            else if (client.IsPostPay) // VIP → Лимит: постоплата как VIP
            {
                earned = (int)(tariff * client.ElapsedSeconds / 3600.0) + client.PenaltyAmount;
            }
            else if (sessionType == "Лимит")
            {
                earned = Math.Min((int)(tariff * client.ElapsedSeconds / 3600.0) + client.PenaltyAmount, paidAmount);
                refund = Math.Max(0, paidAmount - earned);
            }
            else
            {
                earned = (int)(tariff * client.ElapsedSeconds / 3600.0) + client.PenaltyAmount;
            }
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
            client.PenaltyAmount = 0;
            client.SessionStart = null;
            client.IsPaused = false;
            client.AccumulatedSeconds = 0;
            client.SessionId = "";
            client.OriginalLimitSeconds = 0;
            client.IsPostPay = false;
            AdminHub.KnownClients[pcNumber] = client;
            AdminHub.SaveActiveSessions();
            AdminHub.AddPendingCommand(pcNumber, "REMOTE_LOCK", "true");
            AdminHub.RaiseClientUpdated(client);

            string readerId = client.ReaderId ?? "";
            var debts = ServiceTransaction.GetUnpaidForSession(pcNumber, readerId);
            int totalDebt = debts.Sum(d => d.DebtAmount);
            var debtItems = debts.Select(d => new {
                id = d.Id,
                name = d.ServiceName,
                qty = d.Quantity,
                unit = d.Unit,
                debt = d.DebtAmount
            }).ToList();

            int additionalCharge = Math.Max(0, earned - paidAmount);
            await Clients.Caller.SendAsync("sessionSummary", new {
                pcNumber, sessionType, duration, earned, paidAmount, refund, additionalCharge,
                readerId, serviceDebts = debtItems, totalServiceDebt = totalDebt
            });

            // Уведомить всех остальных о завершении сессии
            string displayName = string.IsNullOrWhiteSpace(client.UserName) ? (client.ReaderId ?? "—") : client.UserName;
            OperatorBroadcaster.Instance?.NotifySessionEndedByStaff(pcNumber, displayName, duration, earned);
            AdminBroadcaster.Instance?.NotifySessionEndedByStaff(pcNumber, displayName, duration, earned);
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
            if (client != null) AdminBroadcaster.Instance?.NotifyOfflineResolved(pcNumber, decision);
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

            // Если передан pcNumber — подтянуть ReaderId/ReaderName из активной сессии
            string resolvedReaderId = string.IsNullOrWhiteSpace(readerId) ? "" : readerId;
            string resolvedReaderName = string.IsNullOrWhiteSpace(readerName) ? "" : readerName;
            string resolvedPcNumber = string.IsNullOrWhiteSpace(pcNumber) ? "" : pcNumber;

            if (!string.IsNullOrEmpty(resolvedPcNumber) && AdminHub.KnownClients.TryGetValue(resolvedPcNumber, out var pcClient) && pcClient.IsSession)
            {
                if (string.IsNullOrEmpty(resolvedReaderId)) resolvedReaderId = pcClient.ReaderId ?? "";
                if (string.IsNullOrEmpty(resolvedReaderName)) resolvedReaderName = pcClient.UserName ?? "";
            }

            var tx = new ServiceTransaction
            {
                ServiceTypeId = svc.Id, ServiceName = svc.Name, Unit = svc.Unit,
                Quantity = quantity, PricePerUnit = svc.Price, TotalAmount = total,
                ReaderId = resolvedReaderId,
                ReaderName = resolvedReaderName,
                PcNumber = resolvedPcNumber
            };
            ServiceTransaction.Add(tx);
            if (payNow) ServiceTransaction.MarkAsPaid(tx.Id);
            await Clients.Caller.SendAsync("serviceCreated", new { total, isPaid = payNow, serviceName = svc.Name });
        }

        // Пакетное создание нескольких услуг за один раз
        public async Task CreateServiceBatch(
            string[] serviceTypeIds, int[] quantities,
            string pcNumber, string readerId, string readerName, bool payNow)
        {
            if (!IsAuthorized()) return;
            var settings = GlobalSettings.Load();

            var resolvedPc   = pcNumber?.Trim()    ?? "";
            var resolvedId   = readerId?.Trim()    ?? "";
            var resolvedName = readerName?.Trim()  ?? "";

            if (!string.IsNullOrEmpty(resolvedPc) &&
                AdminHub.KnownClients.TryGetValue(resolvedPc, out var pc) && pc.IsSession)
            {
                if (string.IsNullOrEmpty(resolvedId))   resolvedId   = pc.ReaderId ?? "";
                if (string.IsNullOrEmpty(resolvedName)) resolvedName = pc.UserName ?? "";
            }

            var batchId  = Guid.NewGuid().ToString("N")[..8];
            int totalAll = 0;
            var names    = new System.Collections.Generic.List<string>();

            for (int i = 0; i < serviceTypeIds.Length; i++)
            {
                var svc = settings.Services.FirstOrDefault(s => s.Id == serviceTypeIds[i] && s.IsActive);
                if (svc == null) continue;
                int qty   = i < quantities.Length ? Math.Max(1, quantities[i]) : 1;
                int total = svc.Price * qty;
                totalAll += total;
                names.Add(svc.Name);
                var tx = new ServiceTransaction
                {
                    ServiceTypeId = svc.Id, ServiceName = svc.Name, Unit = svc.Unit,
                    Quantity = qty, PricePerUnit = svc.Price, TotalAmount = total,
                    ReaderId = resolvedId, ReaderName = resolvedName,
                    PcNumber = resolvedPc, BatchId = batchId
                };
                ServiceTransaction.Add(tx);
                if (payNow) ServiceTransaction.MarkAsPaid(tx.Id);
            }

            if (names.Count == 0) return;
            var label = names.Count == 1 ? names[0] : $"{names.Count} услуги";
            await Clients.Caller.SendAsync("serviceCreated",
                new { total = totalAll, isPaid = payNow, serviceName = label });
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

        // Убрать время с возвратом денег (только Лимит)
        public async Task SubtractTime(string pcNumber, int subSeconds, int subAmount)
        {
            if (!IsAuthorized()) return;
            if (!AdminHub.KnownClients.TryGetValue(pcNumber, out var client)) return;
            if (!client.IsSession || client.SessionType != "Лимит") return;
            var actualSub = Math.Min(subSeconds, client.LimitSeconds - 60);
            if (actualSub <= 0) return;
            client.LimitSeconds -= actualSub;
            client.PaidAmount = Math.Max(0, client.PaidAmount - subAmount);
            AdminHub.KnownClients[pcNumber] = client;
            AdminHub.SaveActiveSessions();
            var cmd = new { Type = "EXTEND_SESSION", Value = (-actualSub).ToString(), LimitSeconds = -actualSub };
            if (client.IsOnline)
                await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(cmd));
            else
                AdminHub.AddPendingCommand(pcNumber, "EXTEND_SESSION", (-actualSub).ToString());
            AdminHub.RaiseClientUpdated(client);
        }

        // Штраф: для Лимит — убрать время без возврата; для VIP — добавить денежный штраф
        public async Task ApplyPenalty(string pcNumber, int penaltySeconds, int penaltyAmount)
        {
            if (!IsAuthorized()) return;
            if (!AdminHub.KnownClients.TryGetValue(pcNumber, out var client)) return;
            if (!client.IsSession) return;
            client.PenaltyAmount += penaltyAmount;
            if (client.SessionType == "Лимит" && penaltySeconds > 0)
            {
                var actualSub = Math.Min(penaltySeconds, client.LimitSeconds - 60);
                if (actualSub > 0)
                {
                    client.LimitSeconds -= actualSub;
                    var cmd = new { Type = "PENALTY_SESSION", Value = actualSub.ToString() };
                    if (client.IsOnline)
                        await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(cmd));
                    else
                        AdminHub.AddPendingCommand(pcNumber, "PENALTY_SESSION", actualSub.ToString());
                }
            }
            AdminHub.KnownClients[pcNumber] = client;
            AdminHub.SaveActiveSessions();
            AdminHub.RaiseClientUpdated(client);
        }

        public async Task ChangeSessionType(string pcNumber, string newType, int remainingMinutes)
        {
            if (!IsAuthorized()) return;
            if (!AdminHub.KnownClients.TryGetValue(pcNumber, out var client)) return;
            if (!client.IsSession) return;
            if (client.SessionType == newType) return;

            string cmd;
            if (newType == "VIP")
            {
                client.OriginalLimitSeconds = client.LimitSeconds;
                client.SessionType = "VIP";
                client.Status = "VIP";
                client.LimitSeconds = 0;
                cmd = JsonSerializer.Serialize(new { Type = "CHANGE_SESSION_TYPE", Value = "VIP", LimitSeconds = 0 });
            }
            else
            {
                int newLimit = client.ElapsedSeconds + remainingMinutes * 60;
                client.IsPostPay = true;
                client.SessionType = "Лимит";
                client.Status = "Лимит";
                client.LimitSeconds = newLimit;
                cmd = JsonSerializer.Serialize(new { Type = "CHANGE_SESSION_TYPE", Value = "Лимит", LimitSeconds = newLimit });
            }

            client.SessionTypeLockedUntil = DateTime.UtcNow.AddSeconds(30);
            AdminHub.KnownClients[pcNumber] = client;
            AdminHub.SaveActiveSessions();
            if (client.IsOnline)
                await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", cmd);
            AdminHub.RaiseClientUpdated(client);
        }

        public Task<object[]> GetAllDebts()
        {
            if (!IsAuthorized()) return Task.FromResult(Array.Empty<object>());
            var debts = ServiceTransaction.GetAllUnpaid()
                .Select(t => (object)new {
                    id = t.Id, serviceName = t.ServiceName, unit = t.Unit,
                    quantity = t.Quantity, pricePerUnit = t.PricePerUnit,
                    debtAmount = t.DebtAmount, readerId = t.ReaderId,
                    readerName = t.ReaderName, pcNumber = t.PcNumber,
                    createdAt = t.CreatedAt.ToString("o")
                }).ToArray();
            return Task.FromResult(debts);
        }

        public Task PayDebt(string id)
        {
            if (!IsAuthorized()) return Task.CompletedTask;
            ServiceTransaction.MarkAsPaid(id);
            return Task.CompletedTask;
        }

        public Task PaySessionDebts(string pcNumber, string readerId)
        {
            if (!IsAuthorized()) return Task.CompletedTask;
            if (!string.IsNullOrEmpty(readerId)) ServiceTransaction.MarkAllPaidForReader(readerId);
            if (!string.IsNullOrEmpty(pcNumber)) ServiceTransaction.MarkAllPaidForPc(pcNumber);
            return Task.CompletedTask;
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
