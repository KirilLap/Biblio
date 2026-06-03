using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace BibAdminWeb
{
    public class Reader
    {
        public int Id { get; set; }
        public string CardId { get; set; } = "";
        public string FullName { get; set; } = "";
        public string BirthDate { get; set; } = "";
        public string Category { get; set; } = "";
        public string Gender { get; set; } = "";
        public string RegisteredAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }

    public class ImportResult
    {
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
    }

    public static class ReaderStore
    {
        private static readonly string DbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BibAdmin", "readers.db");

        private static string ConnStr => $"Data Source={DbPath}";

        public static void Init()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
            using var conn = Open();
            Exec(conn, @"CREATE TABLE IF NOT EXISTS readers (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                card_id       TEXT UNIQUE NOT NULL,
                full_name     TEXT NOT NULL DEFAULT '',
                birth_date    TEXT NOT NULL DEFAULT '',
                category      TEXT NOT NULL DEFAULT '',
                gender        TEXT NOT NULL DEFAULT '',
                registered_at TEXT NOT NULL DEFAULT '',
                updated_at    TEXT NOT NULL DEFAULT ''
            )");
            // Migration: add updated_at to existing databases
            try { Exec(conn, "ALTER TABLE readers ADD COLUMN updated_at TEXT NOT NULL DEFAULT ''"); } catch { }
            Logger.Info("📚 ReaderStore инициализирован");
        }

        public static List<Reader> GetAll(string? search = null)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            if (string.IsNullOrWhiteSpace(search))
            {
                cmd.CommandText = "SELECT id,card_id,full_name,birth_date,category,gender,registered_at,updated_at FROM readers ORDER BY full_name COLLATE NOCASE";
            }
            else
            {
                cmd.CommandText = "SELECT id,card_id,full_name,birth_date,category,gender,registered_at,updated_at FROM readers WHERE LOWER(full_name) LIKE @q OR LOWER(card_id) LIKE @q ORDER BY full_name COLLATE NOCASE";
                cmd.Parameters.AddWithValue("@q", $"%{search.ToLower()}%");
            }
            var list = new List<Reader>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(Map(r));
            return list;
        }

        public static Reader? GetByCardId(string cardId)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,card_id,full_name,birth_date,category,gender,registered_at,updated_at FROM readers WHERE card_id=@id";
            cmd.Parameters.AddWithValue("@id", cardId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? Map(r) : null;
        }

        public static int Count()
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM readers";
            return (int)(long)cmd.ExecuteScalar()!;
        }

        public static ImportResult Import(List<Reader> incoming)
        {
            var result = new ImportResult();
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            foreach (var r in incoming)
            {
                if (string.IsNullOrWhiteSpace(r.CardId)) continue;

                string? existUpdatedAt = null;

                using (var check = conn.CreateCommand())
                {
                    check.Transaction = tx;
                    check.CommandText = "SELECT updated_at FROM readers WHERE card_id=@id";
                    check.Parameters.AddWithValue("@id", r.CardId);
                    using var dr = check.ExecuteReader();
                    if (dr.Read()) existUpdatedAt = dr.GetString(0);
                }

                if (existUpdatedAt != null)
                {
                    if (existUpdatedAt == r.UpdatedAt)
                    {
                        result.Skipped++;
                    }
                    else
                    {
                        using var upd = conn.CreateCommand();
                        upd.Transaction = tx;
                        upd.CommandText = "UPDATE readers SET full_name=@n,birth_date=@b,category=@cat,gender=@g,registered_at=@r,updated_at=@u WHERE card_id=@c";
                        upd.Parameters.AddWithValue("@n",   r.FullName);
                        upd.Parameters.AddWithValue("@b",   r.BirthDate);
                        upd.Parameters.AddWithValue("@cat", r.Category);
                        upd.Parameters.AddWithValue("@g",   r.Gender);
                        upd.Parameters.AddWithValue("@r",   r.RegisteredAt);
                        upd.Parameters.AddWithValue("@u",   r.UpdatedAt);
                        upd.Parameters.AddWithValue("@c",   r.CardId);
                        upd.ExecuteNonQuery();
                        result.Updated++;
                    }
                }
                else
                {
                    using var ins = conn.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT INTO readers (card_id,full_name,birth_date,category,gender,registered_at,updated_at) VALUES (@c,@n,@b,@cat,@g,@r,@u)";
                    ins.Parameters.AddWithValue("@c",   r.CardId);
                    ins.Parameters.AddWithValue("@n",   r.FullName);
                    ins.Parameters.AddWithValue("@b",   r.BirthDate);
                    ins.Parameters.AddWithValue("@cat", r.Category);
                    ins.Parameters.AddWithValue("@g",   r.Gender);
                    ins.Parameters.AddWithValue("@r",   r.RegisteredAt);
                    ins.Parameters.AddWithValue("@u",   r.UpdatedAt);
                    ins.ExecuteNonQuery();
                    result.Added++;
                }
            }

            tx.Commit();
            Logger.Info($"📥 Импорт: добавлено={result.Added}, обновлено={result.Updated}, пропущено={result.Skipped}");
            return result;
        }

        public static (int total,
                       Dictionary<string, int> byGender,
                       Dictionary<string, int> byCategory,
                       Dictionary<string, int> byAge) GetStats()
        {
            using var conn = Open();

            using var totalCmd = conn.CreateCommand();
            totalCmd.CommandText = "SELECT COUNT(*) FROM readers";
            var total = (int)(long)totalCmd.ExecuteScalar()!;

            var byGender   = GroupBy(conn, "SELECT CASE WHEN gender='' THEN 'Не указан' ELSE gender END, COUNT(*) FROM readers GROUP BY gender");
            var byCategory = GroupBy(conn, "SELECT CASE WHEN category='' THEN 'Не указано' ELSE category END, COUNT(*) FROM readers GROUP BY category");

            var byAge = new Dictionary<string, int>
            {
                ["до 14"] = 0, ["14–17"] = 0, ["18–30"] = 0, ["31–60"] = 0, ["60+"] = 0, ["Не указан"] = 0
            };

            using (var ageCmd = conn.CreateCommand())
            {
                ageCmd.CommandText = "SELECT birth_date FROM readers";
                using var dr = ageCmd.ExecuteReader();
                var today = DateTime.Today;
                while (dr.Read())
                {
                    var s = dr.GetString(0);
                    if (DateTime.TryParseExact(s, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var bd))
                    {
                        var age = today.Year - bd.Year;
                        if (bd > today.AddYears(-age)) age--;
                        if      (age < 14)  byAge["до 14"]++;
                        else if (age <= 17) byAge["14–17"]++;
                        else if (age <= 30) byAge["18–30"]++;
                        else if (age <= 60) byAge["31–60"]++;
                        else               byAge["60+"]++;
                    }
                    else byAge["Не указан"]++;
                }
            }

            return (total, byGender, byCategory, byAge);
        }

        public static int CalcAge(string birthDate)
        {
            if (!DateTime.TryParseExact(birthDate, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var bd))
                return -1;
            var today = DateTime.Today;
            var age = today.Year - bd.Year;
            if (bd > today.AddYears(-age)) age--;
            return age;
        }

        public static void Update(Reader reader)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE readers SET full_name=@n, birth_date=@b, category=@cat, gender=@g, registered_at=@r, updated_at=@u WHERE card_id=@c";
            cmd.Parameters.AddWithValue("@n",   reader.FullName);
            cmd.Parameters.AddWithValue("@b",   reader.BirthDate);
            cmd.Parameters.AddWithValue("@cat", reader.Category);
            cmd.Parameters.AddWithValue("@g",   reader.Gender);
            cmd.Parameters.AddWithValue("@r",   reader.RegisteredAt);
            cmd.Parameters.AddWithValue("@u",   reader.UpdatedAt);
            cmd.Parameters.AddWithValue("@c",   reader.CardId);
            cmd.ExecuteNonQuery();
            Logger.Info($"✏️ Читатель обновлён: {reader.CardId}");
        }

        private static Dictionary<string, int> GroupBy(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var d = new Dictionary<string, int>();
            using var dr = cmd.ExecuteReader();
            while (dr.Read()) d[dr.GetString(0)] = (int)dr.GetInt64(1);
            return d;
        }

        private static void Exec(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static SqliteConnection Open()
        {
            var conn = new SqliteConnection(ConnStr);
            conn.Open();
            return conn;
        }

        private static Reader Map(SqliteDataReader r) => new()
        {
            Id           = (int)r.GetInt64(0),
            CardId       = r.GetString(1),
            FullName     = r.GetString(2),
            BirthDate    = r.GetString(3),
            Category     = r.GetString(4),
            Gender       = r.GetString(5),
            RegisteredAt = r.GetString(6),
            UpdatedAt    = r.GetString(7),
        };
    }
}
