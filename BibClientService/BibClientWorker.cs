using System.Diagnostics;

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

        // Флаг обновления — BibClient записывает сюда путь к скачанному инсталлятору.
        // Служба запускает инсталлятор от SYSTEM, обходя UAC и AppLocker пользователя.
        private static readonly string UpdateFlagPath = Path.Combine(
            AppContext.BaseDirectory,
            "BibClientUpdate.flag"
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
                        // Проверяем флаг обновления ПЕРВЫМ — приоритет над ожиданием легального закрытия.
                        // BibClient записывает путь к инсталлятору и создаёт оба флага перед выходом.
                        if (File.Exists(UpdateFlagPath))
                        {
                            string installerPath = "";
                            try { installerPath = (await File.ReadAllTextAsync(UpdateFlagPath, stoppingToken)).Trim(); }
                            catch { }
                            File.Delete(UpdateFlagPath);
                            // Убираем флаг легального закрытия — после установки сразу перезапускаем
                            try { File.Delete(LegalCloseFlagPath); } catch { }

                            _logger.LogInformation("Флаг обновления обнаружен — запуск инсталлятора от SYSTEM: {Path}", installerPath);
                            await RunInstallerAsSystemAsync(installerPath, stoppingToken);
                            // Даём инсталлятору время завершить финальные шаги перед следующей итерацией
                            await Task.Delay(3000, stoppingToken);
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

        private async Task RunInstallerAsSystemAsync(string installerPath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            {
                _logger.LogError("Инсталлятор не найден: {Path}", installerPath);
                return;
            }

            try
            {
                // Служба работает как SYSTEM — запуск напрямую без UAC и без ограничений AppLocker
                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/VERYSILENT /NORESTART /COMPONENTS=client",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null)
                {
                    _logger.LogError("Не удалось запустить инсталлятор");
                    return;
                }

                // Таймаут НЕ привязан к ct: инсталлятор запустит "sc stop BibClientWatchdog",
                // что отменит ct. Если привязать — мы убьём инсталлятор раньше времени.
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                try
                {
                    await p.WaitForExitAsync(timeoutCts.Token);
                    _logger.LogInformation("Инсталлятор завершён с кодом {ExitCode}", p.ExitCode);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    // Убиваем только по таймауту, не при остановке службы
                    _logger.LogError("Таймаут ожидания инсталлятора — принудительное завершение");
                    try { p.Kill(); } catch { }
                }
                // Если ct отменён (служба останавливается из-за sc stop) — не трогаем инсталлятор,
                // он продолжает работать как осиротевший процесс и завершит установку самостоятельно
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка запуска инсталлятора от SYSTEM");
            }
            finally
            {
                // Удаляем файл инсталлятора после установки
                try { File.Delete(installerPath); } catch { }
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
