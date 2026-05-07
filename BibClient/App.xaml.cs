using System;
using System.IO;
using System.Windows;

namespace BibClient
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 🔹 КРИТИЧЕСКИ ВАЖНО: Сначала проверяем и копируем Guardian.exe если нужно
            // Это делается ДО всех остальных проверок чтобы Guardian всегда был актуален
            EnsureGuardianInstalled();

            // Проверка на запуск в режиме Guardian
            if (e.Args.Length > 0 && e.Args[0] == "--guardian")
            {
                Logger.Info("🛡️ Запуск в режиме Guardian (устаревший режим - теперь используется отдельный exe)");
                // В новой версии Guardian работает как отдельный exe файл
                // Этот код оставлен для обратной совместимости
                Shutdown();
                return;
            }

            // Проверяем что это единственный экземпляр основного приложения (не guardian)
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

            // 🔹 Метод проверки и копирования BibClientGuardian.exe
            void EnsureGuardianInstalled()
            {
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string guardianSourcePath = Path.Combine(baseDir, "BibClientGuardian.exe");
                    
                    // Проверяем существует ли файл Guardian
                    if (!File.Exists(guardianSourcePath))
                    {
                        Logger.Warn($"⚠️ BibClientGuardian.exe не найден в папке: {guardianSourcePath}");
                        Logger.Warn("⚠️ Функция защиты от закрытия будет недоступна!");
                        return;
                    }

                    Logger.Info($"✅ BibClientGuardian.exe найден: {guardianSourcePath}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"❌ Ошибка проверки Guardian: {ex.Message}");
                }
            }

            // Путь к файлу настроек - используем абсолютный путь относительно exe файла
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string settingsPath = Path.Combine(baseDir, "settings.json");

            // 🔹 Если настроек нет — показываем окно первоначальной настройки
            if (!File.Exists(settingsPath))
            {
                Logger.Info("⚙️ Настроек нет, запускаем окно настройки...");
                var setup = new SetupWindow();

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
                
                var mainWindow = new MainWindow();
                MainWindow = mainWindow;
                
                // Показываем окно
                mainWindow.Show();
                mainWindow.Activate();
                
                // Запускаем Guardian ПОСЛЕ создания главного окна
                // Это критично для восстановления после закрытия через диспетчер задач
                if (SettingsManager.Current.PreventClose)
                {
                    Watchdog.StartGuardian(true);
                }
                
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