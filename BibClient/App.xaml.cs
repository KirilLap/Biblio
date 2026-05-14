using System;
using System.IO;
using System.Windows;

namespace BibClient
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Аварийный лог до инициализации Logger — пишет в %TEMP%\BibClient_startup.txt.
            // Позволяет поймать крашт до того как Logger создаст папку logs\.
            string emergencyCrashLog = Path.Combine(Path.GetTempPath(), "BibClient_startup.txt");
            void EmergencyLog(string msg) { try { File.AppendAllText(emergencyCrashLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\r\n"); } catch { } }
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                EmergencyLog($"UNHANDLED: {args.ExceptionObject}");
            DispatcherUnhandledException += (_, args) =>
                EmergencyLog($"WPF DISPATCHER: {args.Exception}");
            EmergencyLog("OnStartup начало");

            base.OnStartup(e);

            // Проверяем что это единственный экземпляр основного приложения
            if (!Watchdog.EnsureSingleInstance())
            {
                Logger.Warn("⚠️ Приложение уже запущено, завершаем дубликат");
                Shutdown();
                return;
            }

            // Подписываемся на событие закрытия приложения для освобождения мьютекса
            Exit += (s, args) =>
            {
                Watchdog.ReleaseSingleInstance();
            };

            // Снимаем блокировку SmartScreen/Zone.Identifier со всех exe в папке.
            // Файлы, скопированные с другой машины или скачанные, помечаются меткой MOTW
            // (Mark of the Web, ADS :Zone.Identifier=3). SmartScreen блокирует их запуск.
            UnblockDirectory(AppDomain.CurrentDomain.BaseDirectory);

            void UnblockDirectory(string dir)
            {
                try
                {
                    int unblocked = 0;
                    foreach (var file in Directory.GetFiles(dir, "*.exe"))
                    {
                        try { File.Delete(file + ":Zone.Identifier"); unblocked++; }
                        catch { }
                    }
                    if (unblocked > 0)
                        Logger.Info($"🔓 Снята блокировка SmartScreen с {unblocked} файлов");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"⚠️ Ошибка снятия блокировки: {ex.Message}");
                }
            }

            // Путь к файлу настроек - используем абсолютный путь относительно exe файла
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string settingsPath = Path.Combine(baseDir, "settings.json");

            // 🔹 Если настроек нет ИЛИ ServerIp не задан — показываем окно первоначальной настройки
            bool settingsExist = File.Exists(settingsPath);
            if (settingsExist)
            {
                SettingsManager.Load();
                settingsExist = !string.IsNullOrWhiteSpace(SettingsManager.Current.ServerIp);
            }
            if (!settingsExist)
            {
                Logger.Info("⚙️ Настроек нет, запускаем окно настройки...");
                var setup = new SetupWindow();
                setup.Loaded += (_, _) => { setup.Activate(); setup.Focus(); };

                // Показываем модально (главное окно не откроется, пока не завершим настройку)
                if (setup.ShowDialog() == true && setup.Result != null)
                {
                    // Сохраняем настройки
                    SettingsManager.Current = setup.Result;
                    SettingsManager.Save();

                    Logger.Info("✅ Настройки сохранены в файл.");

                    // 🔥 ВАЖНО: Проверяем, что файл реально создан, и запускаем главное окно вручную
                    // Это эмулирует поведение "второго запуска", но внутри текущего процесса
                    if (File.Exists(settingsPath))
                    {
                        Logger.Info("📁 Файл настроек найден, немедленный запуск основного окна...");
                        
                        // Перезагружаем настройки из файла, чтобы быть уверенными в актуальности
                        SettingsManager.Load(); 

                        var mainWindow = new MainWindow();
                        MainWindow = mainWindow;

                        // Показываем окно
                        mainWindow.Show();
                        mainWindow.Activate();

                        // Вызываем блокировку через Dispatcher, чтобы гарантировать полную отрисовку окна
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(
                            new Action(() =>
                            {
                                Logger.Info("🔒 Вызов Lock() после полной загрузки окна...");
                                mainWindow.Lock();
                            }),
                            System.Windows.Threading.DispatcherPriority.Background);

                        // Проверяем обновления через 5 секунд после старта
                        mainWindow.Dispatcher.BeginInvoke(async () =>
                        {
                            await Task.Delay(5000);
                            var url = $"http://{SettingsManager.Current.ServerIp}:{SettingsManager.Current.ServerPort}";
                            await UpdateChecker.CheckAsync(url);
                        });
                    }
                    else
                    {
                        Logger.Error("❌ Ошибка: файл настроек не создан после сохранения!");
                        Shutdown();
                    }
                }
                else
                {
                    // Пользователь отменил настройку — выходим
                    Logger.Warn("⚠️ Настройка отменена пользователем, выход");
                    Shutdown();
                }
            }
            else
            {
                // 🔹 Настройки есть — загружаем и запускаем главное окно
                Logger.Info("📁 Настройки найдены, загрузка");
                SettingsManager.Load();

                // Применяем политики ограничений из настроек (пишем в реестр HKCU напрямую)
                RegistryPolicyEngine.ApplyAll();

                // Регистрируем автозапуск если включено (по умолчанию true)
                // Делаем это в первую очередь, чтобы при следующем старте из автозапуска всё работало
                if (SettingsManager.Current.AutoStartWithUser)
                {
                    Watchdog.RegisterAutostart();
                    Logger.Info($"✅ Автозапуск проверен/обновлен: {Watchdog.GetExePath()}");
                }
                else
                {
                    // Если автозапуск выключен - убираем из реестра
                    Watchdog.UnregisterAutostart();
                    Logger.Info("ℹ️ Автозапуск отключен, запись удалена из реестра");
                }
                
                // Убеждаемся что служба защиты BibClientWatchdog установлена и запущена
                if (SettingsManager.Current.PreventClose)
                {
                    ServiceManager.EnsureServiceRunning();
                }

                var mainWindow = new MainWindow();
                MainWindow = mainWindow;

                // Показываем окно
                mainWindow.Show();
                mainWindow.Activate();

                // Проверяем обновления через 5 секунд после старта
                mainWindow.Dispatcher.BeginInvoke(async () =>
                {
                    await Task.Delay(5000);
                    var url = $"http://{SettingsManager.Current.ServerIp}:{SettingsManager.Current.ServerPort}";
                    await UpdateChecker.CheckAsync(url);
                });

                // Вызываем блокировку через Dispatcher с повторной попыткой если _isReady ещё false
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => 
                    {
                        // Ждём пока окно будет готово (максимум 5 секунд)
                        int attempts = 0;
                        while (!mainWindow.IsReady && attempts < 50)
                        {
                            System.Threading.Thread.Sleep(100);
                            attempts++;
                        }
                        
                        if (mainWindow.IsReady)
                        {
                            mainWindow.Lock();
                        }
                        else
                        {
                            Logger.Warn("⚠️ Окно не готово к блокировке после 5 секунд, повторяем попытку...");
                            // Повторная попытка через 1 секунду
                            System.Windows.Application.Current.Dispatcher.BeginInvoke(
                                new Action(() => mainWindow.Lock()), 
                                System.Windows.Threading.DispatcherPriority.Background);
                        }
                    }), 
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }
}