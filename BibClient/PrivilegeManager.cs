using System;
using System.Diagnostics;
using System.Security.Principal;

namespace BibClient
{
    public static class PrivilegeManager
    {
        public static bool IsAdmin =>
            new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        public static void EnsureAutoStartWithAdmin()
        {
            if (!IsAdmin)
            {
                Logger.Warn("Приложение запущено без прав администратора. Функции политик будут ограничены.");
                return;
            }

            CreateScheduledTask();
        }

        private static void CreateScheduledTask()
        {
            string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;

            string taskName = "BibClient_AutoStart";
            if (IsTaskExists(taskName)) return;

            string args = $"/create /tn \"{taskName}\" /tr \"\\\"{exePath}\\\" /silent\" /sc onlogon /rl highest /f";

            try
            {
                var psi = new ProcessStartInfo("schtasks", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit();
                Logger.Info($"Задача планировщика '{taskName}' успешно создана.");
            }
            catch (Exception ex)
            {
                Logger.Error($"Не удалось создать задачу планировщика: {ex.Message}");
            }
        }

        private static bool IsTaskExists(string taskName)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks", $"/query /tn \"{taskName}\" /fo list")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit();
                return proc?.ExitCode == 0;
            }
            catch { return false; }
        }
    }
}