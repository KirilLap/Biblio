using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;

namespace BibClientGuardian
{
    internal class Program
    {
        // Имя мьютекса для сигнала легального закрытия (должно совпадать с Watchdog.cs)
        private const string LEGAL_CLOSE_MUTEX_NAME = "Global\\BibClient_LegalClose";
        
        // Путь к основному приложению BibClient.exe
        private static readonly string ClientExePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, 
            "BibClient.exe"
        );

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;

        static void Main(string[] args)
        {
            // Скрываем консольное окно СРАЗУ при запуске
            HideConsoleWindow();

            // Проверяем что основной файл существует
            if (!File.Exists(ClientExePath))
            {
                Log($"❌ BibClient.exe не найден по пути: {ClientExePath}");
                return;
            }

            Log($"🛡️ Guardian запущен. Следит за: {ClientExePath}");

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
                Log("🔓 Обнаружен сигнал легального закрытия при старте - завершение работы");
                return;
            }

            DateTime lastRestartAttempt = DateTime.MinValue;
            bool restartAttempted = false;

            while (true)
            {
                Thread.Sleep(3000);

                // Проверяем сигнал легального закрытия
                if (Mutex.TryOpenExisting(LEGAL_CLOSE_MUTEX_NAME, out var existingMutex2))
                {
                    using (existingMutex2)
                    {
                        if (existingMutex2.WaitOne(0))
                        {
                            Log("🔓 Получен сигнал легального закрытия - завершение работы Guardian");
                            return;
                        }
                    }
                }

                // Проверяем, существует ли процесс BibClient
                Process? mainProcess = null;

                try
                {
                    var processes = Process.GetProcessesByName("BibClient");
                    foreach (var p in processes)
                    {
                        try
                        {
                            // Проверяем путь к exe файлу процесса
                            var processPath = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(processPath) &&
                                processPath.Equals(ClientExePath, StringComparison.OrdinalIgnoreCase))
                            {
                                mainProcess = p;
                                break;
                            }
                        }
                        catch
                        {
                            // Процесс может быть недоступен (например, если это другой пользователь или системный процесс)
                        }
                        finally
                        {
                            p?.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"⚠️ Ошибка поиска процесса: {ex.Message}");
                }

                if (mainProcess == null || mainProcess.HasExited)
                {
                    mainProcess?.Dispose();
                    
                    // Основной процесс не найден или завершился - перезапускаем
                    if (!restartAttempted || (DateTime.Now - lastRestartAttempt).TotalSeconds > 10)
                    {
                        Log("⚠️ BibClient не найден или завершился, перезапуск...");
                        restartAttempted = true;
                        lastRestartAttempt = DateTime.Now;

                        try
                        {
                            var startInfo = new ProcessStartInfo
                            {
                                FileName = ClientExePath,
                                UseShellExecute = true,
                                WorkingDirectory = Path.GetDirectoryName(ClientExePath) ?? "",
                                CreateNoWindow = false
                            };

                            if (!File.Exists(ClientExePath))
                            {
                                Log($"❌ Файл BibClient исчез: {ClientExePath}");
                            }
                            else
                            {
                                Process.Start(startInfo);
                                Log($"✅ BibClient перезапущен");
                                restartAttempted = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"❌ Ошибка перезапуска BibClient: {ex.Message}\n{ex.StackTrace}");
                        }
                    }
                    else
                    {
                        Log("⏳ Предыдущий перезапуск был менее 10 секунд назад, пропускаем");
                    }
                }
                else
                {
                    // Процесс найден и работает - сбрасываем флаг
                    restartAttempted = false;
                    mainProcess.Dispose();
                }
            }
        }

        static void HideConsoleWindow()
        {
            try
            {
                // Получаем дескриптор консольного окна и скрываем его
                IntPtr consoleHandle = GetConsoleWindow();
                if (consoleHandle != IntPtr.Zero)
                {
                    ShowWindow(consoleHandle, SW_HIDE);
                }
            }
            catch (Exception ex)
            {
                // Игнорируем ошибки скрытия окна
                try
                {
                    Log($"⚠️ Не удалось скрыть консоль: {ex.Message}");
                }
                catch { }
            }
        }

        static void Log(string message)
        {
            try
            {
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GuardianLog.txt");
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.AppendAllText(logPath, $"[{timestamp}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Игнорируем ошибки логирования
            }
        }
    }
}
