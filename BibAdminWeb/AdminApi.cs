using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace BibAdminWeb
{
    // REST API handlers for the admin web panel.
    public static class AdminApi
    {
        private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public static async Task Handle(HttpContext ctx, RequestDelegate next)
        {
            var path = ctx.Request.Path.Value ?? "";
            var method = ctx.Request.Method;

            if (!path.StartsWith("/api/admin/")) { await next(ctx); return; }

            if (!AdminAuth.IsAuthorized(ctx))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":\"Unauthorized\"}");
                return;
            }

            ctx.Response.ContentType = "application/json";

            // ─── Settings ─────────────────────────────────────────────────────
            if (path == "/api/admin/settings" && method == "GET")
            {
                var s = GlobalSettings.Load();
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(s, _json));
                return;
            }
            if (path == "/api/admin/settings" && method == "POST")
            {
                var body = await ReadBody(ctx);
                var s = JsonSerializer.Deserialize<GlobalSettings>(body, _json) ?? new GlobalSettings();
                s.Save();
                // Push new settings to all online PCs
                AdminBroadcaster.Instance?.PushSettings(s);
                await ctx.Response.WriteAsync("{\"ok\":true}");
                return;
            }

            // ─── Finance: sessions ────────────────────────────────────────────
            if (path == "/api/admin/finance/sessions" && method == "GET")
            {
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(FinanceStore.Sessions, _json));
                return;
            }
            if (path == "/api/admin/finance/sessions" && method == "DELETE")
            {
                FinanceStore.Sessions.Clear();
                FinanceStore.SaveHistory();
                await ctx.Response.WriteAsync("{\"ok\":true}");
                return;
            }

            // ─── Finance: services ────────────────────────────────────────────
            if (path == "/api/admin/finance/services" && method == "GET")
            {
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(ServiceTransaction.All, _json));
                return;
            }
            if (path == "/api/admin/finance/services" && method == "DELETE")
            {
                ServiceTransaction.All.Clear();
                ServiceTransaction.SaveHistory();
                await ctx.Response.WriteAsync("{\"ok\":true}");
                return;
            }
            if (path.StartsWith("/api/admin/finance/services/") && path.EndsWith("/pay") && method == "POST")
            {
                var id = path.Replace("/api/admin/finance/services/", "").Replace("/pay", "");
                ServiceTransaction.MarkAsPaid(id);
                await ctx.Response.WriteAsync("{\"ok\":true}");
                return;
            }

            // ─── Finance: debts ───────────────────────────────────────────────
            if (path == "/api/admin/finance/debts" && method == "GET")
            {
                var debts = ServiceTransaction.GetAllUnpaid()
                    .Select(t => new {
                        t.Id, t.ServiceName, t.Unit, t.Quantity, t.PricePerUnit,
                        t.TotalAmount, t.DebtAmount, t.ReaderId, t.ReaderName,
                        t.PcNumber, createdAt = t.CreatedAt.ToString("o")
                    });
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(debts, _json));
                return;
            }
            if (path.StartsWith("/api/admin/finance/debts/") && path.EndsWith("/pay") && method == "POST")
            {
                var id = path.Replace("/api/admin/finance/debts/", "").Replace("/pay", "");
                ServiceTransaction.MarkAsPaid(id);
                await ctx.Response.WriteAsync("{\"ok\":true}");
                return;
            }
            if (path == "/api/admin/finance/debts/pay-reader" && method == "POST")
            {
                var body = await ReadBody(ctx);
                var data = JsonSerializer.Deserialize<JsonElement>(body, _json);
                var readerIdVal = data.TryGetProperty("readerId", out var rp) ? rp.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(readerIdVal)) ServiceTransaction.MarkAllPaidForReader(readerIdVal);
                await ctx.Response.WriteAsync("{\"ok\":true}");
                return;
            }
            if (path == "/api/admin/finance/debts/pay-pc" && method == "POST")
            {
                var body = await ReadBody(ctx);
                var data = JsonSerializer.Deserialize<JsonElement>(body, _json);
                var pcVal = data.TryGetProperty("pcNumber", out var pp) ? pp.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(pcVal)) ServiceTransaction.MarkAllPaidForPc(pcVal);
                await ctx.Response.WriteAsync("{\"ok\":true}");
                return;
            }

            // ─── Finance: export CSV ─────────────────────────────────────────
            if (path == "/api/admin/finance/export" && method == "GET")
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== СЕССИИ ===");
                sb.AppendLine("ПК;Тип;ID читателя;Пользователь;Длительность;Сумма;Оплачено;Возврат;Оператор;Начало;Конец");
                foreach (var s in FinanceStore.Sessions)
                {
                    int h = s.DurationSeconds / 3600, m = (s.DurationSeconds % 3600) / 60, sec = s.DurationSeconds % 60;
                    sb.AppendLine($"{s.PcNumber};{s.SessionType};{s.ReaderId};{s.UserName};{h:D2}:{m:D2}:{sec:D2};{s.EarnedAmount};{s.PaidAmount};{s.RefundAmount};{s.OperatorName};{s.StartTime:dd.MM.yyyy HH:mm};{s.EndTime:dd.MM.yyyy HH:mm}");
                }
                sb.AppendLine();
                sb.AppendLine("=== УСЛУГИ ===");
                sb.AppendLine("Услуга;Единица;Кол-во;Цена/ед;Итого;Оплачено;Читатель;Дата");
                foreach (var t in ServiceTransaction.All)
                    sb.AppendLine($"{t.ServiceName};{t.Unit};{t.Quantity};{t.PricePerUnit};{t.TotalAmount};{t.PaidAmount};{t.ReaderName};{t.CreatedAt:dd.MM.yyyy HH:mm}");

                var csv = sb.ToString();
                ctx.Response.ContentType = "text/csv; charset=utf-8";
                ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=finance_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                await ctx.Response.WriteAsync(csv, Encoding.UTF8);
                return;
            }

            // ─── Operators ────────────────────────────────────────────────────
            if (path == "/api/admin/operators" && method == "GET")
            {
                var s = GlobalSettings.Load();
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(s.Operators, _json));
                return;
            }
            if (path == "/api/admin/operators" && method == "POST")
            {
                var body = await ReadBody(ctx);
                var data = JsonSerializer.Deserialize<JsonElement>(body);
                var login = data.GetProperty("login").GetString() ?? "";
                var displayName = data.GetProperty("displayName").GetString() ?? "";
                var password = data.GetProperty("password").GetString() ?? "";
                var s = GlobalSettings.Load();
                if (s.Operators.Any(o => o.Login == login))
                {
                    ctx.Response.StatusCode = 409;
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = $"Логин '{login}' уже занят" }));
                    return;
                }
                s.Operators.Add(new OperatorAccount
                {
                    Login = login, DisplayName = displayName,
                    PasswordHash = HashPassword(password), IsActive = true
                });
                s.Save();
                await ctx.Response.WriteAsync("{\"ok\":true}");
                return;
            }
            if (path.StartsWith("/api/admin/operators/"))
            {
                var id = path.Replace("/api/admin/operators/", "");
                if (id.EndsWith("/active") && method == "PATCH")
                {
                    id = id.Replace("/active", "");
                    var body = await ReadBody(ctx);
                    var data = JsonSerializer.Deserialize<JsonElement>(body);
                    var isActive = data.GetProperty("isActive").GetBoolean();
                    var s = GlobalSettings.Load();
                    var op = s.Operators.Find(o => o.Id == id);
                    if (op != null) { op.IsActive = isActive; s.Save(); }
                    await ctx.Response.WriteAsync("{\"ok\":true}");
                    return;
                }
                if (id.EndsWith("/password") && method == "PATCH")
                {
                    id = id.Replace("/password", "");
                    var body = await ReadBody(ctx);
                    var data = JsonSerializer.Deserialize<JsonElement>(body);
                    var password = data.GetProperty("password").GetString() ?? "";
                    var s = GlobalSettings.Load();
                    var op = s.Operators.Find(o => o.Id == id);
                    if (op != null) { op.PasswordHash = HashPassword(password); s.Save(); }
                    await ctx.Response.WriteAsync("{\"ok\":true}");
                    return;
                }
                if (method == "DELETE")
                {
                    var s = GlobalSettings.Load();
                    s.Operators.RemoveAll(o => o.Id == id);
                    s.Save();
                    await ctx.Response.WriteAsync("{\"ok\":true}");
                    return;
                }
            }

            // ─── Computers ────────────────────────────────────────────────────
            if (path == "/api/admin/computers" && method == "GET")
            {
                var all = AdminHub.KnownClients.Values.Select(AdminWebHub.ClientDto).ToList();
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(all, _json));
                return;
            }
            if (path.StartsWith("/api/admin/computers/") && method == "DELETE")
            {
                var pcNumber = Uri.UnescapeDataString(path.Replace("/api/admin/computers/", ""));
                var ok = AdminHub.DeleteClientStatic(pcNumber);
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { ok }));
                return;
            }

            // ─── Check update ─────────────────────────────────────────────────
            if (path == "/api/admin/check-update" && method == "GET")
            {
                var updatesDir = GetUpdatesPath();
                var versionFile = Path.Combine(updatesDir, "bibadminweb-version.json");
                if (!File.Exists(versionFile))
                {
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new {
                        hasUpdate = false,
                        currentVersion = UpdateChecker.CurrentVersion,
                        newVersion = (string?)null,
                        releaseNotes = (string?)null
                    }, _json));
                    return;
                }
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(versionFile));
                    var root = doc.RootElement;
                    var newVer = root.TryGetProperty("Version", out var vp) ? vp.GetString() ?? "" : "";
                    var notes = root.TryGetProperty("ReleaseNotes", out var np) ? np.GetString() ?? "" : "";
                    var hasUpdate = Version.TryParse(newVer, out var r) &&
                                    Version.TryParse(UpdateChecker.CurrentVersion, out var c) && r > c;
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new {
                        hasUpdate,
                        currentVersion = UpdateChecker.CurrentVersion,
                        newVersion = newVer,
                        releaseNotes = notes
                    }, _json));
                }
                catch
                {
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsync("{\"error\":\"Ошибка чтения version.json\"}");
                }
                return;
            }

            // ─── Apply update ─────────────────────────────────────────────────
            if (path == "/api/admin/apply-update" && method == "POST")
            {
                var vfPath = Path.Combine(GetUpdatesPath(), "bibadminweb-version.json");
                string installerName = "bibadminweb-setup.exe";
                if (File.Exists(vfPath))
                {
                    try
                    {
                        using var vDoc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(vfPath));
                        if (vDoc.RootElement.TryGetProperty("InstallerFile", out var ip) && !string.IsNullOrWhiteSpace(ip.GetString()))
                            installerName = ip.GetString()!;
                    }
                    catch { }
                }
                var installerPath = Path.Combine(GetUpdatesPath(), installerName);
                if (!File.Exists(installerPath))
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsync("{\"error\":\"Файл установщика не найден\"}");
                    return;
                }
                await ctx.Response.WriteAsync("{\"ok\":true}");
                _ = Task.Run(async () =>
                {
                    // Уведомляем операторов, даём им 2 секунды увидеть сообщение
                    if (OperatorBroadcaster.Instance != null)
                        await OperatorBroadcaster.Instance.NotifyServerRestartingAsync("Обновление системы");
                    await Task.Delay(2000);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = installerPath,
                        Arguments = "/VERYSILENT /NORESTART /COMPONENTS=adminweb",
                        UseShellExecute = true
                    });
                    await Task.Delay(500);
                    Environment.Exit(0);
                });
                return;
            }

            // ─── Apply folder update (no installer) ──────────────────────────
            if (path == "/api/admin/apply-folder-update" && method == "POST")
            {
                ctx.Response.ContentType = "application/json";
                string body2 = await ReadBody(ctx);
                string sourcePath = "";
                try
                {
                    using var doc = JsonDocument.Parse(body2);
                    sourcePath = doc.RootElement.GetProperty("sourcePath").GetString()?.Trim() ?? "";
                }
                catch { }

                if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("{\"error\":\"Папка не найдена\"}");
                    return;
                }

                var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                var exePath = Path.Combine(appDir, "BibAdminWeb.exe");

                // Create a temp batch script that waits, copies files, then restarts
                var scriptPath = Path.Combine(Path.GetTempPath(), "bib_selfupdate.bat");
                var script = string.Join("\r\n",
                    "@echo off",
                    "timeout /t 5 /nobreak >nul",
                    // Build xcopy exclusion list: skip data/config files that must not be overwritten
                    "set EXF=%TEMP%\\bib_xcopy_exclude.txt",
                    "echo .db> \"%TEMP%\\bib_xcopy_exclude.txt\"",
                    "echo settings.json>> \"%TEMP%\\bib_xcopy_exclude.txt\"",
                    "echo _history.json>> \"%TEMP%\\bib_xcopy_exclude.txt\"",
                    "echo appsettings.json>> \"%TEMP%\\bib_xcopy_exclude.txt\"",
                    $"xcopy /s /y /e /h /EXCLUDE:\"%TEMP%\\bib_xcopy_exclude.txt\" \"{sourcePath}\\\" \"{appDir}\\\"",
                    "del \"%TEMP%\\bib_xcopy_exclude.txt\"",
                    $"start \"\" \"{exePath}\"",
                    "del \"%~f0\""
                );
                File.WriteAllText(scriptPath, script, Encoding.Default);

                await ctx.Response.WriteAsync("{\"ok\":true}");
                _ = Task.Run(async () =>
                {
                    if (OperatorBroadcaster.Instance != null)
                        await OperatorBroadcaster.Instance.NotifyServerRestartingAsync("Обновление из папки");
                    await Task.Delay(1500);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c start \"\" \"{scriptPath}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    await Task.Delay(300);
                    Environment.Exit(0);
                });
                return;
            }

            // ─── Stop Server ──────────────────────────────────────────────────
            if (path == "/api/admin/stop" && method == "POST")
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    Environment.Exit(0);
                });
                await ctx.Response.WriteAsync("{\"ok\":true}");
                return;
            }

            await next(ctx);
        }

        private static string GetUpdatesPath()
        {
            var s = GlobalSettings.Load();
            if (!string.IsNullOrWhiteSpace(s.UpdatesPath))
                return s.UpdatesPath.TrimEnd('\\', '/');
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updates");
        }

        private static async Task<string> ReadBody(HttpContext ctx)
        {
            using var reader = new StreamReader(ctx.Request.Body);
            return await reader.ReadToEndAsync();
        }

        private static string HashPassword(string password)
        {
            var bytes = Encoding.UTF8.GetBytes(password);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
    }
}
