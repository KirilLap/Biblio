using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BibAdminWeb
{
    public class ServiceTransaction
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ServiceTypeId { get; set; } = "";
        public string ServiceName { get; set; } = "";
        public string Unit { get; set; } = "";
        public int Quantity { get; set; } = 1;
        public int PricePerUnit { get; set; }
        public int TotalAmount { get; set; }
        public int PaidAmount { get; set; }
        public string ReaderId { get; set; } = "";
        public string ReaderName { get; set; } = "";
        public string PcNumber { get; set; } = "";
        public string? BatchId { get; set; }           // groups services created together
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? PaidAt { get; set; }

        public bool IsPaid => PaidAmount >= TotalAmount;
        public int DebtAmount => Math.Max(0, TotalAmount - PaidAmount);

        public static List<ServiceTransaction> All { get; } = new();

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BibAdmin", "service_history.json");

        public static void LoadHistory()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]") return;
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                var records = JsonSerializer.Deserialize<List<ServiceTransaction>>(json, opts);
                if (records == null) return;
                var existingIds = new HashSet<string>(All.Select(t => t.Id));
                foreach (var r in records)
                    if (!existingIds.Contains(r.Id)) { All.Add(r); existingIds.Add(r.Id); }
                All.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
                Logger.Info($"📂 Загружено {All.Count} транзакций услуг");
            }
            catch (Exception ex) { Logger.Error($"Ошибка загрузки истории услуг: {ex.Message}"); }
        }

        public static void SaveHistory()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var opts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                var json = JsonSerializer.Serialize(All, opts);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Logger.Error($"Ошибка сохранения истории услуг: {ex.Message}"); }
        }

        public static void Add(ServiceTransaction t) { All.Insert(0, t); SaveHistory(); }

        public static void MarkAsPaid(string id)
        {
            var t = All.FirstOrDefault(x => x.Id == id);
            if (t == null) return;
            t.PaidAmount = t.TotalAmount;
            t.PaidAt = DateTime.UtcNow;
            SaveHistory();
        }

        public static void MarkAllPaidForReader(string readerId)
        {
            var unpaid = All.Where(t => !t.IsPaid && t.ReaderId == readerId).ToList();
            foreach (var t in unpaid) { t.PaidAmount = t.TotalAmount; t.PaidAt = DateTime.UtcNow; }
            if (unpaid.Count > 0) SaveHistory();
        }

        public static List<ServiceTransaction> GetUnpaidForReader(string readerId)
        {
            if (string.IsNullOrEmpty(readerId)) return new();
            return All.Where(t => !t.IsPaid && t.ReaderId == readerId).ToList();
        }

        public static List<ServiceTransaction> GetUnpaidForPc(string pcNumber)
        {
            if (string.IsNullOrEmpty(pcNumber)) return new();
            return All.Where(t => !t.IsPaid && t.PcNumber == pcNumber).ToList();
        }

        public static List<ServiceTransaction> GetAllUnpaid() =>
            All.Where(t => !t.IsPaid).ToList();

        public static List<ServiceTransaction> GetUnpaidForSession(string pcNumber, string readerId)
        {
            var set = new HashSet<string>();
            var result = new List<ServiceTransaction>();
            foreach (var t in All)
            {
                if (t.IsPaid) continue;
                bool match = (!string.IsNullOrEmpty(pcNumber) && t.PcNumber == pcNumber)
                          || (!string.IsNullOrEmpty(readerId) && t.ReaderId == readerId);
                if (match && set.Add(t.Id)) result.Add(t);
            }
            return result;
        }

        public static void MarkAllPaidForPc(string pcNumber)
        {
            var unpaid = All.Where(t => !t.IsPaid && t.PcNumber == pcNumber).ToList();
            foreach (var t in unpaid) { t.PaidAmount = t.TotalAmount; t.PaidAt = DateTime.UtcNow; }
            if (unpaid.Count > 0) SaveHistory();
        }
    }
}
