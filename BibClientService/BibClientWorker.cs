using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace BibClientService
{
    public sealed class BibClientWorker : BackgroundService
    {
        private readonly ILogger<BibClientWorker> _logger;

        // Путь к BibClient.exe — в той же папке, что и сервис
        private static readonly string ClientExePath = Path.Combine(
            AppContext.BaseDirectory,
            "BibClient.exe"
        );

        // Флаг легального закрытия — создаётся BibClient при корректном завершении (unlock admin)
        // Сервис при обнаружении флага не перезапускает BibClient 30 минут
        private static readonly string LegalCloseFlagPath = Path.Combine(
            AppContext.BaseDirectory,
            "BibClientLegalClose.flag"
        );

        // Флаг exe-обновления — BibClient записывает сюда путь к скачанному инсталлятору.
        // Служба планирует запуск инсталлятора через Task Scheduler (вне Job Object сервиса).
        private static readonly string UpdateFlagPath = Path.Combine(
            AppContext.BaseDirectory,
            "BibClientUpdate.flag"
        );

        // Флаг папочного обновления — BibClient записывает сюда путь к zip-архиву.
        // Служба распаковывает архив в каталог приложения без инсталлятора.
        private static readonly string FolderUpdateFlagPath = Path.Combine(
            AppContext.BaseDirectory,
            "BibClientFolderUpdate.flag"
        );

        // Минимальный интервал между перезапусками (защита от бесконечного цикла при краше при старте)
        private static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(5);

        // Время ожидания после легального закрытия администратором
        private static readonly TimeSpan LegalCloseDelay = TimeSpan.FromMinutes(30);

        public BibClientWorker(ILogger<BibClientWorker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("BibClientService запущен. Слежу за: {Path}", ClientExePath);

            DateTime lastRestartTime = DateTime.MinValue;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, stoppingToken);

                    if (!File.Exists(ClientExePath))
                    {
                        _logger.LogWarning("BibClient.exe не найден по пути: {Path}", ClientExePath);
                        continue;
                    }

                    bool isRunning = IsBibClientRunning();

                    if (!isRunning)
                    {
                        // Папочное обновление (zip): приоритет перед exe-установщиком.
                        // BibClient скачал zip и записал путь в BibClientFolderUpdate.flag.
                        if (File.Exists(FolderUpdateFlagPath))
                        {
                            string zipPath = "";
                            try { zipPath = (await File.ReadAllTextAsync(FolderUpdateFlagPath, stoppingToken)).Trim(); }
                            catch { }
                            File.Delete(FolderUpdateFlagPath);
                            try { File.Delete(LegalCloseFlagPath); } catch { }

                            _logger.LogInformation("Флаг папочного обновления — распаковка: {Path}", zipPath);
                            await ApplyFolderUpdateAsync(zipPath, stoppingToken);
                            await Task.Delay(2000, stoppingToken);
                            continue;
                        }

                        // Exe-обновление: BibClient записал путь к скачанному инсталлятору.
                        // Запускаем через Task Scheduler чтобы выйти за пределы Job Object сервиса —
                        // иначе Windows убивает дочерний процесс когда инсталлятор останавливает службу.
                        if (File.Exists(UpdateFlagPath))
                        {
                            string installerPath = "";
                            try { installerPath = (await File.ReadAllTextAsync(UpdateFlagPath, stoppingToken)).Trim(); }
                            catch { }
                            File.Delete(UpdateFlagPath);
                            try { File.Delete(LegalCloseFlagPath); } catch { }

                            if (!string.IsNullOrWhiteSpace(installerPath) && File.Exists(installerPath))
                            {
                                _logger.LogInformation("Флаг обновления — планирую установку через Task Scheduler: {Path}", installerPath);
                                ScheduleInstallerViaTask(installerPath);
                                // Ждём пока планировщик запустит установщик.
                                // Задержка прервётся сама когда инсталлятор остановит службу (stoppingToken).
                                await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
                            }
                            else
                            {
                                _logger.LogError("Инсталлятор не найден: '{Path}'", installerPath);
                            }
                            continue;
                        }

                        // Проверяем флаг легального закрытия (администратор закрыл через пароль)
                        if (File.Exists(LegalCloseFlagPath))
                        {
                            _logger.LogInformation("Флаг легального закрытия обнаружен — ожидание {Minutes} минут", LegalCloseDelay.TotalMinutes);
                            File.Delete(LegalCloseFlagPath);
                            await Task.Delay(LegalCloseDelay, stoppingToken);
                            continue;
                        }

                        // Соблюдаем cooldown чтобы не циклиться если BibClient падает при старте
                        var sinceLastRestart = DateTime.UtcNow - lastRestartTime;
                        if (sinceLastRestart < RestartCooldown)
                        {
                            await Task.Delay(RestartCooldown - sinceLastRestart, stoppingToken);
                        }

                        _logger.LogWarning("BibClient не запущен — перезапуск...");
                        lastRestartTime = DateTime.UtcNow;
                        StartBibClient();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Нормальная остановка службы
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка в цикле мониторинга");
                }
            }

            _logger.LogInformation("BibClientService остановлен");
        }

        // Регистрирует задание в Windows Task Scheduler для запуска инсталлятора
        // примерно через 2 минуты. Задание выполняется как SYSTEM вне Job Object сервиса,
        // поэтому не гибнет когда инсталлятор останавливает службу через "sc stop".
        private void ScheduleInstallerViaTask(string installerPath)
        {
            try
            {
                // XML-формат гарантирует независимость от системной локали
                var triggerAt = DateTime.Now.AddMinutes(2);
                var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers>
    <TimeTrigger>
      <StartBoundary>{triggerAt:yyyy-MM-ddTHH:mm:ss}</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
    <DeleteExpiredTaskAfter>PT1H</DeleteExpiredTaskAfter>
  </Settings>
  <Actions>
    <Exec>
      <Command>""{installerPath}""</Command>
      <Arguments>/VERYSILENT /NORESTART /COMPONENTS=client</Arguments>
    </Exec>
  </Actions>
</Task>";
                var xmlFile = Path.Combine(Path.GetTempPath(), "bib_client_update_task.xml");
                File.WriteAllText(xmlFile, xml, Encoding.Unicode);

                using var p = Process.Start(new ProcessStartInfo("schtasks.exe",
                    $"/create /tn \"BibClientUpdate\" /xml \"{xmlFile}\" /f")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                p?.WaitForExit(5000);
                try { File.Delete(xmlFile); } catch { }

                _logger.LogInformation("Инсталлятор запланирован через Task Scheduler на {At}", triggerAt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка регистрации задания установщика в Task Scheduler");
            }
        }

        // Распаковывает zip-архив обновления в каталог приложения.
        // Пропускает settings.json и заблокированные exe сервиса/стражника.
        private Task ApplyFolderUpdateAsync(string zipPath, CancellationToken ct)
            => Task.Run(() => ApplyFolderUpdateSync(zipPath), ct);

        private void ApplyFolderUpdateSync(string zipPath)
        {
            if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            {
                _logger.LogError("Zip-архив обновления не найден: {Path}", zipPath);
                return;
            }

            var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var tempDir = Path.Combine(Path.GetTempPath(), "BibClientUpdate_" + Guid.NewGuid().ToString("N")[..8]);

            // Файлы настроек — не перезаписываем при обновлении
            var skipFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "settings.json" };
            // Файлы сервиса заблокированы пока служба работает — пропускаем с предупреждением
            var serviceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "BibClientService.exe", "BibClientGuardian.exe" };

            try
            {
                _logger.LogInformation("Распаковка обновления: {Zip} → {Temp}", zipPath, tempDir);
                ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);

                int copied = 0, skipped = 0;
                foreach (var file in Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(tempDir, file);
                    var fileName = Path.GetFileName(file);

                    if (skipFiles.Contains(fileName)) { skipped++; continue; }

                    var destPath = Path.Combine(appDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    try
                    {
                        File.Copy(file, destPath, overwrite: true);
                        copied++;
                    }
                    catch (Exception ex)
                    {
                        if (serviceFiles.Contains(fileName))
                            _logger.LogWarning("Пропускаем заблокированный файл сервиса {File}: {Msg}", fileName, ex.Message);
                        else
                            _logger.LogWarning("Не удалось скопировать {File}: {Msg}", relative, ex.Message);
                        skipped++;
                    }
                }
                _logger.LogInformation("✅ Папочное обновление применено: скопировано {Copied}, пропущено {Skipped}", copied, skipped);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка применения папочного обновления");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                try { File.Delete(zipPath); } catch { }
            }
        }

        private bool IsBibClientRunning()
        {
            try
            {
                var processes = Process.GetProcessesByName("BibClient");
                foreach (var p in processes)
                {
                    try
                    {
                        // Сервис работает как SYSTEM — имеет доступ к MainModule всех процессов
                        var path = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) &&
                            path.Equals(ClientExePath, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Ошибка поиска процесса BibClient: {Message}", ex.Message);
            }

            return false;
        }

        private void StartBibClient()
        {
            try
            {
                // Сервис работает как SYSTEM в Session 0.
                // Чтобы показать UI пользователю нужен CreateProcessAsUser с токеном активного сеанса.
                var sessionId = GetActiveUserSessionId();
                if (sessionId < 0)
                {
                    _logger.LogWarning("Активный пользовательский сеанс не найден — BibClient будет запущен при входе пользователя");
                    return;
                }

                bool launched = LaunchProcessInSession(ClientExePath, sessionId);
                if (launched)
                    _logger.LogInformation("BibClient успешно запущен в сеансе {SessionId}", sessionId);
                else
                    _logger.LogError("Не удалось запустить BibClient в сеансе {SessionId} (Win32Error={Error})",
                        sessionId, System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка запуска BibClient");
            }
        }

        // Получаем ID активного интерактивного сеанса пользователя
        private static int GetActiveUserSessionId()
        {
            try
            {
                // WTSGetActiveConsoleSessionId возвращает ID сеанса на физической консоли
                uint sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
                return sessionId == 0xFFFFFFFF ? -1 : (int)sessionId;
            }
            catch
            {
                return -1;
            }
        }

        // Запускаем процесс в пользовательском сеансе из контекста SYSTEM
        private static bool LaunchProcessInSession(string exePath, int sessionId)
        {
            IntPtr userToken = IntPtr.Zero;
            IntPtr elevatedToken = IntPtr.Zero;
            IntPtr duplicatedToken = IntPtr.Zero;
            IntPtr envBlock = IntPtr.Zero;

            try
            {
                // Получаем токен пользователя из сеанса (фильтрованный — без полных прав admin)
                if (!NativeMethods.WTSQueryUserToken((uint)sessionId, out userToken))
                    return false;

                // BibClient.exe требует requireAdministrator. CreateProcessAsUser с фильтрованным
                // токеном вернёт ERROR_ELEVATION_REQUIRED (740). Получаем linked elevated токен.
                IntPtr linkedToken = IntPtr.Zero;
                if (NativeMethods.GetTokenInformation(userToken, NativeMethods.TokenLinkedToken,
                    ref linkedToken, IntPtr.Size, out _) && linkedToken != IntPtr.Zero)
                {
                    elevatedToken = linkedToken; // полный admin-токен той же сессии
                }

                IntPtr tokenForProcess = elevatedToken != IntPtr.Zero ? elevatedToken : userToken;

                // Дублируем токен для CreateProcessAsUser
                var sa = new NativeMethods.SECURITY_ATTRIBUTES();
                sa.nLength = System.Runtime.InteropServices.Marshal.SizeOf(sa);
                if (!NativeMethods.DuplicateTokenEx(tokenForProcess, NativeMethods.TOKEN_ALL_ACCESS, ref sa,
                    NativeMethods.SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    NativeMethods.TOKEN_TYPE.TokenPrimary, out duplicatedToken))
                    return false;

                // Создаём пользовательское окружение (USERPROFILE, TEMP, APPDATA и т.д.)
                // Без этого процесс получает системное окружение SYSTEM, что ломает WPF
                NativeMethods.CreateEnvironmentBlock(out envBlock, duplicatedToken, false);

                var si = new NativeMethods.STARTUPINFO();
                si.cb = System.Runtime.InteropServices.Marshal.SizeOf(si);
                si.lpDesktop = "winsta0\\default"; // Интерактивный рабочий стол

                var workDir = Path.GetDirectoryName(exePath) ?? "";

                bool result = NativeMethods.CreateProcessAsUser(
                    duplicatedToken,
                    exePath,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                    envBlock,
                    workDir,
                    ref si,
                    out var pi
                );

                if (result)
                {
                    NativeMethods.CloseHandle(pi.hProcess);
                    NativeMethods.CloseHandle(pi.hThread);
                }

                return result;
            }
            finally
            {
                if (envBlock != IntPtr.Zero) NativeMethods.DestroyEnvironmentBlock(envBlock);
                if (duplicatedToken != IntPtr.Zero) NativeMethods.CloseHandle(duplicatedToken);
                if (elevatedToken != IntPtr.Zero) NativeMethods.CloseHandle(elevatedToken);
                if (userToken != IntPtr.Zero) NativeMethods.CloseHandle(userToken);
            }
        }
    }
}
