using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BibAdmin
{
    public partial class OperatorsPage : Page
    {
        public OperatorsPage()
        {
            InitializeComponent();
            ShowWebUrl();
            RenderOperators();
        }

        private void ShowWebUrl()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                var ip = host.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                TxtWebUrl.Text = ip != null
                    ? $"Веб-интерфейс доступен по адресу: http://{ip}:8080/operator.html"
                    : "Веб-интерфейс: http://localhost:8080/operator.html";
            }
            catch
            {
                TxtWebUrl.Text = "Веб-интерфейс: http://localhost:8080/operator.html";
            }
        }

        private void RenderOperators()
        {
            OperatorsPanel.Children.Clear();
            var settings = GlobalSettings.Load();

            if (settings.Operators.Count == 0)
            {
                OperatorsPanel.Children.Add(new TextBlock
                {
                    Text = "Операторы не добавлены",
                    Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                    FontSize = 13,
                    Margin = new Thickness(20, 16, 20, 16)
                });
                return;
            }

            foreach (var op in settings.Operators)
            {
                var row = new Grid { Margin = new Thickness(20, 6, 20, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

                // Имя и логин
                var namePanel = new StackPanel();
                namePanel.Children.Add(new TextBlock
                {
                    Text = op.DisplayName,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 46))
                });
                namePanel.Children.Add(new TextBlock
                {
                    Text = op.Login,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 160))
                });
                Grid.SetColumn(namePanel, 0);
                row.Children.Add(namePanel);

                // Сброс пароля
                var resetBtn = new Button
                {
                    Content = "Новый пароль",
                    FontSize = 11,
                    Padding = new Thickness(8, 4, 8, 4),
                    Background = Brushes.Transparent,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(180, 180, 200)),
                    BorderThickness = new Thickness(1),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    Tag = op.Id
                };
                resetBtn.Click += ResetPassword_Click;
                Grid.SetColumn(resetBtn, 1);
                row.Children.Add(resetBtn);

                // Статус (онлайн/оффлайн — будущее)
                var statusText = new TextBlock
                {
                    Text = op.IsActive ? "Активен" : "Отключён",
                    FontSize = 12,
                    Foreground = op.IsActive
                        ? new SolidColorBrush(Color.FromRgb(29, 158, 117))
                        : new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(statusText, 2);
                row.Children.Add(statusText);

                // Чекбокс активности
                var activeChk = new CheckBox
                {
                    IsChecked = op.IsActive,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Tag = op.Id
                };
                activeChk.Checked += ActiveToggle_Changed;
                activeChk.Unchecked += ActiveToggle_Changed;
                Grid.SetColumn(activeChk, 3);
                row.Children.Add(activeChk);

                // Удалить
                var delBtn = new Button
                {
                    Content = "✕",
                    FontSize = 13,
                    Width = 30, Height = 30,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Foreground = new SolidColorBrush(Color.FromRgb(200, 80, 80)),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Tag = op.Id
                };
                delBtn.Click += DeleteOperator_Click;
                Grid.SetColumn(delBtn, 4);
                row.Children.Add(delBtn);

                OperatorsPanel.Children.Add(row);

                // Разделитель
                OperatorsPanel.Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
                    Margin = new Thickness(20, 0, 20, 0)
                });
            }
        }

        private void AddOperator_Click(object sender, RoutedEventArgs e)
        {
            var displayName = TxtNewDisplayName.Text.Trim();
            var login = TxtNewLogin.Text.Trim();
            var password = TxtNewPassword.Password;

            if (string.IsNullOrEmpty(displayName) || string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password))
            {
                MessageBox.Show("Заполните все поля.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var settings = GlobalSettings.Load();
            if (settings.Operators.Any(o => o.Login == login))
            {
                MessageBox.Show($"Логин '{login}' уже занят.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            settings.Operators.Add(new OperatorAccount
            {
                DisplayName = displayName,
                Login = login,
                PasswordHash = OperatorApi.HashPassword(password),
                IsActive = true
            });
            settings.Save();

            TxtNewDisplayName.Text = "";
            TxtNewLogin.Text = "";
            TxtNewPassword.Password = "";

            ShowSaved();
            RenderOperators();
        }

        private void ResetPassword_Click(object sender, RoutedEventArgs e)
        {
            var id = (string)((Button)sender).Tag;
            var dialog = new PcSettingDialog("", "Новый пароль", "Введите новый пароль:", "");
            if (dialog.ShowDialog() != true || string.IsNullOrEmpty(dialog.Result)) return;

            var settings = GlobalSettings.Load();
            var op = settings.Operators.Find(o => o.Id == id);
            if (op == null) return;

            op.PasswordHash = OperatorApi.HashPassword(dialog.Result);
            settings.Save();
            ShowSaved();
        }

        private void ActiveToggle_Changed(object sender, RoutedEventArgs e)
        {
            var id = (string)((CheckBox)sender).Tag;
            var isActive = ((CheckBox)sender).IsChecked == true;
            var settings = GlobalSettings.Load();
            var op = settings.Operators.Find(o => o.Id == id);
            if (op == null) return;
            op.IsActive = isActive;
            settings.Save();
            RenderOperators();
        }

        private void DeleteOperator_Click(object sender, RoutedEventArgs e)
        {
            var id = (string)((Button)sender).Tag;
            var settings = GlobalSettings.Load();
            var op = settings.Operators.Find(o => o.Id == id);
            if (op == null) return;

            var r = MessageBox.Show($"Удалить оператора '{op.DisplayName}'?",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            settings.Operators.Remove(op);
            settings.Save();
            RenderOperators();
        }

        private void ShowSaved()
        {
            TxtSaved.Text = "✓ Сохранено";
            TxtSaved.Visibility = Visibility.Visible;
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            timer.Tick += (s, e) => { TxtSaved.Visibility = Visibility.Collapsed; timer.Stop(); };
            timer.Start();
        }
    }
}
