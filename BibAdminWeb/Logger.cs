using System;
using System.Diagnostics;
using System.IO;

namespace BibAdminWeb
{
    public enum LogLevel { Info, Warn, Error }

    public static class Logger
    {
        private static readonly object _lock = new();
        private static string _logDir = "";
        private static string _logFile = "";

        private static void EnsureInit()
        {
            if (string.IsNullOrEmpty(_logFile))
            {
                _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(_logDir);
                _logFile = Path.Combine(_logDir, $"bibadminweb_{DateTime.Now:yyyy-MM-dd}.log");
                DeleteOldLogs();
                Log("=== BibAdmin Web запущен ===", LogLevel.Info);
            }
        }

        private static void DeleteOldLogs()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-30);
                foreach (var file in Directory.GetFiles(_logDir, "*.log"))
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
            }
            catch { }
        }

        public static void Log(string message, LogLevel level)
        {
            EnsureInit();
            string entry = $"[{DateTime.Now:HH:mm:ss.fff}] [{level,-5}] {message}";
            Debug.WriteLine(entry);
            lock (_lock)
            {
                try { File.AppendAllText(_logFile, entry + Environment.NewLine, System.Text.Encoding.UTF8); }
                catch { }
            }
        }

        public static void Info(string msg) => Log(msg, LogLevel.Info);
        public static void Warn(string msg) => Log(msg, LogLevel.Warn);
        public static void Error(string msg) => Log(msg, LogLevel.Error);
        public static void Error(Exception ex) => Error($"{ex.GetType().Name}: {ex.Message}");
    }
}
