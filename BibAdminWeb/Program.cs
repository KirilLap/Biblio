using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BibAdminWeb
{
    class Program
    {
        [STAThread]
        static void Main()
        {
            using var mutex = new Mutex(true, "BibAdminWeb_SingleInstance", out bool isNew);
            if (!isNew)
            {
                MessageBox.Show("BibAdmin Web уже запущен.", "BibAdmin Web",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var port = GlobalSettings.Load().ServerPort;

            var server = new ServerHost();
            try
            {
                server.StartAsync(port).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось запустить сервер на порту {port}:\n{ex.Message}",
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            AdminHub.LoadRegistry();
            AdminHub.LoadActiveSessions();
            AdminHub.LoadDeletedPcs();
            FinanceStore.LoadHistory();
            ServiceTransaction.LoadHistory();
            ReaderDebtStore.Load();

            Logger.Info($"🌐 BibAdmin Web запущен: http://localhost:{port}");

            // Проверяем обновления через 5 секунд после старта
            _ = Task.Delay(5000).ContinueWith(_ => UpdateChecker.CheckAsync());

            // После обновления браузер уже открыт и сам перезагружается — не открываем новое окно
            var restartFlag = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update_restart.flag");
            if (File.Exists(restartFlag))
            {
                File.Delete(restartFlag);
                Logger.Info("🔄 Перезапуск после обновления — браузер открывать не нужно");
            }
            else
            {
                OpenBrowser(port);
            }

            using var tray = new TrayIcon(port, () => Application.Exit());
            Application.Run();

            server.StopAsync().GetAwaiter().GetResult();
        }

        private static void OpenBrowser(int port)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = $"http://localhost:{port}",
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
