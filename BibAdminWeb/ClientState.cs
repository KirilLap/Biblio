using System;
using System.Collections.Generic;

namespace BibAdminWeb
{
    public enum OfflineDecision { None, Pause, Continue }

    public class ClientState
    {
        public int PcNumberValue { get; set; } = 1;
        public string CustomName { get; set; } = "";

        public string PcNumber
        {
            get => string.IsNullOrEmpty(CustomName) ? $"ПК {PcNumberValue}" : $"{CustomName} {PcNumberValue}";
            set
            {
                if (value.StartsWith("ПК ") && int.TryParse(value.Substring(3), out var num))
                { PcNumberValue = num; CustomName = ""; }
                else if (int.TryParse(value, out num))
                { PcNumberValue = num; CustomName = ""; }
                else
                { CustomName = value; }
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

        public bool HasIndividualSettings { get; set; } = false;
        public List<string> IndividualSettingKeys { get; set; } = new();

        public bool IsLocked => Status == "Заблокирован" || Status == "Оффлайн";
        public bool IsFree => Status == "Свободный";

        public bool IsSession =>
            Status == "Лимит" || Status == "VIP" || Status == "Пауза" ||
            (!IsOnline && !string.IsNullOrEmpty(SessionType) &&
             SessionType != "Заблокирован" && SessionType != "Свободный" && SessionStart.HasValue);

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
