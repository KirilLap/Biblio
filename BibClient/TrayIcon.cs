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
        private ToolStripMenuItem? _lockMenuItem;
        private ToolStripMenuItem? _exitMenuItem;
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
            
            _lockMenuItem = new ToolStripMenuItem("Заблокировать", null, (s, e) => PolicyEngine.RemoteLockRequested?.Invoke());
            _exitMenuItem = new ToolStripMenuItem("Выход", null, (s, e) => Application.Current.Shutdown());
            
            contextMenu.Items.Add(_lockMenuItem);
            contextMenu.Items.Add(_exitMenuItem);
            _notifyIcon.ContextMenuStrip = contextMenu;
            
            // Подписываемся на изменения сессии для обновления видимости кнопок
            PolicyEngine.StartSessionRequested += OnSessionStarted;
            PolicyEngine.EndSessionRequested += OnSessionEnded;
            
            UpdateMenuVisibility();
        }

        private void OnSessionStarted(string sessionType, int limitSeconds, int paidAmount, int initialElapsedSeconds)
        {
            UpdateMenuVisibility();
        }

        private void OnSessionEnded()
        {
            UpdateMenuVisibility();
        }

        /// <summary>
        /// Скрывает кнопки "Заблокировать" и "Выход" во время платных сессий (VIP и Лимит)
        /// </summary>
        private void UpdateMenuVisibility()
        {
            if (_lockMenuItem == null || _exitMenuItem == null) return;

            // Кнопки скрыты если активна сессия VIP или Лимит
            bool isPaidSession = PolicyEngine.ActiveSessionType == "VIP" || PolicyEngine.ActiveSessionType == "Лимит";
            
            _lockMenuItem.Visible = !isPaidSession;
            _exitMenuItem.Visible = !isPaidSession;
        }

        public void UpdateTooltip(string text) => _notifyIcon.Text = text;

        public void ShowNotification(string title, string message, int timeoutMs = 3000)
        {
            _notifyIcon.ShowBalloonTip(timeoutMs, title, message, ToolTipIcon.Info);
        }

        public void Dispose()
        {
            // Отписываемся от событий PolicyEngine
            PolicyEngine.StartSessionRequested -= OnSessionStarted;
            PolicyEngine.EndSessionRequested -= OnSessionEnded;
            
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}