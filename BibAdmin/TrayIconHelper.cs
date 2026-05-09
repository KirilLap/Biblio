// Изолируем System.Windows.Forms здесь, чтобы избежать конфликтов имён с WPF
using System;
using WinForms = System.Windows.Forms;

namespace BibAdmin
{
    internal sealed class TrayIconHelper : IDisposable
    {
        private readonly WinForms.NotifyIcon _icon;

        public event Action? ShowRequested;
        public event Action? ExitRequested;

        public TrayIconHelper()
        {
            _icon = new WinForms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "BibAdmin — Панель управления",
                Visible = true
            };

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Показать", null, (s, e) => ShowRequested?.Invoke());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (s, e) => ExitRequested?.Invoke());
            _icon.ContextMenuStrip = menu;

            _icon.DoubleClick += (s, e) => ShowRequested?.Invoke();
        }

        public void ShowBalloon(string title, string text)
        {
            _icon.ShowBalloonTip(2500, title, text, WinForms.ToolTipIcon.Info);
        }

        public void Hide()
        {
            _icon.Visible = false;
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
