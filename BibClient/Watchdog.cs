using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace BibClient
{
    public static class Watchdog
    {
        private static Thread? _thread;
        private static bool _running = false;
        private static string _exePath = "";

        // Прописываем автозапуск в реестр
        public static void RegisterAutostart()
        {
            try
            {
                _exePath = Process.GetCurrentProcess().MainModule!.FileName;

                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);

                key?.SetValue("BibClient", $"\"{_exePath}\"");
            }
            catch { }
        }

        // Убираем автозапуск из реестра
        public static void UnregisterAutostart()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                key?.DeleteValue("BibClient", false);
            }
            catch { }
        }

        // Запускаем watchdog в отдельном потоке
        public static void Start()
        {
            _running = true;
            _exePath = Process.GetCurrentProcess().MainModule!.FileName;

            _thread = new Thread(WatchdogLoop)
            {
                IsBackground = true,
                Name = "BibClientWatchdog"
            };
            _thread.Start();
        }

        public static void Stop()
        {
            _running = false;
        }

        private static void WatchdogLoop()
        {
            // Watchdog следит за файлом-флагом
            // Если файл исчез — значит нас убили, перезапускаемся
            string flagPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "running.flag");

            // Создаём флаг
            File.WriteAllText(flagPath, Process.GetCurrentProcess().Id.ToString());

            while (_running)
            {
                Thread.Sleep(2000);

                // Проверяем что наш процесс ещё жив
                // Если нет — перезапускаем
                try
                {
                    var currentId = Process.GetCurrentProcess().Id;
                    var flagContent = File.Exists(flagPath)
                        ? File.ReadAllText(flagPath) : "";

                    if (flagContent != currentId.ToString())
                    {
                        // Флаг изменился — нас перезапустили
                        break;
                    }
                }
                catch { break; }
            }
        }

        // Второй экземпляр — следит за первым и перезапускает
        public static void StartGuardian()
        {
            _exePath = Process.GetCurrentProcess().MainModule!.FileName;

            var guardian = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(3000);

                    // Ищем процесс BibClient
                    var processes = Process.GetProcessesByName("BibClient");

                    if (processes.Length == 0)
                    {
                        // Процесс убили — перезапускаем
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = _exePath,
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    }
                }
            })
            {
                IsBackground = true,
                Name = "Guardian"
            };
            guardian.Start();
        }
    }
}