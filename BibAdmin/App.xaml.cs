using System.Configuration;
using System.Data;
using System.Windows;

namespace BibAdmin
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var settings = GlobalSettings.Load();
            // Показываем окно первого запуска только если файл настроек НЕ существует
            // Установщик теперь создаёт файл с IsFirstRun=false, поэтому окно не появится
            if (!System.IO.File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "global_settings.json")))
            {
                var setup = new FirstRunWindow();
                if (setup.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }
            }

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
    }
}