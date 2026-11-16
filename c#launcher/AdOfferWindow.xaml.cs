using System.Windows;

namespace CraftunityLauncher;

/// <summary>Окно встречного предложения на объявление: количество и цена.</summary>
public partial class AdOfferWindow : Window
{
    public int OfferCount { get; private set; } = 1;
    public int OfferPrice { get; private set; } = 1;

    public AdOfferWindow(AdItem ad)
    {
        InitializeComponent();
        Title = "Предложение на «" + ad.Title + "»";
        InfoText.Text = ad.Side == "sell"
            ? $"Автор продаёт {ad.ItemCount} × {ad.Item} за {ad.PriceCount} × {ad.PriceItem}. Укажите, сколько берёте и сколько платите."
            : $"Автор покупает {ad.ItemCount} × {ad.Item} за {ad.PriceCount} × {ad.PriceItem}. Укажите, сколько продаёте и сколько просите.";
        CountBox.Text = ad.ItemCount.ToString();
        PriceBox.Text = ad.PriceCount.ToString();
        Loaded += (_, _) => CountBox.Focus();
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CountBox.Text.Trim(), out var count) || count < 1)
        {
            InfoText.Text = "Количество должно быть положительным числом";
            return;
        }
        if (!int.TryParse(PriceBox.Text.Trim(), out var price) || price < 1)
        {
            InfoText.Text = "Цена должна быть положительным числом";
            return;
        }
        OfferCount = count;
        OfferPrice = price;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
