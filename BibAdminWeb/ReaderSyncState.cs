using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace BibAdminWeb
{
    /// <summary>
    /// Автообновление базы читателей. Робот ReaderSync (Python + Playwright) работает на этом же
    /// компьютере: скачивает выгрузку читателей с UZNEL и отдаёт её серверу через /api/sync/readers.
    ///
    /// Приём файла разрешён только с этого же компьютера (127.0.0.1) и только с ключом из
    /// data\reader_sync_token.txt — робот читает ключ из того же файла, пароль администратора
    /// ему не нужен. Результат последнего запуска робот сообщает сам; он хранится в
    /// data\reader_sync_status.json и показывается в админке на странице «Читатели».
    /// </summary>
    public static class ReaderSyncState
    {
        // Имя задания в Планировщике Windows — его создаёт ReaderSync\setup.cmd
        public const string TaskName = "BibLibReaderSync";

        private static readonly string DataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        private static readonly string TokenPath = Path.Combine(DataDir, "reader_sync_token.txt");
        private static readonly string StatusPath = Path.Combine(DataDir, "reader_sync_status.json");
        private static readonly object _lock = new();

        /// <summary>Ключ робота; создаётся при первом обращении.</summary>
        public static string Token
        {
            get
            {
                lock (_lock)
                {
                    try
                    {
                        if (File.Exists(TokenPath))
                        {
                            var t = File.ReadAllText(TokenPath).Trim();
                            if (t.Length >= 32) return t;
                        }
                        Directory.CreateDirectory(DataDir);
                        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                        File.WriteAllText(TokenPath, token);
                        return token;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Ошибка ключа автообновления читателей: {ex.Message}");
                        return "";
                    }
                }
            }
        }

        public static bool IsAuthorized(HttpContext ctx)
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip == null || !IPAddress.IsLoopback(ip)) return false;
            var token = Token;
            var given = ctx.Request.Headers["X-Sync-Token"].ToString();
            if (token.Length == 0 || given.Length != token.Length) return false;
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(token));
        }

        public static string StatusJson()
        {
            lock (_lock)
            {
                try { return File.Exists(StatusPath) ? File.ReadAllText(StatusPath) : "{}"; }
                catch { return "{}"; }
            }
        }

        public static void SaveStatus(string json)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(StatusPath, json);
            }
        }
    }
}
