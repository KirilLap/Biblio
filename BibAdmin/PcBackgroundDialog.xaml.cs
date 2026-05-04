using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.AspNetCore.SignalR.Client;

namespace BibAdmin
{
    public partial class PcBackgroundDialog : Window
    {
        private string _pcNumber;
        private HubConnection? _hub;
        private string _filePath = "";

        public PcBackgroundDialog(string pcNumber)
        {
            InitializeComponent();
            _pcNumber = pcNumber;
            TxtPcName.Text = pcNumber;
            ConnectHub();
        }

        private async void ConnectHub()
        {
            try
            {
                _hub = new HubConnectionBuilder()
                    .WithUrl("http://localhost:8080/hub")
                    .Build();
                await _hub.StartAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"Hub error: {ex.Message}");
            }
        }

        private void BrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp|Все файлы|*.*",
                Title = "Выберите фоновое изображение"
            };

            if (dialog.ShowDialog() == true)
            {
                _filePath = dialog.FileName;
                TxtPath.Text = _filePath;
                BtnSend.IsEnabled = true;

                try
                {
                    ImgPreview.Source = new BitmapImage(
                        new Uri(_filePath));
                }
                catch { }
            }
        }

        private async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_filePath)) return;

            try
            {
                BtnSend.IsEnabled = false;
                BtnSend.Content = "Отправка...";

                var bytes = await File.ReadAllBytesAsync(_filePath);
                var fileName = Path.GetFileName(_filePath);

                if (_hub?.State == HubConnectionState.Connected)
                {
                    // Вызываем метод на сервере, который сохранит файл и отправит команду клиенту
                    await _hub.InvokeAsync("UploadFile",
                        fileName, bytes, _pcNumber);
                        
                    MessageBox.Show(
                        $"Фон успешно отправлен на {_pcNumber}",
                        "Готово", MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show("Нет соединения с сервером",
                        "Ошибка", MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}",
                    "Ошибка", MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Logger.Error($"Ошибка отправки фона: {ex.Message}");
            }
            finally
            {
                BtnSend.IsEnabled = true;
                BtnSend.Content = "Отправить на ПК";
            }
        }
    }
}