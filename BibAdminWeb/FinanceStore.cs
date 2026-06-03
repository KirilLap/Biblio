using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BibAdminWeb
{
    public class SessionRecord
    {
        public string PcNumber { get; set; } = "";
        public string SessionType { get; set; } = "";
        public string UserName { get; set; } = "—";
        public string ReaderId { get; set; } = "";
        public int DurationSeconds { get; set; }
        public int EarnedAmount { get; set; }
        public int PaidAmount { get; set; }
        public int RefundAmount { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string OperatorName { get; set; } = "";
    }

    public static class FinanceStore
    {
        public static List<SessionRecord> Sessions { get; } = new();

        private static readonly string HistoryFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "data", "finance_history.json");

        public static void LoadHistory()
        {
            // Миграция: перенести finance_history.json из %APPDATA%\BibAdmin\ в data\
            var oldPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BibAdmin", "finance_history.json");
            if (!File.Exists(HistoryFilePath) && File.Exists(oldPath))
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(HistoryFilePath)!); File.Copy(oldPath, HistoryFilePath); File.Delete(oldPath); } catch { }
            }
            try
            {
                if (!File.Exists(HistoryFilePath)) return;
                var json = File.ReadAllText(HistoryFilePath);
                if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]") return;
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                var records = JsonSerializer.Deserialize<List<SessionRecord>>(json, opts);
                if (records == null) return;
                var existingKeys = new HashSet<(DateTime, string)>(Sessions.Select(s => (s.StartTime, s.PcNumber)));
                int added = 0;
                foreach (var r in records)
                {
                    if (string.IsNullOrEmpty(r.PcNumber) || r.EndTime == default) continue;
                    if (!existingKeys.Contains((r.StartTime, r.PcNumber)))
                    { Sessions.Add(r); existingKeys.Add((r.StartTime, r.PcNumber)); added++; }
                }
                Sessions.Sort((a, b) => b.EndTime.CompareTo(a.EndTime));
                Logger.Info($"📂 Загружено {Sessions.Count} записей финансов (добавлено {added})");
            }
            catch (Exception ex) { Logger.Error($"Ошибка загрузки финансов: {ex.Message}"); }
        }

        public static void SaveHistory()
        {
            try
            {
                var dir = Path.GetDirectoryName(HistoryFilePath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var valid = Sessions.Where(s => !string.IsNullOrEmpty(s.PcNumber) && s.EndTime != default).ToList();
                var opts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                var json = JsonSerializer.Serialize(valid, opts);
                var tmp = HistoryFilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(HistoryFilePath)) File.Replace(tmp, HistoryFilePath, null);
                else File.Move(tmp, HistoryFilePath);
            }
            catch (Exception ex) { Logger.Error($"Ошибка сохранения финансов: {ex.Message}"); }
        }

        public static void AddSession(SessionRecord session)
        {
            if (string.IsNullOrEmpty(session.PcNumber)) return;
            Logger.Info($"💰 Сессия: ПК={session.PcNumber}, Тип={session.SessionType}, Сумма={session.EarnedAmount}");
            Sessions.Insert(0, session);
            SaveHistory();
        }
    }
}
