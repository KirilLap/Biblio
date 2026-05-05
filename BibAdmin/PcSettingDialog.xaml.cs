using System.Windows;

namespace BibAdmin
{
    public partial class PcSettingDialog : Window
    {
        public string Result { get; private set; } = "";

        public PcSettingDialog(string pcNumber, string title,
            string label, string defaultValue = "")
        {
            InitializeComponent();
            TxtTitle.Text = title;
            TxtPcName.Text = pcNumber;
            TxtLabel.Text = label;
            TxtInput.Text = defaultValue;
            TxtInput.SelectAll();
            TxtInput.Focus();
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            Result = TxtInput.Text.Trim();
            if (string.IsNullOrEmpty(Result))
            {
                MessageBox.Show("Поле не может быть пустым",
                    "Ошибка", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            
            // Проверка: ввод должен содержать только буквы (русские/английские) и пробелы
            foreach (char c in Result)
            {
                if (!char.IsLetter(c) && c != ' ')
                {
                    MessageBox.Show("Разрешены только буквы русского или английского алфавита и пробелы.\nЦифры вводить не нужно — номер добавится автоматически.",
                        "Ошибка ввода", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }
            
            DialogResult = true;
            Close();
        }
    }
}