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
        public static MainWindow? Instance { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;
            
            // Устанавливаем размер окна в процентах от размера экрана (80% ширины, 75% высоты)
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            Width = screenWidth * 0.80;
            Height = screenHeight * 0.75;
            
            StartServer();
        }

        private async void StartServer()
        {
            _server = new ServerHost();
            try
            {
                await _server.StartAsync(8080);

                // 1. Загружаем реестр клиентов (сбрасываем статусы в Оффлайн)
                AdminHub.LoadRegistry();
                Logger.Info("✅ Реестр клиентов загружен");

                // ✅ 2. ВОССТАНАВЛИВАЕМ АКТИВНЫЕ СЕССИИ
                // Это критически важно: если сервер перезапустился, 
                // мы читаем active_sessions.json и возвращаем таймеры на место.
                AdminHub.LoadActiveSessions();
                Logger.Info("✅ Активные сессии восстановлены");

                // ✅ 3. ЗАГРУЖАЕМ ИСТОРИЮ ФИНАНСОВ ПРИ ЗАПУСКЕ
                // Критически важно: загружаем историю сразу при старте приложения,
                // чтобы данные были доступны даже до перехода на вкладку "Финансы"
                FinancePage.LoadHistory();
                Logger.Info("✅ История финансов загружена");

                Dispatcher.Invoke(() =>
                {
                    TxtServerStatus.Text = "Сервер запущен :8080";
                    DotServer.Fill = new SolidColorBrush(
                        Color.FromRgb(29, 158, 117)); // Зеленый цвет
                });

                Logger.Info("Сервер запущен успешно");
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    TxtServerStatus.Text = "Ошибка сервера";
                    DotServer.Fill = new SolidColorBrush(
                        Color.FromRgb(226, 75, 74)); // Красный цвет
                });
                Logger.Error($"Ошибка запуска сервера: {ex.Message}");
            }

            // Открываем страницу компьютеров по умолчанию
            MainFrame.Navigate(new ComputersPage());
        }

        // Навигация: Компьютеры
        private void BtnComputers_Click(object sender, RoutedEventArgs e)
        {
            SetActive(BtnComputers);
            MainFrame.Navigate(new ComputersPage());
        }

        // Навигация: Финансы
        private void BtnFinance_Click(object sender, RoutedEventArgs e)
        {
            SetActive(BtnFinance);
            // История уже загружена при старте приложения, просто показываем страницу
            MainFrame.Navigate(new FinancePage());
        }

        // Навигация: Настройки
        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            SetActive(BtnSettings);
            MainFrame.Navigate(new SettingsPage());
        }

        // Подсветка активной кнопки меню
        private void SetActive(Button btn)
        {
            BtnComputers.Tag = null;
            BtnFinance.Tag = null;
            BtnSettings.Tag = null;
            btn.Tag = "active";
        }

        // Корректное завершение работы
        protected override async void OnClosing(
            System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                if (_server != null) await _server.StopAsync();
                Logger.Info("Сервер остановлен");
            }
            catch { }
            base.OnClosing(e);
        }
    }
}