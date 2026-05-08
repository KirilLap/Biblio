using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;

namespace BibClientGuardian
{
    internal class Program
    {
        private const string LEGAL_CLOSE_MUTEX_NAME = "Global\\BibClient_LegalClose";
        private const string GUARDIAN_MUTEX_NAME = "Global\\BibClientGuardian_SingleInstance";

        private static readonly string ClientExePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "BibClient.exe"
        );

        // Статическое поле — GC не соберёт мьютекс пока живёт процесс
        private static Mutex? _guardianMutex;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;

        static void Main(string[] args)
        {
            HideConsoleWindow();

            // Single-instance: используем false + WaitOne(0) чтобы корректно обработать
            // abandoned mutex (когда предыдущий Guardian был убит через диспетчер задач)
            _guardianMutex = new Mutex(false, GUARDIAN_MUTEX_NAME, out _);
            bool isFirstInstance;
            try
            {
                isFirstInstance = _guardianMutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // Предыдущий Guardian был убит принудительно — мьютекс передан нам
                isFirstInstance = true;
                Log("♻️ Мьютекс восстановлен после принудительного завершения предыдущего Guardian");
            }

            if (!isFirstInstance)
            {
                Log("⚠️ Guardian уже запущен — завершение дубликата");
                return;
            }

            if (!File.Exists(ClientExePath))
            {
                Log($"❌ BibClient.exe не найден: {ClientExePath}");
                return;
            }

            Log($"🛡️ Guardian запущен. Следит за: {ClientExePath}");

            // Проверяем сигнал легального закрытия при старте
            if (IsLegalCloseSignaled())
            {
                Log("🔓 Обнаружен сигнал легального закрытия при старте — завершение");
                return;
            }

            DateTime lastRestartAttempt = DateTime.MinValue;
            bool restartAttempted = false;

            while (true)
            {
                try
                {
                    Thread.Sleep(3000);

                    if (IsLegalCloseSignaled())
                    {
                        Log("🔓 Получен сигнал легального закрытия — завершение Guardian");
                        return;
                    }

                    Process? mainProcess = FindBibClientProcess();

                    if (mainProcess == null)
                    {
                        // Процесс не найден — перезапускаем
                        if (!restartAttempted || (DateTime.Now - lastRestartAttempt).TotalSeconds > 10)
                        {
                            Log("⚠️ BibClient не найден, перезапуск...");
                            restartAttempted = true;
                            lastRestartAttempt = DateTime.Now;
                            RestartBibClient();
                        }
                        else
                        {
                            Log("⏳ Предыдущий перезапуск менее 10 секунд назад, пропускаем");
                        }
                    }
                    else
                    {
                        restartAttempted = false;
                        mainProcess.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    // Ловим ВСЕ исключения чтобы Guardian не вышел из цикла при неожиданной ошибке
                    Log($"❌ Ошибка в цикле Guardian: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // Возвращает Process если BibClient найден и работает, иначе null.
        // ВАЖНО: не вызывать Dispose() на возвращённом объекте внутри этого метода —
        // вызывающий код сам управляет временем жизни.
        private static Process? FindBibClientProcess()
        {
            try
            {
                var processes = Process.GetProcessesByName("BibClient");
                foreach (var p in processes)
                {
                    bool isTarget = false;
                    try
                    {
                        var processPath = p.MainModule?.FileName;
                        isTarget = !string.IsNullOrEmpty(processPath) &&
                                   processPath.Equals(ClientExePath, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        // Недостаточно прав для чтения MainModule — считаем что это не наш процесс
                    }

                    if (isTarget)
                    {
                        // Освобождаем остальные процессы из массива
                        foreach (var other in processes)
                            if (!ReferenceEquals(other, p)) other.Dispose();
                        return p; // Возвращаем НЕ освобождённый объект
                    }
                    else
                    {
                        p.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ Ошибка поиска процесса: {ex.Message}");
            }

            return null;
        }

        private static bool IsLegalCloseSignaled()
        {
            try
            {
                if (Mutex.TryOpenExisting(LEGAL_CLOSE_MUTEX_NAME, out var mutex))
                {
                    using (mutex)
                    {
                        try { return mutex.WaitOne(0); }
                        catch (AbandonedMutexException) { return true; }
                    }
                }
            }
            catch { }
            return false;
        }

        private static void RestartBibClient()
        {
            try
            {
                if (!File.Exists(ClientExePath))
                {
                    Log($"❌ Файл BibClient исчез: {ClientExePath}");
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = ClientExePath,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(ClientExePath) ?? "",
                    CreateNoWindow = false,
                };

                Process.Start(startInfo);
                Log("✅ BibClient перезапущен");
            }
            catch (Exception ex)
            {
                Log($"❌ Ошибка перезапуска BibClient: {ex.Message}");
            }
        }

        static void HideConsoleWindow()
        {
            try
            {
                IntPtr handle = GetConsoleWindow();
                if (handle != IntPtr.Zero)
                    ShowWindow(handle, SW_HIDE);
            }
            catch { }
        }

        static void Log(string message)
        {
            try
            {
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GuardianLog.txt");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
