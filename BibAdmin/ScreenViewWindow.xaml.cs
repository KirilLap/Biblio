using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.AspNetCore.SignalR.Client;

namespace BibAdmin
{
    public partial class ScreenViewWindow : Window
    {
        private readonly string _pcNumber;
        private readonly HubConnection _hub;
        private readonly DispatcherTimer _timer;

        public ScreenViewWindow(string pcNumber, HubConnection hub)
        {
            InitializeComponent();
            _pcNumber = pcNumber;
            _hub = hub;
            TitleLabel.Text = $"Экран: {pcNumber}";

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += Timer_Tick;
        }

        protected override async void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            var count = ScreenshotStore.AddViewer(_pcNumber);
            if (count == 1)
                await SendCommand("START_SCREENSHOT_STREAM");
            StatusLabel.Text = "Подключение...";
            _timer.Start();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            var (data, at) = ScreenshotStore.GetFrame(_pcNumber);
            if (data == null || data.Length == 0) return;
            try
            {
                using var ms = new MemoryStream(data);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                ScreenImage.Source = bmp;
                StatusLabel.Text = $"Обновлено: {at.ToLocalTime():HH:mm:ss}";
            }
            catch { /* corrupted frame, skip */ }
        }

        private async void Window_Closed(object? sender, EventArgs e)
        {
            _timer.Stop();
            var count = ScreenshotStore.RemoveViewer(_pcNumber);
            if (count == 0)
                await SendCommand("STOP_SCREENSHOT_STREAM");
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private async System.Threading.Tasks.Task SendCommand(string type)
        {
            try
            {
                if (_hub.State == HubConnectionState.Connected)
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(new { Type = type, Value = "" });
                    await _hub.InvokeAsync("SendCommand", _pcNumber, json);
                }
            }
            catch (Exception ex) { Logger.Warn($"SendCommand {type}: {ex.Message}"); }
        }
    }
}
