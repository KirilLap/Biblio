using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BibAdmin
{
    public class GlobalSettings
    {
        // =====================
        // Экран блокировки
        // =====================
        public double BackgroundOpacity { get; set; } = 0.3;
        public bool ShowPcNumber { get; set; } = true;
        public string PcNumberPosition { get; set; } = "MiddleCenter";
        public double PcNumberFontSize { get; set; } = 52;
        public bool ShowLockedText { get; set; } = true;
        public string LockedTextPosition { get; set; } = "MiddleCenter";
        public double LockedTextFontSize { get; set; } = 16;
        public string TimePosition { get; set; } = "BottomCenter";
        public double TimeFontSize { get; set; } = 36;

        // =====================
        // Ограничения
        // =====================
        public bool UsbBlocked { get; set; } = false;
        public bool TaskMgrDisabled { get; set; } = false;
        public bool BlockRegedit { get; set; } = false;
        public bool BlockCmd { get; set; } = false;
        public bool BlockPowerShell { get; set; } = false;
        public bool HideDriveC { get; set; } = false;
        public bool BlockInstall { get; set; } = false;

        // =====================
        // Фон
        // =====================
        public string BackgroundFileName { get; set; } = "";

        // =====================
        // Тариф и пароль
        // =====================
        public int Tariff { get; set; } = 3000;
        public string AdminPassword { get; set; } = "1234";

        // =====================
        // Поведение при потере сети
        // =====================
        // true = блокировать ПК при потере связи во время платной сессии
        public bool LockOnOffline { get; set; } = false;

        // =====================
        // Метаданные
        // =====================
        public DateTime LastApplied { get; set; } = DateTime.MinValue;
        public List<PendingCommand> PendingCommands { get; set; } = new();
        
        // =====================
        // Сортировка ПК в админке
        // =====================
        public string ClientSortMode { get; set; } = "ByNumber"; // "ByNumber" или "ByName"

        private static readonly string _path = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "global_settings.json");

        // Загрузка настроек
        public static GlobalSettings Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    return JsonSerializer.Deserialize<GlobalSettings>(json) ?? new GlobalSettings();
                }
            }
            catch (Exception ex) 
            { 
                Logger.Error($"Ошибка загрузки GlobalSettings: {ex.Message}"); 
            }
            return new GlobalSettings();
        }

        // Сохранение настроек
        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_path, json);
                Logger.Info("GlobalSettings сохранены");
            }
            catch (Exception ex) 
            { 
                Logger.Error($"Ошибка сохранения GlobalSettings: {ex.Message}"); 
            }
        }

        // Преобразование настроек в команды для клиентов
        public List<PendingCommand> ToCommands()
        {
            var cmds = new List<PendingCommand>
            {
                new("SHOW_PC_NUMBER", ShowPcNumber.ToString().ToLower()),
                new("SET_BACKGROUND_OPACITY", BackgroundOpacity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)),
                new("SET_PC_NUMBER_POSITION", PcNumberPosition),
                new("SET_PC_NUMBER_FONT_SIZE", PcNumberFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("SHOW_LOCKED_TEXT", ShowLockedText.ToString().ToLower()),
                new("SET_LOCKED_TEXT_POSITION", LockedTextPosition),
                new("SET_LOCKED_TEXT_FONT_SIZE", LockedTextFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("SET_TIME_POSITION", TimePosition),
                new("SET_TIME_FONT_SIZE", TimeFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("USB_BLOCK", UsbBlocked.ToString().ToLower()),
                new("TASKMGR_DISABLE", TaskMgrDisabled.ToString().ToLower()),
                // 🔐 Пароль отправляется в открытом виде, клиент сам захеширует
                new("ADMIN_PASSWORD", AdminPassword),
                new("SET_TARIFF", Tariff.ToString()),
                // 🔒 Блокировки
                new("BLOCK_REGEDIT", BlockRegedit.ToString().ToLower()),
                new("BLOCK_CMD", BlockCmd.ToString().ToLower()),
                new("BLOCK_POWERSHELL", BlockPowerShell.ToString().ToLower()),
                new("HIDE_DRIVE_C", HideDriveC.ToString().ToLower()),
                new("BLOCK_INSTALL_UNINSTALL", BlockInstall.ToString().ToLower()),
                new("LOCK_ON_OFFLINE", LockOnOffline.ToString().ToLower())
            };

            if (!string.IsNullOrEmpty(BackgroundFileName))
                cmds.Add(new("SET_BACKGROUND", BackgroundFileName));

            return cmds;
        }
    }

    // Модель команды
    public class PendingCommand
    {
        public string Type { get; set; } = "";
        public string Value { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public PendingCommand() { }
        
        public PendingCommand(string type, string value) 
        { 
            Type = type; 
            Value = value; 
        }
    }
}