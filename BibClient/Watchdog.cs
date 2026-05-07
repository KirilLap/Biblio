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
            
            // КРИТИЧЕСКИ ВАЖНО: Получаем и кэшируем путь к основному приложению СРАЗУ при старте
            // Пока процесс Guardian еще существует и может получить свой MainModule.FileName
            string clientExePath;
            try
            {
                clientExePath = GetExePath();
            }
            catch (Exception ex)
            {
                Logger.Error($"❌ Guardian не смог определить путь к BibClient: {ex.Message}");
                return;
            }
            
            // Проверяем что файл существует
            if (!File.Exists(clientExePath))
            {
                Logger.Error($"❌ Файл BibClient не найден по пути: {clientExePath}");
                return;
            }
            
            Logger.Info($"🛡️ Guardian следит за: {clientExePath}");
            
            // Сохраняем рабочую директорию
            string workingDir = Path.GetDirectoryName(clientExePath) ?? "";
            Logger.Info($"🛡️ Рабочая директория: {workingDir}");
            
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
            
            // Флаг чтобы избежать множественных попыток перезапуска
            bool restartAttempted = false;
            DateTime lastRestartAttempt = DateTime.MinValue;
            
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
                Process? mainProcess = null;
                
                try
                {
                    var allProcesses = Process.GetProcesses();
                    
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
                            catch (Win32Exception)
                            {
                                // Процесс может быть недоступен для чтения MainModule (AccessDenied)
                                // В этом случае проверяем по имени процесса как фоллбэк
                                if (p.ProcessName.Equals("BibClient", StringComparison.OrdinalIgnoreCase))
                                {
                                    mainProcess = p;
                                    break;
                                }
                            }
                            catch { }
                        }
                        finally
                        {
                            p?.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"⚠️ Ошибка enumeration процессов: {ex.Message}");
                }

                if (mainProcess == null)
                {
                    // Основной процесс убит — перезапускаем
                    // Но не чаще чем раз в 10 секунд чтобы избежать цикла перезапусков
                    if (!restartAttempted || (DateTime.Now - lastRestartAttempt).TotalSeconds > 10)
                    {
                        Logger.Info("⚠️ BibClient не найден, перезапуск...");
                        restartAttempted = true;
                        lastRestartAttempt = DateTime.Now;
                        
                        try
                        {
                            // Явно указываем FileName и WorkingDirectory
                            var startInfo = new ProcessStartInfo
                            {
                                FileName = clientExePath,
                                Arguments = "", // Запускаем без аргументов - это будет основной процесс
                                UseShellExecute = true,
                                WorkingDirectory = workingDir,
                                CreateNoWindow = false
                            };
                            
                            // Проверяем что файл все еще существует перед запуском
                            if (!File.Exists(clientExePath))
                            {
                                Logger.Error($"❌ Файл BibClient исчез: {clientExePath}");
                            }
                            else
                            {
                                Process.Start(startInfo);
                                Logger.Info($"✅ BibClient перезапущен: {clientExePath}");
                                
                                // Сбрасываем флаг после успешного запуска
                                restartAttempted = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"❌ Ошибка перезапуска BibClient: {ex.Message}");
                            Logger.Error($"   StackTrace: {ex.StackTrace}");
                            Logger.Error($"   FileName: {clientExePath}");
                            Logger.Error($"   WorkingDirectory: {workingDir}");
                        }
                    }
                    else
                    {
                        Logger.Info("⏳ Предыдущий перезапуск был менее 10 секунд назад, пропускаем");
                    }
                }
                else
                {
                    // Процесс найден - сбрасываем флаг перезапуска
                    restartAttempted = false;
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
