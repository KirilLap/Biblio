using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace BibAdminWeb
{
    /// <summary>
    /// Посещение читального зала, отмеченное оператором вручную
    /// (читатель пришёл, но не садился за ПК и не брал услугу).
    /// </summary>
    public class ManualVisit
    {
        public long Id { get; set; }
        public string ReaderId { get; set; } = "";      // пусто = без билета
        public string ReaderName { get; set; } = "";
        public string Purpose { get; set; } = "";       // цель визита (название из настроек)
        public string Comment { get; set; } = "";
        public string OperatorName { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;   // UTC
    }

    /// <summary>
    /// Хранилище ручных отметок посещений. Лежит в data\readers.db рядом с сессиями,
    /// поэтому при обновлениях не затирается.
    /// </summary>
    public static class VisitStore
    {
        private static readonly List<ManualVisit> _all = new();
        private static readonly object _lock = new();

        private static readonly string DbPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "data", "readers.db");

        private static SqliteConnection Open()
        {
            var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            return conn;
        }

        public static void Load()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
                using var conn = Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS manual_visits (
                        id            INTEGER PRIMARY KEY AUTOINCREMENT,
                        reader_id     TEXT NOT NULL DEFAULT '',
                        reader_name   TEXT NOT NULL DEFAULT '',
                        purpose       TEXT NOT NULL DEFAULT '',
                        comment       TEXT NOT NULL DEFAULT '',
                        operator_name TEXT NOT NULL DEFAULT '',
                        created_at    TEXT NOT NULL
                    )";
                    cmd.ExecuteNonQuery();
                }

                var list = new List<ManualVisit>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, reader_id, reader_name, purpose, comment, operator_name, created_at FROM manual_visits ORDER BY id";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        list.Add(new ManualVisit
                        {
                            Id = r.GetInt64(0),
                            ReaderId = r.GetString(1),
                            ReaderName = r.GetString(2),
                            Purpose = r.GetString(3),
                            Comment = r.GetString(4),
                            OperatorName = r.GetString(5),
                            CreatedAt = DateTime.Parse(r.GetString(6), CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind).ToUniversalTime()
                        });
                    }
                }
                lock (_lock) { _all.Clear(); _all.AddRange(list); }
                Logger.Info($"📂 Загружено {list.Count} ручных отметок посещений");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка загрузки посещений: {ex.Message}");
            }
        }

        public static ManualVisit Add(ManualVisit v)
        {
            v.CreatedAt = DateTime.UtcNow;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"INSERT INTO manual_visits (reader_id, reader_name, purpose, comment, operator_name, created_at)
                                    VALUES ($rid, $rn, $p, $c, $op, $at); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$rid", v.ReaderId);
                cmd.Parameters.AddWithValue("$rn", v.ReaderName);
                cmd.Parameters.AddWithValue("$p", v.Purpose);
                cmd.Parameters.AddWithValue("$c", v.Comment);
                cmd.Parameters.AddWithValue("$op", v.OperatorName);
                cmd.Parameters.AddWithValue("$at", v.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
                v.Id = (long)(cmd.ExecuteScalar() ?? 0L);
            }
            lock (_lock) _all.Add(v);
            Logger.Info($"🚶 Посещение: билет={(v.ReaderId.Length > 0 ? v.ReaderId : "без билета")}, оператор={v.OperatorName}");
            return v;
        }

        public static bool Delete(long id)
        {
            int rows;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM manual_visits WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                rows = cmd.ExecuteNonQuery();
            }
            lock (_lock) _all.RemoveAll(v => v.Id == id);
            if (rows > 0) Logger.Info($"🗑 Отметка посещения удалена: {id}");
            return rows > 0;
        }

        public static List<ManualVisit> Snapshot()
        {
            lock (_lock) return _all.ToList();
        }

        /// <summary>Отметки за один календарный день (по местному времени сервера).</summary>
        public static List<ManualVisit> ForLocalDay(DateTime day)
        {
            var d = day.Date;
            lock (_lock) return _all.Where(v => v.CreatedAt.ToLocalTime().Date == d).ToList();
        }
    }
}
