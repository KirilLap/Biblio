using System;
using Microsoft.Win32;

namespace BibClient
{
    // Применяет политики ограничений напрямую в реестр HKCU.
    // Работает на всех версиях Windows (Home/Pro/Enterprise), вступает в силу немедленно.
    internal static class RegistryPolicyEngine
    {
        private const string SysPolicy = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
        private const string WinSysPolicy = @"Software\Policies\Microsoft\Windows\System";
        private const string PsPolicy = @"Software\Policies\Microsoft\Windows\PowerShell";

        // Применить все политики из текущих настроек — вызывать при старте приложения
        public static void ApplyAll()
        {
            var s = SettingsManager.Current;
            SetTaskMgrBlock(s.TaskMgrDisabled);
            SetRegeditBlock(s.BlockRegedit);
            SetCmdBlock(s.BlockCmd);
            SetPowerShellBlock(s.BlockPowerShell);
        }

        public static void SetTaskMgrBlock(bool block)
        {
            SetDword(SysPolicy, "DisableTaskMgr", block ? 1 : null);
        }

        public static void SetRegeditBlock(bool block)
        {
            SetDword(SysPolicy, "DisableRegistryTools", block ? 1 : null);
        }

        public static void SetCmdBlock(bool block)
        {
            // 1 = CMD отключён, выполнение скриптов запрещено
            SetDword(WinSysPolicy, "DisableCMD", block ? 1 : null);
        }

        public static void SetPowerShellBlock(bool block)
        {
            SetDword(PsPolicy, "EnableScripts", block ? 0 : null);
        }

        private static void SetDword(string subKey, string name, int? value)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
                if (value.HasValue)
                    key.SetValue(name, value.Value, RegistryValueKind.DWord);
                else
                    key.DeleteValue(name, throwOnMissingValue: false);

                Logger.Info($"Registry HKCU\\{subKey}\\{name} = {(value.HasValue ? value.Value.ToString() : "(удалено)")}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Registry ошибка {subKey}\\{name}: {ex.Message}");
            }
        }
    }
}
