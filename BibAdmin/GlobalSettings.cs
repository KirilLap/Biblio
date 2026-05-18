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
        public double BackgroundOpacity { get; set; } = 0.5;
        public bool ShowPcName { get; set; } = true;      // Показывать кастомное имя (например, "ПК" или "Комп")
        public bool ShowPcNumber { get; set; } = true;    // Показывать цифровой номер (например, "1")
        public string PcNumberPosition { get; set; } = "MiddleCenter";  // Общая позиция для имени и номера
        public double PcNumberFontSize { get; set; } = 120;
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
        // Защита и автозапуск (включены по умолчанию для безопасности)
        // =====================
        public bool PreventClose { get; set; } = true;  // Защита от закрытия - включена по умолчанию
        public bool AutoStartWithUser { get; set; } = true;  // Автозапуск с пользователем - включен по умолчанию

        // =====================
        // Фон
        // =====================
        public string BackgroundFileName { get; set; } = "";

        // =====================
        // Тариф и пароль
        // =====================
        public int Tariff { get; set; } = 3000;
        public string AdminPasswordHash { get; set; } = "03ac674216f3e15c761ee1a5e255f067953623c8b388b4459e13f978d7c846f4"; // SHA256 от "1234"
        public bool IsFirstRun { get; set; } = true;
        public int ServerPort { get; set; } = 8080;

        public void SetPassword(string plainText)
            => AdminPasswordHash = HashPassword(plainText);

        public static string HashPassword(string password)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

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

        // =====================
        // Дополнительные услуги
        // =====================
        public List<ServiceType> Services { get; set; } = new()
        {
            new() { Name = "Печать ч/б",     Unit = "лист",  Price = 300  },
            new() { Name = "Печать цветная",  Unit = "лист",  Price = 500  },
            new() { Name = "Сканирование",    Unit = "лист",  Price = 500  },
            new() { Name = "Ксерокопия",      Unit = "лист",  Price = 300  },
            new() { Name = "Ламинирование",   Unit = "штука", Price = 2000 },
        };

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
                new("USB_BLOCK", UsbBlocked.ToString().ToLower()),
                new("TASKMGR_DISABLE", TaskMgrDisabled.ToString().ToLower()),
                new("ADMIN_PASSWORD", AdminPasswordHash),
                new("SET_TARIFF", Tariff.ToString()),
                // 🔒 Блокировки
                new("BLOCK_REGEDIT", BlockRegedit.ToString().ToLower()),
                new("BLOCK_CMD", BlockCmd.ToString().ToLower()),
                new("BLOCK_POWERSHELL", BlockPowerShell.ToString().ToLower()),
                new("HIDE_DRIVE_C", HideDriveC.ToString().ToLower()),
                new("BLOCK_INSTALL_UNINSTALL", BlockInstall.ToString().ToLower()),
                new("LOCK_ON_OFFLINE", LockOnOffline.ToString().ToLower()),
                // 🔒 Защита и автозапуск
                new("PREVENT_CLOSE", PreventClose.ToString().ToLower()),
                new("AUTOSTART_WITH_USER", AutoStartWithUser.ToString().ToLower())
            };

            if (!string.IsNullOrEmpty(BackgroundFileName))
                cmds.Add(new("SET_BACKGROUND", BackgroundFileName));

            return cmds;
        }

        // =====================
        // Операторы
        // =====================
        public List<OperatorAccount> Operators { get; set; } = new();

        // Путь к папке обновлений (пусто = ../BibAdminWeb/updates/ рядом с BibAdmin.exe)
        public string UpdatesPath { get; set; } = "";

        // =====================
        // Настройки полей сессии
        // =====================
        public bool RequireReaderId { get; set; } = true;
        public bool RequireUserName { get; set; } = false;
    }

    // Модель услуги
    public class ServiceType
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public string Unit { get; set; } = "лист";
        public int Price { get; set; } = 0;
        public bool IsActive { get; set; } = true;
    }

    // Модель оператора
    public class OperatorAccount
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Login { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool IsActive { get; set; } = true;
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