using System;
using System.Drawing;
using System.Windows.Forms;

namespace BibAdminWeb
{
    public class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly int _port;

        public TrayIcon(int port, Action onExit)
        {
            _port = port;
            _icon = new NotifyIcon
            {
                Text = "BibAdmin Web",
                Icon = SystemIcons.Application,
                Visible = true
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть панель управления", null, (_, _) => OpenBrowser());
            menu.Items.Add("-");
            menu.Items.Add("Выход", null, (_, _) => onExit());

            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += (_, _) => OpenBrowser();
        }

        private void OpenBrowser()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = $"http://localhost:{_port}",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
