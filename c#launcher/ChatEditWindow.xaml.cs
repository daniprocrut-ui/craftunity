using System.Windows;

namespace CraftunityLauncher;

/// <summary>Окно правки своего сообщения в чате.</summary>
public partial class ChatEditWindow : Window
{
    public string MessageText => Input.Text;

    public ChatEditWindow(string text)
    {
        InitializeComponent();
        Input.Text = text ?? "";
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.CaretIndex = Input.Text.Length;
        };
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
