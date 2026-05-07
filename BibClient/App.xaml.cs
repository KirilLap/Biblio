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

            // Проверка на запуск в режиме Guardian
            if (e.Args.Length > 0 && e.Args[0] == "--guardian")
            {
                Logger.Info("🛡️ Запуск в режиме Guardian");
                Watchdog.RunGuardian();
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

            // Путь к файлу настроек
            string settingsPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "settings.json");

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
                if (SettingsManager.Current.AutoStartWithUser)
                {
                    Watchdog.RegisterAutostart();
                }
                
                // Запускаем Guardian если включена защита от закрытия (по умолчанию true)
                if (SettingsManager.Current.PreventClose)
                {
                    Watchdog.StartGuardian(true);
                }

                var mainWindow = new MainWindow();
                MainWindow = mainWindow;
                
                // Показываем окно
                mainWindow.Show();
                mainWindow.Activate();
                
                // Вызываем блокировку через Dispatcher
                System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    new Action(() => mainWindow.Lock()), 
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }
    }
}