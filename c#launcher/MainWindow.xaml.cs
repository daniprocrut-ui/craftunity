using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CraftunityLauncher;

public partial class MainWindow : Window
{
    private ApiClient? _api;
    private CancellationTokenSource? _pollCts;
    private readonly ObservableCollection<ModCard> _cards = new();
    private readonly ObservableCollection<InstalledCard> _installed = new();
    private readonly ObservableCollection<FileRow> _files = new();
    private readonly ObservableCollection<ChatMessage> _chat = new();
    private long _chatLastId;
    private long _chatLastNotifiedId;
    private ChatMessage? _replyTarget;
    private byte[]? _pendingChatImage;
    private string _chatChannel = "general";
    private string _chatPeerId = "";
    private string _chatPeerName = "";
    private List<ClientEntry> _clientPeers = new();
    private readonly Dictionary<string, string> _chatHistoryPeers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _hiddenPeers = new(StringComparer.OrdinalIgnoreCase);
    private static System.Windows.Media.MediaPlayer? _chatSound;
    private int _chatUnread;
    private int _chatTick;
    private bool _gameRunning;
    private DateTime _lastPresenceAt = DateTime.MinValue;
    private readonly List<ChatMessage> _pendingChat = new();
    private System.Windows.Threading.DispatcherTimer? _chatToastTimer;
    private static readonly System.Net.Http.HttpClient ChatHttp = CreateChatHttp(TimeSpan.FromSeconds(15));

    /// <summary>Отправка сообщений с картинкой: тело большое, нужен запас по времени.</summary>
    private static readonly System.Net.Http.HttpClient ChatSendHttp = CreateChatHttp(TimeSpan.FromSeconds(90), true);

    /// <summary>Скачивание картинок частями: соединение тоже рвётся, поэтому без keep-alive.</summary>
    private static readonly System.Net.Http.HttpClient ChatMediaHttp = CreateChatHttp(TimeSpan.FromSeconds(30), true);

    /// <summary>Cloudflare отдаёт 403 запросам без User-Agent, поэтому он обязателен.</summary>
    private static System.Net.Http.HttpClient CreateChatHttp(TimeSpan timeout, bool closeConnection = false)
    {
        var client = new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler
        {
            PooledConnectionLifetime = closeConnection ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(45),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = timeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CraftunityLauncher/1.6 (+https://craftunity.mcsh.io)");
        if (closeConnection)
        {
            // Прокси посреди канала рвёт долгие соединения — каждую часть шлём новым.
            client.DefaultRequestHeaders.ConnectionClose = true;
        }
        return client;
    }
    private string _kind = "mod";
    private string _installedFilter = "all";
    private int _offset;
    private long _total;
    private string _query = "";
    private byte[]? _pendingSkin;
    private static BitmapSource? _heroAvatar;
    private Overview? _overview;
    private double _progressShown;
    private int _viewIndex = -1;

    private static readonly string[] ThemeIds =
    {
        "dark", "light", "midnight", "forest", "sunset", "ocean", "sakura",
        "neon", "graphite", "crimson", "coffee", "sand", "lavender", "arctic",
    };

    private static readonly string[] ThemeNames =
    {
        "Тёмная", "Светлая", "Полуночная", "Лесная", "Закатная", "Океанская", "Сакура",
        "Неоновая", "Графитовая", "Багровая", "Кофейная", "Песочная", "Лавандовая", "Арктическая",
    };

    private static readonly Dictionary<string, (string Many, string Title, string Hint)> Kinds = new()
    {
        ["mod"] = ("модов", "Установка модов",
            "Моды берутся из каталога Modrinth и ставятся в папку mods выбранной версии."),
        ["resourcepack"] = ("ресурспаков", "Ресурспаки",
            "Ресурспаки из Modrinth и свои .zip попадают в папку resourcepacks."),
        ["shader"] = ("шейдеров", "Шейдеры",
            "Шейдеры из Modrinth и свои .zip попадают в папку shaderpacks."),
    };

    public MainWindow()
    {
        InitializeComponent();
        ModResults.ItemsSource = _cards;
        InstalledList.ItemsSource = _installed;
        FileList.ItemsSource = _files;
        ChatList.ItemsSource = _chat;
        ChatRooms.SelectedIndex = 0;
        ApplyChatChannel();
        BuildThemeTiles();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void ApplyTheme(string? theme)
    {
        Themes.Apply(theme);
        BuildThemeTiles();
    }

    private void BuildThemeTiles()
    {
        ThemeTiles.Children.Clear();
        foreach (var palette in Themes.All)
        {
            var button = new Button
            {
                Content = palette.Name,
                Tag = palette.Id,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(12, 6, 12, 6),
            };
            button.Click += OnThemeTileClick;
            ThemeTiles.Children.Add(button);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadHiddenPeers();
        Nav.SelectedIndex = 0;
        var url = await BackendLauncher.EnsureAsync(ResolveApiUrl());
        await ConnectAsync(url);
        await CheckLauncherUpdateAsync();
    }

    private async Task CheckLauncherUpdateAsync()
    {
        string current;
        try
        {
            current = _overview?.LauncherVersion ?? "";
        }
        catch
        {
            current = "";
        }
        if (string.IsNullOrWhiteSpace(current)) return;
        LauncherUpdate.Release? release;
        try
        {
            release = await LauncherUpdate.CheckAsync(current);
        }
        catch
        {
            return;
        }
        if (release is null) return;
        var answer = MessageBox.Show(
            this,
            $"Доступна новая версия лаунчера {release.Version} (у вас {current}).\n\n" +
            "Скачать и установить сейчас? Лаунчер закроется, установщик запустится сам.",
            "Обновление Craftunity",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        string? installer = null;
        try
        {
            StatusText.Text = "Скачивание обновления…";
            installer = await LauncherUpdate.DownloadAsync(release, p =>
                Dispatcher.Invoke(() => StatusText.Text = $"Скачивание обновления… {p:P0}"));
        }
        catch
        {
            installer = null;
        }
        if (installer is null)
        {
            StatusText.Text = "";
            MessageBox.Show(this, "Не удалось скачать обновление. Попробуйте ещё раз позже или скачайте установщик с сайта.",
                "Обновление Craftunity", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        StatusText.Text = "Запуск установщика…";
        if (LauncherUpdate.RunInstallerAfterExit(installer))
        {
            _pollCts?.Cancel();
            _api?.Dispose();
            BackendLauncher.Shutdown();
            App.ReleaseInstanceLock();
            Close();
            Application.Current.Shutdown();
        }
        else
        {
            StatusText.Text = "";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _pollCts?.Cancel();
        _api?.Dispose();
        BackendLauncher.Shutdown();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        AdaptLayout(sizeInfo.NewSize.Width);
    }

    private void AdaptLayout(double width)
    {
        var narrow = width < 900;
        if (narrow)
        {
            PlaySide.Margin = new Thickness(0, 18, 0, 0);
            Grid.SetColumn(PlaySide, 0);
            Grid.SetRow(PlaySide, 1);
            PlayMain.Margin = new Thickness(0, 0, 0, 0);
        }
        else
        {
            Grid.SetColumn(PlaySide, 1);
            Grid.SetRow(PlaySide, 0);
            PlayMain.Margin = new Thickness(0, 0, 14, 0);
            PlaySide.Margin = new Thickness(0);
        }
    }

    private static string ResolveApiUrl()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var lockPath = Path.Combine(appData, "Craftunity", "launcher.lock");
            if (File.Exists(lockPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(lockPath));
                if (doc.RootElement.TryGetProperty("port", out var value))
                {
                    var port = value.ToString();
                    if (!string.IsNullOrWhiteSpace(port)) return $"http://127.0.0.1:{port}";
                }
            }
        }
        catch
        {
            // lock не читается — идём на стандартный адрес
        }
        return "http://127.0.0.1:8765";
    }

    private async Task ConnectAsync(string url)
    {
        _pollCts?.Cancel();
        _api?.Dispose();
        _api = new ApiClient(url);
        StatusText.Text = "Подключение…";
        if (!await _api.IsAliveAsync())
        {
            StatusText.Text = "API недоступен";
            NetText.Text = "Лаунчер не запущен";
            return;
        }
        NetText.Text = "Сервер доступен";
        await RefreshManifestAsync();
        await RefreshAsync();
        StartPolling();
    }

    private void StartPolling()
    {
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1500, token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                try
                {
                    var state = await _api!.GetAsync("/api/state", token);
                    Dispatcher.Invoke(() => ApplyState(state));
                    _gameRunning = state.Bool("running");
                    if (_overview is null) await TryLoadOverviewAsync(token);
                }
                catch
                {
                    // пропускаем тик
                }
                _chatTick++;
                if (_overview is not null && (DateTime.UtcNow - _lastPresenceAt).TotalSeconds >= 30)
                {
                    _lastPresenceAt = DateTime.UtcNow;
                    try
                    {
                        await SendPresenceAsync();
                    }
                    catch
                    {
                        // присутствие не критично
                    }
                }
                if (_chatTick % 10 == 0 && _overview is not null)
                {
                    try
                    {
                        await PollChatAsync();
                    }
                    catch
                    {
                        // чат не критичен для работы лаунчера
                    }
                }
            }
        }, token);
    }

    private void ApplyState(JsonElement state)
    {
        _progressShown = Math.Clamp(state.Num("progress"), 0, 1);
        ProgressBar.Value = _progressShown * 100;
        ProgressPct.Text = $"{Math.Round(_progressShown * 100)}%";
        var error = state.Str("error");
        var message = state.Str("message");
        ProgressStage.Text = string.IsNullOrWhiteSpace(error)
            ? (string.IsNullOrWhiteSpace(message) ? state.Str("stage") : message)
            : error;
        var running = state.Bool("running");
        PlayButton.Content = running ? "Игра запущена" : "Играть";
        PlayButton.IsEnabled = !running && _overview?.Account is not null;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (LogBox.Visibility == Visibility.Visible)
        {
            var log = string.Join("\n", state.Arr("log").Select(line =>
                line.ValueKind == JsonValueKind.String ? line.GetString() : line.ToString()));
            if (LogText.Text != log)
            {
                LogText.Text = log;
                LogScroll.ScrollToEnd();
            }
        }
    }

    private async Task RefreshManifestAsync()
    {
        if (_api is null) return;
        try
        {
            StatusText.Text = "Обновление манифеста…";
            await _api.PostAsync("/api/manifest/refresh", new { });
            StatusText.Text = "Готово";
        }
        catch
        {
            StatusText.Text = "";
        }
    }

    private async Task RefreshAsync()
    {
        if (_api is null) return;
        try
        {
            var root = await _api.GetAsync("/api/overview");
            _overview = Overview.From(root);
            ApplyOverview(root);
            await ReloadInstalledAsync();
        }
        catch (Exception exc)
        {
            StatusText.Text = "Ошибка: " + exc.Message;
        }
    }

    private async Task TryLoadOverviewAsync(CancellationToken ct = default)
    {
        if (_api is null) return;
        try
        {
            var root = await _api.GetAsync("/api/overview", ct);
            _overview = Overview.From(root);
            ApplyOverview(root);
            StartPolling();
        }
        catch
        {
            // попробуем на следующем тике
        }
    }

    private void ApplyOverview(JsonElement root)
    {
        var overview = _overview!;
        LauncherVerRun.Text = overview.LauncherVersion;
        StatusVersion.Text = "v" + overview.LauncherVersion;

        HeroName.Text = overview.Account?.Username ?? "Гость";
        HeroType.Text = overview.Account is null
            ? "Аккаунт не выбран"
            : (overview.Account.Type == "ely" ? "Аккаунт ely.by" : "Оффлайн-аккаунт");
        if (overview.Account is not null)
        {
            if (string.IsNullOrWhiteSpace(OfflineName.Text)) OfflineName.Text = overview.Account.Username;
            if (string.IsNullOrWhiteSpace(ElyLogin.Text)) ElyLogin.Text = overview.Account.Username;
        }

        HeroChips.Children.Clear();
        void Chip(string caption, string value)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock
            {
                Text = caption + " ", Style = (Style)(FindResource("Muted")),
            });
            panel.Children.Add(new TextBlock { Text = value, FontWeight = FontWeights.SemiBold });
            HeroChips.Children.Add(new Border
            {
                Background = (Brush)FindResource("CardBrush"),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(11, 5, 11, 5),
                Margin = new Thickness(0, 0, 6, 6),
                Child = panel,
            });
        }
        if (!string.IsNullOrWhiteSpace(overview.GameVersion)) Chip("Версия", overview.GameVersion);
        if (!string.IsNullOrWhiteSpace(overview.Loader)) Chip("Загрузчик", overview.Loader);
        if (!string.IsNullOrWhiteSpace(overview.BuildVersion)) Chip("Сборка", overview.BuildVersion);
        Chip("Модов", overview.InstalledMods.ToString());

        PlayHint.Text = overview.Account is null
            ? "Выберите аккаунт, чтобы начать"
            : overview.Progress.Running ? "Minecraft работает" : "Клиент обновится автоматически";

        var missing = string.IsNullOrWhiteSpace(overview.BuildVersion) ||
                      string.Equals(overview.ManifestSource, "none", StringComparison.OrdinalIgnoreCase) ||
                      !string.IsNullOrWhiteSpace(overview.ManifestError);
        ManifestWarn.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        if (missing)
        {
            ManifestWarnText.Text = "Версия не подтверждена сервером. "
                                    + (string.IsNullOrWhiteSpace(overview.ManifestError) ? "манифест не получен" : overview.ManifestError)
                                    + $"\nЗапустится локальная сборка {overview.GameVersion} · Fabric.";
        }

        ServerPill.Text = overview.ServerHost;
        ServerCard.Text = $"{overview.ServerHost}:{overview.ServerPort}\n"
                          + (string.IsNullOrWhiteSpace(overview.ManifestMessage)
                              ? "Добро пожаловать на Craftunity!"
                              : overview.ManifestMessage)
                          + $"\nАвтовход в игру: {(overview.AutoJoin ? "включён" : "выключен")}";

        NetDot.Fill = (Brush)FindResource(string.IsNullOrWhiteSpace(overview.ManifestError) ? "AccentBrush" : "DangerBrush");
        NetText.Text = !string.IsNullOrWhiteSpace(overview.ManifestError)
            ? "Манифест недоступен"
            : string.Equals(overview.ManifestSource, "cache", StringComparison.OrdinalIgnoreCase)
                ? "Манифест из кеша"
                : "Сервер доступен";
        StatusText.Text = string.IsNullOrWhiteSpace(overview.Progress.Message) ? "Готово" : overview.Progress.Message;

        ClientState.Children.Clear();
        foreach (var (name, value) in new[]
                 {
                     ("Манифест", !string.IsNullOrWhiteSpace(overview.ManifestError)
                         ? "недоступен"
                         : string.IsNullOrWhiteSpace(overview.BuildVersion) ? "не загружен" : "подписан и принят"),
                     ("Клиент", overview.Progress.Status switch
                     {
                         "ready" => "готов",
                         "running" => "работает",
                         "installing" or "starting" => "обновляется",
                         _ => "не проверен",
                     }),
                     ("Java", string.IsNullOrWhiteSpace(overview.Java) ? "не найдена" : overview.Java),
                     ("Папка игры", string.IsNullOrWhiteSpace(overview.GameDir) ? "—" : overview.GameDir),
                 })
        {
            ClientState.Children.Add(new Border
            {
                Background = (Brush)FindResource("BgBrush"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(11, 8, 11, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = name, FontSize = 13 },
                        new TextBlock
                        {
                            Text = value, FontSize = 11,
                            Foreground = (Brush)FindResource("MutedBrush"),
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                },
            });
        }

        CurseforgeState.Text = string.IsNullOrWhiteSpace(overview.CurseforgeKey)
            ? "CurseForge выключен: без ключа поиск идёт только по Modrinth"
            : "CurseForge включён: выдача из двух каталогов, дубли ставятся с Modrinth";
        if (string.IsNullOrWhiteSpace(CurseforgeKey.Text)) CurseforgeKey.Text = overview.CurseforgeKey;
        SetMemory.Text = overview.MemoryMb.ToString();
        if (string.IsNullOrWhiteSpace(SetJava.Text)) SetJava.Text = overview.JavaPath;
        SetVersion.Text = $"{overview.GameVersion} / {overview.Loader}";
        SetServer.Text = $"{overview.ServerHost}:{overview.ServerPort}";
        ManifestAuto.Text = string.IsNullOrWhiteSpace(overview.BuildVersion)
            ? "Пока не найден — используется сохранённая копия или локальные настройки"
            : (string.IsNullOrWhiteSpace(overview.ManifestUrl) ? "найден автоматически" : overview.ManifestUrl);
        ManifestInfo.Text = string.IsNullOrWhiteSpace(overview.BuildVersion)
            ? overview.ManifestError
            : $"Сборка {overview.BuildVersion} · обновлена {overview.BuildUpdated} · модов в модпаке: {overview.PackMods}";

        OptReduceMotion.IsChecked = overview.ReduceMotion;
        OptAutoJava.IsChecked = overview.AutoJava;
        OptAutoJoin.IsChecked = overview.AutoJoin;
        OptHoliday.IsChecked = overview.HolidayTheme;
        OptSound.IsChecked = overview.ChatSound;

        Themes.Apply(overview.Theme);
        _ = LoadSkinAsync();
    }

    // ---------------------------------------------------------------- навигация

    /// <summary>Плавное появление страницы: лёгкий сдвиг снизу и проявление.</summary>
    private void AnimatePageIn(UIElement page)
    {
        if (page is null) return;
        if (OptReduceMotion.IsChecked == true)
        {
            page.BeginAnimation(UIElement.OpacityProperty, null);
            page.Opacity = 1;
            return;
        }
        var slide = new TranslateTransform(0, 14);
        page.RenderTransform = slide;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var rise = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        page.BeginAnimation(UIElement.OpacityProperty, fade);
        slide.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    /// <summary>Плавно схлопывает или разворачивает боковое меню.</summary>
    private void AnimateSideWidth(double target)
    {        var current = SideColumn.ActualWidth > 0 ? SideColumn.ActualWidth : SideColumn.Width.Value;
        if (OptReduceMotion.IsChecked == true || !IsLoaded || Math.Abs(current - target) < 1)
        {
            SideColumn.Width = new GridLength(target);
            return;
        }
        var started = DateTime.UtcNow;
        var span = TimeSpan.FromMilliseconds(240);
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(15),
        };
        timer.Tick += (_, _) =>
        {
            var progress = Math.Min(1.0, (DateTime.UtcNow - started).TotalMilliseconds / span.TotalMilliseconds);
            var eased = 1 - Math.Pow(1 - progress, 3);
            SideColumn.Width = new GridLength(current + (target - current) * eased);
            if (progress >= 1.0)
            {
                timer.Stop();
                SideColumn.Width = new GridLength(target);
            }
        };
        timer.Start();
    }

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PagePlay is null) return;
        var index = Nav.SelectedIndex;
        var animate = index != _viewIndex;
        _viewIndex = index;
        var titles = new[]
        {
            ("Играть", "Craftunity — один клиент, один сервер"),
            ("Контент", "Моды, ресурспаки и шейдеры из каталогов"),
            ("Установлено", "Всё, что стоит в клиенте сейчас"),
            ("Файлы", "Свои файлы в папке игры"),
            ("Скин", "Ваш скин и его модель"),
            ("Аккаунт", "Оффлайн-ник или ely.by"),
            ("Чат", "Общий чат участников лаунчера"),
            ("Настройки", "Вид, игра, каталоги, манифест"),
            ("Карта", "Общая карта сервера с игроками"),
        };
        if (index >= 0 && index < titles.Length)
        {
            ViewTitle.Text = titles[index].Item1;
            ViewSub.Text = titles[index].Item2;
        }

        PagePlay.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageMods.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageInstalled.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageFiles.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageSkin.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        PageAccount.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        PageChat.Visibility = index == 6 ? Visibility.Visible : Visibility.Collapsed;
        // На вкладке чата прокручивается только лента сообщений, а не вся страница:
        // внешний скролл экранов иначе растягивает чат и уводит поле ввода за экран.
        PageScroll.VerticalScrollBarVisibility = index == 6
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        PageSettings.Visibility = index == 7 ? Visibility.Visible : Visibility.Collapsed;
        PageMap.Visibility = index == 8 ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.VerticalScrollBarVisibility = index is 6 or 8
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        SetCompactNav(index == 6);

        if (animate)
        {
            var active = index switch
            {
                0 => (UIElement)PagePlay,
                1 => PageMods,
                2 => PageInstalled,
                3 => PageFiles,
                4 => PageSkin,
                5 => PageAccount,
                6 => PageChat,
                7 => PageSettings,
                8 => PageMap,
                _ => PagePlay,
            };
            AnimatePageIn(active);
        }

        switch (index)
        {
            case 1:
                _ = SearchAsync(false);
                break;
            case 2:
                _ = ReloadInstalledAsync();
                break;
            case 3:
                _ = LoadFilesAsync();
                break;
            case 8:
                _ = EnsureMapAsync();
                break;
            case 6:
                ClearChatUnread();
                _ = RefreshClientIconsAsync(true);
                _ = LoadChatAsync(true);
                break;
        }
    }

