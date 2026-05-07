using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace BibClient
{
    public static class Watchdog
    {
        // Имя мьютекса для сигнала легального закрытия
        private const string LEGAL_CLOSE_MUTEX_NAME = "Global\\BibClient_LegalClose";
        
        // Мьютекс для предотвращения множественного запуска основного приложения
        private const string APP_MUTEX_NAME = "Global\\BibClient_SingleInstance";
        
        private static Mutex? _appMutex;

        // Прописываем автозапуск в реестр с полным путем
        public static void RegisterAutostart()
        {
            try
            {
                // Получаем полный путь к исполняемому файлу
                string exePath = GetExePath();

                // Проверяем что файл существует
                if (!File.Exists(exePath))
                {
                    Logger.Error($"❌ Файл не найден для автозапуска: {exePath}");
                    return;
                }

                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);

                if (key == null)
                {
                    Logger.Error("❌ Не удалось открыть ключ реестра для автозапуска");
                    return;
                }

                // Регистрируем с полным путем в кавычках (для путей с пробелами)
                string registryValue = $"\"{exePath}\"";
                key.SetValue("BibClient", registryValue, RegistryValueKind.String);
                
                // Проверяем что значение записалось
                var savedValue = key.GetValue("BibClient");
                if (savedValue != null && savedValue.ToString() == registryValue)
                {
                    Logger.Info($"✅ Автозапуск зарегистрирован: {exePath}");
                }
                else
                {
                    Logger.Warn($"⚠️ Автозапуск записан, но значение отличается: {savedValue}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"❌ Ошибка регистрации автозапуска: {ex.Message}");
                Logger.Error($"StackTrace: {ex.StackTrace}");
            }
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
        
        // Получаем полный путь к exe файлу приложения
        public static string GetExePath()
        {
            // Пробуем получить путь разными способами
            try
            {
                // Способ 1: Через MainModule (наиболее надежный)
                var path = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return Path.GetFullPath(path);
            }
            catch { }
            
            try
            {
                // Способ 2: Через BaseDirectory + процесс
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var exeName = Process.GetCurrentProcess().ProcessName + ".exe";
                var path = Path.Combine(baseDir, exeName);
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }
            catch { }
            
            // Если ничего не помогло, возвращаем путь из аргументов командной строки
            return Process.GetCurrentProcess().StartInfo.FileName;
        }
        
        // Проверка, является ли текущий процесс единственным экземпляром
        public static bool EnsureSingleInstance()
        {
            try
            {
                _appMutex = new Mutex(true, APP_MUTEX_NAME, out bool createdNew);
                
                if (!createdNew)
                {
                    // Другой экземпляр уже работает
                    Logger.Info("⚠️ Обнаружен другой работающий экземпляр BibClient");
                    _appMutex?.Dispose();
                    _appMutex = null;
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка проверки одиночного экземпляра: {ex.Message}");
                return true; // В случае ошибки разрешаем запуск
            }
        }
        
        // Освобождаем мьютекс при корректном закрытии
        public static void ReleaseSingleInstance()
        {
            try
            {
                _appMutex?.ReleaseMutex();
                _appMutex?.Dispose();
                _appMutex = null;
            }
            catch { }
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

            // Получаем полный путь к exe файлу
            string exePath = GetExePath();
            
            // Проверяем, не запущен ли уже guardian - ищем процессы с тем же exe путем
            var allProcesses = Process.GetProcesses();
            bool guardianRunning = false;
            foreach (var p in allProcesses)
            {
                try
                {
                    if (p.Id == Process.GetCurrentProcess().Id)
                        continue;
                    
                    var otherPath = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(otherPath) && 
                        otherPath.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        // Проверяем аргументы командной строки через WMI или эвристику
                        // Если это второй экземпляр того же exe - считаем его guardian'ом
                        guardianRunning = true;
                        break;
                    }
                }
                catch { }
                finally { p?.Dispose(); }
            }
            
            if (guardianRunning)
            {
                Logger.Info("🛡️ Guardian уже запущен (проверка по процессам)");
                return;
            }

            // Запускаем guardian как отдельный процесс с флагом --guardian
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--guardian",
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
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
                        g.WaitForExit(1000);
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
            
            // Получаем путь к основному приложению (используем тот же exe файл)
            string clientExePath = GetExePath();
            Logger.Info($"🛡️ Guardian следит за: {clientExePath}");
            
            // Проверяем мьютекс легального закрытия при старте
            bool legalCloseSignaled = false;
            if (Mutex.TryOpenExisting(LEGAL_CLOSE_MUTEX_NAME, out var existingMutex))
            {
                using (existingMutex)
                {
                    legalCloseSignaled = existingMutex.WaitOne(0);
                }
            }
            
            if (legalCloseSignaled)
            {
                Logger.Info("🔓 Обнаружен сигнал легального закрытия при старте Guardian - завершение работы");
                return;
            }
            
            while (true)
            {
                Thread.Sleep(3000);
                
                // Периодически проверяем сигнал легального закрытия
                if (Mutex.TryOpenExisting(LEGAL_CLOSE_MUTEX_NAME, out var existingMutex2))
                {
                    using (existingMutex2)
                    {
                        if (existingMutex2.WaitOne(0))
                        {
                            Logger.Info("🔓 Получен сигнал легального закрытия - завершение работы Guardian");
                            return;
                        }
                    }
                }

                // Проверяем, существует ли основной процесс BibClient (не guardian)
                var allProcesses = Process.GetProcesses();
                Process? mainProcess = null;
                
                foreach (var p in allProcesses)
                {
                    try
                    {
                        // Пропускаем самого себя (guardian)
                        if (p.Id == Process.GetCurrentProcess().Id)
                            continue;
                        
                        // Проверяем что это тот же самый exe файл
                        try
                        {
                            var otherPath = p.MainModule?.FileName;
                            
                            if (!string.IsNullOrEmpty(otherPath) && 
                                otherPath.Equals(clientExePath, StringComparison.OrdinalIgnoreCase))
                            {
                                mainProcess = p;
                                break;
                            }
                        }
                        catch { }
                    }
                    catch { }
                    finally
                    {
                        p?.Dispose();
                    }
                }

                if (mainProcess == null)
                {
                    // Основной процесс убит — перезапускаем
                    Logger.Info("⚠️ BibClient не найден, перезапуск...");
                    try
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = clientExePath,
                            UseShellExecute = true,
                            WorkingDirectory = Path.GetDirectoryName(clientExePath) ?? ""
                        };
                        
                        Process.Start(startInfo);
                        Logger.Info("✅ BibClient перезапущен");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Ошибка перезапуска BibClient: {ex.Message}");
                    }
                }
                else
                {
                    mainProcess.Dispose();
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
