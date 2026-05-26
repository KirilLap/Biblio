using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace BibAdminWeb
{
    public class ServerHost
    {
        private IHost? _host;
        private DateTime _startedAt;

        private static readonly string HeartbeatPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server_heartbeat.json");

        private static void WriteHeartbeat(DateTime startedAt, DateTime lastAlive, DateTime? gracefulShutdown = null)
        {
            try
            {
                var data = new { StartedAtUtc = startedAt.ToString("o"), LastAliveUtc = lastAlive.ToString("o"), GracefulShutdownUtc = gracefulShutdown?.ToString("o") };
                File.WriteAllText(HeartbeatPath, JsonSerializer.Serialize(data));
            }
            catch { }
        }

        public async Task StartAsync(int port = 8080)
        {
            _startedAt = DateTime.UtcNow;
            WriteHeartbeat(_startedAt, _startedAt);

            var filesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Files");
            var wwwrootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            var gs = GlobalSettings.Load();
            var updatesPath = !string.IsNullOrWhiteSpace(gs.UpdatesPath)
                ? gs.UpdatesPath.TrimEnd('\\', '/')
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updates");
            Directory.CreateDirectory(filesPath);
            Directory.CreateDirectory(wwwrootPath);
            Directory.CreateDirectory(updatesPath);

            _host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseUrls($"http://*:{port}");
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddSignalR(options =>
                        {
                            options.MaximumReceiveMessageSize = 100 * 1024 * 1024;
                            options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                            options.ClientTimeoutInterval = TimeSpan.FromSeconds(25);
                        });
                        services.AddCors(options =>
                            options.AddPolicy("AllowAll", b => b.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
                        services.AddSingleton<OperatorBroadcaster>();
                        services.AddSingleton<AdminBroadcaster>();
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseCors("AllowAll");
                        app.UseRouting();

                        // Redirect / → /setup.html (первый запуск) или /admin-login.html
                        app.Use(async (ctx, next) =>
                        {
                            var path = ctx.Request.Path.Value ?? "";
                            if (path == "/" || path == "")
                            {
                                var s = GlobalSettings.Load();
                                ctx.Response.Redirect(s.IsFirstRun ? "/setup.html" : "/admin-login.html");
                                return;
                            }
                            // Блокируем доступ к защищённым страницам при первом запуске
                            if ((path == "/admin-login.html" || path == "/index.html") && GlobalSettings.Load().IsFirstRun)
                            {
                                ctx.Response.Redirect("/setup.html");
                                return;
                            }
                            await next(ctx);
                        });

                        // wwwroot static files
                        // Явно указываем charset=utf-8 для JS/CSS/HTML — иначе браузер может искажать кириллицу
                        var contentTypeProvider = new FileExtensionContentTypeProvider();
                        contentTypeProvider.Mappings[".js"]   = "application/javascript; charset=utf-8";
                        contentTypeProvider.Mappings[".css"]  = "text/css; charset=utf-8";
                        contentTypeProvider.Mappings[".html"] = "text/html; charset=utf-8";

                        app.UseStaticFiles(new StaticFileOptions
                        {
                            FileProvider = new PhysicalFileProvider(wwwrootPath),
                            RequestPath = "",
                            ContentTypeProvider = contentTypeProvider,
                            OnPrepareResponse = ctx =>
                            {
                                ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                                ctx.Context.Response.Headers["Pragma"] = "no-cache";
                                ctx.Context.Response.Headers["Expires"] = "0";
                            }
                        });

                        // Files (backgrounds)
                        app.UseStaticFiles(new StaticFileOptions
                        {
                            FileProvider = new PhysicalFileProvider(filesPath),
                            RequestPath = "/files"
                        });

                        // Папка обновлений — клиенты скачивают installer отсюда
                        app.UseStaticFiles(new StaticFileOptions
                        {
                            FileProvider = new PhysicalFileProvider(updatesPath),
                            RequestPath = "/updates"
                        });

                        // Auth endpoints
                        app.Use(async (ctx, next) =>
                        {
                            var path = ctx.Request.Path.Value ?? "";
                            var method = ctx.Request.Method;

                            if (method == "POST" && path == "/api/admin/setup")
                            { await AdminAuth.HandleSetup(ctx); return; }

                            if (method == "POST" && path == "/api/admin/login")
                            { await AdminAuth.HandleLogin(ctx); return; }
                            if (method == "POST" && path == "/api/admin/logout")
                            { await AdminAuth.HandleLogout(ctx); return; }
                            if (method == "GET" && path == "/api/admin/check")
                            { await AdminAuth.HandleCheck(ctx); return; }

                            if (method == "POST" && path == "/api/op/login")
                            { await OperatorApi.HandleLogin(ctx); return; }
                            if (method == "POST" && path == "/api/op/logout")
                            { await OperatorApi.HandleLogout(ctx); return; }
                            if (method == "GET" && path == "/api/op/me")
                            { await OperatorApi.HandleMe(ctx); return; }

                            // Публичный эндпоинт: настройки полей сессии (для панели оператора)
                            if (method == "GET" && path == "/api/session-fields")
                            {
                                var sf = GlobalSettings.Load();
                                ctx.Response.ContentType = "application/json";
                                await ctx.Response.WriteAsync(
                                    System.Text.Json.JsonSerializer.Serialize(
                                        new { requireReaderId = sf.RequireReaderId, requireUserName = sf.RequireUserName },
                                        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
                                return;
                            }

                            await next(ctx);
                        });

                        // Screenshot API (upload = no auth, watch/get = admin or operator)
                        app.Use(ScreenshotApi.Handle);

                        // Admin REST API
                        app.Use(AdminApi.Handle);
                        // Operator-facing REST API (/api/op/readers, /api/op/finance/...)
                        app.Use(AdminApi.HandleOperator);
                        // Readers API (lookup for operators + admin management)
                        app.Use(ReadersApi.Handle);

                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHub<AdminHub>("/hub");
                            endpoints.MapHub<AdminWebHub>("/adminhub");
                            endpoints.MapHub<OperatorHub>("/webhub");
                        });
                    });
                })
                .Build();

            await _host.StartAsync();

            // Force init singletons so they subscribe to events immediately
            _host.Services.GetRequiredService<OperatorBroadcaster>();
            _host.Services.GetRequiredService<AdminBroadcaster>();

            // Heartbeat
            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
                while (await timer.WaitForNextTickAsync())
                    WriteHeartbeat(_startedAt, DateTime.UtcNow);
            });

            // Auto-save sessions
            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
                while (await timer.WaitForNextTickAsync())
                {
                    try { AdminHub.SaveActiveSessions(); }
                    catch (Exception ex) { Logger.Error($"Ошибка автосохранения: {ex.Message}"); }
                }
            });

            Logger.Info($"🚀 BibAdmin Web запущен на порту {port}");
        }

        public async Task StopAsync()
        {
            WriteHeartbeat(_startedAt, DateTime.UtcNow, DateTime.UtcNow);
            try { AdminHub.SaveActiveSessions(); } catch { }
            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
                Logger.Info("Сервер остановлен");
            }
        }
    }
}
