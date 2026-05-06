using System;
using System.Diagnostics;
using System.IO;

namespace BibAdmin
{
    public enum LogLevel { Info, Warn, Error }

    public static class Logger
    {
        private static readonly object _lock = new();
        private static string _logDir;
        private static string _logFile;

        // Автоматическая инициализация при первом использовании
        private static void EnsureInit()
        {
            if (string.IsNullOrEmpty(_logFile))
            {
                _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(_logDir);
                _logFile = Path.Combine(_logDir, $"bibadmin_{DateTime.Now:yyyy-MM-dd}.log");
                Log("=== Запуск Админки ===", LogLevel.Info);
            }
        }

        public static void Log(string message, LogLevel level)
        {
            EnsureInit();

            string entry = $"[{DateTime.Now:HH:mm:ss.fff}] [{level,-5}] {message}";

            // Дублируем вывод в консоль Visual Studio
            Debug.WriteLine(entry);

            lock (_lock)
            {
                try { File.AppendAllText(_logFile, entry + Environment.NewLine); }
                catch { }
            }
        }

        public static void Info(string msg) => Log(msg, LogLevel.Info);
        public static void Warn(string msg) => Log(msg, LogLevel.Warn);
        public static void Error(string msg) => Log(msg, LogLevel.Error);
        public static void Error(Exception ex) => Error($"{ex.GetType().Name}: {ex.Message}");
        
        // Debug метод для отладки - пишет как Info
        public static void Debug(string msg)
        {
            EnsureInit();
            string entry = $"[{DateTime.Now:HH:mm:ss.fff}] [Debug] {msg}";
            Debug.WriteLine(entry);
            lock (_lock)
            {
                try { File.AppendAllText(_logFile, entry + Environment.NewLine); }
                catch { }
            }
        }
    }
}