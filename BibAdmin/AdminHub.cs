using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace BibAdmin
{
    public class AdminHub : Hub
    {
        // Хранилище всех известных клиентов
        public static ConcurrentDictionary<string, ClientState> KnownClients { get; } = new();

        // Очередь команд для оффлайн-ПК
        private static readonly ConcurrentDictionary<string, List<PendingCommand>> _pendingCommands = new();

        private static readonly string _registryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "clients.json");
        private static readonly string _pendingPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pending_commands.json");
        private static readonly string SessionsFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "active_sessions.json");

        public static event Action<ClientState>? ClientUpdated;
        public static event Action? ClientsChanged;

        // Срабатывает когда клиент уходит в оффлайн с активной сессией → показываем попап администратору
        public static event Action<ClientState>? ClientOfflineWithSession;

        // Фаза 3: расхождение времени оффлайна между клиентом и сервером
        public static event Action<string, int, int>? ClientTimeMismatch;
        private const int OfflineMismatchThreshold = 60;

        // Фаза 4: дрейф системных часов клиента (pcNumber, offsetSeconds)
        // offsetSeconds > 0 → клиент отстаёт от сервера; < 0 → опережает
        public static event Action<string, double>? ClientTimeDrift;
        private const double ClockDriftThreshold = 30.0;

        // =====================
        // Загрузка при старте
        // =====================
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
                            c.IsOnline = false;
                            c.Status = "Оффлайн";
                            c.LastSeen = DateTime.MinValue;
                            KnownClients[c.PcNumber] = c;
                        }
                        Logger.Info($"✅ Загружено {list.Count} клиентов");
                    }
                }
                catch (Exception ex) { Logger.Error($"Ошибка загрузки clients.json: {ex.Message}"); }
            }

            // Загружаем pending команды
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

        // ==========================================
        // 🔹 СОХРАНЕНИЕ АКТИВНЫХ СЕССИЙ
        // ==========================================
        public static void SaveActiveSessions()
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

        // ==========================================
        // 🔹 ЗАГРУЗКА ПРИ СТАРТЕ СЕРВЕРА
        // ==========================================
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

                    var sessionType = s.GetProperty("SessionType").GetString() ?? "";
                    if (string.IsNullOrEmpty(sessionType) || sessionType == "Заблокирован" || sessionType == "Свободный")
                        continue;

                    DateTime? sessionStart = null;
                    if (s.TryGetProperty("SessionStartUtc", out var startProp))
                    {
                        var startStr = startProp.GetString();
                        if (!string.IsNullOrEmpty(startStr))
                            sessionStart = DateTime.Parse(startStr, null, System.Globalization.DateTimeStyles.RoundtripKind); // ✅ Читаем как UTC
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
                    client.Status = isPaused ? "Пауза" : sessionType;
                    client.SessionId = sessionIdVal;
                    client.DisconnectedAt = disconnectedAt;
                    client.ElapsedAtDisconnect = elapsedAtDisconnect;
                    client.OfflineDecision = offlineDecision;

                    KnownClients[pcNumber] = client;
                    restoredCount++;
                    Logger.Info($"🔄 Восстановлена сессия: {pcNumber} | {sessionType} | {elapsedSeconds}с");
                }
                Logger.Info($"✅ Загружено {restoredCount} сессий из файла");
            }
            catch (Exception ex)
            {
                Logger.Error($"❌ LoadActiveSessions: {ex.Message}");
                Logger.Error($"Stack: {ex.StackTrace}");
            }
        }

        // =====================
        // Подключение / Отключение
        // =====================
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

                if (client.IsSession)
                {
                    // Фиксируем точный elapsed в момент разрыва (не ждём следующего UpdateStatus)
                    int elapsedNow = client.IsPaused
                        ? client.AccumulatedSeconds
                        : Math.Max(0, client.AccumulatedSeconds + (int)(DateTime.UtcNow - client.SessionStart!.Value).TotalSeconds);
                    client.ElapsedAtDisconnect = elapsedNow;
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

                // Уведомляем UI — покажет попап «Пауза/Продолжить» администратору
                if (client.DisconnectedAt.HasValue)
                    ClientOfflineWithSession?.Invoke(client);
            }
            return base.OnDisconnectedAsync(exception);
        }

        // =====================
        // Решение администратора по оффлайн-клиенту
        // =====================
        public static ClientState? SetOfflineDecision(string pcNumber, OfflineDecision decision)
        {
            if (!KnownClients.TryGetValue(pcNumber, out var client)) return null;

            client.OfflineDecision = decision;

            if (decision == OfflineDecision.Pause)
            {
                // Замораживаем таймер на значении elapsed в момент разрыва
                client.IsPaused = true;
                client.AccumulatedSeconds = client.ElapsedAtDisconnect;
                client.ElapsedSeconds = client.ElapsedAtDisconnect;
                client.Status = "Пауза";
            }

            KnownClients[pcNumber] = client;
            SaveActiveSessions();
            ClientUpdated?.Invoke(client);
            Logger.Info($"Решение администратора для {pcNumber}: {decision}, elapsed={client.ElapsedAtDisconnect}с");
            return client;
        }

        // =====================
        // Регистрация клиента
        // =====================
        public async Task<string> RegisterClient(string requestedName, SystemInfoDto info, string macAddress, bool isRestoring = false, string sessionId = "", int offlineSeconds = 0)
        {
            Logger.Info($"Регистрация: {requestedName}, MAC: {macAddress}");

            var existingByMac = KnownClients.Values.FirstOrDefault(c => c.MacAddress == macAddress);
            string finalName = existingByMac?.PcNumber ?? requestedName;

            if (existingByMac == null)
            {
                int counter = 1;
                while (KnownClients.ContainsKey(finalName))
                {
                    counter++;
                    var baseName = Regex.Replace(requestedName, @"\s+\d+$", "");
                    finalName = $"{baseName} {counter}";
                }
            }

            if (existingByMac != null && existingByMac.PcNumber != finalName)
            {
                KnownClients.TryRemove(existingByMac.PcNumber, out _);
                Logger.Info($"Удалена старая запись: {existingByMac.PcNumber}");
            }

            bool isNewClient = existingByMac == null;

            // Определяем, была ли у клиента активная сессия до переподключения
            bool hadActiveSession = existingByMac != null &&
                !string.IsNullOrEmpty(existingByMac.SessionType) &&
                existingByMac.SessionType != "Заблокирован" &&
                existingByMac.SessionType != "Свободный" &&
                existingByMac.SessionStart.HasValue;

            // Восстанавливаем корректный статус
            string restoredStatus = hadActiveSession
                ? (existingByMac!.IsPaused ? "Пауза" : existingByMac.SessionType)
                : "Заблокирован";

            var state = new ClientState
            {
                PcNumber = finalName,
                ConnectionId = Context.ConnectionId,
                Ip = info.LocalIp,
                MacAddress = macAddress,
                OsVersion = info.OsVersion,
                DiskFreeGb = info.DiskFreeGb,
                UptimeHours = info.UptimeHours,
                IsOnline = true,
                LastSeen = DateTime.UtcNow,
                Status = restoredStatus,
                SessionType = existingByMac?.SessionType ?? "",
                SessionStart = existingByMac?.SessionStart,
                LimitSeconds = existingByMac?.LimitSeconds ?? 0,
                ElapsedSeconds = existingByMac?.ElapsedSeconds ?? 0,
                PaidAmount = existingByMac?.PaidAmount ?? 0,
                IsPaused = existingByMac?.IsPaused ?? false,
                AccumulatedSeconds = existingByMac?.AccumulatedSeconds ?? 0,
                HasIndividualSettings = existingByMac?.HasIndividualSettings ?? false,
                IndividualSettingKeys = existingByMac?.IndividualSettingKeys ?? new(),
                // Сохраняем SessionId от клиента; при реконнекте обновится ниже
                SessionId = !string.IsNullOrEmpty(sessionId) ? sessionId : existingByMac?.SessionId ?? "",
                // DisconnectedAt сбрасывается — клиент снова онлайн
                DisconnectedAt = null
            };

            KnownClients.AddOrUpdate(finalName, state, (_, _) => state);
            SaveRegistry();

            ClientUpdated?.Invoke(state);
            ClientsChanged?.Invoke();

            // Логируем расхождение SessionId — в Фазе 2 здесь будет принудительная блокировка
            if (!string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(existingByMac?.SessionId)
                && existingByMac.SessionId != sessionId && hadActiveSession)
                Logger.Warn($"⚠️ SessionId мismatch: {finalName} прислал {sessionId[..8]}…, сервер помнит {existingByMac.SessionId[..8]}…");

            Logger.Info($"Клиент зарегистрирован: {finalName}{(hadActiveSession ? $" (восстановлена сессия: {state.SessionType})" : "")}");
            if (offlineSeconds > 0)
                Logger.Info($"🕐 Клиент {finalName} сообщает о {offlineSeconds}с оффлайна");

            if (isNewClient)
            {
                await SendGlobalSettingsToClient(finalName);
                Logger.Info($"Глобальные настройки отправлены новому ПК: {finalName}");
            }

            await FlushPendingCommands(finalName);

            // Фаза 4: синхронизация системных часов
            if (!string.IsNullOrEmpty(info.ClientTimeUtc) &&
                DateTime.TryParse(info.ClientTimeUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var clientClock))
            {
                double offsetSeconds = (DateTime.UtcNow - clientClock).TotalSeconds;

                // Всегда отправляем смещение — клиент применяет его к таймеру сессии
                var offsetCmd = new { Type = "SET_TIME_OFFSET", Value = offsetSeconds.ToString("F2") };
                await Clients.Client(Context.ConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(offsetCmd));

                if (Math.Abs(offsetSeconds) > ClockDriftThreshold)
                {
                    string direction = offsetSeconds > 0 ? "отстаёт от сервера" : "опережает сервер";
                    Logger.Warn($"⚠️ CLOCK DRIFT {finalName}: клиент {direction} на {Math.Abs(offsetSeconds):F0}с");
                    ClientTimeDrift?.Invoke(finalName, offsetSeconds);

                    // Сообщаем клиенту показать предупреждение
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
                // Фаза 3: верификация времени оффлайна
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
                    // Администратор нажал «Пауза задним числом» — отправляем замороженное время
                    elapsedToSend = client.ElapsedAtDisconnect;
                    sendPause = true;
                    Logger.Info($"✅ {finalName}: применяем решение ПАУЗА, elapsed={elapsedToSend}с");
                }
                else if (!isRestoring || adminChoseContinue)
                {
                    // LAN-реконнект (сессия шла на клиенте) ИЛИ админ явно выбрал «Продолжить»
                    // → диктуем актуальное серверное время
                    int serverElapsed = client.AccumulatedSeconds +
                        (client.IsPaused ? 0 : (int)(DateTime.UtcNow - client.SessionStart!.Value).TotalSeconds);
                    elapsedToSend = Math.Max(0, serverElapsed);
                    sendPause = client.IsPaused;
                    Logger.Info($"🔄 {finalName}: LAN/Continue, elapsed={elapsedToSend}с");
                }
                else
                {
                    // Перезапуск приложения (выключение ПК), решения администратора нет →
                    // умная защита: не считаем время выключения против пользователя
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

                if (sendPause)
                {
                    await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand",
                        JsonSerializer.Serialize(new { Type = "PAUSE_SESSION", Value = "" }));
                }

                // Сбрасываем решение после применения
                client.OfflineDecision = OfflineDecision.None;
                client.DisconnectedAt = null;
                KnownClients[finalName] = client;

                await Task.Delay(1000);
                await SyncSessionTime(finalName, force: true);
            }

            return finalName;
        }

        // ✅ Отправить глобальные настройки с авто-очисткой индивидуальных флагов
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

        // Отправить накопленные pending команды
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
                    var json = JsonSerializer.Serialize(new { cmd.Type, cmd.Value });
                    await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                }
                catch (Exception ex) { Logger.Error($"Ошибка pending команды: {ex.Message}"); }
            }

            _pendingCommands.TryRemove(pcNumber, out _);
            SavePending();
            Logger.Info($"Pending команды очищены для {pcNumber}");
        }

        // =====================
        // Добавить в очередь для оффлайн ПК
        // =====================
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

        // =====================
        // Heartbeat и статус
        // =====================
        public async Task SendHeartbeat(string pcNumber)
        {
            if (KnownClients.TryGetValue(pcNumber, out var client))
            {
                client.IsOnline = true;
                client.LastSeen = DateTime.UtcNow;
                KnownClients[pcNumber] = client;
            }
        }

        // ==========================================
        // 🔹 ОБНОВЛЕНИЕ СТАТУСА (с защитой от перезагрузки)
        // ==========================================
        public async Task UpdateStatus(string pcNumber, string status, string sessionType, int elapsedSeconds)
        {
            if (!KnownClients.TryGetValue(pcNumber, out var client)) return;

            client.LastSeen = DateTime.UtcNow;
            client.IsOnline = true;

            // 🔑 ЗАЩИТА: если есть активная сессия, а клиент прислал 0 — игнорируем
            if (client.SessionStart.HasValue && elapsedSeconds == 0)
            {
                if (status != "Заблокирован" && status != "Свободный")
                    client.Status = status;
                KnownClients[pcNumber] = client;
                ClientUpdated?.Invoke(client);
                return;
            }

            if (elapsedSeconds > 0)
            {
                client.ElapsedSeconds = elapsedSeconds;
                // Постоянно сдвигаем точку старта, чтобы она была точной
                client.SessionStart = DateTime.UtcNow.AddSeconds(-elapsedSeconds);
                // После обновления SessionStart переходим на «чистую» модель:
                // elapsed = 0 + (now − SessionStart). AccumulatedSeconds должен быть 0,
                // иначе Timer_Tick посчитает его дважды (двойной счёт после пауз).
                // Исключение: если сейчас на паузе — AccumulatedSeconds уже содержит
                // правильное замороженное значение и его трогать нельзя.
                if (!client.IsPaused)
                    client.AccumulatedSeconds = 0;
            }

            // Управляем IsPaused только когда клиент явно отправляет статус "Пауза".
            // НЕ снимаем паузу на основе других статусов клиента — администратор является
            // источником истины для паузы (только PauseSession_Click / RESUME_SESSION).
            if (status == "Пауза")
            {
                client.IsPaused = true;
                client.AccumulatedSeconds = elapsedSeconds;
            }

            if (!string.IsNullOrEmpty(sessionType)) client.SessionType = sessionType;
            client.Status = status;
            KnownClients[pcNumber] = client;
            ClientUpdated?.Invoke(client);

            Logger.Info($"Статус обновлён: {pcNumber} → {status} (elapsed={client.ElapsedSeconds}с)");

            if (client.IsSession || status == "Пауза") SaveActiveSessions();
        }

        // ==========================================
        // 🔹 СИНХРОНИЗАЦИЯ ВРЕМЕНИ (умная)
        // ==========================================
        public async Task SyncSessionTime(string pcNumber, bool force = false) // ✅ Добавлен параметр force
        {
            if (!KnownClients.TryGetValue(pcNumber, out var client)) return;
            if (client.IsPaused || !(client.IsSession || client.Status == "Пауза") || !client.SessionStart.HasValue) return;

            // Не синхронизируем, если клиент только что подключился (<10 сек)
            if (!force && DateTime.UtcNow - client.LastSeen < TimeSpan.FromSeconds(10)) return; // ✅ Игнорируем задержку, если force = true

            int serverElapsed = client.AccumulatedSeconds + (int)(DateTime.UtcNow - client.SessionStart.Value).TotalSeconds;
            serverElapsed = Math.Max(0, serverElapsed);

            int diff = Math.Abs(serverElapsed - client.ElapsedSeconds);
            if (!force && diff < 10) return; // ✅ Добавляем !force: если это принудительная синхронизация, игнорируем лимит

            client.ElapsedSeconds = serverElapsed;
            KnownClients[pcNumber] = client;
            SaveActiveSessions();

            var cmd = new { Type = "SESSION_TIME_SYNC", Value = serverElapsed.ToString(), IsPaused = client.IsPaused };
            var json = JsonSerializer.Serialize(cmd);
            await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);

            Logger.Info($"🔄 SyncSessionTime: {pcNumber} → {serverElapsed}с (расхождение {diff}с)");
        }

        // =====================
        // Переименование
        // =====================
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

        // =====================
        // Отправка команд
        // =====================
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

        // =====================
        // Загрузка файлов (фон)
        // =====================
        public async Task UploadFile(string fileName, byte[] fileData, string targetPc)
        {
            try
            {
                var filesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Files");
                Directory.CreateDirectory(filesDir);
                var filePath = Path.Combine(filesDir, fileName);

                if (File.Exists(filePath))
                {
                    try { File.Delete(filePath); }
                    catch (Exception delEx)
                    {
                        Logger.Warn($"Не удалось удалить старый файл: {delEx.Message}");
                        fileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(fileName)}";
                        filePath = Path.Combine(filesDir, fileName);
                    }
                }

                await File.WriteAllBytesAsync(filePath, fileData);

                var global = GlobalSettings.Load();
                global.BackgroundFileName = fileName;
                global.Save();
                Logger.Info($"Фон сохранён: {fileName}");

                var command = new { Type = "SET_BACKGROUND", Value = fileName };
                var json = JsonSerializer.Serialize(command);

                if (targetPc == "*") await SendCommandToAll(json);
                else await SendCommand(targetPc, json);

                Logger.Info($"Файл сохранён: {fileName} → {targetPc}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка UploadFile: {ex.Message}");
                Logger.Error($"Stack: {ex.StackTrace}");
                throw;
            }
        }

        // =====================
        // Методы для индивидуальных настроек
        // =====================
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

        private static void SaveRegistryStatic()
        {
            try { File.WriteAllText(_registryPath, JsonSerializer.Serialize(KnownClients.Values, new JsonSerializerOptions { WriteIndented = true })); }
            catch { }
        }
    }

    public class SystemInfoDto
    {
        public string HostName { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public string LocalIp { get; set; } = "";
        public string MacAddress { get; set; } = "";
        public double DiskFreeGb { get; set; }
        public double UptimeHours { get; set; }
        public string ClientTimeUtc { get; set; } = ""; // Фаза 4: локальное время клиента
    }
}