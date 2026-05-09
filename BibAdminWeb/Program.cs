using System;
using System.Threading;
using System.Windows.Forms;

namespace BibAdminWeb
{
    class Program
    {
        private const int Port = 8080;

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

            var server = new ServerHost();
            try
            {
                server.StartAsync(Port).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось запустить сервер на порту {Port}:\n{ex.Message}",
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            AdminHub.LoadRegistry();
            AdminHub.LoadActiveSessions();
            AdminHub.LoadDeletedPcs();
            FinanceStore.LoadHistory();
            ServiceTransaction.LoadHistory();

            Logger.Info($"🌐 BibAdmin Web запущен: http://localhost:{Port}");

            OpenBrowser(Port);

            using var tray = new TrayIcon(Port, () => Application.Exit());
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
