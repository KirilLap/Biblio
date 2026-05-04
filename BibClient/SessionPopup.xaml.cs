using System;
using System.Windows;
using System.Windows.Input;

namespace BibClient
{
    public partial class SessionPopup : Window
    {
        public SessionPopup()
        {
            InitializeComponent();

            // Размещаем в правом верхнем углу экрана
            Loaded += (s, e) =>
            {
                Left = SystemParameters.PrimaryScreenWidth - Width - 30;
                Top = 30;
            };
        }

        public void UpdateSession(string sessionType, int elapsedSeconds, int limitSeconds, int tariff, bool isPaused)
        {
            TxtSessionType.Text = sessionType;
            TxtElapsed.Text = FormatTime(elapsedSeconds);

            if (limitSeconds > 0 && !isPaused)
            {
                int rem = Math.Max(0, limitSeconds - elapsedSeconds);
                TxtRemaining.Text = $"Осталось: {FormatTime(rem)}";
                TxtRemaining.Visibility = Visibility.Visible;
            }
            else
            {
                TxtRemaining.Visibility = Visibility.Collapsed;
            }

            TxtPaused.Visibility = isPaused ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Кнопка «скрыть в трей» ───────────────────────────────────────────
        private void BtnHide_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            // Подсказка пользователю: показать обратно можно через иконку в трее
            Logger.Info("💬 Попап скрыт в трей (двойной клик на иконке — вернуть)");
        }

        // ── Перетаскивание окна мышью ─────────────────────────────────────────
        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Перетаскивание работает только левой кнопкой
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private string FormatTime(int secs) =>
            $"{secs / 3600:D2}:{(secs % 3600) / 60:D2}:{secs % 60:D2}";
    }
}