    private void Toast(string message, bool bad = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(bad ? "DangerBrush" : "MutedBrush");
    }

    // ------------------------------------------------------------------ карта

    /// <summary>Адрес веб-карты BlueMap на сервере.</summary>
    private const string MapUrl = "http://148.113.0.9:24441/";

    private bool _mapReady;

    /// <summary>Поднимает WebView2 и открывает карту BlueMap.</summary>
    private async Task EnsureMapAsync()
    {
        if (_mapReady) return;
        MapState.Text = "Карта сервера BlueMap";
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Craftunity", "webview");
            Directory.CreateDirectory(folder);
            if (!await MapReachableAsync())
            {
                MapView.Visibility = Visibility.Collapsed;
                MapFallback.Visibility = Visibility.Visible;
                MapFallback.Text = "Сервер сейчас выключен — карта недоступна.\n\n" +
                                   "Включите сервер и нажмите «Обновить».";
                MapHint.Text = "Карта живёт на самом сервере: " + MapUrl;
                return;
            }

            var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, folder);
            await MapView.EnsureCoreWebView2Async(environment);
            MapView.CoreWebView2.Navigate(MapUrl);
            MapView.Visibility = Visibility.Visible;
            MapFallback.Visibility = Visibility.Collapsed;
            MapHint.Text = "Карта открывается прямо в лаунчере.";
            _mapReady = true;
        }
        catch (Exception exc)
        {
            MapView.Visibility = Visibility.Collapsed;
            MapFallback.Visibility = Visibility.Visible;
            MapFallback.Text = "Не удалось открыть карту внутри лаунчера.\n" +
                               "Причина: " + exc.Message + "\n\n" +
                               "Нажмите «Открыть в браузере» — карта откроется в браузере.";
            MapHint.Text = "Для встроенной карты нужен компонент WebView2 Runtime.";
        }
    }

    /// <summary>Проверяет, отвечает ли веб-сервер BlueMap на сервере.</summary>
    private static async Task<bool> MapReachableAsync()
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var response = await client.GetAsync(MapUrl);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void OnMapReloadClick(object sender, RoutedEventArgs e)
    {
        if (_mapReady && MapView.CoreWebView2 is not null)
        {
            MapView.CoreWebView2.Navigate(MapUrl);
        }
        else
        {
            _ = EnsureMapAsync();
        }
    }

    private void OnMapOpenBrowserClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = MapUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    // ------------------------------------------------------------------ чат

    private const string ChatBaseUrl = "https://craftunity-heartbeat.daniprocrut-ui.workers.dev";
    private const string ClientsToken = "fc0cd5aa8afe801c8a2bd3dbe9ee3796";

    /// <summary>Адрес ленты с учётом выбранного чата.</summary>
    private string ChatQuery(string since)
    {
        var url = $"{ChatBaseUrl}/messages?since={since}&channel={Uri.EscapeDataString(_chatChannel)}";
        if (_chatChannel == "dm")
        {
            var self = _overview?.InstanceId ?? "";
            url += $"&self={Uri.EscapeDataString(self)}&peer={Uri.EscapeDataString(_chatPeerId)}";
        }
        return url;
    }

    private void OnChatRoomChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChatRooms.SelectedItem is not ListBoxItem item) return;
        var tag = item.Tag as string ?? "general";
        if (_chatChannel == tag && _chatPeerId.Length == 0) return;
        _chatChannel = tag;
        _chatPeerId = "";
        _chatPeerName = "";
        ChatPeers.SelectedItem = null;
        ApplyChatChannel();
        _ = LoadChatAsync(true);
    }

    /// <summary>Клик по нику в ленте открывает личные сообщения с этим человеком.</summary>
    private void OnChatAuthorClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ChatMessage message }) return;
        e.Handled = true;
        if (message.Mine) return;
        var name = message.Author?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) return;
        OpenDirectChat(message.InstanceId, name);
    }

    /// <summary>Переключает чат на личные сообщения с выбранным игроком.</summary>
    private void OpenDirectChat(string? instanceId, string name)
    {
        var id = (instanceId ?? "").Trim();
        var mine = _overview?.InstanceId ?? "";
        if (string.IsNullOrEmpty(id) || id == mine)
        {
            // Локальной записи нет — ищем по нику в реестре клиентов.
            var hit = _clientPeers.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                ChatState.Text = "Не удалось открыть личные: игрок не в сети";
                return;
            }
            id = hit.Id;
        }
        _chatChannel = "dm";
        _chatPeerId = id;
        _chatPeerName = name;
        ChatRooms.SelectedItem = null;
        // Открыли чат вручную — значит, он снова нужен в списке.
        if (_hiddenPeers.Remove(id))
        {
            SaveHiddenPeers();
            FillChatPeers();
        }
        var peer = _clientPeers.FirstOrDefault(p => p.Id == id);
        if (peer is not null)
        {
            ChatPeers.SelectedItem = peer;
        }
        else
        {
            ChatPeers.SelectedItem = null;
        }
        ApplyChatChannel();
        _ = LoadChatAsync(true);
    }

    private void OnChatPeerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChatPeers.SelectedItem is not ClientEntry entry) return;
        _chatChannel = "dm";
        _chatPeerId = entry.Id;
        _chatPeerName = entry.Name;
        ChatRooms.SelectedItem = null;
        ApplyChatChannel();
        _ = LoadChatAsync(true);
    }

    /// <summary>Переключает шапку, поле ввода и ленту под выбранный чат.</summary>
    private void ApplyChatChannel()
    {
        var mine = _overview?.Account?.Username ?? "";
        // Новости пишет только владелец, объявления — все, остальное — все.
        var canPost = _chatChannel != "news"
                      || string.Equals(mine, "Danipro914", StringComparison.OrdinalIgnoreCase);
        ChatComposer.Visibility = canPost ? Visibility.Visible : Visibility.Collapsed;
        ChatReadOnlyNote.Text = _chatChannel == "news"
            ? "Новости публикует только Danipro914."
            : "Этот чат только для чтения.";
        ChatReadOnlyNote.Visibility = canPost ? Visibility.Collapsed : Visibility.Visible;
        ChatTitle.Text = _chatChannel switch
        {
            "announcements" => "Объявления",
            "news" => "Новости",
            "dm" => string.IsNullOrEmpty(_chatPeerName) ? "Личные сообщения" : "Личные · " + _chatPeerName,
            _ => "Общий чат",
        };
        _chatLastId = 0;
        _chat.Clear();
    }

    /// <summary>Собеседники для списка личных: клиенты из реестра плюс авторы из истории чата.</summary>
    private List<ClientEntry> PeerList()
    {
        var peers = new List<ClientEntry>(_clientPeers);
        var known = new HashSet<string>(peers.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);

        void AddPeer(string? id, string? name)
        {
            var peerId = (id ?? "").Trim();
            var peerName = (name ?? "").Trim();
            if (peerId.Length == 0 || peerName.Length == 0) return;
            if (string.Equals(peerId, _overview?.InstanceId ?? "", StringComparison.OrdinalIgnoreCase)) return;
            if (IsOwnAuthor(peerName)) return;
            if (!known.Add(peerId)) return;
            PresenceState presence;
            lock (ClientIcons)
            {
                presence = ClientPresence.TryGetValue(peerId, out var known2) ? known2 : PresenceState.Offline;
            }
            peers.Add(new ClientEntry(peerId, peerName) { Presence = presence });
        }

        foreach (var message in _chat.ToList())
        {
            AddPeer(message.InstanceId, message.Author);
        }
        foreach (var message in _chatHistoryPeers) AddPeer(message.Key, message.Value);
        return peers;
    }

    private void FillChatPeers()
    {
        var current = _chatPeerId;
        var peers = PeerList();
        ChatPeers.Items.Clear();
        foreach (var peer in peers
                     .Where(p => !_hiddenPeers.Contains(p.Id))
                     .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            ChatPeers.Items.Add(peer);
        }
        if (string.IsNullOrEmpty(current)) return;
        var hit = peers.FirstOrDefault(p => p.Id == current);
        if (hit is not null) ChatPeers.SelectedItem = hit;
    }

    /// <summary>Удаляет личный чат из лаунчера; история переписки на сервере остаётся.</summary>
    /// <summary>Файл со списком удалённых личных чатов: скрытие переживает перезапуск.</summary>
    private static string HiddenPeersPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Craftunity", "hidden-peers.json");

    private void LoadHiddenPeers()
    {
        _hiddenPeers.Clear();
        try
        {
            var path = HiddenPeersPath;
            if (!File.Exists(path)) return;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            foreach (var id in list ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(id)) _hiddenPeers.Add(id);
            }
        }
        catch
        {
            // повреждённый файл не должен мешать запуску
        }
    }

    private void SaveHiddenPeers()
    {
        try
        {
            var path = HiddenPeersPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_hiddenPeers.ToList()));
        }
        catch
        {
            // список можно восстановить вручную
        }
    }

    private void OnPeerRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ClientEntry entry }) return;
        e.Handled = true;
        var answer = MessageBox.Show(
            this,
            $"Удалить чат с «{entry.Name}» из лаунчера?\n\n" +
            "Чат пропадёт из списка личных. История переписки сохранится и вернётся, " +
            "если снова открыть чат с этим игроком.",
            "Удалить личный чат",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _hiddenPeers.Add(entry.Id);
        SaveHiddenPeers();
        if (string.Equals(_chatPeerId, entry.Id, StringComparison.OrdinalIgnoreCase))
        {
            _chatChannel = "general";
            _chatPeerId = "";
            _chatPeerName = "";
            ChatRooms.SelectedIndex = 0;
            ApplyChatChannel();
            _ = LoadChatAsync(true);
        }
        FillChatPeers();
        ChatState.Text = "Чат с " + entry.Name + " удалён из лаунчера";
    }

    /// <summary>В чате боковое меню схлопывается до иконок, чтобы освободить экран.</summary>
    private void SetCompactNav(bool compact)
    {
        AnimateSideWidth(compact ? 72 : 208);
        if (compact)
        {
            SideBrand.Visibility = Visibility.Collapsed;
        }
        else
        {
            SideBrand.Visibility = Visibility.Visible;
            AnimatePageIn(SideBrand);
        }
        NavPlay.Content = compact ? "▶" : "▶  Играть";
        NavContent.Content = compact ? "◀" : "◀  Контент";
        NavInstalled.Content = compact ? "▣" : "▣  Установлено";
        NavFiles.Content = compact ? "▤" : "▤  Файлы";
        NavSkin.Content = compact ? "☻" : "☻  Скин";
        NavAccount.Content = compact ? "◉" : "◉  Аккаунт";
        NavSettings.Content = compact ? "⚙" : "⚙  Настройки";
        NavMap.Content = compact ? "🗺" : "🗺  Карта";
        NavChatText.Text = compact ? "✉" : "✉  Чат";
        PageScroll.Padding = compact ? new Thickness(10, 0, 10, 6) : new Thickness(24, 0, 24, 8);
    }

    /// <summary>Ник → адрес скина из реестра /clients.</summary>
    private static readonly Dictionary<string, string> ClientIcons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>instance_id → состояние игрока из реестра /clients.</summary>
    private static readonly Dictionary<string, PresenceState> ClientPresence = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сколько секунд с последнего heartbeat считаем игрока на связи.</summary>
    private const int PresenceWindowSeconds = 150;

    /// <summary>Сообщает серверу, что лаунчер открыт и запущена ли игра.</summary>
    private async Task SendPresenceAsync()
    {
        var overview = _overview;
        if (overview is null || string.IsNullOrEmpty(overview.InstanceId)) return;
        var name = overview.Account?.Username ?? "";
        var payload = new
        {
            instance_id = overview.InstanceId,
            username = name,
            icon = string.IsNullOrEmpty(name) ? "" : "https://skinsystem.ely.by/skins/" + Uri.EscapeDataString(name),
            version = overview.GameVersion,
            loader = overview.Loader,
            started = 1,
            in_game = _gameRunning,
            launched = _gameRunning,
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload),
            System.Text.Encoding.UTF8, "application/json");
        using var response = await ChatHttp.PostAsync(ChatBaseUrl + "/heartbeat", content);
        _ = response;
    }

    private static DateTime _clientIconsAt = DateTime.MinValue;

    /// <summary>Обновляет реестр клиентов (ник → иконка), не чаще раза в 30 секунд.</summary>
    private async Task RefreshClientIconsAsync(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _clientIconsAt).TotalSeconds < 30) return;
        try
        {
            using var response = await ChatHttp.GetAsync($"{ChatBaseUrl}/clients?light=1&token={ClientsToken}");
            if (!response.IsSuccessStatusCode) return;
            var text = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("clients", out var clients)
                || clients.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            var fresh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var states = new Dictionary<string, PresenceState>(StringComparer.OrdinalIgnoreCase);
            var peers = new List<ClientEntry>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var client in clients.EnumerateArray())
            {
                if (client.ValueKind != JsonValueKind.Object) continue;
                var name = client.Str("username").Trim();
                var icon = client.Str("icon").Trim();
                var id = client.Str("instance_id").Trim();
                var lastSeen = client.Num("last_seen");
                // Зелёный — игра запущена, жёлтый — открыт только лаунчер, красный — давно не было heartbeat.
                var presence = now - lastSeen > PresenceWindowSeconds
                    ? PresenceState.Offline
                    : client.Bool("in_game") ? PresenceState.InGame : PresenceState.Launcher;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(icon)) fresh[name] = icon;
                if (!string.IsNullOrEmpty(id)) states[id] = presence;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id)
                    && id != (_overview?.InstanceId ?? ""))
                {
                    peers.Add(new ClientEntry(id, name) { Presence = presence });
                }
            }
            lock (ClientIcons)
            {
                ClientIcons.Clear();
                foreach (var pair in fresh) ClientIcons[pair.Key] = pair.Value;
                ClientPresence.Clear();
                foreach (var pair in states) ClientPresence[pair.Key] = pair.Value;
            }
            _clientPeers = peers;
            _clientIconsAt = DateTime.UtcNow;
            Dispatcher.Invoke(FillChatPeers);
            await FillPeerHeadsAsync();
        }
        catch
        {
            // реестр не критичен
        }
    }

    /// <summary>Адрес скина автора по нику из реестра клиентов.</summary>
    private static string IconFromRegistry(string? author)
    {
        if (string.IsNullOrWhiteSpace(author)) return "";
        lock (ClientIcons)
        {
            return ClientIcons.TryGetValue(author.Trim(), out var icon) ? icon : "";
        }
    }

    private async void OnChatRefreshClick(object sender, RoutedEventArgs e) => await LoadChatAsync(true);

    private async void OnChatSendClick(object sender, RoutedEventArgs e) => await SendChatAsync();

    /// <summary>Ctrl+V работает при любом фокусе, не только в поле ввода.</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (TryAttachFromClipboard()) e.Handled = true;
    }

    private async void OnChatInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SendChatAsync();
    }

    // ------------------------------------------------------------ картинки в чате

    private const int ChatImageMaxBytes = 480 * 1024;

    private async void OnChatAttachClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Картинки (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|Все файлы (*.*)|*.*",
            Title = "Картинка для чата",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            var prepared = PrepareChatImage(bytes);
            if (prepared is null)
            {
                ChatState.Text = ImageMime(bytes) is null
                    ? "Формат не поддерживается: нужен PNG, JPEG или WebP"
                    : "Картинка слишком тяжёлая — попробуйте скриншот поменьше";
                return;
            }
            SetChatAttachment(prepared);
        }
        catch (Exception exc)
        {
            ChatState.Text = "Не удалось открыть файл: " + exc.Message;
        }
    }

    /// <summary>Берёт картинку из буфера обмена (скриншот или скопированный файл).</summary>
    private bool TryAttachFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                var source = Clipboard.GetImage();
                if (source is null) return false;
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
                using var buffer = new MemoryStream();
                encoder.Save(buffer);
                var prepared = PrepareChatImage(buffer.ToArray());
                if (prepared is null)
                {
                    ChatState.Text = "Не удалось подготовить картинку из буфера";
                    return false;
                }
                SetChatAttachment(prepared);
                return true;
            }
            if (Clipboard.ContainsFileDropList())
            {
                foreach (var path in Clipboard.GetFileDropList())
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp")) continue;
                    var bytes = File.ReadAllBytes(path);
                    var prepared = PrepareChatImage(bytes);
                    if (prepared is null)
                    {
                        ChatState.Text = "Картинка слишком большая (максимум 480 КБ)";
                        return false;
                    }
                    SetChatAttachment(prepared);
                    return true;
                }
            }
        }
        catch (Exception exc)
        {
            ChatState.Text = "Буфер недоступен: " + exc.Message;
        }
        return false;
    }

    /// <summary>Готовит байты картинки к отправке: при необходимости уменьшает и жмёт в JPEG.</summary>
    private static byte[]? PrepareChatImage(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        if (bytes.Length <= ChatImageMaxBytes && ImageMime(bytes) is not null) return bytes;

        var decoded = ImageMime(bytes) is null ? null : DecodeImageBytes(bytes);
        if (decoded is null) return null;
        foreach (var (side, quality) in new[] { (1280, 82), (960, 80), (720, 78), (480, 72), (320, 66) })
        {
            var resized = ResizeImage(decoded, side);
            if (resized is null) continue;
            var jpeg = EncodeJpeg(resized, quality);
            if (jpeg is not null && jpeg.Length <= ChatImageMaxBytes) return jpeg;
        }
        return null;
    }

    /// <summary>Временный журнал отправки: помогает увидеть, где именно срывается.</summary>
    private static void ChatLog(string line)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Craftunity");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "chat-send.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // журнал не критичен
        }
    }

    /// <summary>Загружает картинку частями: сервер не принимает крупные тела запросов.</summary>
    private static async Task<string> UploadChatImageAsync(byte[] bytes)
    {
        const int chunk = 12000;
        var id = Guid.NewGuid().ToString("N");
        var encoded = Convert.ToBase64String(bytes);
        var total = (encoded.Length + chunk - 1) / chunk;
        ChatLog($"upload start bytes={bytes.Length} base64={encoded.Length} parts={total} id={id}");
        string url = "";
        for (var index = 0; index < total; index++)
        {
            var offset = index * chunk;
            var part = encoded.Substring(offset, Math.Min(chunk, encoded.Length - offset));
            var json = JsonSerializer.Serialize(new { id, index, total, data = part });
            string? answer = null;
            Exception? lastError = null;
            for (var attempt = 1; attempt <= 5 && answer is null; attempt++)
            {
                try
                {
                    using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                    using var response = await ChatSendHttp.PostAsync(ChatBaseUrl + "/chat/upload", content);
                    var text = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        ChatLog($"part {index}/{total} http={(int)response.StatusCode} {text}");
                        throw new InvalidOperationException("картинка не загрузилась: " + text);
                    }
                    answer = text;
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (Exception exc)
                {
                    lastError = exc;
                    ChatLog($"part {index}/{total} attempt {attempt} failed {exc.GetType().Name}: {exc.Message}");
                    await Task.Delay(400 * attempt);
                }
            }
            if (answer is null)
            {
                throw new InvalidOperationException("связь оборвалась на части " + (index + 1) + ": " + lastError?.Message);
            }
            if (index % 5 == 0 || index == total - 1)
            {
                ChatLog($"part {index}/{total} ok");
            }
            if (index == total - 1)
            {
                using var doc = JsonDocument.Parse(answer);
                url = doc.RootElement.Str("url");
            }
        }
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("сервер не вернул ссылку на картинку");
        }
        return url;
    }

    /// <summary>Скачивает картинку частями и декодирует.</summary>
    private static async Task<BitmapSource?> DownloadChatImageAsync(string url)
    {
        const int step = 10000;
        using var buffer = new MemoryStream();
        var offset = 0;
        var total = -1;
        while (total < 0 || offset < total)
        {
            var piece = url + (url.Contains('?') ? "&" : "?") + $"o={offset}&n={step}";
            byte[]? data = null;
            for (var attempt = 1; attempt <= 4 && data is null; attempt++)
            {
                try
                {
                    using var response = await ChatMediaHttp.GetAsync(piece);
                    if (!response.IsSuccessStatusCode)
                    {
                        ChatLog($"image piece {offset} http={(int)response.StatusCode}");
                        break;
                    }
                    if (response.Headers.TryGetValues("X-Total", out var values))
                    {
                        total = int.TryParse(values.FirstOrDefault(), out var parsed) ? parsed : 0;
                    }
                    data = await response.Content.ReadAsByteArrayAsync();
                }
                catch (Exception exc)
                {
                    ChatLog($"image piece {offset} attempt {attempt} {exc.GetType().Name}: {exc.Message}");
                    await Task.Delay(300 * attempt);
                }
            }
            if (data is null || data.Length == 0) break;
            buffer.Write(data);
            offset += data.Length;
            if (total <= 0) total = offset;
        }
        if (buffer.Length == 0)
        {
            ChatLog("image download empty for " + url);
            return null;
        }
        var picture = DecodeImageBytes(buffer.ToArray());
        if (picture is null) ChatLog($"image decode failed bytes={buffer.Length}");
        return picture;
    }

    /// <summary>Показывает картинку сообщения: data-url сразу, ссылку — скачиванием.</summary>
    private async void LoadMessageImage(ChatMessage message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message.ImageData)) return;
            if (message.ImageData.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                message.Image = DecodeDataUrl(message.ImageData);
                return;
            }
            var picture = await DownloadChatImageAsync(message.ImageData);
            if (picture is null) return;
            await Dispatcher.InvokeAsync(() => message.Image = picture);
        }
        catch
        {
            // картинка не критична
        }
    }

    /// <summary>Декодирует любые байты картинки (PNG/JPEG/WebP/BMP) без проверки лимита.</summary>
    private static BitmapSource? DecodeImageBytes(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string? BuildImageDataUrl(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > ChatImageMaxBytes) return null;
        var mime = ImageMime(bytes);
        if (mime is null) return null;
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string? ImageMime(byte[] bytes)
    {
        if (bytes.Length < 12) return null;
        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
        if (bytes[0] == 0xFF && bytes[1] == 0xD8) return "image/jpeg";
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return "image/webp";
        return null;
    }

    private static BitmapSource? ResizeImage(BitmapSource source, int maxSide)
    {
        try
        {
            var scale = Math.Min(1.0, (double)maxSide / Math.Max(source.PixelWidth, source.PixelHeight));
            if (scale >= 0.999) return source;
            var transform = new System.Windows.Media.Imaging.TransformedBitmap(source,
                new System.Windows.Media.ScaleTransform(scale, scale));
            var frozen = transform;
            frozen.Freeze();
            return frozen;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? EncodeJpeg(BitmapSource image, int quality)
    {
        try
        {
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private void SetChatAttachment(byte[] bytes)
    {
        _pendingChatImage = bytes;
        ChatAttachmentImage.Source = DecodeImageBytes(bytes);
        ChatAttachmentInfo.Text = $"Картинка · {Math.Max(1, bytes.Length / 1024)} КБ";
        ChatAttachment.Visibility = Visibility.Visible;
        ChatState.Text = "Картинка прикреплена — отправьте сообщение";
    }

    private void ClearChatAttachment()
    {
        _pendingChatImage = null;
        ChatAttachmentImage.Source = null;
        ChatAttachment.Visibility = Visibility.Collapsed;
    }

    private void OnChatAttachmentClearClick(object sender, RoutedEventArgs e)
    {
        ClearChatAttachment();
        ChatState.Text = "Картинка убрана";
    }

    private async Task SendChatAsync()
    {
        var body = ChatInput.Text.Trim();
        var imageBytes = _pendingChatImage;
        if (string.IsNullOrWhiteSpace(body) && imageBytes is null) return;
        ChatState.Text = imageBytes is null ? "Отправка…" : "Загрузка картинки…";
        try
        {
            var imageUrl = "";
            if (imageBytes is not null)
            {
                imageUrl = await UploadChatImageAsync(imageBytes);
                ChatState.Text = "Отправка…";
            }
            var reply = _replyTarget;
            var payload = new
            {
                instance_id = _overview?.InstanceId ?? "",
                username = _overview?.Account?.Username ?? "Гость",
                text = body,
                image = imageUrl,
                channel = _chatChannel,
                peer = _chatChannel == "dm" ? _chatPeerId : "",
                reply_author = reply?.Author ?? "",
                reply_text = reply is null ? "" : ShortReplyText(reply),
            };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await ChatSendHttp.PostAsync(ChatBaseUrl + "/message", content);
            var answer = await response.Content.ReadAsStringAsync();
            ChatLog($"message http={(int)response.StatusCode} image={(imageUrl.Length > 0)} {answer}");
            if (!response.IsSuccessStatusCode)
            {
                ChatState.Text = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? "Слишком часто, подождите пару секунд"
                    : "Не удалось отправить: " + answer;
                return;
            }
            ChatInput.Text = "";
            ClearChatAttachment();
            ChatState.Text = "Отправлено";
            await LoadChatAsync(false);
        }
        catch (Exception exc)
        {
            ChatLog($"send failed {exc.GetType().Name}: {exc.Message}");
            ChatState.Text = ChatErrorText(exc);
        }
    }

    private long ChatId => _chat.Count > 0 ? _chat[^1].Id : 0;

    private static string FormatReactions(JsonElement item)
    {
        if (!item.TryGetProperty("reactions", out var reactions) || reactions.ValueKind != JsonValueKind.Object)
            return "";
        var parts = new List<string>();
        foreach (var entry in reactions.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Array) continue;
            var count = entry.Value.GetArrayLength();
            if (count > 0) parts.Add($"{entry.Name} {count}");
        }
        return string.Join("  ", parts);
    }

    /// <summary>Короткий текст сообщения для цитаты в ответе.</summary>
    private static string ShortReplyText(ChatMessage message)
    {
        var text = (message.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (string.IsNullOrEmpty(text) && message.ImageData is not null && message.ImageData.Length > 0)
        {
            text = "картинка";
        }
        return text.Length > 140 ? text[..140] + "…" : text;
    }

    /// <summary>Подпись «в ответ на …» для сообщения-ответа.</summary>
    private static string FormatReplyTo(JsonElement item)
    {
        if (!item.TryGetProperty("reply_to", out var reply) || reply.ValueKind != JsonValueKind.Object)
            return "";
        var author = reply.Str("author").Trim();
        var text = reply.Str("text").Trim();
        if (string.IsNullOrEmpty(author) && string.IsNullOrEmpty(text)) return "";
        var who = string.IsNullOrEmpty(author) ? "" : author + ": ";
        return "↩ " + who + text;
    }

    /// <summary>Список эмодзи, которые реально стоят на сообщении.</summary>
    private static List<string> ParseReactionList(JsonElement item)
    {
        var list = new List<string>();
        if (!item.TryGetProperty("reactions", out var reactions) || reactions.ValueKind != JsonValueKind.Object)
            return list;
        foreach (var entry in reactions.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Array && entry.Value.GetArrayLength() > 0)
            {
                list.Add(entry.Name);
            }
        }
        return list;
    }

    /// <summary>Палитра для кнопки реакции.</summary>
    private static readonly Dictionary<string, string> ReactionColors = new()
    {
        ["👍"] = "#38BDF8",
        ["❤️"] = "#F472B6",
        ["🔥"] = "#FB923C",
        ["😄"] = "#FACC15",
        ["😢"] = "#60A5FA",
        ["😂"] = "#FDE047",
        ["😮"] = "#A78BFA",
        ["😡"] = "#EF4444",
        ["🎉"] = "#34D399",
        ["💯"] = "#22D3EE",
        ["👌"] = "#4ADE80",
        ["🤔"] = "#C084FC",
        ["😎"] = "#FBBF24",
        ["🥳"] = "#F472B6",
        ["💀"] = "#E5E7EB",
        ["🤝"] = "#2DD4BF",
    };

    private static readonly Brush ReactionBackground = Freeze(new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)));

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>Находит контейнер цветных реакций в ленте и заполняет его.</summary>
    private void RenderReactionsIntoList(ChatMessage message)
    {
        ChatList.UpdateLayout();
        var container = ChatList.ItemContainerGenerator.ContainerFromItem(message) as DependencyObject;
        if (container is null) return;
        var panel = FindChild<WrapPanel>(container, "ReactionChips");
        if (panel is null) return;
        RenderReactions(panel, message);
    }

    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed && typed.Name == name) return typed;
            var nested = FindChild<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>Собирает цветные кнопки уже поставленных реакций.</summary>
    private void RenderReactions(WrapPanel panel, ChatMessage message)
    {
        panel.Children.Clear();
        foreach (var emoji in message.Reactions)
        {
            var color = ReactionColors.TryGetValue(emoji, out var hex)
                ? (Color)ColorConverter.ConvertFromString(hex)
                : Colors.White;
            var button = new Button
            {
                Content = emoji,
                Tag = (message, emoji),
                Margin = new Thickness(0, 4, 6, 0),
                Padding = new Thickness(8, 2, 8, 2),
                Height = 24,
                FontSize = 12,
                Background = ReactionBackground,
                Foreground = Freeze(new SolidColorBrush(color)),
                BorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B))),
                BorderThickness = new Thickness(1),
                ToolTip = "Нажмите, чтобы убрать свою реакцию",
            };
            button.Click += OnChatReactChipClick;
            panel.Children.Add(button);
        }
    }

    private async void OnChatReactChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ValueTuple<ChatMessage, string> tag }) return;
        await PostChatAsync("/message/react", new { id = tag.Item1.Id, emoji = tag.Item2 });
    }

    /// <summary>Выпадающее меню со всеми доступными реакциями.</summary>
    private static readonly string[] ReactionPalette =
    {
        "👍", "❤️", "🔥", "😄", "😢", "😂", "😮", "😡",
        "🎉", "💯", "👌", "🤔", "😎", "🥳", "💀", "🤝",
    };

    private void OnChatReactMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessage message } button) return;
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
        };
        var panel = new WrapPanel { Width = 240 };
        foreach (var emoji in ReactionPalette)
        {
            var color = ReactionColors.TryGetValue(emoji, out var hex)
                ? (Color)ColorConverter.ConvertFromString(hex)
                : Colors.White;
            var chip = new Button
            {
                Content = emoji,
                Margin = new Thickness(2),
                Padding = new Thickness(6, 2, 6, 2),
                Width = 38,
                Height = 32,
                FontSize = 17,
                Background = ReactionBackground,
                BorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B))),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            chip.Click += async (_, _) =>
            {
                menu.IsOpen = false;
                await PostChatAsync("/message/react", new { id = message.Id, emoji });
            };
            panel.Children.Add(chip);
        }
        menu.Items.Add(panel);
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    /// <summary>Ответ на сообщение: автор и начало текста уходят вместе с новым сообщением.</summary>
    private void OnChatReplyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessage message }) return;
        _replyTarget = message;
        ChatReplyAuthor.Text = string.IsNullOrWhiteSpace(message.Author) ? "Ответ" : "Ответ · " + message.Author;
        ChatReplySnippet.Text = string.IsNullOrWhiteSpace(message.Text)
            ? (string.IsNullOrEmpty(message.ImageData) ? "картинка" : "картинка")
            : message.Text;
        ChatReplyBar.Visibility = Visibility.Visible;
        ChatInput.Focus();
    }

    private void OnChatReplyCancelClick(object sender, RoutedEventArgs e)
    {
        _replyTarget = null;
        ChatReplyBar.Visibility = Visibility.Collapsed;
    }

    private async void OnChatDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessage message }) return;
        var answer = MessageBox.Show(this, "Удалить своё сообщение?", "Чат Craftunity",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        await PostChatAsync("/message/delete", new { id = message.Id });
    }

    private async void OnChatEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessage message }) return;
        var dialog = new ChatEditWindow(message.Text) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var body = dialog.MessageText.Trim();
        if (string.IsNullOrWhiteSpace(body) || body == message.Text) return;
        await PostChatAsync("/message/edit", new { id = message.Id, text = body });
    }

    private async void OnChatReactClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessage message, Content: string emoji }) return;
        await PostChatAsync("/message/react", new { id = message.Id, emoji });
    }

    private async Task PostChatAsync(string path, object payload)
    {
        if (_overview is null) return;
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                instance_id = _overview.InstanceId,
                id = GetProp<long>(payload, "id"),
                text = GetProp<string>(payload, "text"),
                emoji = GetProp<string>(payload, "emoji"),
                channel = _chatChannel,
                peer = _chatChannel == "dm" ? _chatPeerId : "",
            });
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await ChatHttp.PostAsync(ChatBaseUrl + path, content);
            if (!response.IsSuccessStatusCode)
            {
                var answer = await response.Content.ReadAsStringAsync();
                ChatState.Text = response.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "Это не ваше сообщение"
                    : "Ошибка: " + answer;
                return;
            }
            ChatState.Text = "Готово";
            await LoadChatAsync(true);
        }
        catch (Exception exc)
        {
            ChatState.Text = ChatErrorText(exc);
        }
    }

    private static T GetProp<T>(object source, string name)
    {
        var prop = source.GetType().GetProperty(name);
        if (prop is null) return default!;
        var value = prop.GetValue(source);
        if (value is null) return default!;
        return value is T typed ? typed : default!;
    }

    private void TrackUnread(ChatMessage message)
    {
        if (message.Mine) return;
        if (Nav.SelectedIndex == 6 && IsActive) return;
        _chatUnread++;
        ChatBadgeText.Text = _chatUnread > 99 ? "99+" : _chatUnread.ToString();
        ChatBadge.Visibility = Visibility.Visible;
        ShowChatToast(message);
        PlayChatSound(message);
    }

    /// <summary>Звук нового сообщения: один на сообщение, не пачкой при загрузке истории.</summary>
    private void PlayChatSound(ChatMessage message)
    {
        if (OptSound.IsChecked != true) return;
        if (message.Id <= _chatLastNotifiedId) return;
        _chatLastNotifiedId = message.Id;
        try
        {
            _chatSound ??= CreateChatSound();
            if (_chatSound is null) return;
            _chatSound.Stop();
            _chatSound.Position = TimeSpan.Zero;
            _chatSound.Play();
        }
        catch
        {
            // звук не критичен
        }
    }

    private static System.Windows.Media.MediaPlayer? CreateChatSound()
    {
        try
        {
            var player = new System.Windows.Media.MediaPlayer();
            var asm = typeof(MainWindow).Assembly;
            using var stream = asm.GetManifestResourceStream("CraftunityLauncher.Resources.Sounds.Message.wav");
            if (stream is null) return null;
            player.Open(new Uri("pack://application:,,,/Resources/Sounds/Message.wav", UriKind.Absolute));
            player.Volume = 0.6;
            return player;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private void ClearChatUnread()
    {
        _chatUnread = 0;
        ChatBadge.Visibility = Visibility.Collapsed;
        ChatToast.Visibility = Visibility.Collapsed;
        _chatToastTimer?.Stop();
    }

    private void ShowChatToast(ChatMessage message)
    {
        ChatToastIcon.Source = AvatarOf(message.IconUrl);
        ChatToastTitle.Text = string.IsNullOrWhiteSpace(message.Author) ? "Новое сообщение" : message.Author;
        ChatToastText.Text = message.Text;
        ChatToastTime.Text = string.IsNullOrWhiteSpace(message.TimeLabel) ? DateTime.Now.ToString("HH:mm") : message.TimeLabel;
        ChatToast.Visibility = Visibility.Visible;
        AnimateToastIn();

        _chatToastTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(6),
        };
        _chatToastTimer.Stop();
        _chatToastTimer.Tick -= HideChatToast;
        _chatToastTimer.Tick += HideChatToast;
        _chatToastTimer.Start();

        var pending = message;
        _ = LoadToastAvatarAsync(pending);
    }

    /// <summary>Голова автора для уведомления: своя — с главной, чужая — через бэкенд.</summary>
    private async Task LoadToastAvatarAsync(ChatMessage message)
    {
        try
        {
            if (message.Mine || IsOwnAuthor(message.Author))
            {
                if (_heroAvatar is not null) ChatToastIcon.Source = _heroAvatar;
                return;
            }
            var avatar = await AvatarForAsync(message);
            if (avatar is not null) ChatToastIcon.Source = avatar;
        }
        catch
        {
            // уведомление не критично
        }
    }

    /// <summary>Плашка выезжает справа снизу и плавно проявляется.</summary>
    private void AnimateToastIn()
    {
        if (OptReduceMotion.IsChecked == true)
        {
            ChatToast.Opacity = 1;
            ChatToast.RenderTransform = new TranslateTransform();
            return;
        }
        var slide = ChatToast.RenderTransform as TranslateTransform ?? new TranslateTransform();
        ChatToast.RenderTransform = slide;
        ChatToast.Opacity = 0;
        slide.Y = 18;
        slide.X = 26;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var rise = new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut },
        };
        var push = new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        ChatToast.BeginAnimation(UIElement.OpacityProperty, fade);
        slide.BeginAnimation(TranslateTransform.YProperty, rise);
        slide.BeginAnimation(TranslateTransform.XProperty, push);
    }

    private void HideChatToast(object? sender, EventArgs e)
    {
        _chatToastTimer?.Stop();
        if (OptReduceMotion.IsChecked == true)
        {
            ChatToast.Visibility = Visibility.Collapsed;
            return;
        }
        var fade = new DoubleAnimation(ChatToast.Opacity, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            ChatToast.Visibility = Visibility.Collapsed;
            ChatToast.Opacity = 1;
        };
        ChatToast.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void OnChatToastClick(object sender, MouseButtonEventArgs e)
    {
        HideChatToast(null, EventArgs.Empty);
        Nav.SelectedIndex = 6;
    }

    private void OnChatToastCloseClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HideChatToast(null, EventArgs.Empty);
    }

    private async Task PollChatAsync()
    {
        if (_overview is null) return;
        try
        {
            using var response = await ChatHttp.GetAsync(ChatQuery(_chatLastId.ToString()));
            if (!response.IsSuccessStatusCode) return;
            var text = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var mine = _overview?.InstanceId ?? "";
            // Опрос идёт в фоновом потоке: к элементам окна обращаемся только через UI-поток.
            var chatOpen = false;
            try
            {
                chatOpen = (bool)Dispatcher.Invoke(() => Nav.SelectedIndex == 6 && IsActive);
            }
            catch
            {
                chatOpen = false;
            }
            foreach (var item in doc.RootElement.Arr("messages"))
            {
                var id = (long)item.Num("id");
                if (id <= _chatLastId) continue;
                _chatLastId = id;
                var senderId = item.Str("instance_id");
                if (!string.IsNullOrEmpty(senderId) && senderId == mine) continue;
                if (chatOpen) continue;
                Dispatcher.Invoke(() =>
                {
                    var incoming = new ChatMessage
                    {
                        Id = id,
                        InstanceId = senderId,
                        Author = item.Str("username", "Гость"),
                        Text = item.Str("text"),
                        IconUrl = item.Str("icon"),
                        AvatarData = item.Str("avatar"),
                        ImageData = item.Str("image"),
                        TimeLabel = FormatChatTime(item.Num("at")),
                    };
                    incoming.Image = DecodeDataUrl(incoming.ImageData);
                    TrackUnread(incoming);
                });
            }
        }
        catch
        {
            // тик пропускаем
        }
    }

    private async Task LoadChatAsync(bool reset)
    {
        try
        {
            var since = reset ? 0 : _chatLastId;
            using var response = await ChatHttp.GetAsync(ChatQuery(since.ToString()));
            if (!response.IsSuccessStatusCode)
            {
                ChatState.Text = "Приёмник недоступен: " + (int)response.StatusCode;
                return;
            }
            var text = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (reset)
            {
                _chat.Clear();
                _chatLastId = 0;
            }
            foreach (var item in root.Arr("messages"))
            {
                var id = (long)item.Num("id");
                if (id <= _chatLastId && !reset) continue;
                var senderId = item.Str("instance_id");
                var newMessage = new ChatMessage
                {
                    Id = id,
                    InstanceId = senderId,
                    Author = item.Str("username", "Гость"),
                    Text = item.Str("text"),
                    IconUrl = item.Str("icon"),
                    AvatarData = item.Str("avatar"),
                    ImageData = item.Str("image"),
                    TimeLabel = FormatChatTime(item.Num("at")),
                    Edited = item.Num("edited_at") > 0,
                    Mine = !string.IsNullOrEmpty(senderId)
                           && senderId == (_overview?.InstanceId ?? ""),
                    ReactionsLabel = FormatReactions(item),
                };
                newMessage.Reactions = ParseReactionList(item);
                newMessage.ReplyToText = FormatReplyTo(item);
                _chat.Add(newMessage);
                LoadMessageImage(newMessage);
                RenderReactionsIntoList(newMessage);
                if (id > _chatLastId) _chatLastId = id;
                if (!reset) TrackUnread(newMessage);
            }
            var count = (long)root.Num("count");
            ChatState.Text = count == 0 ? "Сообщений пока нет" : $"Сообщений: {count}";
            ChatScroll.ScrollToEnd();
            _ = FillChatAvatarsAsync();
        }
        catch (Exception exc)
        {
            ChatState.Text = ChatErrorText(exc);
        }
    }

    private static string FormatChatTime(double seconds)
    {
        if (seconds <= 0) return "";
        var moment = DateTimeOffset.FromUnixTimeSeconds((long)seconds).ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        return moment.Date == today
            ? moment.ToString("HH:mm")
            : moment.ToString("dd.MM HH:mm");
    }

    // ------------------------------------------------------------------ игра

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        if (_overview?.Account is null)
        {
            Nav.SelectedIndex = 5;
            Toast("Сначала войдите в аккаунт", true);
            return;
        }
        try
        {
            PlayButton.IsEnabled = false;
            await _api.PostAsync("/api/launch", new { });
            Toast("Запускаю игру…");
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
            PlayButton.IsEnabled = true;
        }
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        try
        {
            await _api.PostAsync("/api/stop", new { });
            Toast("Игра остановлена");
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async void OnRepairClick(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        try
        {
            await _api.PostAsync("/api/repair", new { });
            Toast("Проверка файлов запущена");
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async void OnRefreshManifestClick(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        try
        {
            await _api.PostAsync("/api/manifest/refresh", new { force = true });
            await RefreshAsync();
            Toast("Манифест обновлён");
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private void OnLogToggleClick(object sender, RoutedEventArgs e)
    {
        var show = LogBox.Visibility != Visibility.Visible;
        LogBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopyServerClick(object sender, RoutedEventArgs e)
    {
        if (_overview is null) return;
        try
        {
            Clipboard.SetText($"{_overview.ServerHost}:{_overview.ServerPort}");
            Toast("Адрес сервера скопирован");
        }
        catch
        {
            // буфер обмена занят другим процессом — не критично
        }
    }

    // ------------------------------------------------------------- установлено

    private async Task ReloadInstalledAsync()
    {
        if (_api is null) return;
        try
        {
            var root = await _api.GetAsync("/api/mods/installed");
            var items = Models.ContentItems(root);
            _installed.Clear();
            var packIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in (_overview?.PackModIds ?? new List<string>())) packIds.Add(mod);

            foreach (var item in items)
            {
                var isMod = string.Equals(item.Kind, "mod", StringComparison.OrdinalIgnoreCase);
                var inPack = isMod && packIds.Contains(item.ProjectId);
                var match = _installedFilter switch
                {
                    "all" => true,
                    "pack" => inPack,
                    _ => string.Equals(item.Kind, _installedFilter, StringComparison.OrdinalIgnoreCase),
                };
                if (!match) continue;
                var locked = inPack;
                _installed.Add(new InstalledCard
                {
                    Title = item.Title,
                    IconUrl = item.Icon,
                    Description = item.Description,
                    KindLabel = KindName(item.Kind),
                    StateLabel = item.Disabled ? "Мод выключен" : "Мод включён",
                    ToggleLabel = item.Disabled ? "Включить" : "Выключить",
                    CanToggle = !locked,
                    CanRemove = !locked && item.Removable,
                    Data = item,
                });
            }
            InstalledHint.Text = $"Всего: {items.Count}"
                                 + (_installedFilter == "all" ? "" : $" · показано: {_installed.Count}");
        }
        catch (Exception exc)
        {
            Toast("Не удалось прочитать контент: " + exc.Message, true);
        }
    }

    private static string KindName(string kind) => kind switch
    {
        "mod" => "Мод",
        "resourcepack" => "Ресурспак",
        "shader" => "Шейдер",
        "modpack" => "Модпак",
        _ => kind,
    };

    private async void OnReloadInstalledClick(object sender, RoutedEventArgs e) => await ReloadInstalledAsync();
    private async void OnFilterAllClick(object sender, RoutedEventArgs e) { _installedFilter = "all"; await ReloadInstalledAsync(); }
    private async void OnFilterModClick(object sender, RoutedEventArgs e) { _installedFilter = "mod"; await ReloadInstalledAsync(); }
    private async void OnFilterPackClick(object sender, RoutedEventArgs e) { _installedFilter = "pack"; await ReloadInstalledAsync(); }
    private async void OnFilterRpClick(object sender, RoutedEventArgs e) { _installedFilter = "resourcepack"; await ReloadInstalledAsync(); }
    private async void OnFilterShaderClick(object sender, RoutedEventArgs e) { _installedFilter = "shader"; await ReloadInstalledAsync(); }

    private async void OnToggleInstalledClick(object sender, RoutedEventArgs e)
    {
        if (_api is null || sender is not Button { Tag: InstalledCard card }) return;
        var item = card.Data;
        try
        {
            await _api.PostAsync("/api/content/toggle", new
            {
                project_id = item.ProjectId,
                kind = item.Kind,
                filename = item.Filename,
                enabled = item.Disabled,
            });
            await ReloadInstalledAsync();
            Toast(card.Title + (item.Disabled ? " включён" : " выключен"));
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async void OnRemoveInstalledClick(object sender, RoutedEventArgs e)
    {
        if (_api is null || sender is not Button { Tag: InstalledCard card }) return;
        var answer = MessageBox.Show(this, $"Удалить «{card.Title}»?", "Craftunity",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        var item = card.Data;
        try
        {
            if (item.Removable && !string.IsNullOrWhiteSpace(item.ProjectId) && !item.ProjectId.StartsWith("file:"))
                await _api.PostAsync("/api/mods/remove", new { project_id = item.ProjectId, force = true });
            else
                await _api.PostAsync("/api/content/remove-file", new { kind = item.Kind, filename = item.Filename });
            await ReloadInstalledAsync();
            Toast("Удалено: " + card.Title);
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    // ----------------------------------------------------------------- поиск

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SearchAsync(false);
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e) => await SearchAsync(false);
    private async void OnSearchMoreClick(object sender, RoutedEventArgs e) => await SearchAsync(true);
    private async void OnKindModClick(object sender, RoutedEventArgs e) => await SwitchKindAsync("mod");
    private async void OnKindRpClick(object sender, RoutedEventArgs e) => await SwitchKindAsync("resourcepack");
    private async void OnKindShaderClick(object sender, RoutedEventArgs e) => await SwitchKindAsync("shader");

    private async Task SwitchKindAsync(string kind)
    {
        _kind = kind;
        var label = Kinds[kind];
        ContentTitle.Text = label.Title;
        ContentHint.Text = label.Hint;
        UploadPackButton.Content = kind == "mod" ? "Добавить .jar" : "Добавить .zip";
        await SearchAsync(false);
    }

    private async Task SearchAsync(bool append)
    {
        if (_api is null) return;
        if (!append)
        {
            _query = ModQuery.Text.Trim();
            _offset = 0;
            _total = 0;
            _cards.Clear();
            ModMore.Visibility = Visibility.Collapsed;
        }
        var sort = (ModSort.SelectedItem as ComboBoxItem)?.Tag as string ?? "relevance";
        try
        {
            ModFilter.Text = "Поиск…";
            var root = await _api.PostAsync("/api/mods/search", new
            {
                query = _query,
                kind = _kind,
                limit = 24,
                offset = _offset,
                sort = string.IsNullOrWhiteSpace(_query) ? "downloads" : sort,
            });
            var hits = Models.SearchHits(root);
            _offset += hits.Count;
            _total = (long)root.Num("total_hits", hits.Count);

            var installedIds = new HashSet<string>(
                _installed.Select(card => card.Data.ProjectId).Where(id => !string.IsNullOrEmpty(id)),
                StringComparer.OrdinalIgnoreCase);
            foreach (var hit in hits)
            {
                var done = installedIds.Contains(hit.ProjectId);
                _cards.Add(new ModCard
                {
                    ProjectId = hit.ProjectId,
                    Slug = hit.Slug,
                    Title = hit.Title,
                    Description = hit.Description,
                    IconUrl = hit.IconUrl,
                    DownloadsLabel = $"{FormatCount(hit.Downloads)} загрузок",
                    SourceLabel = hit.Source == "curseforge" ? "CurseForge" : "Modrinth",
                    AuthorLabel = string.IsNullOrWhiteSpace(hit.Author) ? "" : "автор: " + hit.Author,
                    ActionLabel = done ? "Установлен" : "Установить",
                    CanInstall = !done,
                });
            }
            ModFilter.Text = _cards.Count == 0
                ? "Попробуйте другой запрос"
                : $"Показано {_cards.Count} из {_total} {Kinds[_kind].Many} под текущую версию";
            ModMore.Visibility = _cards.Count < _total ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exc)
        {
            ModFilter.Text = "Ошибка поиска: " + exc.Message;
            Toast(exc.Message, true);
        }
    }

    private static string FormatCount(long value)
    {
        if (value >= 1_000_000) return $"{value / 1_000_000.0:0.#} млн";
        if (value >= 1_000) return $"{value / 1_000.0:0.#} тыс.";
        return value.ToString();
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: ModCard card }) return;
        _ = DescribeProjectAsync(card);
    }

    private void OnProjectCloseClick(object sender, RoutedEventArgs e)
    {
        ProjectPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnProjectInstallClick(object sender, RoutedEventArgs e)
    {
        if (ProjectPanel.DataContext is not ModCard card) return;
        await InstallAsync(card);
        ProjectPanel.Visibility = Visibility.Collapsed;
    }

    private async Task DescribeProjectAsync(ModCard card)
    {
        if (_api is null) return;
        var id = string.IsNullOrWhiteSpace(card.ProjectId) ? card.Slug : card.ProjectId;
        if (string.IsNullOrWhiteSpace(id)) return;
        try
        {
            Toast("Загружаю описание…");
            var root = await _api.GetAsync("/api/project/" + Uri.EscapeDataString(id));
            var project = root.Obj("project");
            var title = project.Str("title", card.Title);
            var description = project.Str("description", card.Description);
            var body = project.Str("body");
            var downloads = project.Num("downloads");
            var likes = project.Num("followers");
            var license = project.Str("license");
            var authors = string.Join(", ", project.Arr("authors")
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.Str("name"))
                .Where(name => !string.IsNullOrWhiteSpace(name)));

            ProjectTitle.Text = title;
            var meta = new List<string>();
            if (downloads > 0) meta.Add("Загрузок: " + FormatCount((long)downloads));
            if (likes > 0) meta.Add("Подписок: " + FormatCount((long)likes));
            if (!string.IsNullOrWhiteSpace(license)) meta.Add("Лицензия: " + license);
            if (!string.IsNullOrWhiteSpace(authors)) meta.Add("Автор: " + authors);
            ProjectMeta.Text = string.Join("   ·   ", meta);

            ProjectIcon.Source = LoadImage(project.Str("icon_hd", card.IconUrl));
            ProjectDescription.Text = BuildDescription(description, body);
            BuildGallery(project);
            ProjectPanel.DataContext = card;
            ProjectPanel.Visibility = Visibility.Visible;
            ProjectScroll.ScrollToTop();
            Toast(title);
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private static string BuildDescription(string summary, string body)
    {
        var text = string.IsNullOrWhiteSpace(body) ? summary : body;
        text = Regex.Replace(text ?? "", "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</p>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text ?? "");
        text = Regex.Replace(text, "[ \\t]+", " ");
        text = Regex.Replace(text, "\\n{3,}", "\n\n");
        text = text.Trim();
        if (string.IsNullOrWhiteSpace(summary)) return string.IsNullOrWhiteSpace(text) ? "Описание не передано." : text;
        if (string.IsNullOrWhiteSpace(text)) return summary;
        if (text.StartsWith(summary, StringComparison.Ordinal)) return text;
        return summary + "\n\n" + text;
    }

    private void BuildGallery(JsonElement project)
    {
        ProjectGallery.Children.Clear();
        var frames = project.Arr("gallery");
        foreach (var shot in frames)
        {
            var url = shot.ValueKind == JsonValueKind.String ? shot.GetString() : shot.Str("url");
            var image = LoadImage(url);
            if (image is null) continue;
            var frame = new Border
            {
                Width = 260,
                Height = 164,
                Margin = new Thickness(0, 0, 10, 0),
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Background = (Brush)FindResource("BgBrush"),
            };
            var picture = new Image { Source = image, Stretch = Stretch.UniformToFill };
            frame.Child = picture;
            ProjectGallery.Children.Add(frame);
        }
        var hasShots = ProjectGallery.Children.Count > 0;
        ProjectGalleryHint.Visibility = hasShots ? Visibility.Visible : Visibility.Collapsed;
        ProjectGalleryScroll.Visibility = hasShots ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string ChatErrorText(Exception exc)
    {
        if (exc is TaskCanceledException || exc is OperationCanceledException)
            return "Чат недоступен: сервер не ответил. Повторю позже.";
        if (exc is HttpRequestException)
            return "Чат недоступен: нет связи с сервером.";
        return "Чат недоступен: " + exc.Message;
    }

    private static readonly Dictionary<string, BitmapSource?> SkinCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient SkinHttp = new(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static ImageSource? LoadImage(string? url)
    {
        var full = string.IsNullOrWhiteSpace(url) ? "" : url;
        if (full.StartsWith("//", StringComparison.Ordinal)) full = "https:" + full;
        if (string.IsNullOrWhiteSpace(full)) return null;
        lock (SkinCache)
        {
            if (SkinCache.TryGetValue(full, out var hit)) return hit;
        }
        try
        {
            var bytes = SkinHttp.GetByteArrayAsync(full).GetAwaiter().GetResult();
            var image = DecodeSkin(bytes);
            lock (SkinCache) SkinCache[full] = image;
            return image;
        }
        catch
        {
            lock (SkinCache) SkinCache[full] = null;
            return null;
        }
    }

    /// <summary>Декодирует PNG/JPEG из байтов; из старых 64×64 скинов берёт голову.</summary>
    private static BitmapSource? DecodeSkin(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void OnProjectPageClick(object sender, RoutedEventArgs e)
    {
        if (ProjectPanel.DataContext is not ModCard card) return;
        var id = string.IsNullOrWhiteSpace(card.ProjectId) ? card.Slug : card.ProjectId;
        if (string.IsNullOrWhiteSpace(id)) return;
        var url = id.StartsWith("cf:", StringComparison.OrdinalIgnoreCase)
            ? "https://www.curseforge.com/minecraft/search?search=" + Uri.EscapeDataString(card.Title)
            : "https://modrinth.com/mod/" + Uri.EscapeDataString(card.Slug ?? id);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async Task OpenProjectAsync(ModCard card)
    {
        if (_api is null) return;
        var id = string.IsNullOrWhiteSpace(card.ProjectId) ? card.Slug : card.ProjectId;
        if (string.IsNullOrWhiteSpace(id)) return;
        try
        {
            Toast("Загружаю описание…");
            var root = await _api.GetAsync("/api/project/" + Uri.EscapeDataString(id));
            var project = root.Obj("project");
            var title = project.Str("title", card.Title);
            var description = project.Str("description", card.Description);
            var downloads = project.Num("downloads");
            var body = string.IsNullOrWhiteSpace(description) ? "Описание не передано." : description;
            var answer = MessageBox.Show(this,
                body + "\n\nЗагрузок: " + FormatCount((long)downloads) + "\n\nУстановить этот проект?",
                title, MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
                await InstallAsync(card, title);
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async Task InstallAsync(ModCard card, string? title = null)
    {
        if (_api is null) return;
        var id = string.IsNullOrWhiteSpace(card.ProjectId) ? card.Slug : card.ProjectId;
        try
        {
            Toast($"Устанавливаю {title ?? card.Title}…");
            var root = await _api.PostAsync("/api/mods/install", new { project_id = id, kind = _kind });
            var warning = root.Str("warning");
            Toast(string.IsNullOrWhiteSpace(warning) ? $"Установлено: {title ?? card.Title}" : warning,
                !string.IsNullOrWhiteSpace(warning));
            await ReloadInstalledAsync();
            card.ActionLabel = "Установлен";
            card.CanInstall = false;
            ModResults.Items.Refresh();
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async void OnInstallCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModCard card }) return;
        await InstallAsync(card);
    }

    private void OnUploadPackClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = _kind == "mod" ? "Моды (*.jar)|*.jar" : "Архивы (*.zip)|*.zip",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        _ = UploadContentFilesAsync(dialog.FileNames);
    }

    private async Task UploadContentFilesAsync(IEnumerable<string> paths)
    {
        if (_api is null) return;
        var count = 0;
        foreach (var path in paths)
        {
            try
            {
                var name = Path.GetFileName(path);
                var bytes = await File.ReadAllBytesAsync(path);
                await _api.PostRawAsync($"/api/content/upload?kind={_kind}&name={Uri.EscapeDataString(name)}", bytes);
                count++;
            }
            catch (Exception exc)
            {
                Toast(exc.Message, true);
            }
        }
        if (count > 0)
        {
            Toast($"Добавлено файлов: {count}");
            await ReloadInstalledAsync();
        }
    }

    // ----------------------------------------------------------------- файлы

    private string CurrentDest => (FileDest.SelectedItem as ComboBoxItem)?.Tag as string ?? "game";

    private async void OnFileDestChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await LoadFilesAsync();
    }

    private async Task LoadFilesAsync()
    {
        if (_api is null) return;
        try
        {
            var dest = CurrentDest;
            var root = await _api.GetAsync($"/api/files/list?dest={Uri.EscapeDataString(dest)}");
            _files.Clear();
            var folder = root.Str("folder");
            FileDestHint.Text = string.IsNullOrWhiteSpace(folder) ? dest : folder;
            foreach (var file in root.Arr("items"))
            {
                _files.Add(new FileRow
                {
                    Name = file.Str("name"),
                    SizeLabel = FormatBytes((long)file.Num("size")),
                    Dest = dest,
                });
            }
            if (_files.Count == 0)
                _files.Add(new FileRow { Name = "Пусто", SizeLabel = "", Dest = dest });
        }
        catch (Exception exc)
        {
            Toast("Не удалось прочитать папку: " + exc.Message, true);
        }
    }

    private static string FormatBytes(long size)
    {
        if (size >= 1024 * 1024) return $"{size / (1024.0 * 1024.0):0.#} МБ";
        if (size >= 1024) return $"{size / 1024.0:0.#} КБ";
        return size + " Б";
    }

    private async void OnFilesUploadClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "Все файлы|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (_api is null) return;
        var dest = CurrentDest;
        var count = 0;
        foreach (var path in dialog.FileNames)
        {
            try
            {
                var name = Path.GetFileName(path);
                var bytes = await File.ReadAllBytesAsync(path);
                await _api.PostRawAsync($"/api/files/upload?dest={dest}&name={Uri.EscapeDataString(name)}", bytes);
                count++;
            }
            catch (Exception exc)
            {
                Toast(exc.Message, true);
            }
        }
        if (count > 0) Toast($"Загружено файлов: {count}");
        await LoadFilesAsync();
    }

    // ------------------------------------------------------------------ скин

    private async Task LoadSkinAsync()
    {
        if (_api is null) return;
        try
        {
            var root = await _api.GetAsync("/api/skin/current");
            var local = root.Obj("local");
            var dataUrl = local.Str("data_url");
            if (string.IsNullOrWhiteSpace(dataUrl))
            {
                SkinHint.Text = "Скин ещё не загружен.";
                AvatarImage.Source = null;
                return;
            }
            var decoded = DecodeDataUrl(dataUrl);
            SkinImage.Source = decoded;
            var head = CropHead(decoded);
            AvatarImage.Source = head;
            _heroAvatar = head;
            if (head is not null) RefreshChatAvatars();
            SkinHint.Text = "Локальный скин применён.";
        }
        catch
        {
            // скин не критичен для работы
        }
    }

    /// <summary>Из ссылки на скин ely.by вырезает только голову — иначе в квадрате 36 px видно весь скин.</summary>
    private static ImageSource? AvatarOf(string? skinUrl)
    {
        var name = ElyNameOf(skinUrl);
        if (!string.IsNullOrEmpty(name))
        {
            lock (SkinCache)
            {
                if (SkinCache.TryGetValue("name:" + name, out var cached)) return cached;
            }
        }
        var image = LoadImage(skinUrl);
        if (image is BitmapSource source)
        {
            var head = CropHead(source) ?? source;
            if (!string.IsNullOrEmpty(name))
            {
                lock (SkinCache) SkinCache["name:" + name] = head;
            }
            return head;
        }
        return image;
    }

    /// <summary>Голова владельца сообщения: своя — с главной, чужая — по data-url из его лаунчера.</summary>
    private async Task<ImageSource?> AvatarForAsync(ChatMessage message)
    {
        if (message.Mine || IsOwnAuthor(message.Author)) return _heroAvatar;

        await RefreshClientIconsAsync();

        var registryIcon = IconFromRegistry(message.Author);
        if (!string.IsNullOrEmpty(registryIcon))
        {
            var fromRegistry = SkinFor(message.Author, registryIcon);
            if (fromRegistry is not null) return fromRegistry;
        }

        if (!string.IsNullOrEmpty(message.AvatarData))
        {
            var decoded = DecodeDataUrl(message.AvatarData);
            if (decoded is not null) return CropHead(decoded) ?? decoded;
        }

        var name = ElyNameOf(message.IconUrl);
        if (string.IsNullOrEmpty(name)) name = message.Author ?? "";
        if (string.IsNullOrEmpty(name) || _api is null) return null;
        try
        {
            var root = await _api.GetAsync("/api/skin/by-name?name=" + Uri.EscapeDataString(name));
            var dataUrl = root.Str("data_url");
            if (string.IsNullOrWhiteSpace(dataUrl)) return null;
            var decoded = DecodeDataUrl(dataUrl);
            return decoded is null ? null : CropHead(decoded) ?? decoded;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Голова по адресу скина с кэшем; null — если скин не скачался.</summary>
    private static ImageSource? SkinFor(string key, string skinUrl)
    {
        var cacheKey = "reg:" + key;
        lock (SkinCache)
        {
            if (SkinCache.TryGetValue(cacheKey, out var cached)) return cached;
        }
        var image = LoadImage(skinUrl);
        BitmapSource? head = image is BitmapSource source ? CropHead(source) ?? source : null;
        lock (SkinCache) SkinCache[cacheKey] = head;
        return head;
    }

    /// <summary>Догружает головы авторов для видимых сообщений и список личных.</summary>
    private async Task FillChatAvatarsAsync()
    {
        foreach (var message in _chat.ToList())
        {
            if (message.Avatar is not null) continue;
            if (message.Mine && _heroAvatar is null) continue;
            var avatar = await AvatarForAsync(message);
            if (avatar is null) continue;
            message.Avatar = avatar;
            ChatList.Items.Refresh();
        }
    }

    /// <summary>Головы игроков в списке личных сообщений.</summary>
    private async Task FillPeerHeadsAsync()
    {
        foreach (var peer in _clientPeers.ToList())
        {
            if (peer.Icon is not null) continue;
            ImageSource? head = null;
            try
            {
                if (IsOwnAuthor(peer.Name) && _heroAvatar is not null)
                {
                    head = _heroAvatar;
                }
                else
                {
                    var iconUrl = IconFromRegistry(peer.Name);
                    if (!string.IsNullOrEmpty(iconUrl)) head = SkinFor(peer.Name, iconUrl);
                    if (head is null && _api is not null)
                    {
                        var root = await _api.GetAsync("/api/skin/by-name?name=" + Uri.EscapeDataString(peer.Name));
                        var dataUrl = root.Str("data_url");
                        if (!string.IsNullOrWhiteSpace(dataUrl))
                        {
                            var decoded = DecodeDataUrl(dataUrl);
                            head = decoded is null ? null : CropHead(decoded) ?? decoded;
                        }
                    }
                }
            }
            catch
            {
                // голова не обязательна
            }
            if (head is not null) peer.Icon = head;
        }
    }

    /// <summary>Подставляет аватар с главной страницы в уже загруженные сообщения чата.</summary>
    private void RefreshChatAvatars()
    {
        if (_heroAvatar is null || _chat.Count == 0) return;
        var changed = false;
        foreach (var message in _chat)
        {
            if (message.Avatar is not null) continue;
            if (!message.Mine && !IsOwnAuthor(message.Author)) continue;
            message.Avatar = _heroAvatar;
            changed = true;
        }
        if (changed) ChatList.Items.Refresh();
    }

    /// <summary>Совпадает ли автор сообщения с активным аккаунтом.</summary>
    private bool IsOwnAuthor(string? author)
    {
        var mine = _overview?.Account?.Username ?? "";
        return !string.IsNullOrEmpty(mine)
               && !string.IsNullOrEmpty(author)
               && string.Equals(mine, author, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Достаёт ник из адреса скина ely.by, чтобы кэшировать аватар по нику.</summary>
    private static string ElyNameOf(string? skinUrl)
    {
        if (string.IsNullOrWhiteSpace(skinUrl)) return "";
        var marker = "/skins/";
        var index = skinUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return "";
        var name = skinUrl[(index + marker.Length)..];
        var cut = name.IndexOfAny(new[] { '?', '/' });
        if (cut >= 0) name = name[..cut];
        return name.Trim();
    }

    /// <summary>Вырезает из скина 64×64 только голову вместе с волосами, а не весь скин.</summary>
    private static BitmapSource? CropHead(BitmapSource? skin)
    {
        if (skin is null) return null;
        try
        {
            var width = skin.PixelWidth;
            var height = skin.PixelHeight;
            if (width < 32 || height < 32) return skin;
            var baseSize = width >= 64 ? 64 : 32;
            var scale = Math.Max(1, width / baseSize);
            var size = width >= 64 ? 8 : 4;
            var x = 8 * scale;
            var y = 8 * scale;
            var w = size * scale;
            if (x + w > width || y + w > height) return skin;
            var head = new CroppedBitmap(skin, new Int32Rect(x, y, w, w));
            var output = new DrawingVisual();
            using (var ctx = output.RenderOpen())
            {
                ctx.DrawImage(head, new Rect(0, 0, 24, 24));
            }
            var result = new RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32);
            result.Render(output);
            result.Freeze();
            return result;
        }
        catch
        {
            return skin;
        }
    }

    private static BitmapImage? DecodeDataUrl(string dataUrl)
    {
        try
        {
            var comma = dataUrl.IndexOf(',');
            if (comma < 0) return null;
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void OnSkinPickClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PNG (*.png)|*.png" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _pendingSkin = File.ReadAllBytes(dialog.FileName);
            SkinImage.Source = DecodeDataUrl("data:image/png;base64," + Convert.ToBase64String(_pendingSkin));
            SkinHint.Text = "Файл выбран. Нажмите «Применить локально» или «Скин на ely.by».";
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async void OnSkinApplyClick(object sender, RoutedEventArgs e) => await UploadSkinAsync("/api/skin/upload");
    private async void OnSkinElyClick(object sender, RoutedEventArgs e) => await UploadSkinAsync("/api/skin/ely");

    private async Task UploadSkinAsync(string endpoint)
    {
        if (_api is null) return;
        if (_pendingSkin is null)
        {
            Toast("Сначала выберите PNG-файл", true);
            return;
        }
        var model = (SkinModel.SelectedItem as ComboBoxItem)?.Tag as string ?? "classic";
        try
        {
            await _api.PostRawAsync(endpoint, _pendingSkin, ("X-Skin-Model", model));
            _pendingSkin = null;
            Toast(endpoint.EndsWith("ely") ? "Скин загружен на ely.by" : "Скин применён локально");
            await LoadSkinAsync();
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        try
        {
            await _api.PostAsync("/api/open", new { target = "data" });
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    // ---------------------------------------------------------------- аккаунт

    private async void OnOfflineLoginClick(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        try
        {
            await _api.PostAsync("/api/account/offline", new { username = OfflineName.Text.Trim() });
            await RefreshAsync();
            Toast("Вход выполнен");
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async void OnElyLoginClick(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        try
        {
            await _api.PostAsync("/api/account/ely", new
            {
                username = ElyLogin.Text.Trim(),
                password = ElyPassword.Password,
            });
            ElyPassword.Clear();
            await RefreshAsync();
            Toast("Вход через ely.by выполнен");
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private void OnElyRegisterClick(object sender, RoutedEventArgs e) => _ = OpenUrlAsync("https://account.ely.by/register");

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        try
        {
            await _api.PostAsync("/api/account/logout", new { });
            await RefreshAsync();
            Toast("Аккаунт отключён");
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async Task OpenUrlAsync(string url)
    {
        if (_api is null) return;
        try
        {
            await _api.PostAsync("/api/open-url", new { url });
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    // -------------------------------------------------------------- настройки

    private async void OnThemeTileClick(object sender, RoutedEventArgs e)
    {
        if (_api is null || sender is not Button { Tag: string theme }) return;
        try
        {
            await _api.PostAsync("/api/settings", new { theme });
            ApplyTheme(theme);
            await RefreshAsync();
            Toast("Тема: " + theme);
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        var next = Themes.NextAfter(_overview?.Theme ?? "dark");
        if (_api is null) return;
        try
        {
            await _api.PostAsync("/api/settings", new { theme = next });
            ApplyTheme(next);
            await RefreshAsync();
            Toast("Тема: " + next);
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        var memory = int.TryParse(SetMemory.Text.Trim(), out var parsed) ? parsed : 4096;
        try
        {
            await _api.PostAsync("/api/settings", new
            {
                memory_mb = memory,
                java_path = SetJava.Text.Trim(),
                reduce_motion = OptReduceMotion.IsChecked == true,
                auto_java = OptAutoJava.IsChecked == true,
                auto_join = OptAutoJoin.IsChecked == true,
                holiday_theme = OptHoliday.IsChecked == true,
                chat_sound = OptSound.IsChecked == true,
                curseforge_key = CurseforgeKey.Text.Trim(),
            });
            await RefreshAsync();
            Toast("Настройки сохранены");
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async void OnCurseforgeSaveClick(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        try
        {
            var root = await _api.PostAsync("/api/curseforge/check", new { key = CurseforgeKey.Text.Trim() });
            Toast(root.Bool("ok") ? "Ключ CurseForge принят" : "Ключ не подошёл", !root.Bool("ok"));
            await RefreshAsync();
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private void OnCurseforgeHelpClick(object sender, RoutedEventArgs e) => _ = OpenUrlAsync("https://console.curseforge.com");

    private async void OnOpenGameDirClick(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        try
        {
            await _api.PostAsync("/api/open", new { target = "game" });
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        try
        {
            await _api.PostAsync("/api/open", new { target = "logs" });
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }
}
