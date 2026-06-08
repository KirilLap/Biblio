using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

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

        private static readonly string DbPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "data", "readers.db");

        private static readonly string LegacyJsonPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "data", "finance_history.json");

        private static string ConnStr => $"Data Source={DbPath}";

        private static SqliteConnection Open()
        {
            var conn = new SqliteConnection(ConnStr);
            conn.Open();
            return conn;
        }

        private static void Exec(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static void InitTable()
        {
            using var conn = Open();
            Exec(conn, @"CREATE TABLE IF NOT EXISTS sessions (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                pc_number     TEXT NOT NULL,
                session_type  TEXT NOT NULL DEFAULT '',
                user_name     TEXT NOT NULL DEFAULT '—',
                reader_id     TEXT NOT NULL DEFAULT '',
                duration_secs INTEGER NOT NULL DEFAULT 0,
                earned        INTEGER NOT NULL DEFAULT 0,
                paid          INTEGER NOT NULL DEFAULT 0,
                refund        INTEGER NOT NULL DEFAULT 0,
                start_time    TEXT NOT NULL,
                end_time      TEXT NOT NULL,
                operator_name TEXT NOT NULL DEFAULT '',
                UNIQUE(start_time, pc_number)
            )");
        }

        public static void LoadHistory()
        {
            InitTable();
            MigrateFromJson();
            LoadFromDb();
            Logger.Info($"📂 Загружено {Sessions.Count} записей финансов");
        }

        private static void MigrateFromJson()
        {
            // Проверяем оба возможных пути (старый %APPDATA% и текущий data\)
            var oldJsonPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BibAdmin", "finance_history.json");

            string? jsonPath = null;
            if (File.Exists(LegacyJsonPath)) jsonPath = LegacyJsonPath;
            else if (File.Exists(oldJsonPath)) jsonPath = oldJsonPath;

            if (jsonPath == null) return;

            try
            {
                var json = File.ReadAllText(jsonPath);
                if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]")
                {
                    File.Move(jsonPath, jsonPath + ".bak", true);
                    return;
                }

                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                var records = JsonSerializer.Deserialize<List<SessionRecord>>(json, opts);
                if (records == null || records.Count == 0)
                {
                    File.Move(jsonPath, jsonPath + ".bak", true);
                    return;
                }

                using var conn = Open();
                using var tx = conn.BeginTransaction();
                int imported = 0;
                foreach (var r in records)
                {
                    if (string.IsNullOrEmpty(r.PcNumber) || r.EndTime == default) continue;
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT OR IGNORE INTO sessions
                        (pc_number, session_type, user_name, reader_id, duration_secs,
                         earned, paid, refund, start_time, end_time, operator_name)
                        VALUES (@pc,@st,@un,@ri,@ds,@ea,@pa,@ra,@s,@e,@op)";
                    BindRecord(cmd, r);
                    imported += cmd.ExecuteNonQuery();
                }
                tx.Commit();

                File.Move(jsonPath, jsonPath + ".bak", true);
                Logger.Info($"📦 Мигрировано {imported} записей финансов из JSON в SQLite");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка миграции финансов: {ex.Message}");
            }
        }

        private static void LoadFromDb()
        {
            Sessions.Clear();
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            // LEFT JOIN с читателями: если имя не записано — подтягиваем актуальное
            cmd.CommandText = @"
                SELECT s.pc_number, s.session_type,
                       CASE WHEN s.user_name = '—' AND r.full_name IS NOT NULL AND r.full_name != ''
                            THEN r.full_name ELSE s.user_name END,
                       s.reader_id, s.duration_secs, s.earned, s.paid, s.refund,
                       s.start_time, s.end_time, s.operator_name
                FROM sessions s
                LEFT JOIN readers r ON s.reader_id != '' AND s.reader_id = r.card_id
                ORDER BY s.end_time DESC";
            using var dr = cmd.ExecuteReader();
            while (dr.Read())
            {
                Sessions.Add(new SessionRecord
                {
                    PcNumber        = dr.GetString(0),
                    SessionType     = dr.GetString(1),
                    UserName        = dr.GetString(2),
                    ReaderId        = dr.GetString(3),
                    DurationSeconds = dr.GetInt32(4),
                    EarnedAmount    = dr.GetInt32(5),
                    PaidAmount      = dr.GetInt32(6),
                    RefundAmount    = dr.GetInt32(7),
                    StartTime       = DateTime.Parse(dr.GetString(8)),
                    EndTime         = DateTime.Parse(dr.GetString(9)),
                    OperatorName    = dr.GetString(10)
                });
            }
        }

        public static void SaveHistory()
        {
            // Данные пишутся в SQLite сразу в AddSession() — метод оставлен для совместимости
        }

        public static void AddSession(SessionRecord session)
        {
            if (string.IsNullOrEmpty(session.PcNumber)) return;

            // Если имя не указано — пробуем подтянуть из базы читателей
            if ((session.UserName == "—" || string.IsNullOrWhiteSpace(session.UserName))
                && !string.IsNullOrWhiteSpace(session.ReaderId))
            {
                var reader = ReaderStore.GetByCardId(session.ReaderId);
                if (reader != null && !string.IsNullOrWhiteSpace(reader.FullName))
                    session.UserName = reader.FullName;
            }

            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT OR IGNORE INTO sessions
                    (pc_number, session_type, user_name, reader_id, duration_secs,
                     earned, paid, refund, start_time, end_time, operator_name)
                    VALUES (@pc,@st,@un,@ri,@ds,@ea,@pa,@ra,@s,@e,@op)";
                BindRecord(cmd, session);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка сохранения сессии: {ex.Message}");
            }

            Logger.Info($"💰 Сессия: ПК={session.PcNumber}, Тип={session.SessionType}, Сумма={session.EarnedAmount}");
            Sessions.Insert(0, session);
        }

        public static void ClearAll()
        {
            try
            {
                using var conn = Open();
                Exec(conn, "DELETE FROM sessions");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка очистки финансов: {ex.Message}");
            }
            Sessions.Clear();
        }

        private static void BindRecord(SqliteCommand cmd, SessionRecord r)
        {
            cmd.Parameters.AddWithValue("@pc", r.PcNumber);
            cmd.Parameters.AddWithValue("@st", r.SessionType);
            cmd.Parameters.AddWithValue("@un", r.UserName);
            cmd.Parameters.AddWithValue("@ri", r.ReaderId);
            cmd.Parameters.AddWithValue("@ds", r.DurationSeconds);
            cmd.Parameters.AddWithValue("@ea", r.EarnedAmount);
            cmd.Parameters.AddWithValue("@pa", r.PaidAmount);
            cmd.Parameters.AddWithValue("@ra", r.RefundAmount);
            cmd.Parameters.AddWithValue("@s",  r.StartTime.ToString("o"));
            cmd.Parameters.AddWithValue("@e",  r.EndTime.ToString("o"));
            cmd.Parameters.AddWithValue("@op", r.OperatorName);
        }
    }
}
