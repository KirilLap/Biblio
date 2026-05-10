using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace BibAdmin
{
    internal static class UpdateChecker
    {
        public static string CurrentVersion =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        public static async Task CheckAsync()
        {
            var versionFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updates", "version.json");
            if (!File.Exists(versionFile)) return;

            VersionInfo? info = null;
            try
            {
                var json = await File.ReadAllTextAsync(versionFile);
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

            var result = MessageBox.Show(msg, "Обновление BibAdmin",
                MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
            if (result != MessageBoxResult.Yes) return;

            var installerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updates", info.InstallerFile);
            if (!File.Exists(installerPath))
            {
                MessageBox.Show($"Файл установщика не найден:\n{installerPath}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            });
            MainWindow.Instance?.Dispatcher.Invoke(() => MainWindow.Instance.Close());
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
