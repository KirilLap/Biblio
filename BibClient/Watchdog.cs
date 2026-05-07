using Microsoft.Win32;
using System;
using System.Collections.Generic;
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
        
        // Имя мьютекса для сигнала легального закрытия
        private const string LEGAL_CLOSE_MUTEX_NAME = "Global\\BibClient_LegalClose";

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

        // Запускаем watchdog в отдельном потоке внутри основного процесса
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

        // Запускает внешний процесс-наблюдатель (Guardian)
        // Guardian работает отдельно от основного процесса и перезапускает его при падении
        public static void StartGuardian(bool preventClose)
        {
            if (!preventClose)
            {
                StopGuardian();
                return;
            }

            _exePath = Process.GetCurrentProcess().MainModule!.FileName;
            
            // Проверяем, не запущен ли уже guardian
            var existing = Process.GetProcessesByName("BibClientGuardian");
            if (existing.Length > 0)
                return;

            // Запускаем guardian как отдельный процесс с флагом --guardian
            var startInfo = new ProcessStartInfo
            {
                FileName = _exePath,
                Arguments = "--guardian",
                UseShellExecute = true,
                CreateNoWindow = true
            };
            
            try
            {
                Process.Start(startInfo);
                Logger.Info("🛡️ Guardian запущен");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка запуска Guardian: {ex.Message}");
            }
        }

        // Останавливает guardian (сигнализирует о легальном закрытии)
        public static void StopGuardian()
        {
            try
            {
                // Сигнализируем всем guardian'ам что закрытие легальное
                using var mutex = new Mutex(false, LEGAL_CLOSE_MUTEX_NAME);
                mutex.WaitOne(); // Устанавливаем сигнал
                
                var guardians = Process.GetProcessesByName("BibClientGuardian");
                foreach (var g in guardians)
                {
                    try
                    {
                        g.Kill();
                    }
                    catch { }
                }
                
                Logger.Info("🔓 Guardian остановлен");
            }
            catch { }
        }

        // Точка входа для guardian процесса
        public static void RunGuardian()
        {
            Logger.Info("🛡️ Guardian процесс запущен");
            
            while (true)
            {
                Thread.Sleep(3000);

                // Проверяем, существует ли основной процесс BibClient
                var processes = Process.GetProcessesByName("BibClient");
                
                // Фильтруем только основные процессы (не guardian)
                var mainProcesses = new List<Process>();
                foreach (var p in processes)
                {
                    try
                    {
                        // Пропускаем самого себя (guardian)
                        if (p.Id == Process.GetCurrentProcess().Id)
                            continue;
                        
                        // Проверяем что это тот же самый exe файл
                        var currentPath = Process.GetCurrentProcess().MainModule?.FileName;
                        var otherPath = p.MainModule?.FileName;
                        
                        if (currentPath != null && otherPath != null && 
                            currentPath.Equals(otherPath, StringComparison.OrdinalIgnoreCase))
                        {
                            mainProcesses.Add(p);
                        }
                    }
                    catch { }
                }

                if (mainProcesses.Count == 0)
                {
                    // Основной процесс убит — перезапускаем
                    Logger.Info("⚠️ BibClient не найден, перезапуск...");
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = _exePath,
                            UseShellExecute = true
                        });
                        Logger.Info("✅ BibClient перезапущен");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Ошибка перезапуска BibClient: {ex.Message}");
                    }
                }
            }
        }
        
        // Сигнал о легальном закрытии (после ввода пароля администратором)
        public static void SignalLegalClose()
        {
            try
            {
                // Создаём именованный мьютекс как сигнал для guardian
                using var mutex = new Mutex(false, LEGAL_CLOSE_MUTEX_NAME);
                mutex.WaitOne(); // Устанавливаем мьютекс
                
                Logger.Info("🔓 Сигнал легального закрытия отправлен");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка сигнала легального закрытия: {ex.Message}");
            }
        }
    }
}