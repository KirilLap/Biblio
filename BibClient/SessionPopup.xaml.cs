using System;
using System.Windows;

namespace BibClient
{
    public partial class SessionPopup : Window
    {
        public SessionPopup()
        {
            InitializeComponent();
            // Размещаем в правом верхнем углу
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
            else TxtRemaining.Visibility = Visibility.Collapsed;

            TxtPaused.Visibility = isPaused ? Visibility.Visible : Visibility.Collapsed;
        }

        private string FormatTime(int secs) => $"{secs / 3600:D2}:{(secs % 3600) / 60:D2}:{secs % 60:D2}";
    }
}