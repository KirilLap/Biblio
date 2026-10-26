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
            AppDomain.CurrentDomain.BaseDirectory, "data", "service_history.json");

        public static void LoadHistory()
        {
            // Миграция: перенести service_history.json из %APPDATA%\BibAdmin\ в data\
            var oldPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BibAdmin", "service_history.json");
            if (!File.Exists(FilePath) && File.Exists(oldPath))
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.Copy(oldPath, FilePath); File.Delete(oldPath); } catch { }
            }
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

        public static void Add(ServiceTransaction t)
        {
            // Анонимный посетитель и временный билет не могут брать услугу в долг
            if (!t.IsPaid && !CanDefer(t.ReaderId)) { t.PaidAmount = t.TotalAmount; t.PaidAt = DateTime.UtcNow; }
            All.Insert(0, t);
            SaveHistory();
        }

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

        // Личный билет читателя — буквы префикса и цифры («FAA260500456»). Пустой номер, один
        // префикс или чисто цифровой временный билет личным не считаются: по ним долг не ведётся.
        public static bool IsPersonalId(string? readerId)
        {
            var id = readerId?.Trim() ?? "";
            return id.Any(char.IsDigit) && id.Any(char.IsLetter);
        }

        // В долг («Позже») может брать только читатель из базы; анонимные и временные — только сразу
        public static bool CanDefer(string? readerId)
            => IsPersonalId(readerId) && ReaderStore.GetByCardId(readerId!.Trim()) != null;

        // Сумма неоплаченных услуг читателя (0 для анонимных и временных билетов)
        public static int ReaderDebt(string? readerId)
        {
            if (!IsPersonalId(readerId)) return 0;
            var id = readerId!.Trim();
            return All.Where(t => !t.IsPaid && string.Equals(t.ReaderId, id, StringComparison.OrdinalIgnoreCase))
                      .Sum(t => t.DebtAmount);
        }

        /// <summary>
        /// Долги читателя на момент завершения сессии: услуги, взятые в долг во время этой
        /// сессии, и отдельно — его прошлые долги. Чужие долги, когда-то оформленные на этот
        /// же ПК, сюда не попадают.
        /// </summary>
        public static (List<ServiceTransaction> Session, List<ServiceTransaction> Previous) GetDebtsForSessionEnd(
            string? readerId, DateTime sessionStart, int durationSeconds)
        {
            var session = new List<ServiceTransaction>();
            var previous = new List<ServiceTransaction>();
            if (!IsPersonalId(readerId)) return (session, previous);
            var id = readerId!.Trim();

            // Начало сессии: берём самое раннее из двух оценок (после паузы SessionStart сдвигается)
            var beginUtc = DateTime.UtcNow.AddSeconds(-Math.Max(0, durationSeconds));
            var startUtc = sessionStart.ToUniversalTime();
            if (startUtc < beginUtc) beginUtc = startUtc;
            beginUtc = beginUtc.AddMinutes(-1);

            foreach (var t in All)
            {
                if (t.IsPaid || !string.Equals(t.ReaderId, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (t.CreatedAt.ToUniversalTime() >= beginUtc) session.Add(t); else previous.Add(t);
            }
            return (session, previous);
        }

        public static void MarkAllPaidForPc(string pcNumber)
        {
            var unpaid = All.Where(t => !t.IsPaid && t.PcNumber == pcNumber).ToList();
            foreach (var t in unpaid) { t.PaidAmount = t.TotalAmount; t.PaidAt = DateTime.UtcNow; }
            if (unpaid.Count > 0) SaveHistory();
        }
    }
}
