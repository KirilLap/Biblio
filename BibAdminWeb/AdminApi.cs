using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ClosedXML.Excel;
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

            // ─── Finance: services batch create ──────────────────────────────
            if (path == "/api/admin/finance/services/batch" && method == "POST")
            {
                var body = await ReadBody(ctx);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var pcNumber   = root.TryGetProperty("pcNumber",   out var pp)  ? pp.GetString()  ?? "" : "";
                var readerId   = root.TryGetProperty("readerId",   out var rp)  ? rp.GetString()  ?? "" : "";
                var readerName = root.TryGetProperty("readerName", out var rnp) ? rnp.GetString() ?? "" : "";
                var payNow     = root.TryGetProperty("payNow",     out var pnp) && pnp.GetBoolean();

                // Resolve reader from active session on that PC
                if (!string.IsNullOrEmpty(pcNumber) &&
                    AdminHub.KnownClients.TryGetValue(pcNumber, out var pcClient) && pcClient.IsSession)
                {
                    if (string.IsNullOrEmpty(readerId))   readerId   = pcClient.ReaderId ?? "";
                    if (string.IsNullOrEmpty(readerName)) readerName = pcClient.UserName ?? "";
                }

                var batchId = Guid.NewGuid().ToString("N")[..8];
                var s2 = GlobalSettings.Load();
                if (root.TryGetProperty("items", out var itemsEl))
                {
                    foreach (var item in itemsEl.EnumerateArray())
                    {
                        var typeId = item.TryGetProperty("serviceTypeId", out var tp) ? tp.GetString() ?? "" : "";
                        var qty    = item.TryGetProperty("quantity",      out var qp) ? qp.GetInt32() : 1;
                        var svc    = s2.Services.FirstOrDefault(x => x.Id == typeId && x.IsActive);
                        if (svc == null) continue;
                        var tx = new ServiceTransaction
                        {
                            ServiceTypeId = svc.Id, ServiceName = svc.Name, Unit = svc.Unit,
                            Quantity = qty, PricePerUnit = svc.Price, TotalAmount = svc.Price * qty,
                            ReaderId = readerId, ReaderName = readerName,
                            PcNumber = pcNumber, BatchId = batchId
                        };
                        ServiceTransaction.Add(tx);
                        if (payNow) ServiceTransaction.MarkAsPaid(tx.Id);
                    }
                }
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

            // ─── Finance: export XLSX ────────────────────────────────────────
            if (path == "/api/admin/finance/export" && method == "GET")
            {
                var bytes = BuildFinanceExcel(FinanceStore.Sessions, ServiceTransaction.All);
                ctx.Response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=finance_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                await ctx.Response.Body.WriteAsync(bytes);
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
                var canReaders = data.TryGetProperty("canViewReaders", out var rvr) && rvr.GetBoolean();
                var canFinance = data.TryGetProperty("canViewFinance", out var rvf) && rvf.GetBoolean();
                s.Operators.Add(new OperatorAccount
                {
                    Login = login, DisplayName = displayName,
                    PasswordHash = HashPassword(password), IsActive = true,
                    CanViewReaders = canReaders, CanViewFinance = canFinance
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
                if (id.EndsWith("/permissions") && method == "PATCH")
                {
                    id = id.Replace("/permissions", "");
                    var body = await ReadBody(ctx);
                    var data = JsonSerializer.Deserialize<JsonElement>(body);
                    var s = GlobalSettings.Load();
                    var op = s.Operators.Find(o => o.Id == id);
                    if (op != null)
                    {
                        if (data.TryGetProperty("canViewReaders", out var rvr)) op.CanViewReaders = rvr.GetBoolean();
                        if (data.TryGetProperty("canViewFinance", out var rvf)) op.CanViewFinance = rvf.GetBoolean();
                        s.Save();
                        // Уведомляем оператора в реальном времени — его браузер обновит вкладки
                        if (OperatorBroadcaster.Instance != null)
                            await OperatorBroadcaster.Instance.NotifyPermissionsUpdatedAsync(id);
                    }
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
                var flagPath   = Path.Combine(appDir, "update_restart.flag");
                var script = string.Join("\r\n",
                    "@echo off",
                    "timeout /t 5 /nobreak >nul",
                    // robocopy: handles spaces in paths, no temp exclusion file needed
                    // /E=subdirs /IS=overwrite same /IT=overwrite tweaked
                    // /XF=exclude files  /XD=exclude dirs
                    // /NFL /NDL /NJH /NJS /NC /NS /NP=suppress output
                    //
                    // Исключаем все файлы настроек и runtime-данных, чтобы обновление
                    // не перезаписало порт, пароль, реестр ПК и историю финансов.
                    $"robocopy \"{sourcePath}\" \"{appDir}\" /E /IS /IT" +
                    // Настройки — самое важное
                    $" /XF global_settings.json" +
                    // Прочие конфиги
                    $" /XF *.db /XF settings.json /XF appsettings.json" +
                    // Runtime JSON-данные (реестр ПК, сессии, история)
                    $" /XF registry.json /XF active_sessions.json /XF deleted_pcs.json" +
                    $" /XF *_history.json /XF server_heartbeat.json /XF readers.json" +
                    // Папка с загруженными фоновыми изображениями
                    $" /XD Files" +
                    $" /NFL /NDL /NJH /NJS /NC /NS /NP",
                    // Flag tells Program.cs not to open a new browser window
                    $"echo.> \"{flagPath}\"",
                    $"start \"\" \"{exePath}\""
                    // Примечание: del "%~f0" НЕ используем — самоудаление батника при работе
                    // в cmd.exe /c оставляет cmd открытым вместо выхода (известная проблема Windows)
                );
                File.WriteAllText(scriptPath, script, Encoding.Default);

                await ctx.Response.WriteAsync("{\"ok\":true}");
                _ = Task.Run(async () =>
                {
                    if (OperatorBroadcaster.Instance != null)
                        await OperatorBroadcaster.Instance.NotifyServerRestartingAsync("Обновление из папки");
                    await Task.Delay(1500);
                    // Сохраняем текущее состояние сессий перед выходом — иначе если сессии
                    // были завершены незадолго до обновления, active_sessions.json может
                    // содержать устаревшие данные и после перезапуска появятся «призрачные» сессии.
                    AdminHub.SaveActiveSessions();
                    // UseShellExecute=true + WindowStyle=Hidden надёжнее скрывает окно для .bat файлов.
                    // UseShellExecute=false + CreateNoWindow иногда не скрывает окно cmd.exe.
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = scriptPath,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                        UseShellExecute = true
                    });
                    await Task.Delay(300);
                    Environment.Exit(0);
                });
                return;
            }

            // ─── Upload BibAdminWeb zip from browser and apply self-update ──
            if (path == "/api/admin/upload-server-zip" && method == "POST")
            {
                ctx.Response.ContentType = "application/json";
                var form = ctx.Request.Form;
                var file = form.Files.GetFile("zip");
                if (file == null || file.Length == 0)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("{\"error\":\"Файл не получен\"}");
                    return;
                }
                if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("{\"error\":\"Ожидается .zip файл\"}");
                    return;
                }

                // Save zip to temp, extract, then apply like folder update
                var tempZip = Path.Combine(Path.GetTempPath(), $"bib_server_update_{Guid.NewGuid():N}.zip");
                var tempDir = Path.Combine(Path.GetTempPath(), $"BibAdminWebUpdate_{Guid.NewGuid().ToString("N")[..8]}");
                try
                {
                    await using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write))
                        await file.CopyToAsync(fs);

                    ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);
                    File.Delete(tempZip);
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = "Ошибка распаковки: " + ex.Message }, _json));
                    try { File.Delete(tempZip); } catch { }
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                    return;
                }

                // Reuse the same self-update bat script logic
                var appDir   = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                var exePath  = Path.Combine(appDir, "BibAdminWeb.exe");
                var flagPath = Path.Combine(appDir, "update_restart.flag");
                var scriptPath = Path.Combine(Path.GetTempPath(), "bib_selfupdate_zip.bat");
                var script = string.Join("\r\n",
                    "@echo off",
                    "timeout /t 5 /nobreak >nul",
                    $"robocopy \"{tempDir}\" \"{appDir}\" /E /IS /IT" +
                    $" /XF global_settings.json" +
                    $" /XF *.db /XF settings.json /XF appsettings.json" +
                    $" /XF registry.json /XF active_sessions.json /XF deleted_pcs.json" +
                    $" /XF *_history.json /XF server_heartbeat.json /XF readers.json" +
                    $" /XD Files" +
                    $" /NFL /NDL /NJH /NJS /NC /NS /NP",
                    $"rmdir /s /q \"{tempDir}\"",
                    $"echo.> \"{flagPath}\"",
                    $"start \"\" \"{exePath}\""
                );
                File.WriteAllText(scriptPath, script, Encoding.Default);

                await ctx.Response.WriteAsync("{\"ok\":true}");
                _ = Task.Run(async () =>
                {
                    if (OperatorBroadcaster.Instance != null)
                        await OperatorBroadcaster.Instance.NotifyServerRestartingAsync("Обновление из zip-архива");
                    await Task.Delay(1500);
                    AdminHub.SaveActiveSessions();
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = scriptPath,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                        UseShellExecute = true
                    });
                    await Task.Delay(300);
                    Environment.Exit(0);
                });
                return;
            }

            // ─── Upload BibClient zip from browser ───────────────────────────
            if (path == "/api/admin/upload-client-zip" && method == "POST")
            {
                ctx.Response.ContentType = "application/json";
                var form = ctx.Request.Form;
                var file = form.Files.GetFile("zip");
                if (file == null || file.Length == 0)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("{\"error\":\"Файл не получен\"}");
                    return;
                }
                if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("{\"error\":\"Ожидается .zip файл\"}");
                    return;
                }
                var updDir = GetUpdatesPath();
                Directory.CreateDirectory(updDir);
                var destPath = Path.Combine(updDir, "bibclient-update.zip");
                try
                {
                    await using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);
                    await file.CopyToAsync(fs);
                    await ctx.Response.WriteAsync("{\"ok\":true}");
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }, _json));
                }
                return;
            }

            // ─── Pack BibClient folder update (zip for folder-based client update) ──
            if (path == "/api/admin/pack-client-update" && method == "POST")
            {
                ctx.Response.ContentType = "application/json";
                string bodyPack = await ReadBody(ctx);
                string packSource = "";
                try
                {
                    using var doc = JsonDocument.Parse(bodyPack);
                    packSource = doc.RootElement.GetProperty("sourcePath").GetString()?.Trim() ?? "";
                }
                catch { }

                if (string.IsNullOrWhiteSpace(packSource) || !Directory.Exists(packSource))
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsync("{\"error\":\"Папка не найдена\"}");
                    return;
                }

                var updDirPack = GetUpdatesPath();
                var zipPath = Path.Combine(updDirPack, "bibclient-update.zip");
                try
                {
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                    foreach (var file in Directory.GetFiles(packSource, "*", SearchOption.AllDirectories))
                    {
                        var entryName = Path.GetRelativePath(packSource, file);
                        archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                    }
                    await ctx.Response.WriteAsync("{\"ok\":true}");
                }
                catch (Exception ex)
                {
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }, _json));
                }
                return;
            }

            // ─── Stop Server ──────────────────────────────────────────────────
            if (path == "/api/admin/stop" && method == "POST")
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    AdminHub.SaveActiveSessions();
                    Environment.Exit(0);
                });
                await ctx.Response.WriteAsync("{\"ok\":true}");
                return;
            }

            await next(ctx);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Operator-facing API (/api/op/...)  — требует куки bib_op + нужное право
        // ══════════════════════════════════════════════════════════════════════
        public static async Task HandleOperator(HttpContext ctx, RequestDelegate next)
        {
            var path   = ctx.Request.Path.Value ?? "";
            var method = ctx.Request.Method;

            if (!path.StartsWith("/api/op/readers") && !path.StartsWith("/api/op/finance"))
            {
                await next(ctx);
                return;
            }

            ctx.Response.ContentType = "application/json";

            // Проверяем авторизацию оператора
            var token = ctx.Request.Cookies["bib_op"];
            if (token == null || !OperatorApi.ValidateToken(token, out var opId))
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("{\"error\":\"Не авторизован\"}");
                return;
            }
            var settings = GlobalSettings.Load();
            var op = settings.Operators.Find(o => o.Id == opId && o.IsActive);
            if (op == null)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("{\"error\":\"Оператор не найден\"}");
                return;
            }

            // ─── Operator: список/поиск читателей ─────────────────────────────
            if (path == "/api/op/readers" && method == "GET")
            {
                if (!op.CanViewReaders)
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsync("{\"error\":\"Нет доступа к разделу читателей\"}");
                    return;
                }
                var search = ctx.Request.Query["search"].ToString();
                var readers = ReaderStore.GetAll(string.IsNullOrWhiteSpace(search) ? null : search);
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(readers, _json));
                return;
            }

            // ─── Operator: история сессий ─────────────────────────────────────
            if (path == "/api/op/finance/sessions" && method == "GET")
            {
                if (!op.CanViewFinance)
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsync("{\"error\":\"Нет доступа к истории финансов\"}");
                    return;
                }
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(FinanceStore.Sessions, _json));
                return;
            }

            // ─── Operator: история услуг ──────────────────────────────────────
            if (path == "/api/op/finance/services" && method == "GET")
            {
                if (!op.CanViewFinance)
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsync("{\"error\":\"Нет доступа к истории финансов\"}");
                    return;
                }
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(ServiceTransaction.All, _json));
                return;
            }

            // ─── Operator: экспорт финансов XLSX ─────────────────────────────
            if (path == "/api/op/finance/export" && method == "GET")
            {
                if (!op.CanViewFinance)
                {
                    ctx.Response.StatusCode = 403;
                    await ctx.Response.WriteAsync("{\"error\":\"Нет доступа к истории финансов\"}");
                    return;
                }
                var bytes = BuildFinanceExcel(FinanceStore.Sessions, ServiceTransaction.All);
                ctx.Response.ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=finance_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                await ctx.Response.Body.WriteAsync(bytes);
                return;
            }

            await next(ctx);
        }

        // ── XLSX-экспорт финансов (сессии + услуги) ───────────────────────────
        private static byte[] BuildFinanceExcel(
            IEnumerable<SessionRecord> sessions,
            IEnumerable<ServiceTransaction> services)
        {
            using var wb = new XLWorkbook();

            // ── Лист «Сессии» ────────────────────────────────────────────────
            var wsS = wb.AddWorksheet("Сессии");
            string[] sHdr = { "ПК", "Тип", "ID читателя", "Пользователь", "Длительность",
                               "Сумма", "Оплачено", "Возврат", "Оператор", "Начало", "Конец" };
            for (int i = 0; i < sHdr.Length; i++)
            {
                wsS.Cell(1, i + 1).Value = sHdr[i];
                wsS.Cell(1, i + 1).Style.Font.Bold = true;
                wsS.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromArgb(0x2D, 0x2D, 0x5B);
                wsS.Cell(1, i + 1).Style.Font.FontColor = XLColor.White;
            }
            int row = 2;
            foreach (var s in sessions)
            {
                int h = s.DurationSeconds / 3600, m = (s.DurationSeconds % 3600) / 60, sec = s.DurationSeconds % 60;
                wsS.Cell(row, 1).Value = s.PcNumber;
                wsS.Cell(row, 2).Value = s.SessionType;
                wsS.Cell(row, 3).Value = s.ReaderId;
                wsS.Cell(row, 4).Value = s.UserName;
                wsS.Cell(row, 5).Value = $"{h:D2}:{m:D2}:{sec:D2}";
                wsS.Cell(row, 6).Value = s.EarnedAmount;
                wsS.Cell(row, 7).Value = s.PaidAmount;
                wsS.Cell(row, 8).Value = s.RefundAmount;
                wsS.Cell(row, 9).Value = s.OperatorName;
                wsS.Cell(row, 10).Value = s.StartTime.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                wsS.Cell(row, 11).Value = s.EndTime.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                row++;
            }
            wsS.Columns().AdjustToContents();

            // ── Лист «Услуги» ────────────────────────────────────────────────
            var wsSvc = wb.AddWorksheet("Услуги");
            string[] tHdr = { "Услуга", "Единица", "Кол-во", "Цена/ед", "Итого", "Оплачено", "Читатель", "ПК", "Дата" };
            for (int i = 0; i < tHdr.Length; i++)
            {
                wsSvc.Cell(1, i + 1).Value = tHdr[i];
                wsSvc.Cell(1, i + 1).Style.Font.Bold = true;
                wsSvc.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromArgb(0x2D, 0x2D, 0x5B);
                wsSvc.Cell(1, i + 1).Style.Font.FontColor = XLColor.White;
            }
            row = 2;
            foreach (var t in services)
            {
                wsSvc.Cell(row, 1).Value = t.ServiceName;
                wsSvc.Cell(row, 2).Value = t.Unit;
                wsSvc.Cell(row, 3).Value = t.Quantity;
                wsSvc.Cell(row, 4).Value = t.PricePerUnit;
                wsSvc.Cell(row, 5).Value = t.TotalAmount;
                wsSvc.Cell(row, 6).Value = t.PaidAmount;
                wsSvc.Cell(row, 7).Value = t.ReaderName;
                wsSvc.Cell(row, 8).Value = t.PcNumber;
                wsSvc.Cell(row, 9).Value = t.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                row++;
            }
            wsSvc.Columns().AdjustToContents();

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
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
