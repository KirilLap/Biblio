using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BibAdminWeb
{
    public class GlobalSettings
    {
        public double BackgroundOpacity { get; set; } = 0.5;
        public bool ShowPcName { get; set; } = true;
        public bool ShowPcNumber { get; set; } = true;
        public string PcNumberPosition { get; set; } = "MiddleCenter";
        public double PcNumberFontSize { get; set; } = 120;
        public bool ShowLockedText { get; set; } = true;
        public string LockedTextPosition { get; set; } = "MiddleCenter";
        public double LockedTextFontSize { get; set; } = 16;
        public string TimePosition { get; set; } = "BottomCenter";
        public double TimeFontSize { get; set; } = 36;
        public int ScreenOffsetX { get; set; } = 0;
        public int ScreenOffsetY { get; set; } = 0;
        public bool ShowStatusDot { get; set; } = true;
        public int PcNumberOrder { get; set; } = 1;
        public int LockedTextOrder { get; set; } = 2;
        public int TimeOrder { get; set; } = 3;

        public bool UsbBlocked { get; set; } = false;
        public bool TaskMgrDisabled { get; set; } = false;
        public bool BlockRegedit { get; set; } = false;
        public bool BlockCmd { get; set; } = false;
        public bool BlockPowerShell { get; set; } = false;
        public bool HideDriveC { get; set; } = false;
        public bool BlockInstall { get; set; } = false;

        public bool PreventClose { get; set; } = true;
        public bool AutoStartWithUser { get; set; } = true;

        public string BackgroundFileName { get; set; } = "";

        public int Tariff { get; set; } = 3000;
        public string AdminPasswordHash { get; set; } = "03ac674216f3e15c761ee1a5e255f067953623c8b388b4459e13f978d7c846f4"; // SHA256 от "1234"
        public bool IsFirstRun { get; set; } = true;
        public int ServerPort { get; set; } = 8080;
        public string ReaderCardPrefix { get; set; } = "FAA";

        public void SetPassword(string plainText)
            => AdminPasswordHash = HashPassword(plainText);

        public static string HashPassword(string password)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public bool LockOnOffline { get; set; } = false;

        public DateTime LastApplied { get; set; } = DateTime.MinValue;
        public List<PendingCommand> PendingCommands { get; set; } = new();
        public string ClientSortMode { get; set; } = "ByNumber";

        public List<ServiceType> Services { get; set; } = new()
        {
            new() { Name = "Печать ч/б",     Unit = "лист",  Price = 300  },
            new() { Name = "Печать цветная",  Unit = "лист",  Price = 500  },
            new() { Name = "Сканирование",    Unit = "лист",  Price = 500  },
            new() { Name = "Ксерокопия",      Unit = "лист",  Price = 300  },
            new() { Name = "Ламинирование",   Unit = "штука", Price = 2000 },
        };

        public List<OperatorAccount> Operators { get; set; } = new();

        // Путь к папке обновлений (пусто = {BaseDirectory}/updates/)
        public string UpdatesPath { get; set; } = "";

        // =====================
        // Настройки полей сессии
        // =====================
        public bool RequireReaderId { get; set; } = true;
        public bool RequireUserName { get; set; } = false;

        private static readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BibAdmin", "global_settings.json");

        public static GlobalSettings Load()
        {
            try
            {
                MigrateIfNeeded();
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    return JsonSerializer.Deserialize<GlobalSettings>(json) ?? new GlobalSettings();
                }
            }
            catch (Exception ex) { Logger.Error($"Ошибка загрузки GlobalSettings: {ex.Message}"); }
            return new GlobalSettings();
        }

        private static void MigrateIfNeeded()
        {
            if (File.Exists(_path)) return;
            var oldPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "global_settings.json");
            if (!File.Exists(oldPath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.Move(oldPath, _path);
                Logger.Info("GlobalSettings перенесены в %APPDATA%\\BibAdmin\\");
            }
            catch (Exception ex) { Logger.Error($"Ошибка миграции GlobalSettings: {ex.Message}"); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
                Logger.Info("GlobalSettings сохранены");
            }
            catch (Exception ex) { Logger.Error($"Ошибка сохранения GlobalSettings: {ex.Message}"); }
        }

        public List<PendingCommand> ToCommands()
        {
            var cmds = new List<PendingCommand>
            {
                new("SHOW_PC_NAME", ShowPcName.ToString().ToLower()),
                new("SHOW_PC_NUMBER", ShowPcNumber.ToString().ToLower()),
                new("SET_BACKGROUND_OPACITY", BackgroundOpacity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)),
                new("SET_PC_NUMBER_POSITION", PcNumberPosition),
                new("SET_PC_NUMBER_FONT_SIZE", PcNumberFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("SHOW_LOCKED_TEXT", ShowLockedText.ToString().ToLower()),
                new("SET_LOCKED_TEXT_POSITION", LockedTextPosition),
                new("SET_LOCKED_TEXT_FONT_SIZE", LockedTextFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("SET_TIME_POSITION", TimePosition),
                new("SET_TIME_FONT_SIZE", TimeFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("SET_SCREEN_OFFSET_X", ScreenOffsetX.ToString()),
                new("SET_SCREEN_OFFSET_Y", ScreenOffsetY.ToString()),
                new("SHOW_STATUS_DOT", ShowStatusDot.ToString().ToLower()),
                new("SET_PC_NUMBER_ORDER", PcNumberOrder.ToString()),
                new("SET_LOCKED_TEXT_ORDER", LockedTextOrder.ToString()),
                new("SET_TIME_ORDER", TimeOrder.ToString()),
                new("USB_BLOCK", UsbBlocked.ToString().ToLower()),
                new("TASKMGR_DISABLE", TaskMgrDisabled.ToString().ToLower()),
                new("ADMIN_PASSWORD", AdminPasswordHash),
                new("SET_TARIFF", Tariff.ToString()),
                new("BLOCK_REGEDIT", BlockRegedit.ToString().ToLower()),
                new("BLOCK_CMD", BlockCmd.ToString().ToLower()),
                new("BLOCK_POWERSHELL", BlockPowerShell.ToString().ToLower()),
                new("HIDE_DRIVE_C", HideDriveC.ToString().ToLower()),
                new("BLOCK_INSTALL_UNINSTALL", BlockInstall.ToString().ToLower()),
                new("LOCK_ON_OFFLINE", LockOnOffline.ToString().ToLower()),
                new("PREVENT_CLOSE", PreventClose.ToString().ToLower()),
                new("AUTOSTART_WITH_USER", AutoStartWithUser.ToString().ToLower())
            };
            if (!string.IsNullOrEmpty(BackgroundFileName))
                cmds.Add(new("SET_BACKGROUND", BackgroundFileName));
            return cmds;
        }
    }

    public class ServiceType
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string Unit { get; set; } = "лист";
        public int Price { get; set; } = 0;
        public bool IsActive { get; set; } = true;
    }

    public class OperatorAccount
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Login { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool IsActive { get; set; } = true;
    }

    public class PendingCommand
    {
        public string Type { get; set; } = "";
        public string Value { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public PendingCommand() { }
        public PendingCommand(string type, string value) { Type = type; Value = value; }
    }
}
