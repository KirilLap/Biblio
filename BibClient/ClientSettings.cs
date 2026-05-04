//ClientSettings.cs

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace BibClient
{
    public class ClientSettings
    {
        public string PcNumber { get; set; } = "ПК 1";

        public string AdminPasswordHash { get; set; } =
            HashPassword("1234");

        // Только запись — при установке сразу хешируется
        public string AdminPassword
        {
            set { AdminPasswordHash = HashPassword(value); }
        }

        public bool ShowPcNumber { get; set; } = true;
        public string BackgroundImagePath { get; set; } = "";
        public string ServerIp { get; set; } = "172.16.5.53";
        public int ServerPort { get; set; } = 8080;
        public string LocalIp { get; set; } = "";
        public string ApiKey { get; set; } = "default-key";
        public LogLevel LogLevel { get; set; } = LogLevel.Info;
        public bool IsSetupCompleted { get; set; } = false;
        public Dictionary<string, bool> Policies { get; set; } = new();
        public DateTime LastServerContact { get; set; } = DateTime.MinValue;
        public int Tariff { get; set; } = 3000;
        public int WarningMinutes { get; set; } = 5;

        // Настройки экрана блокировки
        public double BackgroundOpacity { get; set; } = 0.3;
        public string PcNumberPosition { get; set; } = "MiddleCenter";
        public double PcNumberFontSize { get; set; } = 52;
        public string TimePosition { get; set; } = "BottomCenter";
        public double TimeFontSize { get; set; } = 36;
        public bool ShowLockedText { get; set; } = true;
        public string LockedTextPosition { get; set; } = "MiddleCenter";
        public double LockedTextFontSize { get; set; } = 16;

        // =====================
        // Состояние системных ограничений
        // Сохраняются локально и применяются при каждом запуске
        // =====================
        public bool UsbBlocked { get; set; } = false;
        public bool TaskMgrDisabled { get; set; } = false;
        public bool RegeditBlocked { get; set; } = false;
        public bool CmdBlocked { get; set; } = false;
        public bool PowerShellBlocked { get; set; } = false;
        public bool DriveCHidden { get; set; } = false;
        public bool InstallBlocked { get; set; } = false;
        public bool AppLockerEnabled { get; set; } = false;

        // =====================
        // Методы для работы с паролем
        // =====================
        public static string HashPassword(string password)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(
                Encoding.UTF8.GetBytes(password));
            return Convert.ToBase64String(bytes);
        }

        public static bool VerifyPassword(string password,
            string storedHash)
        {
            return HashPassword(password) == storedHash;
        }

        public void SetPassword(string newPassword)
        {
            AdminPasswordHash = HashPassword(newPassword);
        }
    }
}