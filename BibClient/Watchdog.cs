using Microsoft.Win32;
using System;
using System.ComponentModel;
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
        // Этот метод должен работать надежно даже когда основной процесс завершен
        public static string GetExePath()
        {
            // Способ 1: Через MainModule (наиболее надежный для текущего процесса)
            try
            {
                var currentProcess = Process.GetCurrentProcess();
                var path = currentProcess.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var fullPath = Path.GetFullPath(path);
                    Logger.Info($"📍 GetExePath (MainModule): {fullPath}");
                    return fullPath;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"⚠️ GetExePath способ 1 (MainModule) не сработал: {ex.Message}");
            }
            
            // Способ 2: Через BaseDirectory + имя процесса
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
                {
                    var exeName = Process.GetCurrentProcess().ProcessName + ".exe";
                    var path = Path.Combine(baseDir, exeName);
                    if (File.Exists(path))
                    {
                        var fullPath = Path.GetFullPath(path);
                        Logger.Info($"📍 GetExePath (BaseDirectory): {fullPath}");
                        return fullPath;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"⚠️ GetExePath способ 2 (BaseDirectory) не сработал: {ex.Message}");
            }
            
            // Способ 3: Через Assembly.Location (для .NET Framework / .NET Core)
            try
            {
                var assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(assemblyPath) && File.Exists(assemblyPath))
                {
                    var fullPath = Path.GetFullPath(assemblyPath);
                    Logger.Info($"📍 GetExePath (Assembly.Location): {fullPath}");
                    return fullPath;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"⚠️ GetExePath способ 3 (Assembly.Location) не сработал: {ex.Message}");
            }
            
            // Способ 4: Через Environment.ProcessPath (.NET 5+)
            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var fullPath = Path.GetFullPath(path);
                    Logger.Info($"📍 GetExePath (Environment.ProcessPath): {fullPath}");
                    return fullPath;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"⚠️ GetExePath способ 4 (Environment.ProcessPath) не сработал: {ex.Message}");
            }
            
            // Фоллбэк: возвращаем что есть
            var fallbackPath = Process.GetCurrentProcess().StartInfo.FileName;
            Logger.Warn($"⚠️ GetExePath использует фоллбэк: {fallbackPath}");
            return fallbackPath;
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

            // Получаем полный путь к exe файлу основного приложения
            string clientExePath = GetExePath();
            string baseDir = Path.GetDirectoryName(clientExePath) ?? "";
            string guardianExePath = Path.Combine(baseDir, "BibClientGuardian.exe");
            
            // Проверяем существует ли файл Guardian
            if (!File.Exists(guardianExePath))
            {
                Logger.Error($"❌ BibClientGuardian.exe не найден по пути: {guardianExePath}");
                Logger.Error("⚠️ Защита от закрытия не будет работать!");
                return;
            }
            
            // Проверяем, не запущен ли уже guardian
            var guardianProcesses = Process.GetProcessesByName("BibClientGuardian");
            if (guardianProcesses.Length > 0)
            {
                Logger.Info("🛡️ Guardian уже запущен");
                foreach (var p in guardianProcesses) p?.Dispose();
                return;
            }

            // Запускаем Guardian как отдельный процесс
            var startInfo = new ProcessStartInfo
            {
                FileName = guardianExePath,
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = baseDir,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            
            try
            {
                Process.Start(startInfo);
                Logger.Info($"🛡️ Guardian запущен: {guardianExePath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"❌ Ошибка запуска Guardian: {ex.Message}");
                Logger.Error($"   StackTrace: {ex.StackTrace}");
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
                        if (!g.HasExited)
                        {
                            g.Kill();
                            g.WaitForExit(1000);
                        }
                    }
                    catch { }
                    finally { g?.Dispose(); }
                }
                
                Logger.Info("🔓 Guardian остановлен");
            }
            catch { }
        }

        // Точка входа для guardian процесса (устаревший метод - теперь используется отдельный exe)
        [Obsolete("Теперь Guardian работает как отдельный процесс BibClientGuardian.exe")]
        public static void RunGuardian()
        {
            Logger.Info("⚠️ RunGuardian вызван - этот метод устарел, используйте отдельный BibClientGuardian.exe");
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
