using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BibClient
{
    internal static class UpdateChecker
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly HttpClient _downloadHttp = new() { Timeout = TimeSpan.FromMinutes(5) };
        private static int _downloading = 0;

        public static string CurrentVersion =>
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        /// <summary>
        /// Вызывается когда обновление не случилось: "no_update" — версия та же, "download_failed" — ошибка загрузки.
        /// NetworkManager подписывается и сообщает серверу.
        /// </summary>
        public static event Action<string>? UpdateFailed;

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
                var url = serverBaseUrl.TrimEnd('/') + "/updates/bibclient-version.json";
                var json = await _http.GetStringAsync(url);
                info = JsonSerializer.Deserialize<VersionInfo>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return;
            }

            if (info == null || !IsNewer(info.Version, CurrentVersion))
            {
                Logger.Info($"ℹ️ Обновление не требуется: установлена {CurrentVersion}, на сервере {info?.Version ?? "?"}");
                UpdateFailed?.Invoke("no_update");
                return;
            }

            Logger.Info($"Доступна версия {info.Version}, текущая {CurrentVersion} — запуск тихого обновления");
            await DownloadAndRunAsync(serverBaseUrl, info.InstallerFile);
        }

        // Имя флагового файла, который читает BibClientService для запуска инсталлятора от SYSTEM.
        // Путь к инсталлятору записывается в этот файл; сам файл кладётся рядом с exe.
        public static readonly string UpdateFlagFileName = "BibClientUpdate.flag";

        private static async Task DownloadAndRunAsync(string serverBaseUrl, string installerFile)
        {
            if (Interlocked.CompareExchange(ref _downloading, 1, 0) != 0)
            {
                Logger.Info("⏳ Загрузка обновления уже идёт, пропускаем дублирующий запрос");
                return;
            }

            var downloadUrl = serverBaseUrl.TrimEnd('/') + "/updates/" + installerFile;
            // Кладём инсталлятор в папку приложения, а не в %TEMP% —
            // AppLocker в заблокированных средах запрещает запуск exe из временных папок.
            var installerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, installerFile);
            try
            {
                var bytes = await _downloadHttp.GetByteArrayAsync(downloadUrl);
                await File.WriteAllBytesAsync(installerPath, bytes);
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка загрузки обновления: {ex.Message}");
                UpdateFailed?.Invoke("download_failed");
                Interlocked.Exchange(ref _downloading, 0);
                return;
            }

            // Записываем флаг-файл для BibClientService.
            // Служба работает от SYSTEM — она запустит инсталлятор без UAC и без ограничений AppLocker.
            try
            {
                var flagPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, UpdateFlagFileName);
                await File.WriteAllTextAsync(flagPath, installerPath);
                Logger.Info($"📋 Флаг обновления записан: {flagPath}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Ошибка записи флага обновления: {ex.Message}");
                UpdateFailed?.Invoke("download_failed");
                Interlocked.Exchange(ref _downloading, 0);
                return;
            }

            // Сигнализируем Guardian и Windows-службе что закрытие легальное —
            // иначе они немедленно перезапустят BibClient и инсталлятор не сможет
            // заменить заблокированный exe-файл.
            Watchdog.StopGuardian();
            ServiceManager.SignalLegalClose();

            await Task.Delay(1000);
            Logger.Info("⬆️ Завершаем BibClient — установку выполнит BibClientService от SYSTEM...");
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
