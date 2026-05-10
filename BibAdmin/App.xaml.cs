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
            if (settings.IsFirstRun)
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