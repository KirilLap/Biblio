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

            // Путь к файлу настроек
            string settingsPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "settings.json");

            // 🔹 Если настроек нет — показываем окно первоначальной настройки
            if (!File.Exists(settingsPath))
            {
                var setup = new SetupWindow();

                // Показываем модально (главное окно не откроется, пока не завершим настройку)
                if (setup.ShowDialog() == true && setup.Result != null)
                {
                    // Сохраняем настройки
                    SettingsManager.Current = setup.Result;
                    SettingsManager.Save();

                    Logger.Info("✅ Настройки сохранены, запуск основного окна");

                    // Запускаем главное окно
                    var mainWindow = new MainWindow();
                    MainWindow = mainWindow;
                    mainWindow.Show();
                    
                    // Вызываем блокировку с задержкой, чтобы окно успело полностью инициализироваться
                    System.Windows.Threading.DispatcherTimer timer = new();
                    timer.Interval = TimeSpan.FromSeconds(2); // Увеличенная задержка для надёжности
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        // Дополнительная проверка: убеждаемся, что окно готово
                        if (mainWindow.GetType().GetField("_isReady", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(mainWindow) is bool isReady && isReady)
                        {
                            mainWindow.Lock();
                        }
                        else
                        {
                            // Если ещё не готово, пробуем ещё раз через 500 мс
                            System.Windows.Threading.DispatcherTimer retryTimer = new();
                            retryTimer.Interval = TimeSpan.FromMilliseconds(500);
                            retryTimer.Tick += (retryS, retryE) =>
                            {
                                retryTimer.Stop();
                                mainWindow.Lock();
                            };
                            retryTimer.Start();
                        }
                    };
                    timer.Start();
                }
                else
                {
                    // Пользователь отменил настройку — выходим
                    Logger.Warn("⚠️ Настройка отменена, выход");
                    Shutdown();
                }
            }
            else
            {
                // 🔹 Настройки есть — загружаем и запускаем главное окно
                Logger.Info("📁 Настройки найдены, загрузка");
                SettingsManager.Load();

                var mainWindow = new MainWindow();
                MainWindow = mainWindow;
                mainWindow.Show();
            }
        }
    }
}