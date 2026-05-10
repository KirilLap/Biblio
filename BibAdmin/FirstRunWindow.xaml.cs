using System.Windows;

namespace BibAdmin
{
    public partial class FirstRunWindow : Window
    {
        public FirstRunWindow()
        {
            InitializeComponent();
            PbPassword.Focus();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var password = PbPassword.Password;
            var confirm  = PbConfirm.Password;

            if (password.Length < 4)
            {
                ShowError("Пароль должен быть не менее 4 символов");
                return;
            }

            if (password != confirm)
            {
                ShowError("Пароли не совпадают");
                return;
            }

            var settings = GlobalSettings.Load();
            settings.SetPassword(password);
            settings.IsFirstRun = false;
            settings.Save();

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
