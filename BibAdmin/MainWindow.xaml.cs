using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BibAdmin
{
    public partial class MainWindow : Window
    {
        private ServerHost? _server;
        private TrayIconHelper _tray = null!;
        private bool _realExit = false;

        public static MainWindow? Instance { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;

            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            Width = screenWidth * 0.80;
            Height = screenHeight * 0.75;

            InitTrayIcon();
            StartServer();
        }

        // =====================
        // Трей
        // =====================

        private void InitTrayIcon()
        {
            _tray = new TrayIconHelper();
            _tray.ShowRequested += ShowMainWindow;
            _tray.ExitRequested += DoExit;
        }

        private void ShowMainWindow()
        {
            Dispatcher.Invoke(() =>
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            });
        }

        private void DoExit()
        {
            _realExit = true;
            Dispatcher.Invoke(Close);
        }

        // =====================
        // Кнопки меню
        // =====================

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show(
                "Завершить работу BibAdmin?\n\nСервер будет остановлен, клиентские ПК потеряют связь.",
                "Выход из программы",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (r == MessageBoxResult.Yes)
                DoExit();
        }

        // =====================
        // Сервер
        // =====================

        private async void StartServer()
        {
            _server = new ServerHost();
            try
            {
                await _server.StartAsync(8080);

                AdminHub.LoadRegistry();
                Logger.Info("✅ Реестр клиентов загружен");

                AdminHub.LoadActiveSessions();
                Logger.Info("✅ Активные сессии восстановлены");

                AdminHub.LoadDeletedPcs();
                Logger.Info("✅ Лог удалённых ПК загружен");

                FinancePage.LoadHistory();
                Logger.Info("✅ История финансов загружена");

                ServiceTransaction.LoadHistory();
                Logger.Info("✅ История услуг загружена");

                if (MainFrame.Content is FinancePage financePage)
                    financePage.RefreshUI();

                Dispatcher.Invoke(() =>
                {
                    TxtServerStatus.Text = "Сервер запущен :8080";
                    DotServer.Fill = new SolidColorBrush(Color.FromRgb(29, 158, 117));
                });

                Logger.Info("Сервер запущен успешно");

                // Проверяем обновления через 5 секунд после старта
                _ = Task.Delay(5000).ContinueWith(_ =>
                    Dispatcher.InvokeAsync(() => UpdateChecker.CheckAsync()));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    TxtServerStatus.Text = "Ошибка сервера";
                    DotServer.Fill = new SolidColorBrush(Color.FromRgb(226, 75, 74));
                });
                Logger.Error($"Ошибка запуска сервера: {ex.Message}");
            }

            MainFrame.Navigate(new ComputersPage());
        }

        // =====================
        // Навигация
        // =====================

        private void BtnComputers_Click(object sender, RoutedEventArgs e)
        {
            SetActive(BtnComputers);
            if (MainFrame.Content is not ComputersPage)
                MainFrame.Navigate(new ComputersPage());
        }

        private void BtnFinance_Click(object sender, RoutedEventArgs e)
        {
            SetActive(BtnFinance);
            if (MainFrame.Content is not FinancePage)
                MainFrame.Navigate(new FinancePage());
            else
                ((FinancePage)MainFrame.Content).RefreshUI();
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            SetActive(BtnSettings);
            MainFrame.Navigate(new SettingsPage());
        }

        private void BtnOperators_Click(object sender, RoutedEventArgs e)
        {
            SetActive(BtnOperators);
            MainFrame.Navigate(new OperatorsPage());
        }

        private void BtnNewService_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ServiceOrderDialog();
            if (dialog.ShowDialog() == true && MainFrame.Content is FinancePage fp)
                fp.RefreshUI();
        }

        private void SetActive(Button btn)
        {
            BtnComputers.Tag = null;
            BtnFinance.Tag = null;
            BtnSettings.Tag = null;
            BtnOperators.Tag = null;
            btn.Tag = "active";
        }

        // =====================
        // Закрытие окна
        // =====================

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_realExit)
            {
                // Крестик → скрыть в трей
                e.Cancel = true;
                Hide();
                _tray.ShowBalloon("BibAdmin",
                    "Приложение продолжает работать в фоне. Дважды кликните по иконке в трее для открытия.");
                return;
            }

            // Реальный выход — убираем иконку из трея и останавливаем сервер
            _tray.Dispose();

            _ = StopServerAsync();
            base.OnClosing(e);
        }

        private async Task StopServerAsync()
        {
            try
            {
                if (_server != null) await _server.StopAsync();
                Logger.Info("Сервер остановлен");
            }
            catch { }
        }
    }
}
