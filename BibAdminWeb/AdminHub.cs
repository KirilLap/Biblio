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

        // Р’С‹Р·РѕРІ СЃРѕР±С‹С‚РёР№ РёР· РІРЅРµС€РЅРёС… РєР»Р°СЃСЃРѕРІ (OperatorHub Рё С‚.Рї.)
        public static void RaiseClientUpdated(ClientState cs) => ClientUpdated?.Invoke(cs);
        public static event Action<string, int, int>? ClientTimeMismatch;
        private const int OfflineMismatchThreshold = 60;
        public static event Action<string, double>? ClientTimeDrift;
        private const double ClockDriftThreshold = 30.0;
        // registeredAs, requestedAs, mac, requestedPcNumberValue, requestedCustomName
        public static event Action<string, string, string, int, string>? ClientNameConflict;

        // вњ… Р”Р›РЇ Р—РђР©РРўР« РћРў Р”РЈР‘Р›Р•Р™ РЈР’Р•Р”РћРњР›Р•РќРР™
        private static readonly ConcurrentDictionary<string, DateTime> _lastOfflineAlert = new();

        // MACs РґР»СЏ РєРѕС‚РѕСЂС‹С… РєРѕРЅС„Р»РёРєС‚ СѓР¶Рµ РїРѕРєР°Р·Р°РЅ РІ СЌС‚РѕР№ СЃРµСЃСЃРёРё (РЅРµ РїРѕРєР°Р·С‹РІР°С‚СЊ РїРѕРІС‚РѕСЂРЅРѕ)
        private static readonly HashSet<string> _shownConflicts = new();

        // РћР¶РёРґР°РЅРёРµ СЂРµС€РµРЅРёСЏ Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР° РїРѕ РєРѕРЅС„Р»РёРєС‚Сѓ РёРјС‘РЅ
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<(int PcNumberValue, string CustomName)?>> _conflictDecisions = new();

        // Р›РѕРі СѓРґР°Р»С‘РЅРЅС‹С… РџРљ
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
            catch (Exception ex) { Logger.Error($"РћС€РёР±РєР° Р·Р°РіСЂСѓР·РєРё deleted_pcs.json: {ex.Message}"); }
        }

        private static void SaveDeletedPcs()
        {
            try { File.WriteAllText(_deletedPcsPath, JsonSerializer.Serialize(DeletedPcs, new JsonSerializerOptions { WriteIndented = true })); }
            catch { }
        }

        // Р’С‹Р·С‹РІР°РµС‚СЃСЏ РёР· UI: "Р”Р°" в†’ РїРµСЂРµРґР°С‘Рј РЅРѕРІС‹Рµ РґР°РЅРЅС‹Рµ, "РќРµС‚" в†’ null
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
            Logger.Info($"РџРљ СѓРґР°Р»С‘РЅ РёР· СЂРµРµСЃС‚СЂР°: {pcNumber}");
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
                            c.IsOnline = false;
                            c.Status = c.IsPaused ? "РџР°СѓР·Р°" : "РћС„С„Р»Р°Р№РЅ";
                            c.LastSeen = DateTime.MinValue;
                            KnownClients[c.PcNumber] = c;
                        }
                        Logger.Info($"вњ… Р—Р°РіСЂСѓР¶РµРЅРѕ {list.Count} РєР»РёРµРЅС‚РѕРІ");
                    }
                }
                catch (Exception ex) { Logger.Error($"РћС€РёР±РєР° Р·Р°РіСЂСѓР·РєРё clients.json: {ex.Message}"); }
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
                    Logger.Info($"Р—Р°РіСЂСѓР¶РµРЅРѕ pending РєРѕРјР°РЅРґ: {_pendingCommands.Sum(x => x.Value.Count)}");
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
            catch (Exception ex) { Logger.Error($"вќЊ SaveActiveSessions: {ex.Message}"); }
        }

        /// <summary>
        /// РќРѕСЂРјР°Р»РёР·СѓРµС‚ С‚РёРї СЃРµСЃСЃРёРё: "РџРѕ РІСЂРµРјРµРЅРё" Рё "РџРѕ РґРµРЅСЊРіР°Рј" в†’ "Р›РёРјРёС‚".
        /// РћР±РµСЃРїРµС‡РёРІР°РµС‚ СЃРѕРІРјРµСЃС‚РёРјРѕСЃС‚СЊ СЃРѕ СЃС‚Р°СЂС‹РјРё СЃРѕС…СЂР°РЅС‘РЅРЅС‹РјРё РґР°РЅРЅС‹РјРё.
        /// </summary>
        private static string NormalizeSessionType(string sessionType) =>
            sessionType is "РџРѕ РІСЂРµРјРµРЅРё" or "РџРѕ РґРµРЅСЊРіР°Рј" ? "Р›РёРјРёС‚" : sessionType;

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
                        Logger.Warn($"вљ пёЏ РљР»РёРµРЅС‚ {pcNumber} РёР· active_sessions РЅРµ РЅР°Р№РґРµРЅ");
                        continue;
                    }

                    // РќРѕСЂРјР°Р»РёР·СѓРµРј: СЃС‚Р°СЂС‹Рµ "РџРѕ РІСЂРµРјРµРЅРё"/"РџРѕ РґРµРЅСЊРіР°Рј" в†’ "Р›РёРјРёС‚"
                    var sessionType = NormalizeSessionType(s.GetProperty("SessionType").GetString() ?? "");
                    if (string.IsNullOrEmpty(sessionType) || sessionType == "Р—Р°Р±Р»РѕРєРёСЂРѕРІР°РЅ" || sessionType == "РЎРІРѕР±РѕРґРЅС‹Р№")
                        continue;

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
                        client.Status = "РџР°СѓР·Р°";
                    else
                        client.Status = sessionType;

                    client.SessionId = sessionIdVal;
                    client.DisconnectedAt = disconnectedAt;
                    client.ElapsedAtDisconnect = elapsedAtDisconnect; // вњ… РЎРћРҐР РђРќРЇР•Рњ!
                    client.OfflineDecision = offlineDecision;

                    KnownClients[pcNumber] = client;
                    restoredCount++;
                    Logger.Info($"рџ”„ Р’РѕСЃСЃС‚Р°РЅРѕРІР»РµРЅР° СЃРµСЃСЃРёСЏ: {pcNumber} | {sessionType} | {elapsedSeconds}СЃ");
                }
                Logger.Info($"вњ… Р—Р°РіСЂСѓР¶РµРЅРѕ {restoredCount} СЃРµСЃСЃРёР№ РёР· С„Р°Р№Р»Р°");
            }
            catch (Exception ex)
            {
                Logger.Error($"вќЊ LoadActiveSessions: {ex.Message}");
                Logger.Error($"Stack: {ex.StackTrace}");
            }
        }

        public override Task OnConnectedAsync()
        {
            Logger.Info($"РљР»РёРµРЅС‚ РїРѕРґРєР»СЋС‡РёР»СЃСЏ: {Context.ConnectionId}");
            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            var client = KnownClients.Values.FirstOrDefault(c => c.ConnectionId == Context.ConnectionId);
            if (client != null)
            {
                client.IsOnline = false;
                client.Status = "РћС„С„Р»Р°Р№РЅ";
                client.LastSeen = DateTime.UtcNow;

                if (client.IsSession)
                {
                    int elapsedNow = client.IsPaused
                        ? client.AccumulatedSeconds
                        : Math.Max(0, client.AccumulatedSeconds + (int)(DateTime.UtcNow - client.SessionStart!.Value).TotalSeconds);
                    client.ElapsedAtDisconnect = elapsedNow; // вњ… РЎРћРҐР РђРќРЇР•Рњ РџР•Р Р•Р” РћРўРЎРћР•Р”РРќР•РќРР•Рњ
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
                Logger.Info($"РљР»РёРµРЅС‚ РѕС‚РєР»СЋС‡РёР»СЃСЏ: {client.PcNumber}{(client.DisconnectedAt.HasValue ? $" (Р°РєС‚РёРІРЅР°СЏ СЃРµСЃСЃРёСЏ, elapsed={client.ElapsedAtDisconnect}СЃ)" : "")}");

                // вњ… Р—РђР©РРўРђ РћРў Р”РЈР‘Р›Р•Р™: РЅРµ С‡Р°С‰Рµ 1 СЂР°Р·Р° РІ 5 РјРёРЅСѓС‚
                if (client.DisconnectedAt.HasValue)
                {
                    // Р”РµРґСѓРї 60 СЃРµРє вЂ” Р·Р°С‰РёС‚Р° РѕС‚ rapid-disconnect, СЃР±СЂР°СЃС‹РІР°РµС‚СЃСЏ РїСЂРё СЂРµРєРѕРЅРЅРµРєС‚Рµ
                    if (_lastOfflineAlert.TryGetValue(client.PcNumber, out var lastAlert) &&
                        DateTime.UtcNow - lastAlert < TimeSpan.FromSeconds(60))
                    {
                        Logger.Info($"вЏ­пёЏ РџСЂРѕРїСѓСЃРє РґСѓР±Р»СЏ СѓРІРµРґРѕРјР»РµРЅРёСЏ РґР»СЏ {client.PcNumber} (< 60СЃ)");
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
                client.Status = "РџР°СѓР·Р°";
            }
            else if (decision == OfflineDecision.Continue)
            {
                // РЇРІРЅС‹Р№ Continue вЂ” СЃР±СЂР°СЃС‹РІР°РµРј РїР°СѓР·Сѓ, РІРѕСЃСЃС‚Р°РЅР°РІР»РёРІР°РµРј СЃС‚Р°С‚СѓСЃ РёР· С‚РёРїР° СЃРµСЃСЃРёРё
                client.IsPaused = false;
                if (!string.IsNullOrEmpty(client.SessionType) &&
                    client.SessionType != "Р—Р°Р±Р»РѕРєРёСЂРѕРІР°РЅ" &&
                    client.SessionType != "РЎРІРѕР±РѕРґРЅС‹Р№")
                    client.Status = client.SessionType;
            }

            KnownClients[pcNumber] = client;
            SaveActiveSessions();
            ClientUpdated?.Invoke(client);
            Logger.Info($"Р РµС€РµРЅРёРµ Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР° РґР»СЏ {pcNumber}: {decision}, elapsed={client.ElapsedAtDisconnect}СЃ");
            return client;
        }

        public async Task<string> RegisterClient(SystemInfoDto info, string macAddress, bool isRestoring = false, string sessionId = "", int offlineSeconds = 0)
        {
            Logger.Info($"Р РµРіРёСЃС‚СЂР°С†РёСЏ: РџРљ {info.PcNumberValue}, MAC: {macAddress}");

            var existingByMac = KnownClients.Values.FirstOrDefault(c => c.MacAddress == macAddress);
            
            // РћРїСЂРµРґРµР»СЏРµРј С„РёРЅР°Р»СЊРЅРѕРµ РёРјСЏ: РµСЃР»Рё РєР»РёРµРЅС‚ СѓР¶Рµ РёР·РІРµСЃС‚РµРЅ РїРѕ MAC - Р±РµСЂРµРј РµРіРѕ PcNumberValue Рё CustomName
            int finalPcNumberValue = info.PcNumberValue;
            string finalCustomName = info.CustomName;
            
            if (existingByMac != null)
            {
                // РљР»РёРµРЅС‚ СѓР¶Рµ РёР·РІРµСЃС‚РµРЅ - СЃРѕС…СЂР°РЅСЏРµРј РµРіРѕ РЅР°СЃС‚СЂРѕР№РєРё РёРјРµРЅРё
                finalPcNumberValue = existingByMac.PcNumberValue;
                finalCustomName = existingByMac.CustomName;

                // Р•СЃР»Рё РєР»РёРµРЅС‚ РїРѕРґРєР»СЋС‡РёР»СЃСЏ РїРѕРґ РґСЂСѓРіРёРј РёРјРµРЅРµРј вЂ” Р¶РґС‘Рј СЂРµС€РµРЅРёСЏ Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР°
                string requestedName = string.IsNullOrEmpty(info.CustomName)
                    ? $"РџРљ {info.PcNumberValue}"
                    : $"{info.CustomName} {info.PcNumberValue}";
                if (requestedName != existingByMac.PcNumber && !_shownConflicts.Contains(macAddress))
                {
                    _shownConflicts.Add(macAddress);
                    Logger.Warn($"вљ пёЏ РљРѕРЅС„Р»РёРєС‚ РёРјС‘РЅ: MAC {macAddress} Р±С‹Р» '{existingByMac.PcNumber}', РїРѕРґРєР»СЋС‡Р°РµС‚СЃСЏ РєР°Рє '{requestedName}'");

                    var tcs = new TaskCompletionSource<(int PcNumberValue, string CustomName)?>();
                    _conflictDecisions[macAddress] = tcs;
                    ClientNameConflict?.Invoke(existingByMac.PcNumber, requestedName, macAddress, info.PcNumberValue, info.CustomName);

                    try
                    {
                        // Р–РґС‘Рј РїРѕРєР° Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂ РЅР°Р¶РјС‘С‚ Р”Р°/РќРµС‚ (РјР°РєСЃ 60 СЃРµРє)
                        var decision = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60));
                        if (decision.HasValue)
                        {
                            finalPcNumberValue = decision.Value.PcNumberValue;
                            finalCustomName = decision.Value.CustomName;
                            _shownConflicts.Remove(macAddress); // СЃРЅСЏР»Рё РєРѕРЅС„Р»РёРєС‚ вЂ” СЃР±СЂРѕСЃ, РёРјСЏ Р±СѓРґРµС‚ РїСЂР°РІРёР»СЊРЅС‹Рј
                            Logger.Info($"вњ… РђРґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂ РїСЂРёРЅСЏР»: MAC {macAddress} в†’ РџРљ {finalPcNumberValue}");
                        }
                        else
                        {
                            Logger.Info($"рџљ« РђРґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂ РѕС‚РєР»РѕРЅРёР» РїРµСЂРµРёРјРµРЅРѕРІР°РЅРёРµ MAC {macAddress}");
                        }
                    }
                    catch (TimeoutException)
                    {
                        Logger.Warn($"вЏ° РўР°Р№Рј-Р°СѓС‚ РѕР¶РёРґР°РЅРёСЏ СЂРµС€РµРЅРёСЏ РїРѕ РєРѕРЅС„Р»РёРєС‚Сѓ {macAddress}, РёСЃРїРѕР»СЊР·СѓРµРј СЃС‚Р°СЂРѕРµ РёРјСЏ");
                        _conflictDecisions.TryRemove(macAddress, out _);
                    }
                }
            }
            
            string finalName = string.IsNullOrEmpty(finalCustomName) ? $"РџРљ {finalPcNumberValue}" : $"{finalCustomName} {finalPcNumberValue}";

            if (existingByMac == null)
            {
                // РќРѕРІС‹Р№ РєР»РёРµРЅС‚ - РїСЂРѕРІРµСЂСЏРµРј РЅРµС‚ Р»Рё РєРѕРЅС„Р»РёРєС‚Р° РїРѕ РёРјРµРЅРё
                while (KnownClients.ContainsKey(finalName))
                {
                    finalPcNumberValue++;
                    finalName = $"РџРљ {finalPcNumberValue}";
                }
            }

            if (existingByMac != null && existingByMac.PcNumber != finalName)
            {
                KnownClients.TryRemove(existingByMac.PcNumber, out _);
                Logger.Info($"РЈРґР°Р»РµРЅР° СЃС‚Р°СЂР°СЏ Р·Р°РїРёСЃСЊ: {existingByMac.PcNumber}");
            }

            bool isNewClient = existingByMac == null;

            bool hadActiveSession = existingByMac != null &&
                !string.IsNullOrEmpty(existingByMac.SessionType) &&
                existingByMac.SessionType != "Р—Р°Р±Р»РѕРєРёСЂРѕРІР°РЅ" &&
                existingByMac.SessionType != "РЎРІРѕР±РѕРґРЅС‹Р№" &&
                existingByMac.SessionStart.HasValue;

            // РќРѕСЂРјР°Р»РёР·СѓРµРј С‚РёРї СЃРµСЃСЃРёРё РёР· СЃРѕС…СЂР°РЅС‘РЅРЅС‹С… РґР°РЅРЅС‹С… (СЃРѕРІРјРµСЃС‚РёРјРѕСЃС‚СЊ СЃРѕ СЃС‚Р°СЂС‹РјРё Р·Р°РїРёСЃСЏРјРё)
            var restoredSessionType = NormalizeSessionType(existingByMac?.SessionType ?? "");
            var restoredStatus = (existingByMac?.IsPaused == true) ? "РџР°СѓР·Р°"
                : (hadActiveSession ? restoredSessionType : "Р—Р°Р±Р»РѕРєРёСЂРѕРІР°РЅ");

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
                ElapsedAtDisconnect = existingByMac?.ElapsedAtDisconnect ?? 0, // вњ… РљРћРџРР РЈР•Рњ!
            };

            KnownClients.AddOrUpdate(finalName, state, (_, _) => state);
            SaveRegistry();

            // РќР• СЃР±СЂР°СЃС‹РІР°РµРј _lastOfflineAlert РїСЂРё СЂРµРєРѕРЅРЅРµРєС‚Рµ: РµСЃР»Рё СЃРµС‚СЊ РЅРµСЃС‚Р°Р±РёР»СЊРЅР°,
            // РєР»РёРµРЅС‚ РјРѕР¶РµС‚ СѓСЃРїРµС‚СЊ Р·Р°СЂРµРіРёСЃС‚СЂРёСЂРѕРІР°С‚СЊСЃСЏ Рё С‚СѓС‚ Р¶Рµ СѓРїР°СЃС‚СЊ СЃРЅРѕРІР° вЂ” С‡С‚Рѕ РґР°С‘С‚
            // РґСѓР±Р»РёСЂСѓСЋС‰РµРµ СѓРІРµРґРѕРјР»РµРЅРёРµ. 60-СЃРµРєСѓРЅРґРЅРѕРµ РѕРєРЅРѕ РґРµРґСѓРїР° РёСЃС‚РµС‡С‘С‚ СЃР°РјРѕ.
            // РЈРІРµРґРѕРјР»РµРЅРёРµ РїСЂРё СЃР»РµРґСѓСЋС‰РµРј СЃС‚Р°Р±РёР»СЊРЅРѕРј РѕР±СЂС‹РІРµ Р±СѓРґРµС‚ СЃРіРµРЅРµСЂРёСЂРѕРІР°РЅРѕ РєРѕСЂСЂРµРєС‚РЅРѕ.

            ClientUpdated?.Invoke(state);
            ClientsChanged?.Invoke();

            if (!string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(existingByMac?.SessionId)
                && existingByMac.SessionId != sessionId && hadActiveSession)
                Logger.Warn($"вљ пёЏ SessionId Рјismatch: {finalName} РїСЂРёСЃР»Р°Р» {sessionId[..8]}вЂ¦, СЃРµСЂРІРµСЂ РїРѕРјРЅРёС‚ {existingByMac.SessionId[..8]}вЂ¦");

            Logger.Info($"РљР»РёРµРЅС‚ Р·Р°СЂРµРіРёСЃС‚СЂРёСЂРѕРІР°РЅ: {finalName}{(hadActiveSession ? $" (РІРѕСЃСЃС‚Р°РЅРѕРІР»РµРЅР° СЃРµСЃСЃРёСЏ: {state.SessionType})" : "")}");
            if (offlineSeconds > 0)
                Logger.Info($"рџ•ђ РљР»РёРµРЅС‚ {finalName} СЃРѕРѕР±С‰Р°РµС‚ Рѕ {offlineSeconds}СЃ РѕС„С„Р»Р°Р№РЅР°");

            // Р’СЃРµРіРґР° РѕС‚РїСЂР°РІР»СЏРµРј РіР»РѕР±Р°Р»СЊРЅС‹Рµ РЅР°СЃС‚СЂРѕР№РєРё вЂ” Рё РЅРѕРІС‹Рј, Рё РїРµСЂРµРїРѕРґРєР»СЋС‡РёРІС€РёРјСЃСЏ РєР»РёРµРЅС‚Р°Рј.
            // РњРµС‚РѕРґ РІРЅСѓС‚СЂРё РїСЂРѕРІРµСЂСЏРµС‚ РёРЅРґРёРІРёРґСѓР°Р»СЊРЅС‹Рµ РЅР°СЃС‚СЂРѕР№РєРё Рё РїСЂРѕРїСѓСЃРєР°РµС‚ РёС….
            await SendGlobalSettingsToClient(finalName);
            Logger.Info($"Р“Р»РѕР±Р°Р»СЊРЅС‹Рµ РЅР°СЃС‚СЂРѕР№РєРё РѕС‚РїСЂР°РІР»РµРЅС‹ РџРљ: {finalName} (РЅРѕРІС‹Р№: {isNewClient})");

            await FlushPendingCommands(finalName);

            if (!string.IsNullOrEmpty(info.ClientTimeUtc) &&
                DateTime.TryParse(info.ClientTimeUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var clientClock))
            {
                double offsetSeconds = (DateTime.UtcNow - clientClock).TotalSeconds;

                var offsetCmd = new { Type = "SET_TIME_OFFSET", Value = offsetSeconds.ToString("F2") };
                await Clients.Client(Context.ConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(offsetCmd));

                if (Math.Abs(offsetSeconds) > ClockDriftThreshold)
                {
                    string direction = offsetSeconds > 0 ? "РѕС‚СЃС‚Р°С‘С‚ РѕС‚ СЃРµСЂРІРµСЂР°" : "РѕРїРµСЂРµР¶Р°РµС‚ СЃРµСЂРІРµСЂ";
                    Logger.Warn($"вљ пёЏ CLOCK DRIFT {finalName}: РєР»РёРµРЅС‚ {direction} РЅР° {Math.Abs(offsetSeconds):F0}СЃ");
                    ClientTimeDrift?.Invoke(finalName, offsetSeconds);

                    var mismatchCmd = new { Type = "CLOCK_MISMATCH", Value = offsetSeconds.ToString("F2") };
                    await Clients.Client(Context.ConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(mismatchCmd));
                }
                else
                {
                    Logger.Info($"вњ… Р§Р°СЃС‹ {finalName}: СЂР°СЃС…РѕР¶РґРµРЅРёРµ {offsetSeconds:F1}СЃ вЂ” РІ РЅРѕСЂРјРµ");
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
                        Logger.Warn($"вљ пёЏ TIME MISMATCH {finalName}: РєР»РёРµРЅС‚={offlineSeconds}СЃ, СЃРµСЂРІРµСЂ={serverOfflineSecs}СЃ, СЂР°СЃС…РѕР¶РґРµРЅРёРµ={mismatch}СЃ");
                        ClientTimeMismatch?.Invoke(finalName, offlineSeconds, serverOfflineSecs);
                    }
                    else
                    {
                        Logger.Info($"вњ… Р’РµСЂРёС„РёРєР°С†РёСЏ РѕС„С„Р»Р°Р№РЅР° {finalName}: РєР»РёРµРЅС‚={offlineSeconds}СЃ в‰€ СЃРµСЂРІРµСЂ={serverOfflineSecs}СЃ вњ“");
                    }
                }

                bool adminChosePause = client.OfflineDecision == OfflineDecision.Pause;
                bool adminChoseContinue = client.OfflineDecision == OfflineDecision.Continue;

                int elapsedToSend;
                bool sendPause;

                if (adminChosePause)
                {
                    elapsedToSend = client.ElapsedAtDisconnect; // вњ… РРЎРџРћР›Р¬Р—РЈР•Рњ РЎРћРҐР РђРќРЃРќРќРћР• Р—РќРђР§Р•РќРР•
                    sendPause = true;

                    client.Status = "РџР°СѓР·Р°";
                    client.IsPaused = true;
                    KnownClients[finalName] = client;
                    ClientUpdated?.Invoke(client);
                    Logger.Info($"вњ… {finalName}: РїСЂРёРјРµРЅСЏРµРј СЂРµС€РµРЅРёРµ РџРђРЈР—Рђ, elapsed={elapsedToSend}СЃ");
                }
                else if (!isRestoring || adminChoseContinue)
                {
                    int serverElapsed = client.AccumulatedSeconds +
                        (client.IsPaused ? 0 : (int)(DateTime.UtcNow - client.SessionStart!.Value).TotalSeconds);
                    elapsedToSend = Math.Max(0, serverElapsed);
                    sendPause = client.IsPaused;
                    Logger.Info($"рџ”„ {finalName}: LAN/Continue, elapsed={elapsedToSend}СЃ");
                }
                else
                {
                    int activePart = Math.Max(0, client.ElapsedSeconds - client.AccumulatedSeconds);
                    client.SessionStart = DateTime.UtcNow.AddSeconds(-activePart);
                    KnownClients[finalName] = client;
                    elapsedToSend = client.ElapsedSeconds;
                    sendPause = client.IsPaused;
                    Logger.Info($"рџ›ЎпёЏ {finalName}: smart protection, elapsed={elapsedToSend}СЃ");
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
                            Logger.Info($"РђРІС‚Рѕ-РѕС‡РёСЃС‚РєР°: {pcNumber} в†’ {cmd.Type}");
                        }
                        else { continue; }
                    }
                    var json = JsonSerializer.Serialize(new { cmd.Type, cmd.Value });
                    await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                }

                if (settingsChanged) SaveRegistryStatic();
            }
            catch (Exception ex) { Logger.Error($"РћС€РёР±РєР° РѕС‚РїСЂР°РІРєРё РіР»РѕР±Р°Р»СЊРЅС‹С… РЅР°СЃС‚СЂРѕРµРє: {ex.Message}"); }
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

            Logger.Info($"РћС‚РїСЂР°РІРєР° {commands.Count} pending РєРѕРјР°РЅРґ в†’ {pcNumber}");

            foreach (var cmd in commands)
            {
                try
                {
                    if (client.IsIndividual(cmd.Type)) continue;

                    if (cmd.Type == "REMOTE_LOCK" &&
                        !string.IsNullOrEmpty(client.SessionType) &&
                        client.SessionStart.HasValue)
                    {
                        Logger.Info($"вЏ­пёЏ РџСЂРѕРїСѓСЃРє REMOTE_LOCK РґР»СЏ {pcNumber} вЂ” РµСЃС‚СЊ Р°РєС‚РёРІРЅР°СЏ СЃРµСЃСЃРёСЏ ({client.SessionType})");
                        continue;
                    }

                    var json = JsonSerializer.Serialize(new { cmd.Type, cmd.Value });
                    await Clients.Client(client.ConnectionId).SendAsync("ReceiveCommand", json);
                }
                catch (Exception ex) { Logger.Error($"РћС€РёР±РєР° pending РєРѕРјР°РЅРґС‹: {ex.Message}"); }
            }

            _pendingCommands.TryRemove(pcNumber, out _);
            SavePending();
            Logger.Info($"Pending РєРѕРјР°РЅРґС‹ РѕС‡РёС‰РµРЅС‹ РґР»СЏ {pcNumber}");
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

        public async Task UpdateStatus(string pcNumber, string status, string sessionType, int elapsedSeconds)
        {
            if (!KnownClients.TryGetValue(pcNumber, out var client)) return;

            client.LastSeen = DateTime.UtcNow;
            client.IsOnline = true;

            if (string.IsNullOrEmpty(client.SessionType) && !client.SessionStart.HasValue &&
                status != "Р—Р°Р±Р»РѕРєРёСЂРѕРІР°РЅ" && status != "РЎРІРѕР±РѕРґРЅС‹Р№")
            {
                KnownClients[pcNumber] = client;
                ClientUpdated?.Invoke(client);
                return;
            }

            if (client.SessionStart.HasValue && elapsedSeconds == 0)
            {
                // РџСЂРёРјРµРЅСЏРµРј IsPaused РєР°Рє РїСЂРёРѕСЂРёС‚РµС‚ вЂ” РєР»РёРµРЅС‚ РµС‰С‘ РЅРµ РїСЂРёСЃР»Р°Р» elapsed,
                // РЅРѕ РїР°СѓР·Р° СѓР¶Рµ СЃС‚РѕРёС‚ РЅР° СЃРµСЂРІРµСЂРµ (РЅР°РїСЂРёРјРµСЂ, СЂРµС€РµРЅРёРµ Р°РґРјРёРЅРёСЃС‚СЂР°С‚РѕСЂР°).
                if (status != "Р—Р°Р±Р»РѕРєРёСЂРѕРІР°РЅ" && status != "РЎРІРѕР±РѕРґРЅС‹Р№")
                    client.Status = client.IsPaused ? "РџР°СѓР·Р°" : status;
                KnownClients[pcNumber] = client;
                ClientUpdated?.Invoke(client);
                return;
            }

            if (elapsedSeconds > 0)
            {
                client.ElapsedSeconds = elapsedSeconds;
                client.SessionStart = DateTime.UtcNow.AddSeconds(-elapsedSeconds);
                if (!client.IsPaused)
                    client.AccumulatedSeconds = 0;
            }

            // РќРѕСЂРјР°Р»РёР·СѓРµРј С‚РёРї СЃРµСЃСЃРёРё РѕС‚ РєР»РёРµРЅС‚Р°, РЅРѕ РЅРµ РїРµСЂРµР·Р°РїРёСЃС‹РІР°РµРј РїСЂРё Р±Р»РѕРєРёСЂРѕРІРєРµ вЂ”
            // РёРЅР°С‡Рµ РєР»РёРµРЅС‚ СЃ СѓСЃС‚Р°СЂРµРІС€РёРј ActiveSessionType РІРѕСЃСЃС‚Р°РЅРѕРІРёС‚ "РїСЂРёР·СЂР°С‡РЅСѓСЋ" СЃРµСЃСЃРёСЋ.
            if (status != "Р—Р°Р±Р»РѕРєРёСЂРѕРІР°РЅ" && status != "РЎРІРѕР±РѕРґРЅС‹Р№" && !string.IsNullOrEmpty(sessionType))
                client.SessionType = NormalizeSessionType(sessionType);

            if (status == "РџР°СѓР·Р°")
            {
                client.IsPaused = true;
                client.AccumulatedSeconds = elapsedSeconds;
            }

            // рџ”‘ рџ”Ґ Р“Р›РђР’РќРђРЇ Р—РђР©РРўРђ: Р•РЎР›Р Р•РЎРўР¬ OFFLINE_DECISION = PAUSE, РР“РќРћР РР РЈР•Рњ РЎРўРђРўРЈРЎ РћРў РљР›РР•РќРўРђ
            if (client.OfflineDecision == OfflineDecision.Pause && !client.IsPaused)
            {
                client.IsPaused = true;
                client.AccumulatedSeconds = client.ElapsedAtDisconnect;
                Logger.Info($"рџ›ЎпёЏ {pcNumber}: РїСЂРёРЅСѓРґРёС‚РµР»СЊРЅР°СЏ РїР°СѓР·Р° РїРѕ OfflineDecision");
            }

            // в”Ђв”Ђ РЎС‚СЂРѕРіР°СЏ С†РµРїРѕС‡РєР° РїСЂРёРѕСЂРёС‚РµС‚РѕРІ РІРёР·СѓР°Р»СЊРЅРѕРіРѕ СЃС‚Р°С‚СѓСЃР° в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
            // РЎРµСЂРІРµСЂ СЏРІР»СЏРµС‚СЃСЏ РёСЃС‚РѕС‡РЅРёРєРѕРј РёСЃС‚РёРЅС‹; РєР»РёРµРЅС‚СЃРєРёР№ СЃС‚Р°С‚СѓСЃ РёСЃРїРѕР»СЊР·СѓРµС‚СЃСЏ С‚РѕР»СЊРєРѕ
            // РґР»СЏ С†РёС„СЂ/Р±РёР»Р»РёРЅРіР°, РІРёР·СѓР°Р»СЊРЅС‹Р№ СЃС‚Р°С‚СѓСЃ РїРµСЂРµСЃС‡РёС‚С‹РІР°РµС‚СЃСЏ Р·РґРµСЃСЊ РЅРµР·Р°РІРёСЃРёРјРѕ.
            string visualStatus;
            string reason;

            if (!client.IsOnline)
            {
                visualStatus = "РћС„С„Р»Р°Р№РЅ";
                reason = "IsOnline=false";
            }
            else if (client.IsPaused)
            {
                visualStatus = "РџР°СѓР·Р°";
                reason = "IsPaused=true (РїСЂРёРѕСЂРёС‚РµС‚ РЅР°Рґ РєР»РёРµРЅС‚СЃРєРёРј СЃС‚Р°С‚СѓСЃРѕРј)";
            }
            else if (client.SessionType == "VIP")
            {
                visualStatus = "VIP";
                reason = "SessionType=VIP";
            }
            else if (client.SessionType == "Р›РёРјРёС‚")
            {
                visualStatus = "Р›РёРјРёС‚";
                reason = "SessionType=Р›РёРјРёС‚";
            }
            else if (status == "РЎРІРѕР±РѕРґРЅС‹Р№")
            {
                visualStatus = "РЎРІРѕР±РѕРґРЅС‹Р№";
                reason = "РєР»РёРµРЅС‚ СЃРѕРѕР±С‰РёР» РЎРІРѕР±РѕРґРЅС‹Р№";
            }
            else
            {
                // РќРµС‚ Р°РєС‚РёРІРЅРѕР№ СЃРµСЃСЃРёРё, РЅРµС‚ СЏРІРЅРѕРіРѕ РѕСЃРІРѕР±РѕР¶РґРµРЅРёСЏ вЂ” СЃС‡РёС‚Р°РµРј Р·Р°Р±Р»РѕРєРёСЂРѕРІР°РЅРЅС‹Рј
                visualStatus = "Р—Р°Р±Р»РѕРєРёСЂРѕРІР°РЅ";
                reason = "РЅРµС‚ Р°РєС‚РёРІРЅРѕР№ СЃРµСЃСЃРёРё (fallback)";
            }

            client.Status = visualStatus;

            KnownClients[pcNumber] = client;
            ClientUpdated?.Invoke(client);

            Logger.Info($"рџЋЁ Р’РёР·СѓР°Р» {pcNumber}: \"{client.Status}\" | РџСЂРёС‡РёРЅР°: {reason} (SessionType={client.SessionType}, elapsed={client.ElapsedSeconds}СЃ, paused={client.IsPaused})");

            if (client.IsSession || client.Status == "РџР°СѓР·Р°")
                SaveActiveSessions();
        }

        public async Task SyncSessionTime(string pcNumber, bool force = false)
        {
            if (!KnownClients.TryGetValue(pcNumber, out var client)) return;
            if (client.IsPaused || !(client.IsSession || client.Status == "РџР°СѓР·Р°") || !client.SessionStart.HasValue) return;

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

            Logger.Info($"рџ”„ SyncSessionTime: {pcNumber} в†’ {serverElapsed}СЃ (СЂР°СЃС…РѕР¶РґРµРЅРёРµ {diff}СЃ)");
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
                Logger.Info($"РџРљ РїРµСЂРµРёРјРµРЅРѕРІР°РЅ: {oldName} в†’ {newName}");
            }
        }

        /// <summary>
        /// РЈСЃС‚Р°РЅР°РІР»РёРІР°РµС‚ РёРЅРґРёРІРёРґСѓР°Р»СЊРЅРѕРµ РѕС‚РѕР±СЂР°Р¶Р°РµРјРѕРµ РёРјСЏ РґР»СЏ РєР»РёРµРЅС‚Р° (CustomName).
        /// РЈРЅРёРєР°Р»СЊРЅС‹Р№ РёРґРµРЅС‚РёС„РёРєР°С‚РѕСЂ PcNumberValue РЅРµ РјРµРЅСЏРµС‚СЃСЏ.
        /// </summary>
        public async Task SetClientCustomName(string pcNumber, string customName)
        {
            if (KnownClients.TryGetValue(pcNumber, out var client))
            {
                var oldName = pcNumber;
                
                // РћР±РЅРѕРІР»СЏРµРј CustomName
                client.CustomName = customName;
                
                // Р’С‹С‡РёСЃР»СЏРµРј РЅРѕРІРѕРµ РѕС‚РѕР±СЂР°Р¶Р°РµРјРѕРµ РёРјСЏ (С‚РµРїРµСЂСЊ СЌС‚Рѕ "{CustomName} {PcNumberValue}" РёР»Рё "РџРљ {PcNumberValue}")
                var newName = string.IsNullOrEmpty(customName) ? $"РџРљ {client.PcNumberValue}" : $"{customName} {client.PcNumberValue}";
                
                // Р•СЃР»Рё РёРјСЏ РёР·РјРµРЅРёР»РѕСЃСЊ - РїРµСЂРµРЅРѕСЃРёРј РІ СЃР»РѕРІР°СЂРµ
                if (oldName != newName)
                {
                    KnownClients.TryRemove(oldName, out _);
                    KnownClients[newName] = client;
                    
                    // РџРµСЂРµРЅРѕСЃРёРј РѕС‚Р»РѕР¶РµРЅРЅС‹Рµ РєРѕРјР°РЅРґС‹
                    if (_pendingCommands.TryRemove(oldName, out var cmds))
                        _pendingCommands[newName] = cmds;
                        
                    Logger.Info($"вњ… РРјСЏ РџРљ РёР·РјРµРЅРµРЅРѕ: {oldName} в†’ {newName} (CustomName={customName})");
                }
                else
                {
                    Logger.Info($"вњ… CustomName РѕР±РЅРѕРІР»С‘РЅ РґР»СЏ {pcNumber}: '{customName}'");
                }
                
                SaveRegistry();
                SavePending();
                ClientUpdated?.Invoke(client);  // вњ… Р’С‹Р·С‹РІР°РµРј ClientUpdated РґР»СЏ РѕР±РЅРѕРІР»РµРЅРёСЏ UI
                ClientsChanged?.Invoke();
            }
        }

        /// <summary>
        /// РЈСЃС‚Р°РЅР°РІР»РёРІР°РµС‚ РёРЅРґРёРІРёРґСѓР°Р»СЊРЅРѕРµ РѕС‚РѕР±СЂР°Р¶Р°РµРјРѕРµ РёРјСЏ РґР»СЏ РєР»РёРµРЅС‚Р° РїРѕ РµРіРѕ С‡РёСЃР»РѕРІРѕРјСѓ РёРґРµРЅС‚РёС„РёРєР°С‚РѕСЂСѓ.
        /// Р­С‚Рѕ Р±РѕР»РµРµ РЅР°РґС‘Р¶РЅС‹Р№ СЃРїРѕСЃРѕР±, С‚Р°Рє РєР°Рє PcNumberValue РЅРµ РјРµРЅСЏРµС‚СЃСЏ РїСЂРё РїРµСЂРµРёРјРµРЅРѕРІР°РЅРёРё.
        /// </summary>
        public async Task SetClientCustomNameByValue(int pcNumberValue, string customName)
        {
            var client = KnownClients.Values.FirstOrDefault(c => c.PcNumberValue == pcNumberValue);
            if (client != null)
            {
                var oldName = client.PcNumber;
                var oldConnectionId = client.ConnectionId;
                
                // РћР±РЅРѕРІР»СЏРµРј CustomName
                client.CustomName = customName;
                
                // Р’С‹С‡РёСЃР»СЏРµРј РЅРѕРІРѕРµ РѕС‚РѕР±СЂР°Р¶Р°РµРјРѕРµ РёРјСЏ (С‚РµРїРµСЂСЊ СЌС‚Рѕ "{CustomName} {PcNumberValue}" РёР»Рё "РџРљ {PcNumberValue}")
                var newName = string.IsNullOrEmpty(customName) ? $"РџРљ {pcNumberValue}" : $"{customName} {pcNumberValue}";
                
                // Р•СЃР»Рё РёРјСЏ РёР·РјРµРЅРёР»РѕСЃСЊ - РїРµСЂРµРЅРѕСЃРёРј РІ СЃР»РѕРІР°СЂРµ
                if (oldName != newName)
                {
                    KnownClients.TryRemove(oldName, out _);
                    KnownClients[newName] = client;
                    
                    // РџРµСЂРµРЅРѕСЃРёРј РѕС‚Р»РѕР¶РµРЅРЅС‹Рµ РєРѕРјР°РЅРґС‹
                    if (_pendingCommands.TryRemove(oldName, out var cmds))
                        _pendingCommands[newName] = cmds;
                        
                    Logger.Info($"вњ… РРјСЏ РџРљ РёР·РјРµРЅРµРЅРѕ: {oldName} в†’ {newName} (CustomName={customName})");
                }
                else
                {
                    Logger.Info($"вњ… CustomName РѕР±РЅРѕРІР»С‘РЅ РґР»СЏ РџРљ {pcNumberValue}: '{customName}'");
                }
                
                SaveRegistry();
                SavePending();
                ClientUpdated?.Invoke(client);  // вњ… Р’С‹Р·С‹РІР°РµРј ClientUpdated РґР»СЏ РѕР±РЅРѕРІР»РµРЅРёСЏ UI
                ClientsChanged?.Invoke();
                
                // РћС‚РїСЂР°РІР»СЏРµРј РєРѕРјР°РЅРґСѓ РЅР° РєР»РёРµРЅС‚ СЃ РЅРѕРІС‹Рј РёРјРµРЅРµРј СЃСЂР°Р·Сѓ РїРѕСЃР»Рµ РѕР±РЅРѕРІР»РµРЅРёСЏ СЃРµСЂРІРµСЂР°
                if (!string.IsNullOrEmpty(oldConnectionId))
                {
                    try
                    {
                        // РћС‚РїСЂР°РІР»СЏРµРј С‚РѕР»СЊРєРѕ CustomName (Р±РµР· РЅРѕРјРµСЂР°), РєР»РёРµРЅС‚ СЃР°Рј СЃС„РѕСЂРјРёСЂСѓРµС‚ РїРѕР»РЅРѕРµ РёРјСЏ
                        var cmd = new { Type = "SET_PC_NAME", Value = customName ?? "" };
                        await Clients.Client(oldConnectionId).SendAsync("ReceiveCommand", JsonSerializer.Serialize(cmd));
                        Logger.Info($"рџ“¤ РљРѕРјР°РЅРґР° SET_PC_NAME РѕС‚РїСЂР°РІР»РµРЅР° РєР»РёРµРЅС‚Сѓ: '{customName}' (PcNumberValue={pcNumberValue})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"РћС€РёР±РєР° РѕС‚РїСЂР°РІРєРё РєРѕРјР°РЅРґС‹ SET_PC_NAME: {ex.Message}");
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
                    Logger.Info($"РџРљ {pcNumber} РѕС„С„Р»Р°Р№РЅ вЂ” РєРѕРјР°РЅРґР° РґРѕР±Р°РІР»РµРЅР° РІ РѕС‡РµСЂРµРґСЊ");
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

                // РџСЂРѕСЃС‚Рѕ РїРµСЂРµР·Р°РїРёСЃС‹РІР°РµРј С„Р°Р№Р» вЂ” Р±РµР· СЃРѕР·РґР°РЅРёСЏ РІРµСЂСЃРёР№ СЃ timestamp
                await File.WriteAllBytesAsync(filePath, fileData);
                Logger.Info($"Р¤РѕРЅ СЃРѕС…СЂР°РЅС‘РЅ: {fileName}");

                var command = new { Type = "SET_BACKGROUND", Value = fileName };
                var json = JsonSerializer.Serialize(command);

                if (targetPc == "*")
                {
                    // Р“Р»РѕР±Р°Р»СЊРЅС‹Р№ С„РѕРЅ вЂ” РѕР±РЅРѕРІР»СЏРµРј GlobalSettings
                    var global = GlobalSettings.Load();
                    global.BackgroundFileName = fileName;
                    global.Save();

                    foreach (var client in KnownClients.Values)
                    {
                        bool isIndividual = client.IsIndividual("SET_BACKGROUND");

                        if (isIndividual && !replaceIndividual)
                            continue; // РћСЃС‚Р°РІР»СЏРµРј РёРЅРґРёРІРёРґСѓР°Р»СЊРЅС‹Р№ С„РѕРЅ РЅРµС‚СЂРѕРЅСѓС‚С‹Рј

                        if (isIndividual && replaceIndividual)
                        {
                            // РЎР±СЂР°СЃС‹РІР°РµРј РёРЅРґРёРІРёРґСѓР°Р»СЊРЅС‹Р№ С„РѕРЅ
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
                    // РРЅРґРёРІРёРґСѓР°Р»СЊРЅС‹Р№ С„РѕРЅ вЂ” РЅРµ С‚СЂРѕРіР°РµРј GlobalSettings
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
                Logger.Info($"Р¤РѕРЅ РїСЂРёРјРµРЅС‘РЅ: {fileName} в†’ {targetPc}");
            }
            catch (Exception ex)
            {
                Logger.Error($"РћС€РёР±РєР° UploadFile: {ex.Message}");
                Logger.Error($"Stack: {ex.StackTrace}");
                throw;
            }
        }

        private static void CleanupUnusedBackgroundFiles()
        {
            try
            {
                var filesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Files");
                if (!Directory.Exists(filesDir)) return;

                // РЎРѕР±РёСЂР°РµРј РІСЃРµ С„Р°Р№Р»С‹, РЅР° РєРѕС‚РѕСЂС‹Рµ РєС‚Рѕ-С‚Рѕ СЃСЃС‹Р»Р°РµС‚СЃСЏ
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var global = GlobalSettings.Load();
                if (!string.IsNullOrEmpty(global.BackgroundFileName))
                    referenced.Add(global.BackgroundFileName);

                foreach (var client in KnownClients.Values)
                {
                    if (client.IsIndividual("SET_BACKGROUND") && !string.IsNullOrEmpty(client.BackgroundFileName))
                        referenced.Add(client.BackgroundFileName);
                }

                // РЈРґР°Р»СЏРµРј РІСЃРµ С„Р°Р№Р»С‹, РєРѕС‚РѕСЂС‹Рµ Р±РѕР»СЊС€Рµ РЅРµ РёСЃРїРѕР»СЊР·СѓСЋС‚СЃСЏ
                foreach (var file in Directory.GetFiles(filesDir))
                {
                    var name = Path.GetFileName(file);
                    if (!referenced.Contains(name))
                    {
                        try
                        {
                            File.Delete(file);
                            Logger.Info($"РЈРґР°Р»С‘РЅ РЅРµРёСЃРїРѕР»СЊР·СѓРµРјС‹Р№ С„РѕРЅ: {name}");
                        }
                        catch (Exception ex) { Logger.Warn($"РќРµ СѓРґР°Р»РѕСЃСЊ СѓРґР°Р»РёС‚СЊ {name}: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { Logger.Error($"РћС€РёР±РєР° РѕС‡РёСЃС‚РєРё Files: {ex.Message}"); }
        }

        public async Task<string> TransferSession(string fromPcNumber, string toPcNumber)
        {
            if (!KnownClients.TryGetValue(fromPcNumber, out var source))
                return $"РћС€РёР±РєР°: РџРљ {fromPcNumber} РЅРµ РЅР°Р№РґРµРЅ";
            if (!KnownClients.TryGetValue(toPcNumber, out var target))
                return $"РћС€РёР±РєР°: РџРљ {toPcNumber} РЅРµ РЅР°Р№РґРµРЅ";
            if (!source.IsSession)
                return "РћС€РёР±РєР°: РЅР° РёСЃС…РѕРґРЅРѕРј РџРљ РЅРµС‚ Р°РєС‚РёРІРЅРѕР№ СЃРµСЃСЃРёРё";
            if (!target.IsOnline)
                return "РћС€РёР±РєР°: РџРљ РЅР°Р·РЅР°С‡РµРЅРёСЏ РЅРµ РІ СЃРµС‚Рё";
            if (target.IsSession)
                return "РћС€РёР±РєР°: РЅР° РџРљ РЅР°Р·РЅР°С‡РµРЅРёСЏ СѓР¶Рµ РµСЃС‚СЊ Р°РєС‚РёРІРЅР°СЏ СЃРµСЃСЃРёСЏ";

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
            source.Status = "Р—Р°Р±Р»РѕРєРёСЂРѕРІР°РЅ";
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

            Logger.Info($"вњ… РЎРµСЃСЃРёСЏ РїРµСЂРµРЅРµСЃРµРЅР°: {fromPcNumber} в†’ {toPcNumber} ({sessionType}, РїСЂРѕС€Р»Рѕ {elapsed}СЃ)");
            return "OK";
        }

        public static void MarkIndividualSetting(string pcNumber, string commandType)
        {
            if (KnownClients.TryGetValue(pcNumber, out var client))
            {
                client.MarkIndividual(commandType);
                KnownClients[pcNumber] = client;
                SaveRegistryStatic();
                Logger.Info($"РРЅРґРёРІРёРґСѓР°Р»СЊРЅР°СЏ РЅР°СЃС‚СЂРѕР№РєР°: {pcNumber} в†’ {commandType}");
            }
        }

        public static void ClearIndividualSettings(string pcNumber)
        {
            if (KnownClients.TryGetValue(pcNumber, out var client))
            {
                client.ClearIndividual();
                KnownClients[pcNumber] = client;
                SaveRegistryStatic();
                Logger.Info($"РРЅРґРёРІРёРґСѓР°Р»СЊРЅС‹Рµ РЅР°СЃС‚СЂРѕР№РєРё СЃР±СЂРѕС€РµРЅС‹: {pcNumber}");
            }
        }

        private static void SaveRegistryStatic()
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
        // вњ… РќРѕРІС‹Рµ РїРѕР»СЏ РґР»СЏ СЂР°Р·РґРµР»РµРЅРёСЏ РёРјРµРЅРё Рё РЅРѕРјРµСЂР°
        public int PcNumberValue { get; set; }
        public string CustomName { get; set; } = "";
    }
}
