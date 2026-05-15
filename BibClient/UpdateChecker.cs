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

            // Завершаем процесс — установщик остановит нас принудительно,
            // но лучше выйти чисто чтобы Guardian не мешал установке
            System.Windows.Application.Current.Dispatcher.Invoke(
                () => System.Windows.Application.Current.Shutdown());
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
