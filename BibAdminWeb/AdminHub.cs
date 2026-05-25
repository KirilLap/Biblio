using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace BibAdminWeb
{
    public class AdminHub : Hub
    {
        public static ConcurrentDictionary<string, ClientState> KnownClients { get; } = new();
        private static readonly ConcurrentDictionary<string, List<PendingCommand>> _pendingCommands = new();
        private static readonly string _registryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clients.json");
        private static readonly string _pendingPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pending_commands.json");
        private static readonly string SessionsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "active_sessions.json");

        public static event Action<ClientState>? ClientUpdated;
        public static event Action? ClientsChanged;
        public static event Action<ClientState>? ClientOfflineWithSession;
        // pcNumber, logContent
        public static event Action<string, string>? ClientLogsReceived;

        // Вызов событий из внешних классов (OperatorHub и т.п.)
        public static void RaiseClientUpdated(ClientState cs) => ClientUpdated?.Invoke(cs);
        public static event Action<string, int, int>? ClientTimeMismatch;
        private const int OfflineMismatchThreshold = 60;
        public static event Action<string, double>? ClientTimeDrift;
        private const double ClockDriftThreshold = 30.0;
        // registeredAs, requestedAs, mac, requestedPcNumberValue, requestedCustomName
        public static event Action<string, string, string, int, string>? ClientNameConflict;
        // mac, takenPcName, requestedPcNumberValue, requestedCustomName
        public static event Action<string, string, int, string>? ClientNumberConflict;

        // Блокировка записи сессий — защита от race condition при одновременных вызовах
        private static readonly object _sessionsLock = new();

        // ✅ ДЛЯ ЗАЩИТЫ ОТ ДУБЛЕЙ УВЕДОМЛЕНИЙ
        private static readonly ConcurrentDictionary<string, DateTime> _lastOfflineAlert = new();

        // MACs для которых конфликт уже показан в этой сессии (не показывать повторно)
        private static readonly HashSet<string> _shownConflicts = new();

        // Ожидание решения администратора по конфликту имён (переименование)
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<(int PcNumberValue, string CustomName)?>> _conflictDecisions = new();
        // Ожидание решения администратора по конфликту номера (новый ПК)
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _numberConflictDecisions = new();

        // Лог удалённых ПК
        private static readonly string _deletedPcsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "deleted_pcs.json");
        public static List<DeletedPcRecord> DeletedPcs { get; } = new();

        public static void LoadDeletedPcs()
        {
            if (!File.Exists(_deletedPcsPath)) return;
            try
            {
                var json = File.ReadAllText(_deletedPcsPath);
                var list = JsonSerializer.Deserialize<List<DeletedPcRecord>>(json);
                if (list != null) { DeletedPcs.Clear(); DeletedPcs.AddRange(list); }
            }
            catch (Exception ex) { Logger.Error($"Ошибка загрузки deleted_pcs.json: {ex.Message}"); }
        }

        private static void SaveDeletedPcs()
        {
            try { File.WriteAllText(_deletedPcsPath, JsonSerializer.Serialize(DeletedPcs, new JsonSerializerOptions { WriteIndented = true })); }
            catch { }
        }

        // Вызывается из UI: "Да" → передаём новые данные, "Нет" → null
        public static void ResolveConflict(string mac, int? newPcNumberValue, string? newCustomName)
        {
            if (_conflictDecisions.TryRemove(mac, out var tcs))
            {
                if (newPcNumberValue.HasValue)
                    tcs.TrySetResult((newPcNumberValue.Value, newCustomName ?? ""));
                else
                    tcs.TrySetResult(null);
            }
        }

        public static void ResolveNumberConflict(string mac, bool accept)
        {
            if (_numberConflictDecisions.TryRemove(mac, out var tcs))
                tcs.TrySetResult(accept);
        }

        public static bool DeleteClientStatic(string pcNumber)
        {
            if (!KnownClients.TryGetValue(pcNumber, out var client)) return false;
            if (client.IsOnline) return false;
            if (client.IsSession) return false;

            DeletedPcs.Add(new DeletedPcRecord
            {
                PcNumber = pcNumber,
                MacAddress = client.MacAddress,
                Ip = client.Ip,
                DeletedAt = DateTime.Now
            });
            SaveDeletedPcs();

            KnownClients.TryRemove(pcNumber, out _);
            _pendingCommands.TryRemove(pcNumber, out _);
            SaveRegistry();
            SavePending();
            SaveActiveSessions();

            ClientsChanged?.Invoke();
            Logger.Info($"ПК удалён из реестра: {pcNumber}");
            return true;
        }

        public static void LoadRegistry()
        {
            if (File.Exists(_registryPath))
            {
                try
                {
                    var json = File.ReadAllText(_registryPath);
                    var list = JsonSerializer.Deserialize<List<ClientState>>(json);
                    if (list != null)
                    {
                        foreach (var c in list)
                        {
                            // Миграция: убираем суффикс " N" из CustomName если он попал туда
                            // из-за старого бага (PcNumber setter перезаписывал CustomName)
                            if (!string.IsNullOrEmpty(c.CustomName))
                            {
                                var sfx = " " + c.PcNumberValue;
                                while (c.CustomName.EndsWith(sfx))
                                    c.CustomName = c.CustomName.Substring(0, c.CustomName.Length - sfx.Length).TrimEnd();
                                if (c.CustomName == "ПК") c.CustomName = "";
                            }
                            c.IsOnline = false;
                            c.Status = c.IsPaused ? "Пауза" : "Оффлайн";
                            c.LastSeen = DateTime.MinValue;
                            KnownClients[c.PcNumber] = c;
                        }
                        Logger.Info($"✅ Загружено {list.Count} клиентов");
                    }
                }
                catch (Exception ex) { Logger.Error($"Ошибка загрузки clients.json: {ex.Message}"); }
            }

            if (File.Exists(_pendingPath))
            {
                try
                {
                    var json = File.ReadAllText(_pendingPath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, List<PendingCommand>>>(json);
                    if (dict != null)
                        foreach (var kv in dict)
                            _pendingCommands[kv.Key] = kv.Value;
                    Logger.Info($"Загружено pending команд: {_pendingCommands.Sum(x => x.Value.Count)}");
                }
                catch { }
            }
        }

        private static void SaveRegistry()
        {
            try
            {
                File.WriteAllText(_registryPath, JsonSerializer.Serialize(KnownClients.Values, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private static void SavePending()
        {
            try
            {
                File.WriteAllText(_pendingPath, JsonSerializer.Serialize(_pendingCommands.ToDictionary(kv => kv.Key, kv => kv.Value), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static void SaveActiveSessions()
        {
            lock (_sessionsLock)
            {
                try
                {
                    var active = KnownClients.Values
                        .Where(c => !string.IsNullOrEmpty(c.SessionType) && c.SessionStart.HasValue)
                        .Select(c => new
                        {
                            PcNumber = c.PcNumber,
                            SessionType = c.SessionType,
                            SessionStartUtc = c.SessionStart,
                            LimitSeconds = c.LimitSeconds,
                            ElapsedSeconds = c.ElapsedSeconds,
                            IsPaused = c.IsPaused,
                            AccumulatedSeconds = c.AccumulatedSeconds,
                            PaidAmount = c.PaidAmount,
                            SessionId = c.SessionId,
                            DisconnectedAtUtc = c.DisconnectedAt?.ToString("o"),
                            ElapsedAtDisconnect = c.ElapsedAtDisconnect,
                            OfflineDecision = c.OfflineDecision.ToString(),
                            ReaderId = c.ReaderId ?? "",
                            UserName = c.UserName ?? "",
                            SavedAtUtc = DateTime.UtcNow
                        })
                        .ToList();

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var json = JsonSerializer.Serialize(active, options);

                    var tempPath = SessionsFilePath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    if (File.Exists(SessionsFilePath))
                        File.Replace(tempPath, SessionsFilePath, null);
                    else
                        File.Move(tempPath, SessionsFilePath);
                }
                catch (Exception ex) { Logger.Error($"❌ SaveActiveSessions: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Нормализует тип сессии: "По времени" и "По деньгам" → "Лимит".
        /// Обеспечивает совместимость со старыми сохранёнными данными.
        /// </summary>
        private static string ResolveUpdateStatus(ClientState? prev, string newVersion)
        {
            if (prev == null) return "";
            if (prev.UpdateStatus != "updating" && prev.UpdateStatus != "pending") return "";
            return !string.IsNullOrEmpty(newVersion) && !string.IsNullOrEmpty(prev.PreUpdateVersion)
                   && newVersion != prev.PreUpdateVersion ? "done" : "failed";
        }

        private static string NormalizeSessionType(string sessionType) =>
            sessionType is "По времени" or "По деньгам" ? "Лимит" : sessionType;

        public static void LoadActiveSessions()
        {
            if (!File.Exists(SessionsFilePath)) return;

            try
            {
                var json = File.ReadAllText(SessionsFilePath);
                var states = JsonSerializer.Deserialize<List<JsonElement>>(json);
                if (states == null) return;

                int restoredCount = 0;
                foreach (var s in states)
                {
                    var pcNumber = s.GetProperty("PcNumber").GetString();
                    if (string.IsNullOrEmpty(pcNumber)) continue;

                    if (!KnownClients.TryGetValue(pcNumber, out var client))
                    {
                        Logger.Warn($"⚠️ Клиент {pcNumber} из active_sessions не найден");
                        continue;
                    }

                    // Нормализуем: старые "По времени"/"По деньгам" → "Лимит"
                    var sessionType = NormalizeSessionType(s.GetProperty("SessionType").GetString() ?? "");
                    if (string.IsNullOrEmpty(sessionType) || sessionType == "Заблокирован" || sessionType == "Свободный")
                        continue;

                    // Пропускаем сессии старше 8 часов (защита от "фантомных" сессий после ночного выключения)
                    if (s.TryGetProperty("SavedAtUtc", out var savedAtProp) && savedAtProp.GetString() is string savedAtStr &&
                        DateTime.TryParse(savedAtStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var savedAt) &&
                        (DateTime.UtcNow - savedAt).TotalHours > 8)
                    {
                        Logger.Warn($"⚠️ Сессия {pcNumber} пропущена: устарела (сохранена {savedAt:HH:mm} UTC)");
                        continue;
                    }

                    DateTime? sessionStart = null;
                    if (s.TryGetProperty("SessionStartUtc", out var startProp))
                    {
                        var startStr = startProp.GetString();
                        if (!string.IsNullOrEmpty(startStr))
                            sessionStart = DateTime.Parse(startStr, null, System.Globalization.DateTimeStyles.RoundtripKind);
                    }

                    int elapsedSeconds = 0;
                    if (s.TryGetProperty("ElapsedSeconds", out var elapsedProp))
                        elapsedSeconds = elapsedProp.GetInt32();

                    bool isPaused = false;
                    if (s.TryGetProperty("IsPaused", out var pausedProp))
                        isPaused = pausedProp.GetBoolean();

                    int accumulatedSeconds = 0;
                    if (s.TryGetProperty("AccumulatedSeconds", out var accumProp))
                        accumulatedSeconds = accumProp.GetInt32();

                    int paidAmount = 0;
                    if (s.TryGetProperty("PaidAmount", out var paidProp))
                        paidAmount = paidProp.GetInt32();

                    int limitSeconds = 0;
                    if (s.TryGetProperty("LimitSeconds", out var limitProp))
                        limitSeconds = limitProp.GetInt32();

                    string sessionIdVal = "";
                    if (s.TryGetProperty("SessionId", out var sidProp))
                        sessionIdVal = sidProp.GetString() ?? "";

                    DateTime? disconnectedAt = null;
                    if (s.TryGetProperty("DisconnectedAtUtc", out var daProp))
                    {
                        var daStr = daProp.GetString();
                        if (!string.IsNullOrEmpty(daStr) && DateTime.TryParse(daStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var daParsed))
                            disconnectedAt = daParsed;
                    }

                    int elapsedAtDisconnect = 0;
                    if (s.TryGetProperty("ElapsedAtDisconnect", out var eadProp))
                        elapsedAtDisconnect = eadProp.GetInt32();

                    var offlineDecision = OfflineDecision.None;
                    if (s.TryGetProperty("OfflineDecision", out var odProp) &&
                        Enum.TryParse<OfflineDecision>(odProp.GetString(), out var parsedDecision))
                        offlineDecision = parsedDecision;

                    client.SessionType = sessionType;
                    client.SessionStart = sessionStart;
                    client.ElapsedSeconds = elapsedSeconds;
                    client.IsPaused = isPaused;
                    client.AccumulatedSeconds = accumulatedSeconds;
                    client.PaidAmount = paidAmount;
                    client.LimitSeconds = limitSeconds;

                    if (isPaused)
                        client.Status = "Пауза";
                    else
                        client.Status = sessionType;

                    client.SessionId = sessionIdVal;
                    client.DisconnectedAt = disconnectedAt;
                    client.ElapsedAtDisconnect = elapsedAtDisconnect;
                    client.OfflineDecision = offlineDecision;

                    // Восстанавливаем данные читателя (чтобы после перезапуска сессия
                    // не становилась «анонимной»)
                    if (s.TryGetProperty("ReaderId", out var ridProp) && ridProp.GetString() is string rid && !string.IsNullOrEmpty(rid))
                        client.ReaderId = rid;
                    if (s.TryGetProperty("UserName", out var unProp) && unProp.GetString() is string un && !string.IsNullOrEmpty(un))
                        client.UserName = un;

                    KnownClients[pcNumber] = client;
                    restoredCount++;
                    Logger.Info($"🔄 Восстановлена сессия: {pcNumber} | {sessionType} | {elapsedSeconds}с | читатель: {client.ReaderId}");
                }
                Logger.Info($"✅ Загружено {restoredCount} сессий из файла");
            }
            catch (Exception ex)
            {
                Logger.Error($"❌ LoadActiveSessions: {ex.Message}");
                Logger.Error($"Stack: {ex.StackTrace}");
            }
        }

        public override Task OnConnectedAsync()
        {
            Logger.Info($"Клиент подключился: {Context.ConnectionId}");
            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            var client = KnownClients.Values.FirstOrDefault(c => c.ConnectionId == Context.ConnectionId);
            if (client != null)
            {
                client.IsOnline = false;
                client.Status = "Оффлайн";
                client.LastSeen = DateTime.UtcNow;
                if (client.UpdateStatus == "pending")
                    client.UpdateStatus = "updating";

                if (client.IsSession)
                {
                    int elapsedNow = client.IsPaused
                        ? client.AccumulatedSeconds
                        : Math.Max(0, client.AccumulatedSeconds + (int)(DateTime.UtcNow - client.SessionStart!.Value).TotalSeconds);
                    client.ElapsedAtDisconnect = elapsedNow; // ✅ СОХРАНЯЕМ ПЕРЕД ОТСОЕДИНЕНИЕМ
                    client.DisconnectedAt = DateTime.UtcNow;
                    client.OfflineDecision = OfflineDecision.None;
                }
                else
                {
                    client.DisconnectedAt = null;
                    client.ElapsedAtDisconnect = 0;
                }

                KnownClients[client.PcNumber] = client;
                SaveRegistry();
                SaveActiveSessions();
                ClientUpdated?.Invoke(client);
                ClientsChanged?.Invoke();
                Logger.Info($"Клиент отключился: {client.PcNumber}{(client.DisconnectedAt.HasValue ? $" (активная сессия, elapsed={client.ElapsedAtDisconnect}с)" : "")}");

                // ✅ ЗАЩИТА ОТ ДУБЛЕЙ: не чаще 1 раза в 5 минут
                if (client.DisconnectedAt.HasValue)
                {
                    // Дедуп 60 сек — защита от rapid-disconnect, сбрасывается при реконнекте
                    if (_lastOfflineAlert.TryGetValue(client.PcNumber, out var lastAlert) &&
                        DateTime.UtcNow - lastAlert < TimeSpan.FromSeconds(60))
                    {
                        Logger.Info($"⏭️ Пропуск дубля уведомления для {client.PcNumber} (< 60с)");
                    }
                    else
                    {
                        _lastOfflineAlert[client.PcNumber] = DateTime.UtcNow;
                        ClientOfflineWithSession?.Invoke(client);
                    }
                }
            }
            return base.OnDisconnectedAsync(exception);
        }

        public static ClientState? SetOfflineDecision(string pcNumber, OfflineDecision decision)
        {
            if (!KnownClients.TryGetValue(pcNumber, out var client)) return null;

            client.OfflineDecision = decision;

            if (decision == OfflineDecision.Pause)
            {
                client.IsPaused = true;
                client.AccumulatedSeconds = client.ElapsedAtDisconnect;
                client.ElapsedSeconds = client.ElapsedAtDisconnect;
                client.Status = "Пауза";
            }
            else if (decision == OfflineDecision.Continue)
            {
                // Явный Continue — сбрасываем паузу, восстанавливаем статус из типа сессии
                client.IsPaused = false;
                if (!string.IsNullOrEmpty(client.SessionType) &&
                    client.SessionType != "Заблокирован" &&
                    client.SessionType != "Свободный")
                    client.Status = client.SessionType;
            }

            KnownClients[pcNumber] = client;
            SaveActiveSessions();
            ClientUpdated?.Invoke(client);
            Logger.Info($"Решение администратора для {pcNumber}: {decision}, elapsed={client.ElapsedAtDisconnect}с");
            return client;
        }

        public async Task<string> RegisterClient(SystemInfoDto info, string macAddress, bool isRestoring = false, string sessionId = "", int offlineSeconds = 0)
        {
            Logger.Info($"Регистрация: ПК {info.PcNumberValue}, MAC: {macAddress}");

            var existingByMac = KnownClients.Values.FirstOrDefault(c => c.MacAddress == macAddress);
            
            // Определяем финальное имя: если клиент уже известен по MAC - берем его PcNumberValue и CustomName
            int finalPcNumberValue = info.PcNumberValue;
            string finalCustomName = info.CustomName;
            
            if (existingByMac != null)
            {
                // Клиент уже известен - сохраняем его настройки имени
                finalPcNumberValue = existingByMac.PcNumberValue;
                finalCustomName = existingByMac.CustomName;

                // Если клиент подключился под другим именем — ждём решения администратора
                string requestedName = string.IsNullOrEmpty(info.CustomName)
                    ? $"ПК {info.PcNumberValue}"
                    : $"{info.CustomName} {info.PcNumberValue}";
                if (requestedName != existingByMac.PcNumber && !_shownConflicts.Contains(macAddress))
                {
                    _shownConflicts.Add(macAddress);
                    Logger.Warn($"⚠️ Конфликт имён: MAC {macAddress} был '{existingByMac.PcNumber}', подключается как '{requestedName}'");

                    var tcs = new TaskCompletionSource<(int PcNumberValue, string CustomName)?>();
                    _conflictDecisions[macAddress] = tcs;
                    ClientNameConflict?.Invoke(existingByMac.PcNumber, requestedName, macAddress, info.PcNumberValue, info.CustomName);

                    try
                    {
                        // Ждём пока администратор нажмёт Да/Нет (макс 60 сек)
                        var decision = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60));
                        if (decision.HasValue)
                        {
                            finalPcNumberValue = decision.Value.PcNumberValue;
                            finalCustomName = decision.Value.CustomName;
                            _shownConflicts.Remove(macAddress); // сняли конфликт — сброс, имя будет правильным
                            Logger.Info($"✅ Администратор принял: MAC {macAddress} → ПК {finalPcNumberValue}");
                        }
                        else
                        {
                            Logger.Info($"🚫 Администратор отклонил переименование MAC {macAddress}");
                        }
                    }
                    catch (TimeoutException)
                    {
                        Logger.Warn($"⏰ Тайм-аут ожидания решения по конфликту {macAddress}, используем старое имя");
                        _conflictDecisions.TryRemove(macAddress, out _);
                    }
                }
            }
            
            string finalName = string.IsNullOrEmpty(finalCustomName) ? $"ПК {finalPcNumberValue}" : $"{finalCustomName} {finalPcNumberValue}";

            if (existingByMac == null)
            {
                // Новый клиент — если номер занят другим ПК, спрашиваем администратора
                if (KnownClients.ContainsKey(finalName) && !_shownConflicts.Contains(macAddress))
                {
                    _shownConflicts.Add(macAddress);
                    var takenName = finalName;
                    Logger.Warn($"⚠️ Новый ПК (MAC {macAddress}) хочет номер уже занятый: '{takenName}'");

                    var tcs = new TaskCompletionSource<bool>();
                    _numberConflictDecisions[macAddress] = tcs;
                    ClientNumberConflict?.Invoke(macAddress, takenName, finalPcNumberValue, finalCustomName);

                    bool accepted = false;
                    try
                    {
                        accepted = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60));
                    }
                    catch (TimeoutException)
                    {
                        Logger.Warn($"⏰ Тайм-аут по конфликту номера {macAddress}, авторегистрация");
                        _numberConflictDecisions.TryRemove(macAddress, out _);
                        accepted = true;
                    }

                    if (!accepted)
                    {
                        _shownConflicts.Remove(macAddress);
                        Logger.Info($"🚫 Администратор отклонил регистрацию нового ПК MAC {macAddress}");
                        return "REJECTED";
                    }
                }

                // Найти первый свободный номер, сохраняя CustomName
                while (KnownClients.ContainsKey(finalName))
                {
                    finalPcNumberValue++;
                    finalName = string.IsNullOrEmpty(finalCustomName)
                        ? $"ПК {finalPcNumberValue}"
                        : $"{finalCustomName} {finalPcNumberValue}";
                }
                _shownConflicts.Remove(macAddress);
            }

            if (existingByMac != null && existingByMac.PcNumber != finalName)
            {
                KnownClients.TryRemove(existingByMac.PcNumber, out _);
                Logger.Info($"Удалена старая запись: {existingByMac.PcNumber}");
            }

            bool isNewClient = existingByMac == null;

            bool hadActiveSession = existingByMac != null &&
                !string.IsNullOrEmpty(existingByMac.SessionType) &&
                existingByMac.SessionType != "Заблокирован" &&
                existingByMac.SessionType != "Свободный" &&
                existingByMac.SessionStart.HasValue;

            // Нормализуем тип сессии из сохранённых данных (совместимость со старыми записями)
            var restoredSessionType = NormalizeSessionType(existingByMac?.SessionType ?? "");
            var restoredStatus = (existingByMac?.IsPaused == true) ? "Пауза"
                : (hadActiveSession ? restoredSessionType : "Заблокирован");

            var state = new ClientState
            {
                PcNumberValue = finalPcNumberValue,
                CustomName = finalCustomName,
                ConnectionId = Context.ConnectionId,
                Ip = info.LocalIp,
                MacAddress = macAddress,
                OsVersion = info.OsVersion,
                DiskFreeGb = info.DiskFreeGb,
                UptimeHours = info.UptimeHours,
                IsOnline = true,
                LastSeen = DateTime.UtcNow,
                Status = restoredStatus,
                SessionType = restoredSessionType,
                SessionStart = existingByMac?.SessionStart,
                LimitSeconds = existingByMac?.LimitSeconds ?? 0,
                ElapsedSeconds = existingByMac?.ElapsedSeconds ?? 0,
                PaidAmount = existingByMac?.PaidAmount ?? 0,
                IsPaused = existingByMac?.IsPaused ?? false,
                AccumulatedSeconds = existingByMac?.AccumulatedSeconds ?? 0,
                HasIndividualSettings = existingByMac?.HasIndividualSettings ?? false,
                IndividualSettingKeys = existingByMac?.IndividualSettingKeys ?? new(),
                SessionId = !string.IsNullOrEmpty(sessionId) ? sessionId : existingByMac?.SessionId ?? "",
                DisconnectedAt = null,
                OfflineDecision = existingByMac?.OfflineDecision ?? OfflineDecision.None,
                ElapsedAtDisconnect = existingByMac?.ElapsedAtDisconnect ?? 0, // ✅ КОПИРУЕМ!
                ClientVersion = !string.IsNullOrEmpty(info.ClientVersion) ? info.ClientVersion : (existingByMac?.ClientVersion ?? ""),
                PreUpdateVersion = existingByMac?.PreUpdateVersion ?? "",
                UpdateStatus = ResolveUpdateStatus(existingByMac, info.ClientVersion),
            };

            KnownClients.AddOrUpdate(finalName, state, (_, _) => state);
            SaveRegistry();

            // НЕ сбрасываем _lastOfflineAlert при реконнекте: если сеть нестабильна,
            // клиент может успеть зарегистрироваться и тут же упасть снова — что даёт
            // дублирующее уведомление. 60-секундное окно дедупа истечёт само.
            // Уведомление при следующем стабильном обрыве будет сгенерировано корректно.

            ClientUpdated?.Invoke(state);
            ClientsChanged?.Invoke();

            if (!string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(existingByMac?.SessionId)
                && existingByMac.SessionId != sessionId && hadActiveSession)
                Logger.Warn($"⚠️ SessionId мismatch: {finalName} прислал {sessionId[..8]}…, сервер помнит {existingByMac.SessionId[..8]}…");

            Logger.Info($"Клиент зарегистрирован: {finalName}{(hadActiveSession ? $" (восстановлена сессия: {state.SessionType})" : "")}");
            if (offlineSeconds > 0)
                Logger.Info($"🕐 Клиент {finalName} сообщает о {offlineSeconds}с оффлайна");

            // Всегда отправляем глобальные настройки — и новым, и переподключившимся клиентам.
            // Метод внутри проверяет индивидуальные настройки и пропускает их.
            await SendGlobalSettingsToClient(finalName);
            Logger.Info($"Глобальные настройки отправлены ПК: {finalName} (новый: {isNewClient})");

            await FlushPendingCommands(finalName);

            if (!string.IsNullOrEmpty(info.ClientTimeUtc) &&
                DateTime.TryParse(info.ClientTimeUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var clientClock))
            {
                double offsetSeconds = (DateTime.UtcNow - clientClock).TotalSeconds;

                var offsetCmd = new { Type = "SET_TIME_OFFSET", Value = offsetSeconds.ToString("F2") };
                await Clients.Client(Context.ConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(offsetCmd));

                if (Math.Abs(offsetSeconds) > ClockDriftThreshold)
                {
                    string direction = offsetSeconds > 0 ? "отстаёт от сервера" : "опережает сервер";
                    Logger.Warn($"⚠️ CLOCK DRIFT {finalName}: клиент {direction} на {Math.Abs(offsetSeconds):F0}с");
                    ClientTimeDrift?.Invoke(finalName, offsetSeconds);

                    var mismatchCmd = new { Type = "CLOCK_MISMATCH", Value = offsetSeconds.ToString("F2") };
                    await Clients.Client(Context.ConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(mismatchCmd));
                }
                else
                {
                    Logger.Info($"✅ Часы {finalName}: расхождение {offsetSeconds:F1}с — в норме");
                }
            }

            if (hadActiveSession && KnownClients.TryGetValue(finalName, out var client))
            {
                if (isRestoring && offlineSeconds > 0 && existingByMac?.DisconnectedAt.HasValue == true)
                {
                    int serverOfflineSecs = (int)(DateTime.UtcNow - existingByMac.DisconnectedAt!.Value).TotalSeconds;
                    int mismatch = Math.Abs(offlineSeconds - serverOfflineSecs);
                    if (mismatch > OfflineMismatchThreshold)
                    {
                        Logger.Warn($"⚠️ TIME MISMATCH {finalName}: клиент={offlineSeconds}с, сервер={serverOfflineSecs}с, расхождение={mismatch}с");
                        ClientTimeMismatch?.Invoke(finalName, offlineSeconds, serverOfflineSecs);
                    }
                    else
                    {
                        Logger.Info($"✅ Верификация оффлайна {finalName}: клиент={offlineSeconds}с ≈ сервер={serverOfflineSecs}с ✓");
                    }
                }

                bool adminChosePause = client.OfflineDecision == OfflineDecision.Pause;
                bool adminChoseContinue = client.OfflineDecision == OfflineDecision.Continue;

                int elapsedToSend;
                bool sendPause;

                if (adminChosePause)
                {
                    elapsedToSend = client.ElapsedAtDisconnect; // ✅ ИСПОЛЬЗУЕМ СОХРАНЁННОЕ ЗНАЧЕНИЕ
                    sendPause = true;

                    client.Status = "Пауза";
                    client.IsPaused = true;
                    KnownClients[finalName] = client;
                    ClientUpdated?.Invoke(client);
                    Logger.Info($"✅ {finalName}: применяем решение ПАУЗА, elapsed={elapsedToSend}с");
                }
                else if (!isRestoring || adminChoseContinue)
                {
                    int serverElapsed = client.AccumulatedSeconds +
                        (client.IsPaused ? 0 : (int)(DateTime.UtcNow - client.SessionStart!.Value).TotalSeconds);
                    elapsedToSend = Math.Max(0, serverElapsed);
                    sendPause = client.IsPaused;
                    Logger.Info($"🔄 {finalName}: LAN/Continue, elapsed={elapsedToSend}с");
                }
                else
                {
                    int activePart = Math.Max(0, client.ElapsedSeconds - client.AccumulatedSeconds);
                    client.SessionStart = DateTime.UtcNow.AddSeconds(-activePart);
                    KnownClients[finalName] = client;
                    elapsedToSend = client.ElapsedSeconds;
                    sendPause = client.IsPaused;
                    Logger.Info($"🛡️ {finalName}: smart protection, elapsed={elapsedToSend}с");
                }

                var restoreCmd = new
                {
                    Type = "START_SESSION",
                    SessionType = client.SessionType,
                    LimitSeconds = client.LimitSeconds,
                    PaidAmount = client.PaidAmount,
                    ElapsedSeconds = elapsedToSend
                };
                await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(restoreCmd));

                // Grace period: клиент пришлёт «Заблокирован» сразу после RegisterClient,
                // до того как обработает START_SESSION. Ставим метку чтобы UpdateStatus его проигнорировал.
                client.PendingStartSessionSentAt = DateTime.UtcNow;
                KnownClients[finalName] = client;

                if (sendPause)
                {
                    await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand",
                        JsonSerializer.Serialize(new { Type = "PAUSE_SESSION", Value = "" }));
                }

                client.OfflineDecision = OfflineDecision.None;
                client.DisconnectedAt = null;
                KnownClients[finalName] = client;

                ClientUpdated?.Invoke(client);

                await Task.Delay(1000);
                await SyncSessionTime(finalName, force: true);
            }

            return finalName;
        }

        private async Task SendGlobalSettingsToClient(string pcNumber)
        {
            try
            {
                if (!KnownClients.TryGetValue(pcNumber, out var client) || !client.IsOnline)
                    return;

                var global = GlobalSettings.Load();
                var commands = global.ToCommands();
                bool settingsChanged = false;

                foreach (var cmd in commands)
                {
                    if (client.IsIndividual(cmd.Type))
                    {
                        if (IsValueMatchingClientState(client, cmd.Type, cmd.Value))
                        {
                            client.IndividualSettingKeys.Remove(cmd.Type);
                            if (client.IndividualSettingKeys.Count == 0)
                                client.HasIndividualSettings = false;
                            settingsChanged = true;
                            Logger.Info($"Авто-очистка: {pcNumber} → {cmd.Type}");
                        }
                        else { continue; }
                    }
                    var json = JsonSerializer.Serialize(new { cmd.Type, cmd.Value });
                    await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                }

                if (settingsChanged) SaveRegistryStatic();
            }
            catch (Exception ex) { Logger.Error($"Ошибка отправки глобальных настроек: {ex.Message}"); }
        }

        private bool IsValueMatchingClientState(ClientState client, string commandType, string globalValue)
        {
            return commandType.ToUpper() switch
            {
                "USB_BLOCK" => client.UsbBlocked.ToString().ToLower() == globalValue.ToLower(),
                "TASKMGR_DISABLE" => client.TaskMgrDisabled.ToString().ToLower() == globalValue.ToLower(),
                "SHOW_PC_NUMBER" => client.ShowPcNumber.ToString().ToLower() == globalValue.ToLower(),
                "SET_PC_NUMBER_POSITION" or "SET_LOCKED_TEXT_POSITION" or "SET_TIME_POSITION" => true,
                "SET_PC_NUMBER_FONT_SIZE" or "SET_LOCKED_TEXT_FONT_SIZE" or "SET_TIME_FONT_SIZE" => true,
                "SET_BACKGROUND_OPACITY" => true,
                "SET_BACKGROUND" => true,
                "ADMIN_PASSWORD" or "SET_TARIFF" => true,
                _ => false
            };
        }

        private async Task FlushPendingCommands(string pcNumber)
        {
            if (!_pendingCommands.TryGetValue(pcNumber, out var commands) || commands.Count == 0)
                return;
            if (!KnownClients.TryGetValue(pcNumber, out var client) || !client.IsOnline)
                return;

            Logger.Info($"Отправка {commands.Count} pending команд → {pcNumber}");

            foreach (var cmd in commands)
            {
                try
                {
                    if (client.IsIndividual(cmd.Type)) continue;

                    if (cmd.Type == "REMOTE_LOCK" &&
                        !string.IsNullOrEmpty(client.SessionType) &&
                        client.SessionStart.HasValue)
                    {
                        Logger.Info($"⏭️ Пропуск REMOTE_LOCK для {pcNumber} — есть активная сессия ({client.SessionType})");
                        continue;
                    }

                    var json = JsonSerializer.Serialize(new { cmd.Type, cmd.Value });
                    await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                }
                catch (Exception ex) { Logger.Error($"Ошибка pending команды: {ex.Message}"); }
            }

            _pendingCommands.TryRemove(pcNumber, out _);
            SavePending();
            Logger.Info($"Pending команды очищены для {pcNumber}");
        }

        public static void AddPendingCommand(string pcNumber, string type, string value)
        {
            var cmd = new PendingCommand(type, value);
            _pendingCommands.AddOrUpdate(
                pcNumber,
                _ => new List<PendingCommand> { cmd },
                (_, existing) => { existing.RemoveAll(c => c.Type == type); existing.Add(cmd); return existing; });
            SavePending();
        }

        public static void AddPendingCommandForAll(string type, string value, IEnumerable<string> skipPcNumbers)
        {
            foreach (var pc in KnownClients.Values.Where(c => !c.IsOnline))
            {
                if (pc.IsIndividual(type) && skipPcNumbers.Contains(pc.PcNumber)) continue;
                AddPendingCommand(pc.PcNumber, type, value);
            }
        }

        public async Task SendHeartbeat(string pcNumber)
        {
            if (KnownClients.TryGetValue(pcNumber, out var client))
            {
                client.IsOnline = true;
                client.LastSeen = DateTime.UtcNow;
                KnownClients[pcNumber] = client;
            }
        }

        /// <summary>Клиент отправляет содержимое своего лог-файла по запросу GET_LOGS.</summary>
        public Task ReportLogs(string pcNumber, string logContent)
        {
            Logger.Info($"📋 {pcNumber}: получены логи ({logContent.Length} символов)");
            ClientLogsReceived?.Invoke(pcNumber, logContent);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Клиент сообщает что обновление не состоялось: "no_update" — версия та же, "download_failed" — ошибка загрузки.
        /// </summary>
        public Task ReportUpdateResult(string pcNumber, string reason)
        {
            if (KnownClients.TryGetValue(pcNumber, out var client) &&
                (client.UpdateStatus == "pending" || client.UpdateStatus == "updating"))
            {
                client.UpdateStatus = reason == "no_update" ? "" : "failed";
                KnownClients[pcNumber] = client;
                ClientUpdated?.Invoke(client);
                Logger.Info($"⬆️ {pcNumber}: ReportUpdateResult={reason} → UpdateStatus='{client.UpdateStatus}'");
            }
            return Task.CompletedTask;
        }

        public async Task UpdateStatus(string pcNumber, string status, string sessionType, int elapsedSeconds)
        {
            if (!KnownClients.TryGetValue(pcNumber, out var client)) return;

            client.LastSeen = DateTime.UtcNow;
            client.IsOnline = true;

            // Клиент явно сообщает что заблокирован — очищаем сессию немедленно.
            // Guard 2 ниже некорректно глотает этот статус (elapsed==0), поэтому обрабатываем первым.
            if (status == "Заблокирован")
            {
                // Grace period: после реконнекта клиент всегда шлёт «Заблокирован» сразу после RegisterClient,
                // до того как получает и обрабатывает START_SESSION. Игнорируем в течение 15 секунд.
                if (client.PendingStartSessionSentAt.HasValue &&
                    (DateTime.UtcNow - client.PendingStartSessionSentAt.Value).TotalSeconds < 15)
                {
                    Logger.Info($"⏭️ {pcNumber}: Заблокирован проигнорирован (grace period, ожидается старт восстановленной сессии)");
                    client.LastSeen = DateTime.UtcNow;
                    KnownClients[pcNumber] = client;
                    return;
                }
                bool hadSession = !string.IsNullOrEmpty(client.SessionType) && client.SessionStart.HasValue;
                bool deferredUpdate = client.UpdateStatus == "deferred";
                client.Status = "Заблокирован";
                client.SessionType = "";
                client.SessionStart = null;
                client.ElapsedSeconds = 0;
                client.LimitSeconds = 0;
                client.IsPaused = false;
                client.AccumulatedSeconds = 0;
                client.PendingStartSessionSentAt = null;
                if (deferredUpdate) client.UpdateStatus = "pending";
                KnownClients[pcNumber] = client;
                ClientUpdated?.Invoke(client);
                if (hadSession) SaveActiveSessions();
                Logger.Info($"🔒 {pcNumber}: клиент заблокирован{(hadSession ? " — сессия очищена" : "")}");
                if (deferredUpdate)
                {
                    var updateJson = JsonSerializer.Serialize(new { Type = "UPDATE_NOW", Value = "" });
                    await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", updateJson);
                    Logger.Info($"⬆️ {pcNumber}: отправлена отложенная команда UPDATE_NOW");
                }
                return;
            }

            if (string.IsNullOrEmpty(client.SessionType) && !client.SessionStart.HasValue &&
                status != "Свободный")
            {
                KnownClients[pcNumber] = client;
                ClientUpdated?.Invoke(client);
                return;
            }

            if (client.SessionStart.HasValue && elapsedSeconds == 0)
            {
                // Применяем IsPaused как приоритет — клиент ещё не прислал elapsed,
                // но пауза уже стоит на сервере (например, решение администратора).
                if (status != "Свободный")
                    client.Status = client.IsPaused ? "Пауза" : status;
                KnownClients[pcNumber] = client;
                ClientUpdated?.Invoke(client);
                return;
            }

            if (elapsedSeconds > 0)
            {
                client.PendingStartSessionSentAt = null; // сессия запущена — grace period больше не нужен
                client.ElapsedSeconds = elapsedSeconds;
                client.SessionStart = DateTime.UtcNow.AddSeconds(-elapsedSeconds);
                if (!client.IsPaused)
                    client.AccumulatedSeconds = 0;
            }

            // Нормализуем тип сессии от клиента, но не перезаписываем при блокировке —
            // иначе клиент с устаревшим ActiveSessionType восстановит "призрачную" сессию.
            if (status != "Заблокирован" && status != "Свободный" && !string.IsNullOrEmpty(sessionType))
                client.SessionType = NormalizeSessionType(sessionType);

            if (status == "Пауза")
            {
                client.IsPaused = true;
                client.AccumulatedSeconds = elapsedSeconds;
            }

            // 🔑 🔥 ГЛАВНАЯ ЗАЩИТА: ЕСЛИ ЕСТЬ OFFLINE_DECISION = PAUSE, ИГНОРИРУЕМ СТАТУС ОТ КЛИЕНТА
            if (client.OfflineDecision == OfflineDecision.Pause && !client.IsPaused)
            {
                client.IsPaused = true;
                client.AccumulatedSeconds = client.ElapsedAtDisconnect;
                Logger.Info($"🛡️ {pcNumber}: принудительная пауза по OfflineDecision");
            }

            // ── Строгая цепочка приоритетов визуального статуса ──────────────────────
            // Сервер является источником истины; клиентский статус используется только
            // для цифр/биллинга, визуальный статус пересчитывается здесь независимо.
            string visualStatus;
            string reason;

            if (!client.IsOnline)
            {
                visualStatus = "Оффлайн";
                reason = "IsOnline=false";
            }
            else if (client.IsPaused)
            {
                visualStatus = "Пауза";
                reason = "IsPaused=true (приоритет над клиентским статусом)";
            }
            else if (client.SessionType == "VIP")
            {
                visualStatus = "VIP";
                reason = "SessionType=VIP";
            }
            else if (client.SessionType == "Лимит")
            {
                visualStatus = "Лимит";
                reason = "SessionType=Лимит";
            }
            else if (status == "Свободный")
            {
                visualStatus = "Свободный";
                reason = "клиент сообщил Свободный";
            }
            else
            {
                // Нет активной сессии, нет явного освобождения — считаем заблокированным
                visualStatus = "Заблокирован";
                reason = "нет активной сессии (fallback)";
            }

            client.Status = visualStatus;

            KnownClients[pcNumber] = client;
            ClientUpdated?.Invoke(client);

            Logger.Info($"🎨 Визуал {pcNumber}: \"{client.Status}\" | Причина: {reason} (SessionType={client.SessionType}, elapsed={client.ElapsedSeconds}с, paused={client.IsPaused})");

            if (client.IsSession || client.Status == "Пауза")
                SaveActiveSessions();
        }

        public async Task SyncSessionTime(string pcNumber, bool force = false)
        {
            if (!KnownClients.TryGetValue(pcNumber, out var client)) return;
            if (client.IsPaused || !(client.IsSession || client.Status == "Пауза") || !client.SessionStart.HasValue) return;

            if (!force && DateTime.UtcNow - client.LastSeen < TimeSpan.FromSeconds(10)) return;

            int serverElapsed = client.AccumulatedSeconds + (int)(DateTime.UtcNow - client.SessionStart.Value).TotalSeconds;
            serverElapsed = Math.Max(0, serverElapsed);

            int diff = Math.Abs(serverElapsed - client.ElapsedSeconds);
            if (!force && diff < 10) return;

            client.ElapsedSeconds = serverElapsed;
            KnownClients[pcNumber] = client;
            SaveActiveSessions();

            var cmd = new { Type = "SESSION_TIME_SYNC", Value = serverElapsed.ToString(), IsPaused = client.IsPaused };
            var json = JsonSerializer.Serialize(cmd);
            await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);

            Logger.Info($"🔄 SyncSessionTime: {pcNumber} → {serverElapsed}с (расхождение {diff}с)");
        }

        public async Task RenameClient(string oldName, string newName)
        {
            if (KnownClients.TryGetValue(oldName, out var client))
            {
                if (_pendingCommands.TryRemove(oldName, out var cmds))
                    _pendingCommands[newName] = cmds;

                client.PcNumber = newName;
                KnownClients.TryRemove(oldName, out _);
                KnownClients[newName] = client;
                SaveRegistry();
                SavePending();
                ClientsChanged?.Invoke();
                Logger.Info($"ПК переименован: {oldName} → {newName}");
            }
        }

        /// <summary>
        /// Устанавливает индивидуальное отображаемое имя для клиента (CustomName).
        /// Уникальный идентификатор PcNumberValue не меняется.
        /// </summary>
        public async Task SetClientCustomName(string pcNumber, string customName)
        {
            if (KnownClients.TryGetValue(pcNumber, out var client))
            {
                var oldName = pcNumber;
                
                // Обновляем CustomName
                client.CustomName = customName;
                
                // Вычисляем новое отображаемое имя (теперь это "{CustomName} {PcNumberValue}" или "ПК {PcNumberValue}")
                var newName = string.IsNullOrEmpty(customName) ? $"ПК {client.PcNumberValue}" : $"{customName} {client.PcNumberValue}";
                
                // Если имя изменилось - переносим в словаре
                if (oldName != newName)
                {
                    KnownClients.TryRemove(oldName, out _);
                    KnownClients[newName] = client;
                    
                    // Переносим отложенные команды
                    if (_pendingCommands.TryRemove(oldName, out var cmds))
                        _pendingCommands[newName] = cmds;
                        
                    Logger.Info($"✅ Имя ПК изменено: {oldName} → {newName} (CustomName={customName})");
                }
                else
                {
                    Logger.Info($"✅ CustomName обновлён для {pcNumber}: '{customName}'");
                }
                
                SaveRegistry();
                SavePending();
                ClientUpdated?.Invoke(client);  // ✅ Вызываем ClientUpdated для обновления UI
                ClientsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Устанавливает индивидуальное отображаемое имя для клиента по его числовому идентификатору.
        /// Это более надёжный способ, так как PcNumberValue не меняется при переименовании.
        /// </summary>
        public async Task SetClientCustomNameByValue(int pcNumberValue, string customName)
        {
            var client = KnownClients.Values.FirstOrDefault(c => c.PcNumberValue == pcNumberValue);
            if (client != null)
            {
                var oldName = client.PcNumber;
                var oldConnectionId = client.ConnectionId;
                
                // Обновляем CustomName
                client.CustomName = customName;
                
                // Вычисляем новое отображаемое имя (теперь это "{CustomName} {PcNumberValue}" или "ПК {PcNumberValue}")
                var newName = string.IsNullOrEmpty(customName) ? $"ПК {pcNumberValue}" : $"{customName} {pcNumberValue}";
                
                // Если имя изменилось - переносим в словаре
                if (oldName != newName)
                {
                    KnownClients.TryRemove(oldName, out _);
                    KnownClients[newName] = client;
                    
                    // Переносим отложенные команды
                    if (_pendingCommands.TryRemove(oldName, out var cmds))
                        _pendingCommands[newName] = cmds;
                        
                    Logger.Info($"✅ Имя ПК изменено: {oldName} → {newName} (CustomName={customName})");
                }
                else
                {
                    Logger.Info($"✅ CustomName обновлён для ПК {pcNumberValue}: '{customName}'");
                }
                
                SaveRegistry();
                SavePending();
                ClientUpdated?.Invoke(client);  // ✅ Вызываем ClientUpdated для обновления UI
                ClientsChanged?.Invoke();
                
                // Отправляем команду на клиент с новым именем сразу после обновления сервера
                if (!string.IsNullOrEmpty(oldConnectionId))
                {
                    try
                    {
                        // Отправляем только CustomName (без номера), клиент сам сформирует полное имя
                        var cmd = new { Type = "SET_PC_NAME", Value = customName ?? "" };
                        await Clients.Client(oldConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(cmd));
                        Logger.Info($"📤 Команда SET_PC_NAME отправлена клиенту: '{customName}' (PcNumberValue={pcNumberValue})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Ошибка отправки команды SET_PC_NAME: {ex.Message}");
                    }
                }
            }
        }

        public async Task SendCommand(string pcNumber, string commandJson)
        {
            if (KnownClients.TryGetValue(pcNumber, out var client))
            {
                if (client.IsOnline)
                {
                    await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", commandJson);
                }
                else
                {
                    try
                    {
                        var cmd = JsonSerializer.Deserialize<PendingCommand>(commandJson);
                        if (cmd != null) AddPendingCommand(pcNumber, cmd.Type, cmd.Value);
                    }
                    catch { }
                    Logger.Info($"ПК {pcNumber} оффлайн — команда добавлена в очередь");
                }
            }
        }

        public async Task SendCommandToAll(string commandJson)
        {
            foreach (var client in KnownClients.Values)
            {
                if (client.IsOnline)
                {
                    await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", commandJson);
                }
                else
                {
                    try
                    {
                        var cmd = JsonSerializer.Deserialize<PendingCommand>(commandJson);
                        if (cmd != null) AddPendingCommand(client.PcNumber, cmd.Type, cmd.Value);
                    }
                    catch { }
                }
            }
        }

        public async Task UploadFile(string fileName, byte[] fileData, string targetPc, bool replaceIndividual = true)
        {
            try
            {
                var filesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Files");
                Directory.CreateDirectory(filesDir);
                var filePath = Path.Combine(filesDir, fileName);

                // Просто перезаписываем файл — без создания версий с timestamp
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

                    foreach (var client in KnownClients.Values)
                    {
                        bool isIndividual = client.IsIndividual("SET_BACKGROUND");

                        if (isIndividual && !replaceIndividual)
                            continue; // Оставляем индивидуальный фон нетронутым

                        if (isIndividual && replaceIndividual)
                        {
                            // Сбрасываем индивидуальный фон
                            client.IndividualSettingKeys.Remove("SET_BACKGROUND");
                            if (client.IndividualSettingKeys.Count == 0)
                                client.HasIndividualSettings = false;
                            client.BackgroundFileName = "";
                        }

                        if (client.IsOnline)
                            await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                        else
                            AddPendingCommand(client.PcNumber, "SET_BACKGROUND", fileName);
                    }

                    SaveRegistry();
                }
                else
                {
                    // Индивидуальный фон — не трогаем GlobalSettings
                    if (KnownClients.TryGetValue(targetPc, out var client))
                    {
                        client.BackgroundFileName = fileName;
                        client.MarkIndividual("SET_BACKGROUND");
                        KnownClients[targetPc] = client;
                        SaveRegistry();

                        if (client.IsOnline)
                            await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                        else
                            AddPendingCommand(targetPc, "SET_BACKGROUND", fileName);
                    }
                }

                CleanupUnusedBackgroundFiles();
                Logger.Info($"Фон применён: {fileName} → {targetPc}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка UploadFile: {ex.Message}");
                Logger.Error($"Stack: {ex.StackTrace}");
                throw;
            }
        }

        public static void CleanupUnusedBackgroundFiles()
        {
            try
            {
                var filesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Files");
                if (!Directory.Exists(filesDir)) return;

                // Собираем все файлы, на которые кто-то ссылается
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var global = GlobalSettings.Load();
                if (!string.IsNullOrEmpty(global.BackgroundFileName))
                    referenced.Add(global.BackgroundFileName);

                foreach (var client in KnownClients.Values)
                {
                    if (client.IsIndividual("SET_BACKGROUND") && !string.IsNullOrEmpty(client.BackgroundFileName))
                        referenced.Add(client.BackgroundFileName);
                }

                // Удаляем все файлы, которые больше не используются
                foreach (var file in Directory.GetFiles(filesDir))
                {
                    var name = Path.GetFileName(file);
                    if (!referenced.Contains(name))
                    {
                        try
                        {
                            File.Delete(file);
                            Logger.Info($"Удалён неиспользуемый фон: {name}");
                        }
                        catch (Exception ex) { Logger.Warn($"Не удалось удалить {name}: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { Logger.Error($"Ошибка очистки Files: {ex.Message}"); }
        }

        public async Task<string> TransferSession(string fromPcNumber, string toPcNumber)
        {
            if (!KnownClients.TryGetValue(fromPcNumber, out var source))
                return $"Ошибка: ПК {fromPcNumber} не найден";
            if (!KnownClients.TryGetValue(toPcNumber, out var target))
                return $"Ошибка: ПК {toPcNumber} не найден";
            if (!source.IsSession)
                return "Ошибка: на исходном ПК нет активной сессии";
            if (!target.IsOnline)
                return "Ошибка: ПК назначения не в сети";
            if (target.IsSession)
                return "Ошибка: на ПК назначения уже есть активная сессия";

            // Capture session data before clearing source
            var sessionType = source.SessionType;
            var limitSeconds = source.LimitSeconds;
            var paidAmount = source.PaidAmount;
            int elapsed = source.IsPaused
                ? source.AccumulatedSeconds
                : source.AccumulatedSeconds + (int)(DateTime.UtcNow - (source.SessionStart ?? DateTime.UtcNow)).TotalSeconds;
            elapsed = Math.Max(0, elapsed);

            // Lock source
            var lockCmd = JsonSerializer.Serialize(new { Type = "REMOTE_LOCK", Value = "true" });
            if (source.IsOnline)
                await Clients.Client(source.ConnectionId).SendAsync("ReceiveCommand", lockCmd);
            else
                AddPendingCommand(source.PcNumber, "REMOTE_LOCK", "true");

            // Clear source session state
            source.Status = "Заблокирован";
            source.SessionType = "";
            source.ElapsedSeconds = 0;
            source.LimitSeconds = 0;
            source.PaidAmount = 0;
            source.SessionStart = null;
            source.IsPaused = false;
            source.AccumulatedSeconds = 0;
            source.SessionId = "";
            source.DisconnectedAt = null;
            source.OfflineDecision = OfflineDecision.None;

            // Start session on target with transferred data
            var newStart = DateTime.UtcNow.AddSeconds(-elapsed);
            var startCmd = JsonSerializer.Serialize(new
            {
                Type = "START_SESSION",
                Value = sessionType,
                SessionType = sessionType,
                LimitSeconds = limitSeconds,
                PaidAmount = paidAmount,
                ElapsedSeconds = elapsed,
                ServerStartTime = newStart.ToString("o")
            });
            await Clients.Client(target.ConnectionId).SendAsync("ReceiveCommand", startCmd);

            // Update target session state
            target.SessionType = sessionType;
            target.Status = sessionType;
            target.LimitSeconds = limitSeconds;
            target.PaidAmount = paidAmount;
            target.ElapsedSeconds = elapsed;
            target.AccumulatedSeconds = elapsed;
            target.SessionStart = newStart;
            target.IsPaused = false;
            target.SessionId = "";

            SaveRegistry();
            SaveActiveSessions();
            ClientUpdated?.Invoke(source);
            ClientUpdated?.Invoke(target);

            Logger.Info($"✅ Сессия перенесена: {fromPcNumber} → {toPcNumber} ({sessionType}, прошло {elapsed}с)");
            return "OK";
        }

        public static void MarkIndividualSetting(string pcNumber, string commandType)
        {
            if (KnownClients.TryGetValue(pcNumber, out var client))
            {
                client.MarkIndividual(commandType);
                KnownClients[pcNumber] = client;
                SaveRegistryStatic();
                Logger.Info($"Индивидуальная настройка: {pcNumber} → {commandType}");
            }
        }

        public static void ClearIndividualSettings(string pcNumber)
        {
            if (KnownClients.TryGetValue(pcNumber, out var client))
            {
                client.ClearIndividual();
                KnownClients[pcNumber] = client;
                SaveRegistryStatic();
                Logger.Info($"Индивидуальные настройки сброшены: {pcNumber}");
            }
        }

        public static void SaveRegistryStatic()
        {
            try { File.WriteAllText(_registryPath, JsonSerializer.Serialize(KnownClients.Values, new JsonSerializerOptions { WriteIndented = true })); }
            catch { }
        }
    }

    public class DeletedPcRecord
    {
        public string PcNumber { get; set; } = "";
        public string MacAddress { get; set; } = "";
        public string Ip { get; set; } = "";
        public DateTime DeletedAt { get; set; }
    }

    public class SystemInfoDto
    {
        public string HostName { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public string LocalIp { get; set; } = "";
        public string MacAddress { get; set; } = "";
        public double DiskFreeGb { get; set; }
        public double UptimeHours { get; set; }
        public string ClientTimeUtc { get; set; } = "";
        public string ClientVersion { get; set; } = "";
        // ✅ Новые поля для разделения имени и номера
        public int PcNumberValue { get; set; }
        public string CustomName { get; set; } = "";
    }
}