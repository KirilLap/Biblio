using System;
using System.Collections.Generic;

namespace BibAdmin
{
    public enum OfflineDecision { None, Pause, Continue }

    public class ClientState
    {
        // =====================
        // Основная информация
        // =====================
        public string PcNumber { get; set; } = "";
        public string ConnectionId { get; set; } = "";
        public string Ip { get; set; } = "";
        public string MacAddress { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public double DiskFreeGb { get; set; }
        public double UptimeHours { get; set; }
        public bool IsOnline { get; set; }
        public DateTime LastSeen { get; set; }
        public string Status { get; set; } = "Оффлайн";

        // =====================
        // Сессия (Критично для восстановления)
        // =====================
        public string SessionType { get; set; } = "";
        public DateTime? SessionStart { get; set; } // Время старта сессии (UTC)
        public int LimitSeconds { get; set; } = 0;
        public int ElapsedSeconds { get; set; } = 0;
        public int PaidAmount { get; set; } = 0;
        public string? UserName { get; set; }

        // ✅ Поля для паузы
        public bool IsPaused { get; set; } = false;
        public int AccumulatedSeconds { get; set; } = 0;

        // =====================
        // Отслеживание сессии и оффлайна
        // =====================
        public string SessionId { get; set; } = "";           // ID текущей сессии (от клиента)
        public DateTime? DisconnectedAt { get; set; }         // Когда ушёл в оффлайн с активной сессией
        public int ElapsedAtDisconnect { get; set; } = 0;     // Elapsed зафиксированный в момент разрыва
        public OfflineDecision OfflineDecision { get; set; } = OfflineDecision.None; // Решение администратора

        // =====================
        // Текущее состояние настроек
        // =====================
        public bool UsbBlocked { get; set; } = false;
        public bool TaskMgrDisabled { get; set; } = false;
        public bool ShowPcNumber { get; set; } = true;

        // =====================
        // Индивидуальные настройки
        // =====================
        public bool HasIndividualSettings { get; set; } = false;
        public List<string> IndividualSettingKeys { get; set; } = new();

        // =====================
        // Вычисляемые свойства
        // =====================
        public bool IsLocked => Status == "Заблокирован" || Status == "Оффлайн";
        public bool IsFree => Status == "Свободный";

        // Активная сессия: по статусу ИЛИ оффлайн-клиент с незакрытой сессией (таймер продолжает идти)
        public bool IsSession =>
            Status == "По времени" ||
            Status == "По деньгам" ||
            Status == "VIP" ||
            Status == "Пауза" ||
            (!IsOnline &&
             !string.IsNullOrEmpty(SessionType) &&
             SessionType != "Заблокирован" &&
             SessionType != "Свободный" &&
             SessionStart.HasValue);

        public int RemainingSeconds => LimitSeconds > 0
            ? Math.Max(0, LimitSeconds - ElapsedSeconds) : -1;

        // =====================
        // Методы управления индивидуальными настройками
        // =====================
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

        public bool IsIndividual(string commandType)
            => IndividualSettingKeys.Contains(commandType);
    }
}