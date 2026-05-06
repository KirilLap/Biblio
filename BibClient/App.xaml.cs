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
                    
                    // Вызываем блокировку с небольшой задержкой, чтобы окно успело инициализироваться
                    System.Windows.Threading.DispatcherTimer timer = new();
                    timer.Interval = TimeSpan.FromMilliseconds(500);
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        mainWindow.Lock();
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