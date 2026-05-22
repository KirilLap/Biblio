using System;
using System.IO;

namespace BibAdminWeb
{
    public static class AuditLogger
    {
        private static readonly object _lock = new();
        private static string _logFile = "";

        private static void EnsureInit()
        {
            if (!string.IsNullOrEmpty(_logFile)) return;
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            _logFile = Path.Combine(dir, "audit.log");
        }

        public static void Log(string action, string details = "")
        {
            EnsureInit();
            var line = string.IsNullOrEmpty(details)
                ? $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {action}"
                : $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {action} | {details}";
            lock (_lock)
            {
                try { File.AppendAllText(_logFile, line + Environment.NewLine, System.Text.Encoding.UTF8); }
                catch { }
            }
        }

        public static void SessionStarted(string pcNumber, string sessionType, int limitSeconds, int paid)
            => Log("SESSION_START", $"pc={pcNumber} type={sessionType} limit={limitSeconds}s paid={paid}");

        public static void SessionEnded(string pcNumber, string sessionType, int elapsedSeconds)
            => Log("SESSION_END", $"pc={pcNumber} type={sessionType} elapsed={elapsedSeconds}s");

        public static void SessionExtended(string pcNumber, int addSeconds)
            => Log("SESSION_EXTEND", $"pc={pcNumber} add={addSeconds}s");

        public static void PcRenamed(string oldName, string newName)
            => Log("PC_RENAME", $"from='{oldName}' to='{newName}'");

        public static void SettingsChanged(string what = "")
            => Log("SETTINGS_CHANGED", what);

        public static void OperatorAdded(string name)
            => Log("OPERATOR_ADD", $"name='{name}'");

        public static void OperatorRemoved(string name)
            => Log("OPERATOR_REMOVE", $"name='{name}'");

        public static void PcLocked(string pcNumber)
            => Log("PC_LOCK", $"pc={pcNumber}");

        public static void PcUnlocked(string pcNumber)
            => Log("PC_UNLOCK", $"pc={pcNumber}");
    }
}
