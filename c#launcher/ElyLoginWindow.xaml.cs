using System.Windows;

namespace CraftunityLauncher;

public partial class ElyLoginWindow : Window
{
    public string Login => LoginBox.Text.Trim();
    public string Password => PasswordBox.Password;

    public ElyLoginWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LoginBox.Focus();
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Login) || string.IsNullOrWhiteSpace(Password))
        {
            MessageBox.Show(this, "Введите логин и пароль.", "Craftunity",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
