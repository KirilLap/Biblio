using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BibClient
{
    internal static class UpdateChecker
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public static string CurrentVersion =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        /// <summary>
        /// Запускает фоновую задачу с периодической проверкой раз в час.
        /// Возвращается сразу — проверка идёт в фоне.
        /// </summary>
        public static Task StartPeriodicCheckAsync(string serverBaseUrl)
        {
            return Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromHours(1));
                    await CheckAsync(serverBaseUrl);
                }
            });
        }

        public static async Task CheckAsync(string serverBaseUrl)
        {
            VersionInfo? info = null;
            try
            {
                var url = serverBaseUrl.TrimEnd('/') + "/updates/version.json";
                var json = await _http.GetStringAsync(url);
                info = JsonSerializer.Deserialize<VersionInfo>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return;
            }

            if (info == null || !IsNewer(info.Version, CurrentVersion)) return;

            Logger.Info($"Доступна версия {info.Version}, текущая {CurrentVersion} — запуск тихого обновления");
            await DownloadAndRunAsync(serverBaseUrl, info.InstallerFile);
        }

        private static async Task DownloadAndRunAsync(string serverBaseUrl, string installerFile)
        {
            var downloadUrl = serverBaseUrl.TrimEnd('/') + "/updates/" + installerFile;
            var tempPath = Path.Combine(Path.GetTempPath(), installerFile);
            try
            {
                var bytes = await _http.GetByteArrayAsync(downloadUrl);
                await File.WriteAllBytesAsync(tempPath, bytes);
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка загрузки обновления: {ex.Message}");
                return;
            }

            // /VERYSILENT — без окон, /NORESTART — без перезагрузки ПК,
            // /COMPONENTS=client — только BibClient, без серверных компонентов
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = tempPath,
                Arguments = "/VERYSILENT /NORESTART /COMPONENTS=client",
                UseShellExecute = true
            });

            // Даём инсталлятору 1 сек на старт, затем завершаем процесс.
            // Используем Environment.Exit, а не Application.Shutdown — иначе
            // настройка PreventClose может отменить закрытие через Window.Closing.
            // Инсталлятор сам убьёт процесс через taskkill, но лучше выйти чисто.
            await Task.Delay(1000);
            Logger.Info("⬆️ Завершаем BibClient для установки обновления...");
            Environment.Exit(0);
        }

        private static bool IsNewer(string remote, string current) =>
            Version.TryParse(remote, out var r) && Version.TryParse(current, out var c) && r > c;
    }

    internal class VersionInfo
    {
        public string Version { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public string InstallerFile { get; set; } = "";
    }
}
