using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Http;

namespace BibAdmin
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
                var data = new
                {
                    StartedAtUtc = startedAt.ToString("o"),
                    LastAliveUtc = lastAlive.ToString("o"),
                    GracefulShutdownUtc = gracefulShutdown?.ToString("o")
                };
                File.WriteAllText(HeartbeatPath, JsonSerializer.Serialize(data));
            }
            catch { }
        }

        private static double ReadPrevDowntimeSeconds()
        {
            try
            {
                if (!File.Exists(HeartbeatPath)) return 0;
                using var doc = JsonDocument.Parse(File.ReadAllText(HeartbeatPath));
                var root = doc.RootElement;

                // Если было корректное завершение — простоя нет
                if (root.TryGetProperty("GracefulShutdownUtc", out var gsProp) && gsProp.GetString() != null)
                    return 0;

                if (root.TryGetProperty("LastAliveUtc", out var laProp) &&
                    DateTime.TryParse(laProp.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastAlive))
                    return Math.Max(0, (DateTime.UtcNow - lastAlive).TotalSeconds);
            }
            catch { }
            return 0;
        }

        public async Task StartAsync(int port = 8080)
        {
            _startedAt = DateTime.UtcNow;

            // Читаем предыдущий пульс: если сервер упал без graceful-stop — логируем простой
            var downtimeSec = ReadPrevDowntimeSeconds();
            if (downtimeSec > 5)
                Logger.Warn($"⚠️ Сервер был недоступен ~{downtimeSec:F0}с (аварийное завершение)");
            else
                Logger.Info("✅ Предыдущее завершение было корректным");

            // Сразу пишем «живой» пульс (сбрасываем GracefulShutdownUtc)
            WriteHeartbeat(_startedAt, _startedAt);

            // Пути к папкам
            var filesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Files");
            var wwwrootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            var updatesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updates");
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
                        {
                            options.AddPolicy("AllowAll", builder =>
                            {
                                builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
                            });
                        });
                        services.AddSingleton<OperatorBroadcaster>();
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseCors("AllowAll");
                        app.UseRouting();

                        // Статические файлы веб-оператора
                        var contentTypeProvider = new FileExtensionContentTypeProvider();
                        // Явно задаём charset=utf-8 для JS и CSS — иначе браузер может не распознать кириллицу
                        contentTypeProvider.Mappings[".js"]  = "application/javascript; charset=utf-8";
                        contentTypeProvider.Mappings[".css"] = "text/css; charset=utf-8";
                        contentTypeProvider.Mappings[".html"] = "text/html; charset=utf-8";

                        app.UseStaticFiles(new StaticFileOptions
                        {
                            FileProvider = new PhysicalFileProvider(wwwrootPath),
                            RequestPath = "",
                            ContentTypeProvider = contentTypeProvider
                        });

                        // Статические файлы (фоны)
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

                        // Screenshot upload (no auth — from BibClient)
                        app.Use(async (ctx, next) =>
                        {
                            if (ctx.Request.Method == "POST" && ctx.Request.Path == "/api/screenshot/upload")
                            {
                                var pc = ctx.Request.Query["pc"].ToString();
                                if (!string.IsNullOrEmpty(pc))
                                {
                                    using var ms = new System.IO.MemoryStream();
                                    await ctx.Request.Body.CopyToAsync(ms);
                                    var bytes = ms.ToArray();
                                    if (bytes.Length > 0) ScreenshotStore.StoreFrame(pc, bytes);
                                }
                                ctx.Response.StatusCode = 204;
                                return;
                            }
                            await next();
                        });

                        // REST API для авторизации операторов
                        app.Use(async (ctx, next) =>
                        {
                            if (ctx.Request.Method == "POST" && ctx.Request.Path == "/api/op/login")
                            {
                                await OperatorApi.HandleLogin(ctx);
                                return;
                            }
                            if (ctx.Request.Method == "POST" && ctx.Request.Path == "/api/op/logout")
                            {
                                await OperatorApi.HandleLogout(ctx);
                                return;
                            }
                            if (ctx.Request.Method == "GET" && ctx.Request.Path == "/api/op/me")
                            {
                                await OperatorApi.HandleMe(ctx);
                                return;
                            }
                            await next();
                        });

                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHub<AdminHub>("/hub");
                            endpoints.MapHub<OperatorHub>("/webhub");
                        });
                    });
                })
                .Build();

            await _host.StartAsync();

            // Инициализируем broadcaster — он подписывается на события AdminHub
            _host.Services.GetRequiredService<OperatorBroadcaster>();

            // Пульс сервера каждые 30 секунд — защита от аварийного завершения
            _ = Task.Run(async () =>
            {
                using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));
                while (await heartbeatTimer.WaitForNextTickAsync())
                    WriteHeartbeat(_startedAt, DateTime.UtcNow);
            });

            // Автосохранение активных сессий каждую минуту
            _ = Task.Run(async () =>
            {
                using var saveTimer = new PeriodicTimer(TimeSpan.FromMinutes(1));
                while (await saveTimer.WaitForNextTickAsync())
                {
                    try
                    {
                        AdminHub.SaveActiveSessions();
                        Logger.Info("💾 Автосохранение активных сессий выполнено");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"❌ Ошибка автосохранения: {ex.Message}");
                    }
                }
            });

            Logger.Info($"🚀 Сервер запущен на порту {port}");
        }

        public async Task StopAsync()
        {
            // Пишем метку корректного завершения — при следующем старте поймём, что краша не было
            WriteHeartbeat(_startedAt, DateTime.UtcNow, DateTime.UtcNow);

            try { AdminHub.SaveActiveSessions(); } catch { }

            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
                Logger.Info(" Сервер остановлен");
            }
        }
    }
}