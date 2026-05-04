using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Windows;

namespace BibClient
{
    public partial class SetupWindow : Window
    {
        public ClientSettings? Result { get; private set; }

        public SetupWindow()
        {
            InitializeComponent();
            TxtLocalIp.Text = GetLocalIp();
        }

        private string GetLocalIp()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                var ip = host.AddressList
                    .FirstOrDefault(a =>
                        a.AddressFamily == AddressFamily.InterNetwork &&
                        !a.ToString().StartsWith("127."));
                return ip?.ToString() ?? "Не определён";
            }
            catch
            {
                return "Не определён";
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtServerIp.Text)) { ShowError("Введите IP адрес сервера"); return; }
            if (!int.TryParse(TxtServerPort.Text, out int port) || port < 1 || port > 65535) { ShowError("Введите корректный порт (1-65535)"); return; }
            if (string.IsNullOrWhiteSpace(TxtPcNumber.Text)) { ShowError("Введите номер компьютера"); return; }

            Result = new ClientSettings
            {
                ServerIp = TxtServerIp.Text.Trim(),
                ServerPort = port,
                PcNumber = TxtPcNumber.Text.Trim(),

                // ✅ Теперь это работает: пароль хешируется автоматически
                AdminPassword = "1234",

                ShowPcNumber = true
                // Остальные свойства берут значения по умолчанию
            };

            DialogResult = true;
            Close();
        }

        private void ShowError(string msg)
        {
            TxtError.Text = msg;
            TxtError.Visibility = Visibility.Visible;
        }
    }
}