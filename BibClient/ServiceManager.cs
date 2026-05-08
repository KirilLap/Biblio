using System;
using System.Diagnostics;
using System.IO;

namespace BibClient
{
    // Управляет Windows-службой BibClientWatchdog, которая перезапускает BibClient
    // если он был убит через диспетчер задач.
    // Служба запускается как SYSTEM, поэтому не видна в списке процессов обычного пользователя.
    public static class ServiceManager
    {
        private const string ServiceName = "BibClientWatchdog";
        private const string ServiceDisplayName = "BibClient Watchdog";

        private static readonly string ServiceExePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "BibClientService.exe"
        );

        // Флаговый файл — сигнализирует службе что закрытие легальное (не перезапускать 30 мин)
        private static readonly string LegalCloseFlagPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "BibClientLegalClose.flag"
        );

        // Вызывается при старте BibClient — проверяет что служба установлена и запущена
        public static void EnsureServiceRunning()
        {
            try
            {
                // Удаляем флаг предыдущего легального закрытия (BibClient снова запустился)
                ClearLegalCloseFlag();

                if (!File.Exists(ServiceExePath))
                {
                    Logger.Warn($"⚠️ BibClientService.exe не найден: {ServiceExePath}");
                    Logger.Warn("⚠️ Служба защиты от закрытия недоступна");
                    return;
                }

                if (!IsServiceInstalled())
                {
                    Logger.Info("🔧 Устанавливаем службу BibClientWatchdog...");
                    InstallService();
                }
                else
                {
                    Logger.Info("✅ Служба BibClientWatchdog уже установлена");
                    // Обновляем binPath на случай если exe переместили (те же правила кавычек)
                    RunSc($"config {ServiceName} binPath= \"\\\"{ServiceExePath}\\\"\"");
                }

                StartService();
            }
            catch (Exception ex)
            {
                Logger.Error($"❌ Ошибка установки/запуска службы: {ex.Message}");
            }
        }

        // Создаёт флаговый файл — служба не перезапустит BibClient 30 минут
        public static void SignalLegalClose()
        {
            try
            {
                File.WriteAllText(LegalCloseFlagPath, DateTime.UtcNow.ToString("O"));
                Logger.Info("🔓 Флаг легального закрытия создан");
            }
            catch (Exception ex)
            {
                Logger.Error($"❌ Ошибка создания флага легального закрытия: {ex.Message}");
            }
        }

        private static void ClearLegalCloseFlag()
        {
            try
            {
                if (File.Exists(LegalCloseFlagPath))
                {
                    File.Delete(LegalCloseFlagPath);
                    Logger.Info("🗑️ Флаг легального закрытия удалён");
                }
            }
            catch { }
        }

        private static bool IsServiceInstalled()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"query {ServiceName}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        private static void InstallService()
        {
            // binPath должен содержать кавычки ВНУТРИ значения если путь содержит пробелы.
            // SCM сохраняет строку binPath дословно в реестре и использует её для запуска.
            // Без внутренних кавычек "C:\Program Files\..." → SCM запускает "C:\Program" → ошибка 1053.
            int exitCode = RunSc($"create {ServiceName} binPath= \"\\\"{ServiceExePath}\\\"\" start= auto DisplayName= \"{ServiceDisplayName}\"");
            if (exitCode != 0)
            {
                Logger.Error($"❌ Не удалось создать службу (exit code {exitCode})");
                return;
            }

            // Политика восстановления: перезапускать при сбое (сброс счётчика через 0 сек)
            RunSc($"failure {ServiceName} reset= 0 actions= restart/5000/restart/5000/restart/5000");

            Logger.Info("✅ Служба BibClientWatchdog установлена");
        }

        private static void StartService()
        {
            // sc start не возвращает ошибку если служба уже запущена — exit code 1056
            int exitCode = RunSc($"start {ServiceName}");
            if (exitCode == 0)
            {
                Logger.Info("✅ Служба BibClientWatchdog запущена");
            }
            else if (exitCode == 1056)
            {
                Logger.Info("ℹ️ Служба BibClientWatchdog уже запущена");
            }
            else if (exitCode == 1053)
            {
                // Служба не ответила — скорее всего зарегистрирована с неправильным binPath.
                // Переустанавливаем с правильными кавычками вокруг пути.
                Logger.Warn("⚠️ Ошибка 1053 — переустанавливаем службу с корректным binPath...");
                RunSc($"stop {ServiceName}");
                RunSc($"delete {ServiceName}");
                System.Threading.Thread.Sleep(1000);
                InstallService();
                int retryCode = RunSc($"start {ServiceName}");
                if (retryCode == 0)
                    Logger.Info("✅ Служба запущена после переустановки");
                else
                    Logger.Error($"❌ Не удалось запустить службу после переустановки (exit={retryCode})");
            }
            else
            {
                Logger.Warn($"⚠️ sc start вернул код {exitCode}");
            }
        }

        // Возвращает exit code sc.exe
        private static int RunSc(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return -1;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(10000);
                Logger.Info($"sc {arguments.Split(' ')[0]}: exit={p.ExitCode}");
                if (!string.IsNullOrWhiteSpace(output))
                    Logger.Info($"  → {output.Trim()}");
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                Logger.Error($"❌ Ошибка sc.exe: {ex.Message}");
                return -1;
            }
        }
    }
}
