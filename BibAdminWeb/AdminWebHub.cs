using System;
using System.IO;
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
            client.PenaltyAmount = 0;
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
            AuditLogger.SessionStarted(pcNumber, sessionType, limitSeconds, paidAmount);
            AdminHub.RaiseClientUpdated(client);
        }

        public async Task EndSession(string pcNumber)
        {
            if (!IsAuthorized()) return;
            if (!AdminHub.KnownClients.TryGetValue(pcNumber, out var client)) return;
            if (!client.IsSession) return;

            string sessionType = string.IsNullOrEmpty(client.SessionType) ? client.Status : client.SessionType;
            int tariff = GlobalSettings.Load().Tariff;
            int earned = (int)(tariff * client.ElapsedSeconds / 3600.0) + client.PenaltyAmount;
            int paidAmount = client.PaidAmount;
            if (sessionType == "Лимит") earned = Math.Min(earned, paidAmount);
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

            AuditLogger.SessionEnded(pcNumber, sessionType, duration);
            client.Status = "Заблокирован"; client.SessionType = ""; client.ElapsedSeconds = 0;
            client.LimitSeconds = 0; client.PaidAmount = 0; client.PenaltyAmount = 0; client.SessionStart = null;
            client.IsPaused = false; client.AccumulatedSeconds = 0; client.SessionId = "";
            AdminHub.KnownClients[pcNumber] = client;
            AdminHub.SaveActiveSessions();
            AdminHub.AddPendingCommand(pcNumber, "REMOTE_LOCK", "true");
            AdminHub.RaiseClientUpdated(client);

            if (client.UpdateStatus == "deferred" && client.IsOnline)
            {
                client.UpdateStatus = "pending";
                AdminHub.KnownClients[pcNumber] = client;
                var updateJson = JsonSerializer.Serialize(new { Type = "UPDATE_NOW", Value = "" });
                await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", updateJson);
                AdminHub.RaiseClientUpdated(client);
            }

            string sessionReaderId = client.ReaderId ?? "";
            var debts = ServiceTransaction.GetUnpaidForSession(pcNumber, sessionReaderId);
            int totalDebt = debts.Sum(d => d.DebtAmount);
            var debtItems = debts.Select(d => new {
                id = d.Id, name = d.ServiceName, qty = d.Quantity,
                unit = d.Unit, debt = d.DebtAmount
            }).ToList();

            await Clients.Caller.SendAsync("sessionSummary", new {
                pcNumber, sessionType, duration, earned, paidAmount, refund,
                readerId = sessionReaderId, serviceDebts = debtItems, totalServiceDebt = totalDebt
            });

            // Уведомить всех остальных о завершении сессии
            string displayName = string.IsNullOrWhiteSpace(client.UserName) ? (client.ReaderId ?? "—") : client.UserName;
            AdminBroadcaster.Instance?.NotifySessionEndedByStaff(pcNumber, displayName, duration, earned);
            OperatorBroadcaster.Instance?.NotifySessionEndedByStaff(pcNumber, displayName, duration, earned);
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
            if (client != null) OperatorBroadcaster.Instance?.NotifyOfflineResolved(pcNumber, decision);
            if (client != null) AdminHub.RaiseClientUpdated(client);
            return Task.CompletedTask;
        }

        public Task ResolveNameConflict(string mac, bool accept, int pcNumberValue, string customName)
        {
            if (!IsAuthorized()) return Task.CompletedTask;
            AdminHub.ResolveConflict(mac, accept ? pcNumberValue : null, accept ? customName : null);
            return Task.CompletedTask;
        }

        public Task ResolveNumberConflict(string mac, bool accept)
        {
            if (!IsAuthorized()) return Task.CompletedTask;
            AdminHub.ResolveNumberConflict(mac, accept);
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

            // Читаем актуальную версию из version.json один раз для всего цикла
            string latestVersion = "";
            if (type == "UPDATE_NOW")
            {
                try
                {
                    var gs = GlobalSettings.Load();
                    var updDir = !string.IsNullOrWhiteSpace(gs.UpdatesPath)
                        ? gs.UpdatesPath.TrimEnd('\\', '/')
                        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updates");
                    var vf = Path.Combine(updDir, "bibclient-version.json");
                    if (File.Exists(vf))
                    {
                        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(vf));
                        latestVersion = doc.RootElement.TryGetProperty("Version", out var vp) ? vp.GetString() ?? "" : "";
                    }
                }
                catch { }
            }

            foreach (var pcNumber in AdminHub.KnownClients.Keys.ToList())
            {
                if (!AdminHub.KnownClients.TryGetValue(pcNumber, out var client)) continue;
                if (type == "UPDATE_NOW" && client.IsOnline && string.IsNullOrEmpty(client.UpdateStatus))
                {
                    // Клиент уже на актуальной версии — не трогаем
                    if (!string.IsNullOrEmpty(latestVersion) &&
                        !string.IsNullOrEmpty(client.ClientVersion) &&
                        Version.TryParse(latestVersion, out var lv) &&
                        Version.TryParse(client.ClientVersion, out var cv) &&
                        lv <= cv)
                        continue;

                    if (client.IsSession)
                    {
                        client.UpdateStatus = "deferred";
                        client.PreUpdateVersion = client.ClientVersion;
                        AdminHub.KnownClients[pcNumber] = client;
                        AdminHub.RaiseClientUpdated(client);
                        continue; // не отправляем команду, подождём конца сессии
                    }
                    client.UpdateStatus = "pending";
                    client.PreUpdateVersion = client.ClientVersion;
                    AdminHub.KnownClients[pcNumber] = client;
                    AdminHub.RaiseClientUpdated(client);
                }
                if (type == "UPDATE_FOLDER_NOW" && client.IsOnline)
                {
                    // Если клиент в сессии — откладываем обновление до её конца
                    if (client.IsSession)
                    {
                        client.UpdateStatus = "deferred";
                        client.PreUpdateVersion = client.ClientVersion;
                        AdminHub.KnownClients[pcNumber] = client;
                        AdminHub.RaiseClientUpdated(client);
                        continue;
                    }
                    client.UpdateStatus = "pending";
                    client.PreUpdateVersion = client.ClientVersion;
                    AdminHub.KnownClients[pcNumber] = client;
                    AdminHub.RaiseClientUpdated(client);
                }
                if (client.IsOnline)
                    await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                else
                    AdminHub.AddPendingCommand(client.PcNumber, type, value);
            }
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
                AuditLogger.PcRenamed(oldName, newName);
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

        /// <summary>
        /// Загружает фоновое изображение и рассылает его клиентам.
        /// targetPc == "*" — глобальный фон для всех ПК, иначе — индивидуальный.
        /// replaceIndividual — заменять ли индивидуальные фоны при глобальной загрузке.
        /// </summary>
        /// <summary>
        /// fileDataBase64 — base64-кодированное содержимое файла (браузер передаёт через btoa).
        /// </summary>
        public async Task UploadFile(string fileName, string fileDataBase64, string targetPc, bool replaceIndividual = true)
        {
            if (!IsAuthorized()) return;
            try
            {
                var fileData = Convert.FromBase64String(fileDataBase64);
                var filesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Files");
                Directory.CreateDirectory(filesDir);
                var filePath = Path.Combine(filesDir, fileName);
                await File.WriteAllBytesAsync(filePath, fileData);
                Logger.Info($"Фон сохранён: {fileName}");

                var command = new { Type = "SET_BACKGROUND", Value = fileName };
                var json = JsonSerializer.Serialize(command);

                if (targetPc == "*")
                {
                    // Глобальный фон — обновляем GlobalSettings
                    var global = GlobalSettings.Load();
                    global.BackgroundFileName = fileName;
                    global.Save();

                    foreach (var client in AdminHub.KnownClients.Values)
                    {
                        bool isIndividual = client.IsIndividual("SET_BACKGROUND");
                        if (isIndividual && !replaceIndividual) continue;
                        if (isIndividual && replaceIndividual)
                        {
                            client.IndividualSettingKeys.Remove("SET_BACKGROUND");
                            if (client.IndividualSettingKeys.Count == 0)
                                client.HasIndividualSettings = false;
                            client.BackgroundFileName = "";
                        }
                        if (client.IsOnline)
                            await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                        else
                            AdminHub.AddPendingCommand(client.PcNumber, "SET_BACKGROUND", fileName);
                    }
                    AdminHub.SaveRegistryStatic();
                }
                else
                {
                    // Индивидуальный фон
                    if (AdminHub.KnownClients.TryGetValue(targetPc, out var client))
                    {
                        client.BackgroundFileName = fileName;
                        client.MarkIndividual("SET_BACKGROUND");
                        AdminHub.KnownClients[targetPc] = client;
                        AdminHub.SaveRegistryStatic();
                        if (client.IsOnline)
                            await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                        else
                            AdminHub.AddPendingCommand(targetPc, "SET_BACKGROUND", fileName);
                    }
                }

                AdminHub.CleanupUnusedBackgroundFiles();
                Logger.Info($"Фон применён: {fileName} → {targetPc}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка UploadFile: {ex.Message}");
                throw;
            }
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

        /// <summary>Toggle an individual per-PC setting (SHOW_PC_NUMBER, USB_BLOCK, TASKMGR_DISABLE).</summary>
        public async Task TogglePcSetting(string pcNumber, string settingType)
        {
            if (!IsAuthorized()) return;
            if (!AdminHub.KnownClients.TryGetValue(pcNumber, out var client)) return;

            string value;
            switch (settingType.ToUpper())
            {
                case "SHOW_PC_NUMBER":
                    client.ShowPcNumber = !client.ShowPcNumber;
                    value = client.ShowPcNumber.ToString().ToLower();
                    break;
                case "USB_BLOCK":
                    client.UsbBlocked = !client.UsbBlocked;
                    value = client.UsbBlocked.ToString().ToLower();
                    break;
                case "TASKMGR_DISABLE":
                    client.TaskMgrDisabled = !client.TaskMgrDisabled;
                    value = client.TaskMgrDisabled.ToString().ToLower();
                    break;
                default:
                    return;
            }
            AdminHub.KnownClients[pcNumber] = client;
            AdminHub.MarkIndividualSetting(pcNumber, settingType);
            var json = JsonSerializer.Serialize(new { Type = settingType, Value = value });
            if (client.IsOnline)
                await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
            else
                AdminHub.AddPendingCommand(pcNumber, settingType, value);
            AdminHub.RaiseClientUpdated(client);
        }

        /// <summary>Toggle a global setting and send the command to one specific PC (marking it individual).</summary>
        public async Task ToggleGlobalSetting(string pcNumber, string settingType)
        {
            if (!IsAuthorized()) return;
            var global = GlobalSettings.Load();
            bool newVal;
            switch (settingType.ToUpper())
            {
                case "BLOCK_REGEDIT":     global.BlockRegedit     = !global.BlockRegedit;     newVal = global.BlockRegedit;     break;
                case "BLOCK_CMD":         global.BlockCmd         = !global.BlockCmd;         newVal = global.BlockCmd;         break;
                case "BLOCK_POWERSHELL":  global.BlockPowerShell  = !global.BlockPowerShell;  newVal = global.BlockPowerShell;  break;
                case "BLOCK_INSTALL_UNINSTALL": global.BlockInstall = !global.BlockInstall;   newVal = global.BlockInstall;     break;
                default: return;
            }
            global.Save();
            AdminHub.MarkIndividualSetting(pcNumber, settingType);
            if (AdminHub.KnownClients.TryGetValue(pcNumber, out var client))
            {
                var json = JsonSerializer.Serialize(new { Type = settingType, Value = newVal.ToString().ToLower() });
                if (client.IsOnline)
                    await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                else
                    AdminHub.AddPendingCommand(pcNumber, settingType, newVal.ToString().ToLower());
                AdminHub.RaiseClientUpdated(client);
            }
            // Also update browser admin clients about the settings change
            AdminBroadcaster.Instance?.PushSettings(global);
        }

        /// <summary>Clear all individual settings for a PC and push current global settings to it.</summary>
        public async Task ResetIndividualSettings(string pcNumber)
        {
            if (!IsAuthorized()) return;
            AdminHub.ClearIndividualSettings(pcNumber);
            if (AdminHub.KnownClients.TryGetValue(pcNumber, out var client) && client.IsOnline)
            {
                var global = GlobalSettings.Load();
                foreach (var cmd in global.ToCommands())
                {
                    var json = JsonSerializer.Serialize(new { cmd.Type, cmd.Value });
                    await _adminCtx.Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                }
                AdminHub.RaiseClientUpdated(client);
            }
        }

        // ─── Utilities ────────────────────────────────────────────────────────

        internal static object ClientDto(ClientState c) => new
        {
            pcNumber = c.PcNumber, pcNumberValue = c.PcNumberValue, customName = c.CustomName,
            status = c.Status, isOnline = c.IsOnline, isSession = c.IsSession,
            isFree = c.IsFree, isLocked = c.IsLocked, isPaused = c.IsPaused,
            sessionType = c.SessionType,
            // Для offline+Continue считаем elapsed с учётом времени с момента обрыва
            elapsedSeconds = (!c.IsOnline && c.IsSession && !c.IsPaused && c.DisconnectedAt.HasValue)
                ? c.ElapsedAtDisconnect + (int)(DateTime.UtcNow - c.DisconnectedAt.Value).TotalSeconds
                : c.ElapsedSeconds,
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
            backgroundFileName = c.BackgroundFileName,
            clientVersion = c.ClientVersion,
            updateStatus = c.UpdateStatus,
            preUpdateVersion = c.PreUpdateVersion
        };

        private bool IsAuthorized()
        {
            var token = Context.GetHttpContext()?.Request.Cookies["bib_admin"];
            return token != null && AdminAuth.IsValidToken(token);
        }
    }
}
