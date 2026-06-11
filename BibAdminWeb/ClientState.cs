using System;
using System.Collections.Generic;

namespace BibAdminWeb
{
    public enum OfflineDecision { None, Pause, Continue }

    public class ClientState
    {
        public int PcNumberValue { get; set; } = 1;
        public string CustomName { get; set; } = "";

        // JsonIgnore: computed from CustomName+PcNumberValue, must NOT be deserialized
        // (without this, JSON loader calls the setter which overwrites CustomName with the full display name)
        [System.Text.Json.Serialization.JsonIgnore]
        public string PcNumber
        {
            get => string.IsNullOrEmpty(CustomName) ? $"ПК {PcNumberValue}" : $"{CustomName} {PcNumberValue}";
            set
            {
                // Parse "ПК N" format
                if (value.StartsWith("ПК ") && int.TryParse(value.Substring(3), out var num))
                { PcNumberValue = num; CustomName = ""; }
                // Parse pure number
                else if (int.TryParse(value, out num))
                { PcNumberValue = num; CustomName = ""; }
                // Parse "CustomName N" format (e.g. "Комп 1")
                else
                {
                    var lastSpace = value.LastIndexOf(' ');
                    if (lastSpace > 0 && int.TryParse(value.Substring(lastSpace + 1), out num))
                    {
                        PcNumberValue = num;
                        var prefix = value.Substring(0, lastSpace).TrimEnd();
                        CustomName = prefix == "ПК" ? "" : prefix;
                    }
                    else
                    {
                        CustomName = value;
                    }
                }
            }
        }

        public string ConnectionId { get; set; } = "";
        public string Ip { get; set; } = "";
        public string MacAddress { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public double DiskFreeGb { get; set; }
        public double UptimeHours { get; set; }
        public bool IsOnline { get; set; }
        public DateTime LastSeen { get; set; }
        public string Status { get; set; } = "Оффлайн";

        // Transient: время отправки START_SESSION клиенту при реконнекте.
        // Используется для игнорирования «Заблокирован» в grace period (клиент шлёт его
        // сразу после RegisterClient, до обработки START_SESSION).
        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime? PendingStartSessionSentAt { get; set; }

        public string SessionType { get; set; } = "";
        public DateTime? SessionStart { get; set; }
        public int LimitSeconds { get; set; } = 0;
        public int ElapsedSeconds { get; set; } = 0;
        public int PaidAmount { get; set; } = 0;
        public string? UserName { get; set; }
        public string? ReaderId { get; set; }
        public string StartedByOperatorName { get; set; } = "";

        public bool IsPaused { get; set; } = false;
        public int AccumulatedSeconds { get; set; } = 0;
        public int PenaltyAmount { get; set; } = 0;

        public string BackgroundFileName { get; set; } = "";
        public string SessionId { get; set; } = "";
        public DateTime? DisconnectedAt { get; set; }
        public int ElapsedAtDisconnect { get; set; } = 0;
        public OfflineDecision OfflineDecision { get; set; } = OfflineDecision.None;

        public bool UsbBlocked { get; set; } = false;
        public bool TaskMgrDisabled { get; set; } = false;
        public bool ShowPcNumber { get; set; } = true;
        public bool PreventClose { get; set; } = true;
        public bool AutoStartWithUser { get; set; } = true;

        public string ClientVersion { get; set; } = "";

        // Транзитный статус обновления — не сохраняется в clients.json
        [System.Text.Json.Serialization.JsonIgnore]
        public string UpdateStatus { get; set; } = ""; // "pending" | "updating" | "done" | "failed"
        [System.Text.Json.Serialization.JsonIgnore]
        public string PreUpdateVersion { get; set; } = "";

        public bool HasIndividualSettings { get; set; } = false;
        public List<string> IndividualSettingKeys { get; set; } = new();

        public bool IsLocked => Status == "Заблокирован" || Status == "Оффлайн";
        public bool IsFree => Status == "Свободный";

        public bool IsSession =>
            Status == "Лимит" || Status == "VIP" || Status == "Пауза" ||
            (!IsOnline && !string.IsNullOrEmpty(SessionType) &&
             SessionType != "Заблокирован" && SessionType != "Свободный" && SessionStart.HasValue);

        // Биллинг при конвертации типа сессии
        // Лимит→VIP: OriginalLimitSeconds = LimitSeconds на момент конвертации (оплаченные секунды)
        public int OriginalLimitSeconds { get; set; } = 0;
        // VIP→Лимит: IsPostPay = true, оплата по тарифу в конце (как VIP)
        public bool IsPostPay { get; set; } = false;

        public int RemainingSeconds => LimitSeconds > 0 ? Math.Max(0, LimitSeconds - ElapsedSeconds) : -1;

        public void MarkIndividual(string commandType)
        {
            HasIndividualSettings = true;
            if (!IndividualSettingKeys.Contains(commandType))
                IndividualSettingKeys.Add(commandType);
        }

        public void ClearIndividual()
        {
            HasIndividualSettings = false;
            IndividualSettingKeys.Clear();
        }

        public bool IsIndividual(string commandType) => IndividualSettingKeys.Contains(commandType);
    }
}
