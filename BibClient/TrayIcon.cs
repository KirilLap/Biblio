using System;
using System.Windows.Forms;
using System.Drawing;      
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace BibClient
{
    public class TrayIcon : IDisposable
    {
        private NotifyIcon _notifyIcon;
        public event Action? ShowPopupRequested;

        public TrayIcon()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                    System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? ""),
                Visible = true,
                Text = "BibClient"
            };

            _notifyIcon.DoubleClick += (s, e) => ShowPopupRequested?.Invoke();

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("Показать", null, (s, e) => ShowPopupRequested?.Invoke());
            contextMenu.Items.Add("Заблокировать", null, (s, e) => PolicyEngine.RemoteLockRequested?.Invoke());
            contextMenu.Items.Add("Выход", null, (s, e) => Application.Current.Shutdown());
            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        public void UpdateTooltip(string text) => _notifyIcon.Text = text;

        public void ShowNotification(string title, string message, int timeoutMs = 3000)
        {
            _notifyIcon.ShowBalloonTip(timeoutMs, title, message, ToolTipIcon.Info);
        }

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}