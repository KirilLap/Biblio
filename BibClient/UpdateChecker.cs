using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

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

            var msg = $"Доступна версия {info.Version}  (у вас {CurrentVersion})";
            if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
                msg += $"\n\n{info.ReleaseNotes}";
            msg += "\n\nОбновить сейчас?";

            var result = MessageBox.Show(msg, "Обновление BibClient",
                MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
            if (result != MessageBoxResult.Yes) return;

            await DownloadAndRunAsync(serverBaseUrl, info.InstallerFile);
        }

        private static async Task DownloadAndRunAsync(string serverBaseUrl, string installerFile)
        {
            var downloadUrl = serverBaseUrl.TrimEnd('/') + "/updates/" + installerFile;
            var tempPath = Path.Combine(Path.GetTempPath(), installerFile);
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var bytes = await _http.GetByteArrayAsync(downloadUrl);
                await File.WriteAllBytesAsync(tempPath, bytes);
            }
            catch (Exception ex)
            {
                Mouse.OverrideCursor = null;
                MessageBox.Show($"Ошибка загрузки обновления:\n{ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true
            });
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
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
