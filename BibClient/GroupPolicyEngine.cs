//GroupPolicyEngine.cs    


    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Text;

    namespace BibClient
    {
    /// <summary>
    /// Управление локальными групповыми политиками через Registry.pol
    /// Работает на всех версиях Windows (Home, Pro, Enterprise)
    /// Требует прав администратора
    /// </summary>
    public static class GroupPolicyEngine
    {
        // НОВЫЙ ПУТЬ: Применяем политики ТОЛЬКО к локальной группе "Пользователи" (S-1-5-32-545)
        private static readonly string UserPolPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\GroupPolicyUsers\S-1-5-32-545\User\Registry.pol");

        // Для MLGPO обязательно нужен файл gpt.ini, иначе Windows проигнорирует папку
        private static readonly string GptIniPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\GroupPolicyUsers\S-1-5-32-545\gpt.ini");

        private static readonly string MachinePolPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\GroupPolicy\Machine\Registry.pol");

        private static readonly byte[] PolHeader =
            { 0x50, 0x52, 0x65, 0x67, 0x01, 0x00, 0x00, 0x00 };

        // Метод для создания gpt.ini
        private static void EnsureGptIni()
        {
            EnsureDirectory(GptIniPath);
            if (!File.Exists(GptIniPath))
            {
                string content = "[General]\r\ngPCUserExtensionNames=[{35378EAC-683F-11D2-A89A-00C04FBBCFA2}{D02B1F72-3407-48AE-BA88-E8213C6761F1}]\r\nVersion=10000\r\n";
                File.WriteAllText(GptIniPath, content, Encoding.UTF8);
            }
        }

        // =====================
        // Блокировка меню Ctrl+Alt+Del
        // =====================
        public static void SetCtrlAltDelBlock(bool block)
        {
            string sysKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
            string expKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

            if (block)
            {
                SetUserPolicy(sysKey, "DisableLockWorkstation", 1); // Скрывает кнопку "Заблокировать"
                SetUserPolicy(sysKey, "DisableChangePassword", 1);  // Скрывает "Сменить пароль"
                SetUserPolicy(sysKey, "DisableTaskMgr", 1);         // Скрывает "Диспетчер задач"
                SetUserPolicy(expKey, "NoLogoff", 1);               // Скрывает "Выйти" (САМОЕ ВАЖНОЕ)
            }
            else
            {
                RemoveUserPolicy(sysKey, "DisableLockWorkstation");
                RemoveUserPolicy(sysKey, "DisableChangePassword");
                RemoveUserPolicy(sysKey, "DisableTaskMgr");
                RemoveUserPolicy(expKey, "NoLogoff");
            }

            Logger.Info($"GPO Ctrl+Alt+Del: {(block ? "Заблокировано" : "Разблокировано")}");
        }

        // =====================
        // Публичные методы блокировки
        // =====================

        public static void SetRegeditBlock(bool block)
            {
                if (block)
                    SetUserPolicy(
                        @"Software\Microsoft\Windows\CurrentVersion\Policies\System",
                        "DisableRegistryTools", 1);
                else
                    RemoveUserPolicy(
                        @"Software\Microsoft\Windows\CurrentVersion\Policies\System",
                        "DisableRegistryTools");

                Logger.Info($"GPO Regedit: {(block ? "Заблокирован" : "Разблокирован")}");
            }

            public static void SetCmdBlock(bool block)
            {
                if (block)
                    SetUserPolicy(
                        @"Software\Policies\Microsoft\Windows\System",
                        "DisableCMD", 1);
                else
                    RemoveUserPolicy(
                        @"Software\Policies\Microsoft\Windows\System",
                        "DisableCMD");

                Logger.Info($"GPO CMD: {(block ? "Заблокирована" : "Разблокирована")}");
            }

            public static void SetPowerShellBlock(bool block)
            {
                if (block)
                {
                    SetUserPolicy(
                        @"Software\Policies\Microsoft\Windows\PowerShell",
                        "EnableScripts", 0);
                    SetUserPolicyString(
                        @"Software\Policies\Microsoft\Windows\PowerShell",
                        "ExecutionPolicy", "Restricted");
                }
                else
                {
                    RemoveUserPolicy(
                        @"Software\Policies\Microsoft\Windows\PowerShell",
                        "EnableScripts");
                    RemoveUserPolicy(
                        @"Software\Policies\Microsoft\Windows\PowerShell",
                        "ExecutionPolicy");
                }

                Logger.Info($"GPO PowerShell: {(block ? "Заблокирован" : "Разблокирован")}");
            }

            public static void SetInstallBlock(bool block)
            {
                // Запрет MSI установщика — через машинную политику
                if (block)
                    SetMachinePolicy(
                        @"Software\Policies\Microsoft\Windows\Installer",
                        "DisableMSI", 2);
                else
                    RemoveMachinePolicy(
                        @"Software\Policies\Microsoft\Windows\Installer",
                        "DisableMSI");

                // Скрыть "Программы и компоненты"
                if (block)
                    SetUserPolicy(
                        @"Software\Microsoft\Windows\CurrentVersion\Policies\Uninstall",
                        "NoRemovePage", 1);
                else
                    RemoveUserPolicy(
                        @"Software\Microsoft\Windows\CurrentVersion\Policies\Uninstall",
                        "NoRemovePage");

                Logger.Info($"GPO Install: {(block ? "Запрещена" : "Разрешена")}");
            }

            // =====================
            // Применить все политики одной командой
            // =====================
            public static void ApplyAll(
                bool blockRegedit,
                bool blockCmd,
                bool blockPowerShell,
                bool blockInstall)
            {
                SetRegeditBlock(blockRegedit);
                SetCmdBlock(blockCmd);
                SetPowerShellBlock(blockPowerShell);
                SetInstallBlock(blockInstall);
                RunGpUpdate();
            }

            // =====================
            // gpupdate /force — применяет политики немедленно
            // =====================
            public static void RunGpUpdate()
            {
                try
                {
                    Logger.Info("Запуск gpupdate /force...");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "gpupdate.exe",
                        Arguments = "/force /wait:0",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(15000); // Ждём максимум 15 сек
                    Logger.Info("gpupdate завершён");
                }
                catch (Exception ex)
                {
                    Logger.Error($"gpupdate ошибка: {ex.Message}");
                }
            }

            // =====================
            // Запись политики пользователя (User Policy)
            // =====================
            private static void SetUserPolicy(string keyPath,
                string valueName, int value)
            {
                WritePolEntry(UserPolPath, keyPath, valueName,
                    value, isMachine: false);
            }

            private static void SetUserPolicyString(string keyPath,
                string valueName, string value)
            {
                WritePolEntryString(UserPolPath, keyPath, valueName,
                    value, isMachine: false);
            }

            private static void RemoveUserPolicy(string keyPath,
                string valueName)
            {
                RemovePolEntry(UserPolPath, keyPath, valueName);
            }

            // =====================
            // Запись машинной политики (Machine Policy)
            // =====================
            private static void SetMachinePolicy(string keyPath,
                string valueName, int value)
            {
                WritePolEntry(MachinePolPath, keyPath, valueName,
                    value, isMachine: true);
            }

            private static void RemoveMachinePolicy(string keyPath,
                string valueName)
            {
                RemovePolEntry(MachinePolPath, keyPath, valueName);
            }

            // =====================
            // Низкоуровневая работа с .pol файлом (PReg формат)
            // =====================

            // Формат записи в .pol файле:
            // [keyPath\0valueName\0type\0size\0data\0]
            // Всё в UTF-16LE (Unicode)

            private static void WritePolEntry(string polPath,
                string keyPath, string valueName,
                int value, bool isMachine)
            {
                try
                {
                    EnsureDirectory(polPath);
                if (!isMachine) EnsureGptIni();
                var entries = ReadPolEntries(polPath);

                    // Удаляем старую запись если есть
                    entries.RemoveAll(e =>
                        e.KeyPath.Equals(keyPath,
                            StringComparison.OrdinalIgnoreCase) &&
                        e.ValueName.Equals(valueName,
                            StringComparison.OrdinalIgnoreCase));

                    // Добавляем новую
                    entries.Add(new PolEntry
                    {
                        KeyPath = keyPath,
                        ValueName = valueName,
                        Type = 4, // REG_DWORD
                        Data = BitConverter.GetBytes(value)
                    });

                    WritePolFile(polPath, entries);
                    Logger.Info($"GPO запись: {keyPath}\\{valueName} = {value}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"GPO WritePolEntry ошибка: {ex.Message}");
                }
            }

            private static void WritePolEntryString(string polPath,
                string keyPath, string valueName, string value,
                bool isMachine)
            {
                try
                {
                    EnsureDirectory(polPath);
                if (!isMachine) EnsureGptIni();
                var entries = ReadPolEntries(polPath);

                    entries.RemoveAll(e =>
                        e.KeyPath.Equals(keyPath,
                            StringComparison.OrdinalIgnoreCase) &&
                        e.ValueName.Equals(valueName,
                            StringComparison.OrdinalIgnoreCase));

                    // REG_SZ — строка в UTF-16LE с нулевым символом в конце
                    var strBytes = Encoding.Unicode.GetBytes(value + "\0");
                    entries.Add(new PolEntry
                    {
                        KeyPath = keyPath,
                        ValueName = valueName,
                        Type = 1, // REG_SZ
                        Data = strBytes
                    });

                    WritePolFile(polPath, entries);
                    Logger.Info($"GPO запись: {keyPath}\\{valueName} = {value}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"GPO WritePolEntryString ошибка: {ex.Message}");
                }
            }

            private static void RemovePolEntry(string polPath,
                string keyPath, string valueName)
            {
                try
                {
                    if (!File.Exists(polPath)) return;

                    var entries = ReadPolEntries(polPath);
                    int removed = entries.RemoveAll(e =>
                        e.KeyPath.Equals(keyPath,
                            StringComparison.OrdinalIgnoreCase) &&
                        e.ValueName.Equals(valueName,
                            StringComparison.OrdinalIgnoreCase));

                    if (removed > 0)
                    {
                        WritePolFile(polPath, entries);
                        Logger.Info($"GPO удалено: {keyPath}\\{valueName}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"GPO RemovePolEntry ошибка: {ex.Message}");
                }
            }

            // =====================
            // Чтение .pol файла
            // =====================
            private static List<PolEntry> ReadPolEntries(string polPath)
            {
                var entries = new List<PolEntry>();

                if (!File.Exists(polPath))
                    return entries;

                try
                {
                    var fileBytes = File.ReadAllBytes(polPath);

                    // Проверяем минимальную длину и сигнатуру PReg
                    if (fileBytes.Length < 8) return entries;
                    if (fileBytes[0] != 0x50 || fileBytes[1] != 0x52 ||
                        fileBytes[2] != 0x65 || fileBytes[3] != 0x67)
                        return entries;

                    int pos = 8; // Пропускаем заголовок

                    while (pos < fileBytes.Length - 4)
                    {
                        // Каждая запись начинается с '[' в UTF-16LE = 0x5B 0x00
                        if (pos + 2 > fileBytes.Length) break;
                        ushort bracket = BitConverter.ToUInt16(fileBytes, pos);
                        if (bracket != '[') break;
                        pos += 2;

                        var entry = new PolEntry();

                        // KeyPath (до нулевого символа \0\0 в UTF-16LE)
                        entry.KeyPath = ReadWideString(fileBytes, ref pos);

                        // ';' разделитель
                        if (pos + 2 > fileBytes.Length) break;
                        if (BitConverter.ToUInt16(fileBytes, pos) != ';') break;
                        pos += 2;

                        // ValueName
                        entry.ValueName = ReadWideString(fileBytes, ref pos);

                        // ';' разделитель
                        if (pos + 2 > fileBytes.Length) break;
                        if (BitConverter.ToUInt16(fileBytes, pos) != ';') break;
                        pos += 2;

                        // Type (4 байта)
                        if (pos + 4 > fileBytes.Length) break;
                        entry.Type = BitConverter.ToInt32(fileBytes, pos);
                        pos += 4;

                        // ';' разделитель
                        if (pos + 2 > fileBytes.Length) break;
                        if (BitConverter.ToUInt16(fileBytes, pos) != ';') break;
                        pos += 2;

                        // Size (4 байта)
                        if (pos + 4 > fileBytes.Length) break;
                        int size = BitConverter.ToInt32(fileBytes, pos);
                        pos += 4;

                        // ';' разделитель
                        if (pos + 2 > fileBytes.Length) break;
                        if (BitConverter.ToUInt16(fileBytes, pos) != ';') break;
                        pos += 2;

                        // Data
                        if (pos + size > fileBytes.Length) break;
                        entry.Data = new byte[size];
                        Array.Copy(fileBytes, pos, entry.Data, 0, size);
                        pos += size;

                        // ']' завершающий символ
                        if (pos + 2 > fileBytes.Length) break;
                        if (BitConverter.ToUInt16(fileBytes, pos) != ']') break;
                        pos += 2;

                        entries.Add(entry);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"GPO чтение файла: {ex.Message}");
                }

                return entries;
            }

            // Читает строку UTF-16LE из массива байт до нулевого символа
            private static string ReadWideString(byte[] data, ref int pos)
            {
                var sb = new StringBuilder();
                while (pos + 2 <= data.Length)
                {
                    ushort ch = BitConverter.ToUInt16(data, pos);
                    pos += 2;
                    if (ch == 0) break; // Нулевой символ — конец строки
                    sb.Append((char)ch);
                }
                return sb.ToString();
            }

            // =====================
            // Запись .pol файла
            // =====================
            private static void WritePolFile(string polPath,
                List<PolEntry> entries)
            {
                using var writer = new BinaryWriter(
                    File.Open(polPath, FileMode.Create,
                        FileAccess.Write, FileShare.None));

                // Заголовок
                writer.Write(PolHeader);

                // Записи
                foreach (var entry in entries)
                {
                    // '[' + KeyPath + '\0' + ';' + ValueName + '\0' +
                    // ';' + Type(4) + ';' + Size(4) + ';' + Data + ']'
                    writer.Write((ushort)'[');

                    writer.Write(Encoding.Unicode.GetBytes(
                        entry.KeyPath + "\0"));
                    writer.Write((ushort)';');

                    writer.Write(Encoding.Unicode.GetBytes(
                        entry.ValueName + "\0"));
                    writer.Write((ushort)';');

                    writer.Write(entry.Type);
                    writer.Write((ushort)';');

                    writer.Write(entry.Data.Length);
                    writer.Write((ushort)';');

                    writer.Write(entry.Data);

                    writer.Write((ushort)']');
                }
            }

            // =====================
            // Вспомогательные методы
            // =====================
        
        

            private static void EnsureDirectory(string filePath)
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) &&
                    !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }

            // =====================
            // Модель записи политики
            // =====================
            private class PolEntry
            {
                public string KeyPath { get; set; } = "";
                public string ValueName { get; set; } = "";
                public int Type { get; set; } = 4; // REG_DWORD по умолчанию
                public byte[] Data { get; set; } = Array.Empty<byte>();
            }
        }
    }