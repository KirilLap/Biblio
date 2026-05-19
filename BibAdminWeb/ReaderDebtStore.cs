using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BibAdminWeb
{
    public class ReaderDebt
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ReaderId { get; set; } = "";
        public int Amount { get; set; }
        public string Note { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public static class ReaderDebtStore
    {
        public static List<ReaderDebt> Debts { get; } = new();

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BibAdmin", "reader_debts.json");

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]") return;
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                var records = JsonSerializer.Deserialize<List<ReaderDebt>>(json, opts);
                if (records == null) return;
                var existing = new HashSet<string>(Debts.Select(d => d.Id));
                foreach (var r in records)
                    if (!existing.Contains(r.Id)) { Debts.Add(r); existing.Add(r.Id); }
                Logger.Info($"📂 Загружено {Debts.Count} записей долгов читателей");
            }
            catch (Exception ex) { Logger.Error($"Ошибка загрузки долгов: {ex.Message}"); }
        }

        public static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var opts = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                var json = JsonSerializer.Serialize(Debts, opts);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Logger.Error($"Ошибка сохранения долгов: {ex.Message}"); }
        }

        /// <summary>Добавляет долг за читателя.</summary>
        public static void Add(string readerId, int amount, string note)
        {
            if (string.IsNullOrEmpty(readerId) || amount <= 0) return;
            Debts.Add(new ReaderDebt { ReaderId = readerId, Amount = amount, Note = note });
            Save();
            Logger.Info($"💸 Долг записан: читатель={readerId}, сумма={amount}, примечание={note}");
        }

        /// <summary>Возвращает суммарный долг читателя.</summary>
        public static int GetDebtAmount(string readerId)
        {
            if (string.IsNullOrEmpty(readerId)) return 0;
            return Debts.Where(d => d.ReaderId == readerId).Sum(d => d.Amount);
        }

        /// <summary>Удаляет все долги читателя (погашение).</summary>
        public static void ClearDebts(string readerId)
        {
            if (string.IsNullOrEmpty(readerId)) return;
            int removed = Debts.RemoveAll(d => d.ReaderId == readerId);
            if (removed > 0) Save();
        }

        /// <summary>Возвращает долги конкретного читателя.</summary>
        public static List<ReaderDebt> GetForReader(string readerId)
        {
            if (string.IsNullOrEmpty(readerId)) return new();
            return Debts.Where(d => d.ReaderId == readerId).ToList();
        }
    }
}
