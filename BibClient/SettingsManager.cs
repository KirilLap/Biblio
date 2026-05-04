using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BibClient
{
    public class ClientSettings
    {
        public string PcNumber { get; set; } = "ПК 1";
        public string ServerIp { get; set; } = "127.0.0.1";
        public int ServerPort { get; set; } = 8080;

        // ✅ Хеш пароля (хранится, читается)
        public string AdminPasswordHash { get; set; } = "81DC9BDB52D04DC20036DBD8313ED055"; // MD5 от "1234"

        // ✅ Пароль (только запись — при установке сразу хешируется)
        public string AdminPassword
        {
            set { AdminPasswordHash = HashPassword(value); }
        }

        public bool ShowPcNumber { get; set; } = true;
        public double PcNumberFontSize { get; set; } = 52;
        public string PcNumberPosition { get; set; } = "MiddleCenter";
        public bool ShowLockedText { get; set; } = true;
        public double LockedTextFontSize { get; set; } = 16;
        public string LockedTextPosition { get; set; } = "MiddleCenter";
        public string TimePosition { get; set; } = "BottomCenter";
        public double TimeFontSize { get; set; } = 36;
        public double BackgroundOpacity { get; set; } = 0.3;
        public string BackgroundImagePath { get; set; } = "";
        public int Tariff { get; set; } = 3000;

        // Ограничения (сохраняются для восстановления при перезапуске)
        public bool UsbBlocked { get; set; } = false;
        public bool TaskMgrDisabled { get; set; } = false;
        public bool BlockRegedit { get; set; } = false;
        public bool BlockCmd { get; set; } = false;
        public bool BlockPowerShell { get; set; } = false;
        public bool HideDriveC { get; set; } = false;
        public bool BlockInstall { get; set; } = false;

        public static string HashPassword(string password)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(password));
            var sb = new StringBuilder();
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static bool VerifyPassword(string password, string hash) => HashPassword(password) == hash;
    }

    public static class SettingsManager
    {
        private static readonly string _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        public static ClientSettings Current { get; set; } = new ClientSettings();

        public static void Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var loaded = JsonSerializer.Deserialize<ClientSettings>(json);
                    if (loaded != null) Current = loaded;
                }
            }
            catch { Current = new ClientSettings(); }
        }

        public static void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
            }
            catch { }
        }
    }
}