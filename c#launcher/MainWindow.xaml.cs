using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
        LoadWatched();
        LoadChatSeen();
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

        // Плашка на главной: напоминает об обновлении, не мешая работе.
        _pendingRelease = release;
        UpdateBannerText.Text = $"Доступна версия {release.Version} (у вас {current})";
        UpdateBanner.Visibility = Visibility.Visible;
        return;
        string? installer = null;
        try
        {
            StatusText.Text = "Скачивание обновления…";
            installer = await LauncherUpdate.DownloadAsync(release, p =>
                Dispatcher.InvokeAsync(() => StatusText.Text = $"Скачивание обновления… {p:P0}"));
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
                    await Dispatcher.InvokeAsync(() => ApplyState(state));
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
                if (_chatTick % 20 == 0 && _overview is not null)
                {
                    try
                    {
                        await PollAdsAsync();
                    }
                    catch
                    {
                        // объявления не критичны для работы лаунчера
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

        SyncAccountPanels();
        if (overview.Account is not null && !_profileLoading)
        {
            var logged = overview.Account.Username;
            if (!string.Equals(_profileShown, logged, StringComparison.OrdinalIgnoreCase))
            {
                _profileShown = logged;
                _ = ShowProfileAsync(logged);
            }
        }
        else if (overview.Account is null)
        {
            _profileShown = "";
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

    // ------------------------------------------------------------ Кнопки окна

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        SyncMaximizeGlyph();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Иконка кнопки меняется на «восстановить», когда окно развёрнуто.</summary>
    private void SyncMaximizeGlyph()
    {
        if (MaximizeGlyph is null) return;
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    /// <summary>Правый клик по «Развернуть» открывает меню положения окна.</summary>
    private void OnMaximizeRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (WindowPlaceMenu is null) return;
        WindowPlaceMenu.PlacementTarget = MaximizeButton;
        WindowPlaceMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        WindowPlaceMenu.IsOpen = true;
    }

    /// <summary>Двигает окно к краю экрана или в центр, размер не меняет.</summary>
    private void OnSnapClick(object sender, RoutedEventArgs e)
    {
        var where = sender switch
        {
            MenuItem { Tag: string m } => m,
            Border { Tag: string b } => b,
            _ => "",
        };
        if (where.Length == 0) return;
        WindowPlaceMenu.IsOpen = false;

        if (where == "maximize")
        {
            WindowState = WindowState.Maximized;
            SyncMaximizeGlyph();
            return;
        }

        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;

        var area = SystemParameters.WorkArea;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        double left, top;
        switch (where)
        {
            case "left":
                left = area.Left;
                top = area.Top + (area.Height - height) / 2;
                break;
            case "right":
                left = area.Right - width;
                top = area.Top + (area.Height - height) / 2;
                break;
            case "top":
                left = area.Left + (area.Width - width) / 2;
                top = area.Top;
                break;
            case "bottom":
                left = area.Left + (area.Width - width) / 2;
                top = area.Bottom - height;
                break;
            default:
                left = area.Left + (area.Width - width) / 2;
                top = area.Top + (area.Height - height) / 2;
                break;
        }

        Left = left;
        Top = top;
        SyncMaximizeGlyph();
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
            ("Объявления", "Купля-продажа предметов между игроками"),
            ("Новости", "Объявления сервера: публикует только Danipro914"),
            ("Настройки", "Вид, игра, каталоги, манифест"),
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
        PageAds.Visibility = index == 7 ? Visibility.Visible : Visibility.Collapsed;
        PageNews.Visibility = index == 8 ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = index == 9 ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.VerticalScrollBarVisibility = index is 6
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
                7 => PageAds,
                8 => PageNews,
                9 => PageSettings,
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
            case 7:
                _ = LoadAdsAsync();
                break;
            case 8:
                _ = LoadNewsAsync();
                break;
            case 6:
                MarkChatSeen();
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

    // ------------------------------------------------------------------ объявления

    /// <summary>Все предметы Minecraft: русское название и id для иконки.</summary>
    private static readonly (string Name, string Id)[] AdItems =
    {
        ("Азалия", "azalea"), ("Акациевая грузовая лодка", "acacia_chest_boat"),
        ("Акациевая дверь", "acacia_door"), ("Акациевая калитка", "acacia_fence_gate"),
        ("Акациевая кнопка", "acacia_button"), ("Акациевая лодка", "acacia_boat"),
        ("Акациевая нажимная плита", "acacia_pressure_plate"), ("Акациевая плита", "acacia_slab"),
        ("Акациевая подвесная табличка", "acacia_hanging_sign"), ("Акациевая табличка", "acacia_sign"),
        ("Акациевое бревно", "acacia_log"), ("Акациевые доски", "acacia_planks"),
        ("Акациевые ступеньки", "acacia_stairs"), ("Акациевый забор", "acacia_fence"),
        ("Акациевый люк", "acacia_trapdoor"), ("Акация", "acacia_wood"),
        ("Аксолотль в ведре", "axolotl_bucket"), ("Активирующие рельсы", "activator_rail"),
        ("Алмаз", "diamond"), ("Алмазная кирка", "diamond_pickaxe"),
        ("Алмазная конская броня", "diamond_horse_armor"), ("Алмазная лопата", "diamond_shovel"),
        ("Алмазная мотыга", "diamond_hoe"), ("Алмазная руда", "diamond_ore"),
        ("Алмазные ботинки", "diamond_boots"), ("Алмазные поножи", "diamond_leggings"),
        ("Алмазный блок", "diamond_block"), ("Алмазный меч", "diamond_sword"),
        ("Алмазный нагрудник", "diamond_chestplate"), ("Алмазный топор", "diamond_axe"),
        ("Алмазный шлем", "diamond_helmet"),
        ("Алмазоносный глубинный сланец", "deepslate_diamond_ore"),
        ("Аметистовая друза", "amethyst_cluster"), ("Аметистовый блок", "amethyst_block"),
        ("Андезит", "andesite"), ("Андезитовая ограда", "andesite_wall"),
        ("Андезитовая плита", "andesite_slab"), ("Андезитовые ступеньки", "andesite_stairs"),
        ("Арбалет", "crossbow"), ("Арбуз", "melon"), ("Багровая дверь", "crimson_door"),
        ("Багровая калитка", "crimson_fence_gate"), ("Багровая кнопка", "crimson_button"),
        ("Багровая нажимная плита", "crimson_pressure_plate"), ("Багровая плита", "crimson_slab"),
        ("Багровая подвесная табличка", "crimson_hanging_sign"), ("Багровая табличка", "crimson_sign"),
        ("Багровые гифы", "crimson_hyphae"), ("Багровые доски", "crimson_planks"),
        ("Багровые корни", "crimson_roots"), ("Багровые ступеньки", "crimson_stairs"),
        ("Багровый гриб", "crimson_fungus"), ("Багровый забор", "crimson_fence"),
        ("Багровый люк", "crimson_trapdoor"), ("Багровый нилий", "crimson_nylium"),
        ("Багровый стебель", "crimson_stem"), ("Базальт", "basalt"), ("Бамбук", "bamboo"),
        ("Бамбуковая дверь", "bamboo_door"), ("Бамбуковая калитка", "bamboo_fence_gate"),
        ("Бамбуковая кнопка", "bamboo_button"), ("Бамбуковая мозаика", "bamboo_mosaic"),
        ("Бамбуковая нажимная плита", "bamboo_pressure_plate"), ("Бамбуковая плита", "bamboo_slab"),
        ("Бамбуковая подвесная табличка", "bamboo_hanging_sign"),
        ("Бамбуковая табличка", "bamboo_sign"), ("Бамбуковые доски", "bamboo_planks"),
        ("Бамбуковые ступеньки", "bamboo_stairs"), ("Бамбуковый забор", "bamboo_fence"),
        ("Бамбуковый люк", "bamboo_trapdoor"), ("Бамбуковый плот", "bamboo_raft"),
        ("Барьер", "barrier"), ("Бедрок", "bedrock"),
        ("Белая глазурованная керамика", "white_glazed_terracotta"),
        ("Белая керамика", "white_terracotta"), ("Белая кровать", "white_bed"),
        ("Белая свеча", "white_candle"), ("Белая стеклянная панель", "white_stained_glass_pane"),
        ("Белая шерсть", "white_wool"), ("Белое стекло", "white_stained_glass"),
        ("Белый бетон", "white_concrete"), ("Белый ковёр", "white_carpet"),
        ("Белый краситель", "white_dye"), ("Белый мешок", "white_bundle"),
        ("Белый сухой бетон", "white_concrete_powder"), ("Белый тюльпан", "white_tulip"),
        ("Белый флаг", "white_banner"), ("Белый шалкеровый ящик", "white_shulker_box"),
        ("Берёза", "birch_wood"), ("Берёзовая грузовая лодка", "birch_chest_boat"),
        ("Берёзовая дверь", "birch_door"), ("Берёзовая калитка", "birch_fence_gate"),
        ("Берёзовая кнопка", "birch_button"), ("Берёзовая лодка", "birch_boat"),
        ("Берёзовая нажимная плита", "birch_pressure_plate"), ("Берёзовая плита", "birch_slab"),
        ("Берёзовая подвесная табличка", "birch_hanging_sign"), ("Берёзовая табличка", "birch_sign"),
        ("Берёзовое бревно", "birch_log"), ("Берёзовые доски", "birch_planks"),
        ("Берёзовые листья", "birch_leaves"), ("Берёзовые ступеньки", "birch_stairs"),
        ("Берёзовый забор", "birch_fence"), ("Берёзовый люк", "birch_trapdoor"), ("Бирка", "name_tag"),
        ("Бирюзовая глазурованная керамика", "cyan_glazed_terracotta"),
        ("Бирюзовая керамика", "cyan_terracotta"), ("Бирюзовая кровать", "cyan_bed"),
        ("Бирюзовая свеча", "cyan_candle"), ("Бирюзовая стеклянная панель", "cyan_stained_glass_pane"),
        ("Бирюзовая шерсть", "cyan_wool"), ("Бирюзовое стекло", "cyan_stained_glass"),
        ("Бирюзовый бетон", "cyan_concrete"), ("Бирюзовый ковёр", "cyan_carpet"),
        ("Бирюзовый краситель", "cyan_dye"), ("Бирюзовый мешок", "cyan_bundle"),
        ("Бирюзовый сухой бетон", "cyan_concrete_powder"), ("Бирюзовый флаг", "cyan_banner"),
        ("Бирюзовый шалкеровый ящик", "cyan_shulker_box"), ("Бледный дуб", "pale_oak_wood"),
        ("Бледный моховой ковёр", "pale_moss_carpet"), ("Блок бамбука", "bamboo_block"),
        ("Блок бледного мха", "pale_moss_block"), ("Блок искажённого нароста", "warped_wart_block"),
        ("Блок коралла-мозговика", "brain_coral_block"),
        ("Блок коричневого гриба", "brown_mushroom_block"),
        ("Блок красного гриба", "red_mushroom_block"), ("Блок мха", "moss_block"),
        ("Блок мёда", "honey_block"), ("Блок мёртвого коралла-мозговика", "dead_brain_coral_block"),
        ("Блок мёртвого огненного коралла", "dead_fire_coral_block"),
        ("Блок мёртвого пузырчатого коралла", "dead_bubble_coral_block"),
        ("Блок мёртвого рогового коралла", "dead_horn_coral_block"),
        ("Блок мёртвого трубчатого коралла", "dead_tube_coral_block"),
        ("Блок незерского нароста", "nether_wart_block"),
        ("Блок обтёсанного бамбука", "stripped_bamboo_block"),
        ("Блок огненного коралла", "fire_coral_block"),
        ("Блок пузырчатого коралла", "bubble_coral_block"), ("Блок пчелиных сот", "honeycomb_block"),
        ("Блок рогового коралла", "horn_coral_block"), ("Блок рудного железа", "raw_iron_block"),
        ("Блок рудного золота", "raw_gold_block"), ("Блок рудной меди", "raw_copper_block"),
        ("Блок слизи", "slime_block"), ("Блок снега", "snow_block"),
        ("Блок сушёной ламинарии", "dried_kelp_block"),
        ("Блок трубчатого коралла", "tube_coral_block"), ("Блок-конструктор", "structure_block"),
        ("Большая бросянка", "big_dripleaf"),
        ("Большегрузная весовая пластина", "heavy_weighted_pressure_plate"),
        ("Большой аметистовый бутон", "large_amethyst_bud"), ("Бочка", "barrel"),
        ("Бревно бледного дуба", "pale_oak_log"), ("Бревно тропического дерева", "jungle_log"),
        ("Бревно тёмного дуба", "dark_oak_log"), ("Булава", "mace"),
        ("Булыжная ограда", "cobblestone_wall"), ("Булыжная плита", "cobblestone_slab"),
        ("Булыжник", "cobblestone"), ("Булыжные ступеньки", "cobblestone_stairs"), ("Бумага", "paper"),
        ("Бутылочка", "glass_bottle"), ("Бутылочка мёда", "honey_bottle"), ("Вагонетка", "minecart"),
        ("Вагонетка с динамитом", "tnt_minecart"),
        ("Вагонетка с командным блоком", "command_block_minecart"), ("Ведро", "bucket"),
        ("Ведро воды", "water_bucket"), ("Ведро лавы", "lava_bucket"), ("Ведро молока", "milk_bucket"),
        ("Ведро с рыхлым снегом", "powder_snow_bucket"),
        ("Веерный коралл-мозговик", "brain_coral_fan"), ("Веерный огненный коралл", "fire_coral_fan"),
        ("Веерный пузырчатый коралл", "bubble_coral_fan"),
        ("Веерный роговый коралл", "horn_coral_fan"), ("Веерный трубчатый коралл", "tube_coral_fan"),
        ("Верстак", "crafting_table"), ("Весовая пластина", "light_weighted_pressure_plate"),
        ("Вихревой стержень", "breeze_rod"), ("Вишня", "cherry_wood"),
        ("Вишнёвая грузовая лодка", "cherry_chest_boat"), ("Вишнёвая дверь", "cherry_door"),
        ("Вишнёвая калитка", "cherry_fence_gate"), ("Вишнёвая кнопка", "cherry_button"),
        ("Вишнёвая лодка", "cherry_boat"), ("Вишнёвая нажимная плита", "cherry_pressure_plate"),
        ("Вишнёвая плита", "cherry_slab"), ("Вишнёвая подвесная табличка", "cherry_hanging_sign"),
        ("Вишнёвая табличка", "cherry_sign"), ("Вишнёвое бревно", "cherry_log"),
        ("Вишнёвые доски", "cherry_planks"), ("Вишнёвые листья", "cherry_leaves"),
        ("Вишнёвые ступеньки", "cherry_stairs"), ("Вишнёвый забор", "cherry_fence"),
        ("Вишнёвый люк", "cherry_trapdoor"), ("Возвратный компас", "recovery_compass"),
        ("Волчья броня", "wolf_armor"), ("Воронка", "hopper"),
        ("Вощёная медная дверь", "waxed_copper_door"), ("Вощёная медная лампа", "waxed_copper_bulb"),
        ("Вощёная медная решётка", "waxed_copper_grate"),
        ("Вощёная окисленная медная дверь", "waxed_oxidized_copper_door"),
        ("Вощёная окисленная медная лампа", "waxed_oxidized_copper_bulb"),
        ("Вощёная окисленная медная решётка", "waxed_oxidized_copper_grate"),
        ("Вощёная окисленная резная медная плита", "waxed_oxidized_cut_copper_slab"),
        ("Вощёная потемневшая медная дверь", "waxed_exposed_copper_door"),
        ("Вощёная потемневшая медная лампа", "waxed_exposed_copper_bulb"),
        ("Вощёная потемневшая медная решётка", "waxed_exposed_copper_grate"),
        ("Вощёная потемневшая резная медная плита", "waxed_exposed_cut_copper_slab"),
        ("Вощёная резная медная плита", "waxed_cut_copper_slab"),
        ("Вощёная состаренная медная дверь", "waxed_weathered_copper_door"),
        ("Вощёная состаренная медная лампа", "waxed_weathered_copper_bulb"),
        ("Вощёная состаренная медная решётка", "waxed_weathered_copper_grate"),
        ("Вощёная состаренная резная медная плита", "waxed_weathered_cut_copper_slab"),
        ("Вощёные окисленные резные медные ступеньки", "waxed_oxidized_cut_copper_stairs"),
        ("Вощёные потемневшие резные медные ступеньки", "waxed_exposed_cut_copper_stairs"),
        ("Вощёные резные медные ступеньки", "waxed_cut_copper_stairs"),
        ("Вощёные состаренные резные медные ступеньки", "waxed_weathered_cut_copper_stairs"),
        ("Вощёный медный блок", "waxed_copper_block"), ("Вощёный медный люк", "waxed_copper_trapdoor"),
        ("Вощёный окисленный медный блок", "waxed_oxidized_copper"),
        ("Вощёный окисленный медный люк", "waxed_oxidized_copper_trapdoor"),
        ("Вощёный окисленный резной медный блок", "waxed_oxidized_cut_copper"),
        ("Вощёный окисленный точёный медный блок", "waxed_oxidized_chiseled_copper"),
        ("Вощёный потемневший медный блок", "waxed_exposed_copper"),
        ("Вощёный потемневший медный люк", "waxed_exposed_copper_trapdoor"),
        ("Вощёный потемневший резной медный блок", "waxed_exposed_cut_copper"),
        ("Вощёный потемневший точёный медный блок", "waxed_exposed_chiseled_copper"),
        ("Вощёный резной медный блок", "waxed_cut_copper"),
        ("Вощёный состаренный медный блок", "waxed_weathered_copper"),
        ("Вощёный состаренный медный люк", "waxed_weathered_copper_trapdoor"),
        ("Вощёный состаренный резной медный блок", "waxed_weathered_cut_copper"),
        ("Вощёный состаренный точёный медный блок", "waxed_weathered_chiseled_copper"),
        ("Вощёный точёный медный блок", "waxed_chiseled_copper"), ("Выбрасыватель", "dropper"),
        ("Вырезанная тыква", "carved_pumpkin"), ("Высокая трава", "tall_grass"),
        ("Вьющаяся лоза", "twisting_vines"), ("Гладкий базальт", "smooth_basalt"),
        ("Гладкий камень", "smooth_stone"), ("Гладкий кварц", "smooth_quartz"),
        ("Гладкий красный песчаник", "smooth_red_sandstone"), ("Гладкий песчаник", "smooth_sandstone"),
        ("Глина", "clay"), ("Глиняный черепок «Вихрь»", "guster_pottery_sherd"),
        ("Глиняный черепок «Вой»", "howl_pottery_sherd"),
        ("Глиняный черепок «Друг»", "friend_pottery_sherd"),
        ("Глиняный черепок «Зельевар»", "brewer_pottery_sherd"),
        ("Глиняный черепок «Изобилие»", "plenty_pottery_sherd"),
        ("Глиняный черепок «Исследователь»", "explorer_pottery_sherd"),
        ("Глиняный черепок «Клинок»", "blade_pottery_sherd"),
        ("Глиняный черепок «Лучник»", "archer_pottery_sherd"),
        ("Глиняный черепок «Награда»", "prize_pottery_sherd"),
        ("Глиняный черепок «Обтёсывание»", "scrape_pottery_sherd"),
        ("Глиняный черепок «Пламя»", "burn_pottery_sherd"),
        ("Глиняный черепок «Поток»", "flow_pottery_sherd"),
        ("Глиняный черепок «Разбитое сердце»", "heartbreak_pottery_sherd"),
        ("Глиняный черепок «Руки вверх»", "arms_up_pottery_sherd"),
        ("Глиняный черепок «Рыбак»", "angler_pottery_sherd"),
        ("Глиняный черепок «Сердце»", "heart_pottery_sherd"),
        ("Глиняный черепок «Скорбь»", "mourner_pottery_sherd"),
        ("Глиняный черепок «Сноп»", "sheaf_pottery_sherd"),
        ("Глиняный черепок «Угроза»", "danger_pottery_sherd"),
        ("Глиняный черепок «Укрытие»", "shelter_pottery_sherd"),
        ("Глиняный черепок «Череп»", "skull_pottery_sherd"),
        ("Глиняный черепок «Шахтёр»", "miner_pottery_sherd"),
        ("Глиняный черепок «Шмыг»", "snort_pottery_sherd"),
        ("Глубинносланцевые кирпичи", "deepslate_bricks"),
        ("Глубинносланцевый плитняк", "deepslate_tiles"), ("Глубинный сланец", "deepslate"),
        ("Гнилая плоть", "rotten_flesh"), ("Голова дракона", "dragon_head"),
        ("Голова зомби", "zombie_head"), ("Голова игрока", "player_head"),
        ("Голова крипера", "creeper_head"), ("Голова пиглина", "piglin_head"),
        ("Головастик в ведре", "tadpole_bucket"),
        ("Голубая глазурованная керамика", "light_blue_glazed_terracotta"),
        ("Голубая керамика", "light_blue_terracotta"), ("Голубая кровать", "light_blue_bed"),
        ("Голубая свеча", "light_blue_candle"),
        ("Голубая стеклянная панель", "light_blue_stained_glass_pane"),
        ("Голубая шерсть", "light_blue_wool"), ("Голубое стекло", "light_blue_stained_glass"),
        ("Голубой бетон", "light_blue_concrete"), ("Голубой ковёр", "light_blue_carpet"),
        ("Голубой краситель", "light_blue_dye"), ("Голубой мешок", "light_blue_bundle"),
        ("Голубой сухой бетон", "light_blue_concrete_powder"), ("Голубой флаг", "light_blue_banner"),
        ("Голубой шалкеровый ящик", "light_blue_shulker_box"), ("Гравий", "gravel"),
        ("Гранит", "granite"), ("Гранитная ограда", "granite_wall"),
        ("Гранитная плита", "granite_slab"), ("Гранитные ступеньки", "granite_stairs"),
        ("Грибосвет", "shroomlight"), ("Громоотвод", "lightning_rod"),
        ("Грузовая вагонетка", "chest_minecart"),
        ("Грузовая лодка из бледного дуба", "pale_oak_chest_boat"),
        ("Грузовая лодка из тропического дерева", "jungle_chest_boat"),
        ("Грузовая лодка из тёмного дуба", "dark_oak_chest_boat"),
        ("Грузовой бамбуковый плот", "bamboo_chest_raft"),
        ("Грязные мангровые корни", "muddy_mangrove_roots"), ("Грязь", "mud"), ("Губка", "sponge"),
        ("Датчик дневного света", "daylight_detector"), ("Дверь из бледного дуба", "pale_oak_door"),
        ("Дверь из тропического дерева", "jungle_door"), ("Дверь из тёмного дуба", "dark_oak_door"),
        ("Деревянная кирка", "wooden_pickaxe"), ("Деревянная лопата", "wooden_shovel"),
        ("Деревянная мотыга", "wooden_hoe"), ("Деревянный меч", "wooden_sword"),
        ("Деревянный топор", "wooden_axe"), ("Динамит", "tnt"), ("Диорит", "diorite"),
        ("Диоритовая ограда", "diorite_wall"), ("Диоритовая плита", "diorite_slab"),
        ("Диоритовые ступеньки", "diorite_stairs"), ("Доски из бледного дуба", "pale_oak_planks"),
        ("Доски из тропического дерева", "jungle_planks"),
        ("Доски из тёмного дуба", "dark_oak_planks"), ("Драконье дыхание", "dragon_breath"),
        ("Древесный уголь", "charcoal"), ("Древние обломки", "ancient_debris"), ("Дуб", "oak_wood"),
        ("Дубовая грузовая лодка", "oak_chest_boat"), ("Дубовая дверь", "oak_door"),
        ("Дубовая калитка", "oak_fence_gate"), ("Дубовая кнопка", "oak_button"),
        ("Дубовая лодка", "oak_boat"), ("Дубовая нажимная плита", "oak_pressure_plate"),
        ("Дубовая плита", "oak_slab"), ("Дубовая подвесная табличка", "oak_hanging_sign"),
        ("Дубовая табличка", "oak_sign"), ("Дубовое бревно", "oak_log"),
        ("Дубовые доски", "oak_planks"), ("Дубовые листья", "oak_leaves"),
        ("Дубовые ступеньки", "oak_stairs"), ("Дубовый забор", "oak_fence"),
        ("Дубовый люк", "oak_trapdoor"), ("Дёрн", "grass_block"),
        ("Еловая грузовая лодка", "spruce_chest_boat"), ("Еловая дверь", "spruce_door"),
        ("Еловая калитка", "spruce_fence_gate"), ("Еловая кнопка", "spruce_button"),
        ("Еловая лодка", "spruce_boat"), ("Еловая нажимная плита", "spruce_pressure_plate"),
        ("Еловая плита", "spruce_slab"), ("Еловая подвесная табличка", "spruce_hanging_sign"),
        ("Еловая табличка", "spruce_sign"), ("Еловое бревно", "spruce_log"),
        ("Еловые доски", "spruce_planks"), ("Еловые ступеньки", "spruce_stairs"),
        ("Еловый забор", "spruce_fence"), ("Еловый люк", "spruce_trapdoor"), ("Ель", "spruce_wood"),
        ("Жареная баранина", "cooked_mutton"), ("Жареная крольчатина", "cooked_rabbit"),
        ("Жареная курица", "cooked_chicken"), ("Жареная свинина", "cooked_porkchop"),
        ("Жареная треска", "cooked_cod"), ("Жареный лосось", "cooked_salmon"),
        ("Железная дверь", "iron_door"), ("Железная кирка", "iron_pickaxe"),
        ("Железная конская броня", "iron_horse_armor"), ("Железная лопата", "iron_shovel"),
        ("Железная мотыга", "iron_hoe"), ("Железная руда", "iron_ore"),
        ("Железные ботинки", "iron_boots"), ("Железные поножи", "iron_leggings"),
        ("Железные прутья", "iron_bars"), ("Железный блок", "iron_block"),
        ("Железный люк", "iron_trapdoor"), ("Железный меч", "iron_sword"),
        ("Железный нагрудник", "iron_chestplate"), ("Железный слиток", "iron_ingot"),
        ("Железный топор", "iron_axe"), ("Железный шлем", "iron_helmet"),
        ("Железоносный глубинный сланец", "deepslate_iron_ore"),
        ("Жёлтая глазурованная керамика", "yellow_glazed_terracotta"),
        ("Жёлтая керамика", "yellow_terracotta"), ("Жёлтая кровать", "yellow_bed"),
        ("Жёлтая свеча", "yellow_candle"), ("Жёлтая стеклянная панель", "yellow_stained_glass_pane"),
        ("Жёлтая шерсть", "yellow_wool"), ("Жёлтое стекло", "yellow_stained_glass"),
        ("Жёлтый бетон", "yellow_concrete"), ("Жёлтый ковёр", "yellow_carpet"),
        ("Жёлтый краситель", "yellow_dye"), ("Жёлтый мешок", "yellow_bundle"),
        ("Жёлтый сухой бетон", "yellow_concrete_powder"), ("Жёлтый флаг", "yellow_banner"),
        ("Жёлтый шалкеровый ящик", "yellow_shulker_box"), ("Забор из бледного дуба", "pale_oak_fence"),
        ("Забор из незерского кирпича", "nether_brick_fence"),
        ("Забор из тропического дерева", "jungle_fence"), ("Забор из тёмного дуба", "dark_oak_fence"),
        ("Завершённая книга", "written_book"), ("Загадочное рагу", "suspicious_stew"),
        ("Загрузочная вагонетка", "hopper_minecart"), ("Закрытая глазалия", "closed_eyeblossom"),
        ("Замшелая булыжная ограда", "mossy_cobblestone_wall"),
        ("Замшелая булыжная плита", "mossy_cobblestone_slab"),
        ("Замшелые булыжные ступеньки", "mossy_cobblestone_stairs"),
        ("Замшелые каменные кирпичи", "mossy_stone_bricks"),
        ("Замшелый булыжник", "mossy_cobblestone"),
        ("Заражённые замшелые каменные кирпичи", "infested_mossy_stone_bricks"),
        ("Заражённые каменные кирпичи", "infested_stone_bricks"),
        ("Заражённые потрескавшиеся каменные кирпичи", "infested_cracked_stone_bricks"),
        ("Заражённые резные каменные кирпичи", "infested_chiseled_stone_bricks"),
        ("Заражённый булыжник", "infested_cobblestone"),
        ("Заражённый глубинный сланец", "infested_deepslate"), ("Заражённый камень", "infested_stone"),
        ("Заряд ветра", "wind_charge"), ("Зачарованное золотое яблоко", "enchanted_golden_apple"),
        ("Звезда Незера", "nether_star"), ("Зельеварка", "brewing_stand"),
        ("Зелёная глазурованная керамика", "green_glazed_terracotta"),
        ("Зелёная керамика", "green_terracotta"), ("Зелёная кровать", "green_bed"),
        ("Зелёная свеча", "green_candle"), ("Зелёная стеклянная панель", "green_stained_glass_pane"),
        ("Зелёная шерсть", "green_wool"), ("Зелёное стекло", "green_stained_glass"),
        ("Зелёный бетон", "green_concrete"), ("Зелёный ковёр", "green_carpet"),
        ("Зелёный краситель", "green_dye"), ("Зелёный мешок", "green_bundle"),
        ("Зелёный сухой бетон", "green_concrete_powder"), ("Зелёный флаг", "green_banner"),
        ("Зелёный шалкеровый ящик", "green_shulker_box"), ("Земля", "dirt"),
        ("Зловещая бутылочка", "ominous_bottle"), ("Зловещий ключ испытаний", "ominous_trial_key"),
        ("Золотая кирка", "golden_pickaxe"), ("Золотая конская броня", "golden_horse_armor"),
        ("Золотая лопата", "golden_shovel"), ("Золотая морковь", "golden_carrot"),
        ("Золотая мотыга", "golden_hoe"), ("Золотая руда", "gold_ore"),
        ("Золотое яблоко", "golden_apple"), ("Золотой блок", "gold_block"),
        ("Золотой меч", "golden_sword"), ("Золотой нагрудник", "golden_chestplate"),
        ("Золотой слиток", "gold_ingot"), ("Золотой топор", "golden_axe"),
        ("Золотой шлем", "golden_helmet"), ("Золотоносный глубинный сланец", "deepslate_gold_ore"),
        ("Золотые ботинки", "golden_boots"), ("Золотые поножи", "golden_leggings"),
        ("Золочёный чернит", "gilded_blackstone"), ("Иглобрюх", "pufferfish"),
        ("Иглобрюх в ведре", "pufferfish_bucket"), ("Изумруд", "emerald"),
        ("Изумрудная руда", "emerald_ore"), ("Изумрудный блок", "emerald_block"),
        ("Изумрудоносный глубинный сланец", "deepslate_emerald_ore"),
        ("Искажённая дверь", "warped_door"), ("Искажённая калитка", "warped_fence_gate"),
        ("Искажённая кнопка", "warped_button"), ("Искажённая нажимная плита", "warped_pressure_plate"),
        ("Искажённая плита", "warped_slab"), ("Искажённая подвесная табличка", "warped_hanging_sign"),
        ("Искажённая табличка", "warped_sign"), ("Искажённые гифы", "warped_hyphae"),
        ("Искажённые доски", "warped_planks"), ("Искажённые корни", "warped_roots"),
        ("Искажённые ступеньки", "warped_stairs"), ("Искажённый гриб", "warped_fungus"),
        ("Искажённый забор", "warped_fence"), ("Искажённый люк", "warped_trapdoor"),
        ("Искажённый нилий", "warped_nylium"), ("Искажённый стебель", "warped_stem"),
        ("Какао-бобы", "cocoa_beans"), ("Кактус", "cactus"),
        ("Калитка из бледного дуба", "pale_oak_fence_gate"),
        ("Калитка из тропического дерева", "jungle_fence_gate"),
        ("Калитка из тёмного дуба", "dark_oak_fence_gate"), ("Кальцит", "calcite"),
        ("Каменистая земля", "coarse_dirt"), ("Каменная кирка", "stone_pickaxe"),
        ("Каменная кнопка", "stone_button"), ("Каменная лопата", "stone_shovel"),
        ("Каменная мотыга", "stone_hoe"), ("Каменная нажимная плита", "stone_pressure_plate"),
        ("Каменная плита", "stone_slab"), ("Каменные кирпичи", "stone_bricks"),
        ("Каменные ступеньки", "stone_stairs"), ("Каменный меч", "stone_sword"),
        ("Каменный топор", "stone_axe"), ("Камень", "stone"), ("Камнерез", "stonecutter"),
        ("Капельник", "pointed_dripstone"), ("Карта", "filled_map"), ("Картина", "painting"),
        ("Картофель", "potato"), ("Кафедра", "lectern"), ("Кварцевая колонна", "quartz_pillar"),
        ("Кварцевая плита", "quartz_slab"), ("Кварцевые кирпичи", "quartz_bricks"),
        ("Кварцевые ступеньки", "quartz_stairs"), ("Кварцевый блок", "quartz_block"),
        ("Кирпич", "brick"), ("Кирпичи", "bricks"), ("Кирпичная ограда", "brick_wall"),
        ("Кирпичная плита", "brick_slab"), ("Кирпичные ступеньки", "brick_stairs"), ("Кисть", "brush"),
        ("Ключ испытаний", "trial_key"), ("Книга", "book"), ("Книга знаний", "knowledge_book"),
        ("Книга и перо", "writable_book"), ("Книжная полка", "bookshelf"),
        ("Кнопка из бледного дуба", "pale_oak_button"),
        ("Кнопка из полированного чернита", "polished_blackstone_button"),
        ("Кнопка из тропического дерева", "jungle_button"),
        ("Кнопка из тёмного дуба", "dark_oak_button"), ("Кожа", "leather"),
        ("Кожаная конская броня", "leather_horse_armor"), ("Кожаная куртка", "leather_chestplate"),
        ("Кожаные ботинки", "leather_boots"), ("Кожаные штаны", "leather_leggings"),
        ("Кожаный шлем", "leather_helmet"), ("Козий рог", "goat_horn"), ("Койф", "chainmail_helmet"),
        ("Колокол", "bell"), ("Колотый глубинный сланец", "cobbled_deepslate"),
        ("Кольчуга", "chainmail_chestplate"), ("Кольчужные ботинки", "chainmail_boots"),
        ("Кольчужные поножи", "chainmail_leggings"), ("Командный блок", "command_block"),
        ("Комок глины", "clay_ball"), ("Компас", "compass"), ("Компостница", "composter"),
        ("Коптильня", "smoker"), ("Коралл-мозговик", "brain_coral"),
        ("Коричневая глазурованная керамика", "brown_glazed_terracotta"),
        ("Коричневая керамика", "brown_terracotta"), ("Коричневая кровать", "brown_bed"),
        ("Коричневая свеча", "brown_candle"),
        ("Коричневая стеклянная панель", "brown_stained_glass_pane"),
        ("Коричневая шерсть", "brown_wool"), ("Коричневое стекло", "brown_stained_glass"),
        ("Коричневый бетон", "brown_concrete"), ("Коричневый гриб", "brown_mushroom"),
        ("Коричневый ковёр", "brown_carpet"), ("Коричневый краситель", "brown_dye"),
        ("Коричневый мешок", "brown_bundle"), ("Коричневый сухой бетон", "brown_concrete_powder"),
        ("Коричневый флаг", "brown_banner"), ("Коричневый шалкеровый ящик", "brown_shulker_box"),
        ("Корнистая земля", "rooted_dirt"), ("Костная мука", "bone_meal"),
        ("Костный блок", "bone_block"), ("Кость", "bone"), ("Костёр", "campfire"),
        ("Костёр душ", "soul_campfire"), ("Котёл", "cauldron"),
        ("Красная глазурованная керамика", "red_glazed_terracotta"),
        ("Красная керамика", "red_terracotta"), ("Красная кровать", "red_bed"),
        ("Красная свеча", "red_candle"), ("Красная стеклянная панель", "red_stained_glass_pane"),
        ("Красная шерсть", "red_wool"), ("Красное стекло", "red_stained_glass"),
        ("Красные незерские кирпичи", "red_nether_bricks"), ("Красный бетон", "red_concrete"),
        ("Красный гриб", "red_mushroom"), ("Красный ковёр", "red_carpet"),
        ("Красный краситель", "red_dye"), ("Красный мешок", "red_bundle"),
        ("Красный песок", "red_sand"), ("Красный песчаник", "red_sandstone"),
        ("Красный сухой бетон", "red_concrete_powder"), ("Красный тюльпан", "red_tulip"),
        ("Красный флаг", "red_banner"), ("Красный шалкеровый ящик", "red_shulker_box"),
        ("Кремень", "flint"), ("Кристалл призмарина", "prismarine_crystals"),
        ("Кристалл Энда", "end_crystal"), ("Кроличья лапка", "rabbit_foot"),
        ("Кроличья шкурка", "rabbit_hide"), ("Крюк", "tripwire_hook"), ("Кувшинка", "lily_pad"),
        ("Кувшинница", "pitcher_plant"), ("Кузнечный шаблон", "bolt_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "coast_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "dune_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "eye_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "flow_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "host_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "netherite_upgrade_smithing_template"),
        ("Кузнечный шаблон", "raiser_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "rib_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "sentry_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "shaper_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "silence_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "snout_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "spire_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "tide_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "vex_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "ward_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "wayfinder_armor_trim_smithing_template"),
        ("Кузнечный шаблон", "wild_armor_trim_smithing_template"), ("Курятина", "chicken"),
        ("Кусочек железа", "iron_nugget"), ("Кусочек золота", "gold_nugget"),
        ("Лазурит", "lapis_lazuli"), ("Лазуритовая руда", "lapis_ore"),
        ("Лазуритовый блок", "lapis_block"),
        ("Лазуритоносный глубинный сланец", "deepslate_lapis_ore"),
        ("Лаймовая глазурованная керамика", "lime_glazed_terracotta"),
        ("Лаймовая керамика", "lime_terracotta"), ("Лаймовая кровать", "lime_bed"),
        ("Лаймовая свеча", "lime_candle"), ("Лаймовая стеклянная панель", "lime_stained_glass_pane"),
        ("Лаймовая шерсть", "lime_wool"), ("Лаймовое стекло", "lime_stained_glass"),
        ("Лаймовый бетон", "lime_concrete"), ("Лаймовый ковёр", "lime_carpet"),
        ("Лаймовый краситель", "lime_dye"), ("Лаймовый мешок", "lime_bundle"),
        ("Лаймовый сухой бетон", "lime_concrete_powder"), ("Лаймовый флаг", "lime_banner"),
        ("Лаймовый шалкеровый ящик", "lime_shulker_box"), ("Ламинария", "kelp"),
        ("Ландыш", "lily_of_the_valley"), ("Лестница", "ladder"), ("Лианы", "vine"),
        ("Липкий поршень", "sticky_piston"), ("Листья азалии", "azalea_leaves"),
        ("Листья акации", "acacia_leaves"), ("Листья бледного дуба", "pale_oak_leaves"),
        ("Листья тропического дерева", "jungle_leaves"), ("Листья тёмного дуба", "dark_oak_leaves"),
        ("Листья цветущей азалии", "flowering_azalea_leaves"),
        ("Лодка из бледного дуба", "pale_oak_boat"), ("Лодка из тропического дерева", "jungle_boat"),
        ("Лодка из тёмного дуба", "dark_oak_boat"), ("Ломтик арбуза", "melon_slice"),
        ("Лосось в ведре", "salmon_bucket"), ("Лук", "bow"), ("Лук-батун", "allium"),
        ("Люк из бледного дуба", "pale_oak_trapdoor"),
        ("Люк из тропического дерева", "jungle_trapdoor"),
        ("Люк из тёмного дуба", "dark_oak_trapdoor"), ("Лягушачья икра", "frogspawn"), ("Лёд", "ice"),
        ("Магма", "magma_block"), ("Магнетит", "lodestone"), ("Мак", "poppy"),
        ("Малахитовая квампа", "verdant_froglight"), ("Маленькая бросянка", "small_dripleaf"),
        ("Малый аметистовый бутон", "small_amethyst_bud"),
        ("Мангровая грузовая лодка", "mangrove_chest_boat"), ("Мангровая дверь", "mangrove_door"),
        ("Мангровая калитка", "mangrove_fence_gate"), ("Мангровая кнопка", "mangrove_button"),
        ("Мангровая лодка", "mangrove_boat"), ("Мангровая нажимная плита", "mangrove_pressure_plate"),
        ("Мангровая плита", "mangrove_slab"),
        ("Мангровая подвесная табличка", "mangrove_hanging_sign"),
        ("Мангровая табличка", "mangrove_sign"), ("Мангровое бревно", "mangrove_log"),
        ("Мангровое дерево", "mangrove_wood"), ("Мангровые доски", "mangrove_planks"),
        ("Мангровые корни", "mangrove_roots"), ("Мангровые листья", "mangrove_leaves"),
        ("Мангровые ступеньки", "mangrove_stairs"), ("Мангровый забор", "mangrove_fence"),
        ("Мангровый люк", "mangrove_trapdoor"), ("Мангровый отросток", "mangrove_propagule"),
        ("Маринованный паучий глаз", "fermented_spider_eye"), ("Маяк", "beacon"),
        ("Меденосный глубинный сланец", "deepslate_copper_ore"), ("Медная дверь", "copper_door"),
        ("Медная лампа", "copper_bulb"), ("Медная решётка", "copper_grate"),
        ("Медная руда", "copper_ore"), ("Медный блок", "copper_block"),
        ("Медный люк", "copper_trapdoor"), ("Медный слиток", "copper_ingot"),
        ("Мембрана фантома", "phantom_membrane"), ("Мешок", "bundle"), ("Миска", "bowl"),
        ("Мицелий", "mycelium"), ("Мишень", "target"), ("Мокрая губка", "wet_sponge"),
        ("Морковь", "carrot"), ("Морская трава", "seagrass"), ("Морской источник", "conduit"),
        ("Морской огурец", "sea_pickle"), ("Морской фонарь", "sea_lantern"),
        ("Моховой ковёр", "moss_carpet"), ("Мёртвый веерный коралл-мозговик", "dead_brain_coral_fan"),
        ("Мёртвый веерный огненный коралл", "dead_fire_coral_fan"),
        ("Мёртвый веерный пузырчатый коралл", "dead_bubble_coral_fan"),
        ("Мёртвый веерный роговый коралл", "dead_horn_coral_fan"),
        ("Мёртвый веерный трубчатый коралл", "dead_tube_coral_fan"),
        ("Мёртвый коралл-мозговик", "dead_brain_coral"), ("Мёртвый куст", "dead_bush"),
        ("Мёртвый огненный коралл", "dead_fire_coral"),
        ("Мёртвый пузырчатый коралл", "dead_bubble_coral"),
        ("Мёртвый роговый коралл", "dead_horn_coral"), ("Мёртвый трубчатый коралл", "dead_tube_coral"),
        ("Наблюдатель", "observer"), ("Навершие булавы", "heavy_core"),
        ("Нажимная плита из бледного дуба", "pale_oak_pressure_plate"),
        ("Нажимная плита из полированного чернита", "polished_blackstone_pressure_plate"),
        ("Нажимная плита из тропического дерева", "jungle_pressure_plate"),
        ("Нажимная плита из тёмного дуба", "dark_oak_pressure_plate"), ("Наковальня", "anvil"),
        ("Натёчный камень", "dripstone_block"), ("Незер-кварц", "quartz"),
        ("Незер-кварцевая руда", "nether_quartz_ore"), ("Незерак", "netherrack"),
        ("Незеритовая кирка", "netherite_pickaxe"), ("Незеритовая лопата", "netherite_shovel"),
        ("Незеритовая мотыга", "netherite_hoe"), ("Незеритовые ботинки", "netherite_boots"),
        ("Незеритовые поножи", "netherite_leggings"), ("Незеритовый блок", "netherite_block"),
        ("Незеритовый лом", "netherite_scrap"), ("Незеритовый меч", "netherite_sword"),
        ("Незеритовый нагрудник", "netherite_chestplate"), ("Незеритовый слиток", "netherite_ingot"),
        ("Незеритовый топор", "netherite_axe"), ("Незеритовый шлем", "netherite_helmet"),
        ("Незерская золотая руда", "nether_gold_ore"), ("Незерские кирпичи", "nether_bricks"),
        ("Незерские ростки", "nether_sprouts"), ("Незерский кирпич", "nether_brick"),
        ("Незерский нарост", "nether_wart"), ("Низкая трава", "short_grass"), ("Нить", "string"),
        ("Ножка гриба", "mushroom_stem"), ("Ножницы", "shears"), ("Нотный блок", "note_block"),
        ("Обсидиан", "obsidian"), ("Обтёсанная акациевая древесина", "stripped_acacia_wood"),
        ("Обтёсанная берёзовая древесина", "stripped_birch_wood"),
        ("Обтёсанная вишнёвая древесина", "stripped_cherry_wood"),
        ("Обтёсанная древесина бледного дуба", "stripped_pale_oak_wood"),
        ("Обтёсанная древесина тёмного дуба", "stripped_dark_oak_wood"),
        ("Обтёсанная дубовая древесина", "stripped_oak_wood"),
        ("Обтёсанная еловая древесина", "stripped_spruce_wood"),
        ("Обтёсанная мангровая древесина", "stripped_mangrove_wood"),
        ("Обтёсанная тропическая древесина", "stripped_jungle_wood"),
        ("Обтёсанное акациевое бревно", "stripped_acacia_log"),
        ("Обтёсанное берёзовое бревно", "stripped_birch_log"),
        ("Обтёсанное бревно бледного дуба", "stripped_pale_oak_log"),
        ("Обтёсанное бревно тропического дерева", "stripped_jungle_log"),
        ("Обтёсанное бревно тёмного дуба", "stripped_dark_oak_log"),
        ("Обтёсанное вишнёвое бревно", "stripped_cherry_log"),
        ("Обтёсанное дубовое бревно", "stripped_oak_log"),
        ("Обтёсанное еловое бревно", "stripped_spruce_log"),
        ("Обтёсанное мангровое бревно", "stripped_mangrove_log"), ("Огненный заряд", "fire_charge"),
        ("Огненный коралл", "fire_coral"), ("Огненный порошок", "blaze_powder"),
        ("Огненный стержень", "blaze_rod"), ("Огниво", "flint_and_steel"),
        ("Ограда из глубинносланцевого кирпича", "deepslate_brick_wall"),
        ("Ограда из глубинносланцевого плитняка", "deepslate_tile_wall"),
        ("Ограда из замшелого каменного кирпича", "mossy_stone_brick_wall"),
        ("Ограда из каменного кирпича", "stone_brick_wall"),
        ("Ограда из колотого глубинного сланца", "cobbled_deepslate_wall"),
        ("Ограда из красного незерского кирпича", "red_nether_brick_wall"),
        ("Ограда из красного песчаника", "red_sandstone_wall"),
        ("Ограда из незерского кирпича", "nether_brick_wall"),
        ("Ограда из полированно-чернитного кирпича", "polished_blackstone_brick_wall"),
        ("Ограда из полированного глубинного сланца", "polished_deepslate_wall"),
        ("Ограда из полированного туфа", "polished_tuff_wall"),
        ("Ограда из полированного чернита", "polished_blackstone_wall"),
        ("Ограда из смоляного кирпича", "resin_brick_wall"),
        ("Ограда из туфового кирпича", "tuff_brick_wall"),
        ("Ограда из эндернякового кирпича", "end_stone_brick_wall"), ("Одуванчик", "dandelion"),
        ("Окаменевшая дубовая плита", "petrified_oak_slab"),
        ("Окисленная медная дверь", "oxidized_copper_door"),
        ("Окисленная медная лампа", "oxidized_copper_bulb"),
        ("Окисленная медная решётка", "oxidized_copper_grate"),
        ("Окисленная резная медная плита", "oxidized_cut_copper_slab"),
        ("Окисленные резные медные ступеньки", "oxidized_cut_copper_stairs"),
        ("Окисленный медный блок", "oxidized_copper"),
        ("Окисленный медный люк", "oxidized_copper_trapdoor"),
        ("Окисленный резной медный блок", "oxidized_cut_copper"),
        ("Окисленный точёный медный блок", "oxidized_chiseled_copper"), ("Око Эндера", "ender_eye"),
        ("Оранжевая глазурованная керамика", "orange_glazed_terracotta"),
        ("Оранжевая керамика", "orange_terracotta"), ("Оранжевая кровать", "orange_bed"),
        ("Оранжевая свеча", "orange_candle"),
        ("Оранжевая стеклянная панель", "orange_stained_glass_pane"),
        ("Оранжевая шерсть", "orange_wool"), ("Оранжевое стекло", "orange_stained_glass"),
        ("Оранжевый бетон", "orange_concrete"), ("Оранжевый ковёр", "orange_carpet"),
        ("Оранжевый краситель", "orange_dye"), ("Оранжевый мешок", "orange_bundle"),
        ("Оранжевый сухой бетон", "orange_concrete_powder"), ("Оранжевый тюльпан", "orange_tulip"),
        ("Оранжевый флаг", "orange_banner"), ("Оранжевый шалкеровый ящик", "orange_shulker_box"),
        ("Осколок аметиста", "amethyst_shard"), ("Осколок пластинки", "disc_fragment_5"),
        ("Осколок призмарина", "prismarine_shard"), ("Осколок эха", "echo_shard"),
        ("Откалиброванный скалк-сенсор", "calibrated_sculk_sensor"),
        ("Охристая квампа", "ochre_froglight"), ("Очищенные багровые гифы", "stripped_crimson_hyphae"),
        ("Очищенные искажённые гифы", "stripped_warped_hyphae"),
        ("Очищенный багровый стебель", "stripped_crimson_stem"),
        ("Очищенный искажённый стебель", "stripped_warped_stem"), ("Пазл", "jigsaw"),
        ("Палка", "stick"), ("Палочка отладки", "debug_stick"), ("Панцирь шалкера", "shulker_shell"),
        ("Папоротник", "fern"), ("Паутина", "cobweb"), ("Паучий глаз", "spider_eye"),
        ("Пашня", "farmland"), ("Перламутровая квампа", "pearlescent_froglight"), ("Перо", "feather"),
        ("Песок", "sand"), ("Песок душ", "soul_sand"), ("Песчаник", "sandstone"),
        ("Песчаниковая ограда", "sandstone_wall"), ("Песчаниковая плита", "sandstone_slab"),
        ("Песчаниковые ступеньки", "sandstone_stairs"), ("Печенье", "cookie"), ("Печь", "furnace"),
        ("Печёный картофель", "baked_potato"), ("Пиленый красный песчаник", "cut_red_sandstone"),
        ("Пиленый песчаник", "cut_sandstone"), ("Пион", "peony"),
        ("Пиротехническая звезда", "firework_star"), ("Плавильная печь", "blast_furnace"),
        ("Плакучая лоза", "weeping_vines"), ("Плачущий обсидиан", "crying_obsidian"),
        ("Плита из бамбуковой мозаики", "bamboo_mosaic_slab"),
        ("Плита из бледного дуба", "pale_oak_slab"), ("Плита из гладкого камня", "smooth_stone_slab"),
        ("Плита из гладкого кварца", "smooth_quartz_slab"),
        ("Плита из гладкого красного песчаника", "smooth_red_sandstone_slab"),
        ("Плита из гладкого песчаника", "smooth_sandstone_slab"),
        ("Плита из глубинносланцевого кирпича", "deepslate_brick_slab"),
        ("Плита из глубинносланцевого плитняка", "deepslate_tile_slab"),
        ("Плита из замшелого каменного кирпича", "mossy_stone_brick_slab"),
        ("Плита из каменного кирпича", "stone_brick_slab"),
        ("Плита из колотого глубинного сланца", "cobbled_deepslate_slab"),
        ("Плита из красного незерского кирпича", "red_nether_brick_slab"),
        ("Плита из красного песчаника", "red_sandstone_slab"),
        ("Плита из незерского кирпича", "nether_brick_slab"),
        ("Плита из пиленого красного песчаника", "cut_red_sandstone_slab"),
        ("Плита из пиленого песчаника", "cut_sandstone_slab"),
        ("Плита из полированно-чернитного кирпича", "polished_blackstone_brick_slab"),
        ("Плита из полированного андезита", "polished_andesite_slab"),
        ("Плита из полированного глубинного сланца", "polished_deepslate_slab"),
        ("Плита из полированного гранита", "polished_granite_slab"),
        ("Плита из полированного диорита", "polished_diorite_slab"),
        ("Плита из полированного туфа", "polished_tuff_slab"),
        ("Плита из полированного чернита", "polished_blackstone_slab"),
        ("Плита из призмаринового кирпича", "prismarine_brick_slab"),
        ("Плита из смоляного кирпича", "resin_brick_slab"),
        ("Плита из тропического дерева", "jungle_slab"),
        ("Плита из туфового кирпича", "tuff_brick_slab"), ("Плита из тёмного дуба", "dark_oak_slab"),
        ("Плита из тёмного призмарина", "dark_prismarine_slab"),
        ("Плита из эндернякового кирпича", "end_stone_brick_slab"), ("Плод хоруса", "chorus_fruit"),
        ("Плотная грязь", "packed_mud"), ("Плотный лёд", "packed_ice"), ("Поводок", "lead"),
        ("Повреждённая наковальня", "chipped_anvil"),
        ("Повторяющий командный блок", "repeating_command_block"),
        ("Подвесная табличка из бледного дуба", "pale_oak_hanging_sign"),
        ("Подвесная табличка из тропического дерева", "jungle_hanging_sign"),
        ("Подвесная табличка из тёмного дуба", "dark_oak_hanging_sign"), ("Подзол", "podzol"),
        ("Подзорная труба", "spyglass"), ("Подмостки", "scaffolding"),
        ("Подозрительный гравий", "suspicious_gravel"), ("Подозрительный песок", "suspicious_sand"),
        ("Подсолнух", "sunflower"), ("Полированно-чернитные кирпичи", "polished_blackstone_bricks"),
        ("Полированный андезит", "polished_andesite"), ("Полированный базальт", "polished_basalt"),
        ("Полированный глубинный сланец", "polished_deepslate"),
        ("Полированный гранит", "polished_granite"), ("Полированный диорит", "polished_diorite"),
        ("Полированный туф", "polished_tuff"), ("Полированный чернит", "polished_blackstone"),
        ("Порох", "gunpowder"), ("Поршень", "piston"),
        ("Последовательный командный блок", "chain_command_block"),
        ("Потемневшая медная дверь", "exposed_copper_door"),
        ("Потемневшая медная лампа", "exposed_copper_bulb"),
        ("Потемневшая медная решётка", "exposed_copper_grate"),
        ("Потемневшая резная медная плита", "exposed_cut_copper_slab"),
        ("Потемневшие резные медные ступеньки", "exposed_cut_copper_stairs"),
        ("Потемневший медный блок", "exposed_copper"),
        ("Потемневший медный люк", "exposed_copper_trapdoor"),
        ("Потемневший резной медный блок", "exposed_cut_copper"),
        ("Потемневший точёный медный блок", "exposed_chiseled_copper"),
        ("Потрескавшиеся глубинносланцевые кирпичи", "cracked_deepslate_bricks"),
        ("Потрескавшиеся каменные кирпичи", "cracked_stone_bricks"),
        ("Потрескавшиеся незерские кирпичи", "cracked_nether_bricks"),
        ("Потрескавшиеся полированно-чернитные кирпичи", "cracked_polished_blackstone_bricks"),
        ("Потрескавшийся глубинносланцевый плитняк", "cracked_deepslate_tiles"),
        ("Почва душ", "soul_soil"), ("Призмарин", "prismarine"),
        ("Призмариновая ограда", "prismarine_wall"), ("Призмариновая плита", "prismarine_slab"),
        ("Призмариновые кирпичи", "prismarine_bricks"),
        ("Призмариновые ступеньки", "prismarine_stairs"),
        ("Прожаренный плод хоруса", "popped_chorus_fruit"), ("Проигрыватель", "jukebox"),
        ("Пузырчатый коралл", "bubble_coral"), ("Пузырёк опыта", "experience_bottle"),
        ("Пурпур", "purpur_block"), ("Пурпурная глазурованная керамика", "magenta_glazed_terracotta"),
        ("Пурпурная керамика", "magenta_terracotta"), ("Пурпурная кровать", "magenta_bed"),
        ("Пурпурная свеча", "magenta_candle"),
        ("Пурпурная стеклянная панель", "magenta_stained_glass_pane"),
        ("Пурпурная шерсть", "magenta_wool"), ("Пурпурное стекло", "magenta_stained_glass"),
        ("Пурпурный бетон", "magenta_concrete"), ("Пурпурный ковёр", "magenta_carpet"),
        ("Пурпурный краситель", "magenta_dye"), ("Пурпурный мешок", "magenta_bundle"),
        ("Пурпурный сухой бетон", "magenta_concrete_powder"), ("Пурпурный флаг", "magenta_banner"),
        ("Пурпурный шалкеровый ящик", "magenta_shulker_box"), ("Пурпуровая плита", "purpur_slab"),
        ("Пурпуровые ступеньки", "purpur_stairs"), ("Пурпуровый пилон", "purpur_pillar"),
        ("Пустой блок", "structure_void"), ("Пчелиное гнездо", "bee_nest"),
        ("Пчелиные соты", "honeycomb"), ("Пшеница", "wheat"), ("Разбитая наковальня", "damaged_anvil"),
        ("Раздатчик", "dispenser"), ("Раковина наутилуса", "nautilus_shell"), ("Рамка", "item_frame"),
        ("Рамка портала в Энд", "end_portal_frame"), ("Раскидистый папоротник", "large_fern"),
        ("Раскрытая глазалия", "open_eyeblossom"), ("Рассадник испытаний", "trial_spawner"),
        ("Рассадник монстров", "spawner"), ("Редстоуновая пыль", "redstone"),
        ("Редстоуновая руда", "redstone_ore"), ("Редстоуновый блок", "redstone_block"),
        ("Редстоуновый компаратор", "comparator"), ("Редстоуновый повторитель", "repeater"),
        ("Редстоуновый факел", "redstone_torch"), ("Редстоуновый фонарь", "redstone_lamp"),
        ("Редстоуноносный глубинный сланец", "deepslate_redstone_ore"),
        ("Резная книжная полка", "chiseled_bookshelf"), ("Резная медная плита", "cut_copper_slab"),
        ("Резной глубинный сланец", "chiseled_deepslate"),
        ("Резной кварцевый блок", "chiseled_quartz_block"),
        ("Резной красный песчаник", "chiseled_red_sandstone"), ("Резной медный блок", "cut_copper"),
        ("Резной песчаник", "chiseled_sandstone"),
        ("Резной полированный чернит", "chiseled_polished_blackstone"),
        ("Резной туф", "chiseled_tuff"), ("Резные каменные кирпичи", "chiseled_stone_bricks"),
        ("Резные медные ступеньки", "cut_copper_stairs"),
        ("Резные незерские кирпичи", "chiseled_nether_bricks"),
        ("Резные смоляные кирпичи", "chiseled_resin_bricks"),
        ("Резные туфовые кирпичи", "chiseled_tuff_bricks"), ("Рельсы", "rail"),
        ("Рельсы с датчиком", "detector_rail"), ("Роговой коралл", "horn_coral"),
        ("Роза визера", "wither_rose"), ("Розовая глазурованная керамика", "pink_glazed_terracotta"),
        ("Розовая керамика", "pink_terracotta"), ("Розовая кровать", "pink_bed"),
        ("Розовая свеча", "pink_candle"), ("Розовая стеклянная панель", "pink_stained_glass_pane"),
        ("Розовая шерсть", "pink_wool"), ("Розовое стекло", "pink_stained_glass"),
        ("Розовые лепестки", "pink_petals"), ("Розовый бетон", "pink_concrete"),
        ("Розовый ковёр", "pink_carpet"), ("Розовый краситель", "pink_dye"),
        ("Розовый куст", "rose_bush"), ("Розовый мешок", "pink_bundle"),
        ("Розовый сухой бетон", "pink_concrete_powder"), ("Розовый тюльпан", "pink_tulip"),
        ("Розовый флаг", "pink_banner"), ("Розовый шалкеровый ящик", "pink_shulker_box"),
        ("Ромашка", "oxeye_daisy"), ("Рудная медь", "raw_copper"), ("Рудное железо", "raw_iron"),
        ("Рудное золото", "raw_gold"), ("Рычаг", "lever"), ("Саженец акации", "acacia_sapling"),
        ("Саженец берёзы", "birch_sapling"), ("Саженец бледного дуба", "pale_oak_sapling"),
        ("Саженец вишни", "cherry_sapling"), ("Саженец дуба", "oak_sapling"),
        ("Саженец ели", "spruce_sapling"), ("Саженец тропического дерева", "jungle_sapling"),
        ("Саженец тёмного дуба", "dark_oak_sapling"), ("Саманная ограда", "mud_brick_wall"),
        ("Саманная плита", "mud_brick_slab"), ("Саманные кирпичи", "mud_bricks"),
        ("Саманные ступеньки", "mud_brick_stairs"), ("Самоходная вагонетка", "furnace_minecart"),
        ("Сахар", "sugar"), ("Сахарный тростник", "sugar_cane"), ("Сборщик", "crafter"),
        ("Свекольный суп", "beetroot_soup"), ("Сверкающий ломтик арбуза", "glistering_melon_slice"),
        ("Свет", "light"), ("Светильник Джека", "jack_o_lantern"),
        ("Светло-серая глазурованная керамика", "light_gray_glazed_terracotta"),
        ("Светло-серая керамика", "light_gray_terracotta"), ("Светло-серая кровать", "light_gray_bed"),
        ("Светло-серая свеча", "light_gray_candle"),
        ("Светло-серая стеклянная панель", "light_gray_stained_glass_pane"),
        ("Светло-серая шерсть", "light_gray_wool"),
        ("Светло-серое стекло", "light_gray_stained_glass"),
        ("Светло-серый бетон", "light_gray_concrete"), ("Светло-серый ковёр", "light_gray_carpet"),
        ("Светло-серый краситель", "light_gray_dye"), ("Светло-серый мешок", "light_gray_bundle"),
        ("Светло-серый сухой бетон", "light_gray_concrete_powder"),
        ("Светло-серый флаг", "light_gray_banner"),
        ("Светло-серый шалкеровый ящик", "light_gray_shulker_box"),
        ("Светокаменная пыль", "glowstone_dust"), ("Светокамень", "glowstone"),
        ("Светящаяся рамка", "glow_item_frame"), ("Светящиеся ягоды", "glow_berries"),
        ("Светящийся лишайник", "glow_lichen"), ("Светящийся чернильный мешок", "glow_ink_sac"),
        ("Свеча", "candle"), ("Свисающие корни", "hanging_roots"),
        ("Свисающий бледный мох", "pale_hanging_moss"), ("Свёкла", "beetroot"),
        ("Сгусток магмы", "magma_cream"), ("Сгусток слизи", "slime_ball"),
        ("Сгусток смолы", "resin_clump"), ("Седло", "saddle"), ("Семена арбуза", "melon_seeds"),
        ("Семена пшеницы", "wheat_seeds"), ("Семена свёклы", "beetroot_seeds"),
        ("Семена тыквы", "pumpkin_seeds"), ("Семена факельника", "torchflower_seeds"),
        ("Серая глазурованная керамика", "gray_glazed_terracotta"),
        ("Серая керамика", "gray_terracotta"), ("Серая кровать", "gray_bed"),
        ("Серая свеча", "gray_candle"), ("Серая стеклянная панель", "gray_stained_glass_pane"),
        ("Серая шерсть", "gray_wool"), ("Сердце моря", "heart_of_the_sea"),
        ("Серое стекло", "gray_stained_glass"), ("Серый бетон", "gray_concrete"),
        ("Серый ковёр", "gray_carpet"), ("Серый краситель", "gray_dye"),
        ("Серый мешок", "gray_bundle"), ("Серый сухой бетон", "gray_concrete_powder"),
        ("Серый флаг", "gray_banner"), ("Серый шалкеровый ящик", "gray_shulker_box"),
        ("Синее стекло", "blue_stained_glass"), ("Синий бетон", "blue_concrete"),
        ("Синий василёк", "cornflower"), ("Синий ковёр", "blue_carpet"),
        ("Синий краситель", "blue_dye"), ("Синий лёд", "blue_ice"), ("Синий мешок", "blue_bundle"),
        ("Синий сухой бетон", "blue_concrete_powder"), ("Синий флаг", "blue_banner"),
        ("Синий шалкеровый ящик", "blue_shulker_box"),
        ("Синяя глазурованная керамика", "blue_glazed_terracotta"),
        ("Синяя керамика", "blue_terracotta"), ("Синяя кровать", "blue_bed"),
        ("Синяя орхидея", "blue_orchid"), ("Синяя свеча", "blue_candle"),
        ("Синяя стеклянная панель", "blue_stained_glass_pane"), ("Синяя шерсть", "blue_wool"),
        ("Сирень", "lilac"), ("Скалк", "sculk"), ("Скалк-сенсор", "sculk_sensor"),
        ("Скалковая жила", "sculk_vein"), ("Скалковый катализатор", "sculk_catalyst"),
        ("Скалковый крикун", "sculk_shrieker"), ("Скрипцевина", "creaking_heart"),
        ("Сладкие ягоды", "sweet_berries"), ("Слеза гаста", "ghast_tear"),
        ("Смоляной блок", "resin_block"), ("Смоляной кирпич", "resin_brick"),
        ("Смоляные кирпичи", "resin_bricks"), ("Снег", "snow"), ("Снежок", "snowball"),
        ("Сноп сена", "hay_block"), ("Состаренная медная дверь", "weathered_copper_door"),
        ("Состаренная медная лампа", "weathered_copper_bulb"),
        ("Состаренная медная решётка", "weathered_copper_grate"),
        ("Состаренная резная медная плита", "weathered_cut_copper_slab"),
        ("Состаренные резные медные ступеньки", "weathered_cut_copper_stairs"),
        ("Состаренный медный блок", "weathered_copper"),
        ("Состаренный медный люк", "weathered_copper_trapdoor"),
        ("Состаренный резной медный блок", "weathered_cut_copper"),
        ("Состаренный точёный медный блок", "weathered_chiseled_copper"),
        ("Спектральная стрела", "spectral_arrow"), ("Спороцвет", "spore_blossom"),
        ("Средний аметистовый бутон", "medium_amethyst_bud"), ("Стейк", "cooked_beef"),
        ("Стекло", "glass"), ("Стеклянная панель", "glass_pane"), ("Стержень Энда", "end_rod"),
        ("Стойка для брони", "armor_stand"), ("Стол картографа", "cartography_table"),
        ("Стол кузнеца", "smithing_table"), ("Стол лучника", "fletching_table"), ("Стрела", "arrow"),
        ("Стручок кувшинницы", "pitcher_pod"),
        ("Ступеньки из бамбуковой мозаики", "bamboo_mosaic_stairs"),
        ("Ступеньки из бледного дуба", "pale_oak_stairs"),
        ("Ступеньки из гладкого кварца", "smooth_quartz_stairs"),
        ("Ступеньки из гладкого красного песчаника", "smooth_red_sandstone_stairs"),
        ("Ступеньки из гладкого песчаника", "smooth_sandstone_stairs"),
        ("Ступеньки из глубинносланцевого кирпича", "deepslate_brick_stairs"),
        ("Ступеньки из глубинносланцевого плитняка", "deepslate_tile_stairs"),
        ("Ступеньки из замшелого каменного кирпича", "mossy_stone_brick_stairs"),
        ("Ступеньки из каменного кирпича", "stone_brick_stairs"),
        ("Ступеньки из колотого глубинного сланца", "cobbled_deepslate_stairs"),
        ("Ступеньки из красного незерского кирпича", "red_nether_brick_stairs"),
        ("Ступеньки из красного песчаника", "red_sandstone_stairs"),
        ("Ступеньки из незерского кирпича", "nether_brick_stairs"),
        ("Ступеньки из полированно-чернитного кирпича", "polished_blackstone_brick_stairs"),
        ("Ступеньки из полированного андезита", "polished_andesite_stairs"),
        ("Ступеньки из полированного глубинного сланца", "polished_deepslate_stairs"),
        ("Ступеньки из полированного гранита", "polished_granite_stairs"),
        ("Ступеньки из полированного диорита", "polished_diorite_stairs"),
        ("Ступеньки из полированного туфа", "polished_tuff_stairs"),
        ("Ступеньки из полированного чернита", "polished_blackstone_stairs"),
        ("Ступеньки из призмаринового кирпича", "prismarine_brick_stairs"),
        ("Ступеньки из смоляного кирпича", "resin_brick_stairs"),
        ("Ступеньки из тропического дерева", "jungle_stairs"),
        ("Ступеньки из туфового кирпича", "tuff_brick_stairs"),
        ("Ступеньки из тёмного дуба", "dark_oak_stairs"),
        ("Ступеньки из тёмного призмарина", "dark_prismarine_stairs"),
        ("Ступеньки из эндернякового кирпича", "end_stone_brick_stairs"), ("Сундук", "chest"),
        ("Сундук-ловушка", "trapped_chest"), ("Сушёная ламинария", "dried_kelp"),
        ("Сырая баранина", "mutton"), ("Сырая говядина", "beef"), ("Сырая крольчатина", "rabbit"),
        ("Сырая свинина", "porkchop"), ("Сырая треска", "cod"), ("Сырой лосось", "salmon"),
        ("Табличка из бледного дуба", "pale_oak_sign"),
        ("Табличка из тропического дерева", "jungle_sign"),
        ("Табличка из тёмного дуба", "dark_oak_sign"), ("Терракота", "terracotta"),
        ("Ткацкий станок", "loom"), ("Тонированное стекло", "tinted_glass"), ("Торт", "cake"),
        ("Тотем бессмертия", "totem_of_undying"), ("Точило", "grindstone"),
        ("Точёный медный блок", "chiseled_copper"), ("Трезубец", "trident"),
        ("Треска в ведре", "cod_bucket"), ("Тропинка", "dirt_path"),
        ("Тропическая рыба", "tropical_fish"), ("Тропическая рыба в ведре", "tropical_fish_bucket"),
        ("Тропическое дерево", "jungle_wood"), ("Трубчатый коралл", "tube_coral"), ("Туф", "tuff"),
        ("Туфовая ограда", "tuff_wall"), ("Туфовая плита", "tuff_slab"),
        ("Туфовые кирпичи", "tuff_bricks"), ("Туфовые ступеньки", "tuff_stairs"),
        ("Тушёные грибы", "mushroom_stew"), ("Тушёный кролик", "rabbit_stew"), ("Тыква", "pumpkin"),
        ("Тыквенный пирог", "pumpkin_pie"), ("Тёмный дуб", "dark_oak_wood"),
        ("Тёмный призмарин", "dark_prismarine"), ("Угленосный глубинный сланец", "deepslate_coal_ore"),
        ("Уголь", "coal"), ("Угольная руда", "coal_ore"), ("Угольный блок", "coal_block"),
        ("Удочка", "fishing_rod"), ("Удочка с искажённым грибом", "warped_fungus_on_a_stick"),
        ("Удочка с морковкой", "carrot_on_a_stick"), ("Узорчатая ваза", "decorated_pot"),
        ("Укреплённый глубинный сланец", "reinforced_deepslate"), ("Улей", "beehive"),
        ("Факел", "torch"), ("Факел душ", "soul_torch"), ("Факельник", "torchflower"),
        ("Фейерверк", "firework_rocket"),
        ("Фиолетовая глазурованная керамика", "purple_glazed_terracotta"),
        ("Фиолетовая керамика", "purple_terracotta"), ("Фиолетовая кровать", "purple_bed"),
        ("Фиолетовая свеча", "purple_candle"),
        ("Фиолетовая стеклянная панель", "purple_stained_glass_pane"),
        ("Фиолетовая шерсть", "purple_wool"), ("Фиолетовое стекло", "purple_stained_glass"),
        ("Фиолетовый бетон", "purple_concrete"), ("Фиолетовый ковёр", "purple_carpet"),
        ("Фиолетовый краситель", "purple_dye"), ("Фиолетовый мешок", "purple_bundle"),
        ("Фиолетовый сухой бетон", "purple_concrete_powder"), ("Фиолетовый флаг", "purple_banner"),
        ("Фиолетовый шалкеровый ящик", "purple_shulker_box"), ("Фонарь", "lantern"),
        ("Фонарь душ", "soul_lantern"), ("Хаустония серая", "azure_bluet"), ("Хвоя", "spruce_leaves"),
        ("Хлеб", "bread"), ("Хорус", "chorus_plant"), ("Хранилище", "vault"),
        ("Цветок хоруса", "chorus_flower"), ("Цветочный горшок", "flower_pot"),
        ("Цветущая азалия", "flowering_azalea"), ("Цветущий аметист", "budding_amethyst"),
        ("Цепь", "chain"), ("Чародейская книга", "enchanted_book"),
        ("Чародейский стол", "enchanting_table"), ("Часы", "clock"), ("Череп", "skeleton_skull"),
        ("Череп визер-скелета", "wither_skeleton_skull"), ("Черепаший панцирь", "turtle_helmet"),
        ("Черепаший щиток", "turtle_scute"), ("Черепашье яйцо", "turtle_egg"),
        ("Чернильный мешок", "ink_sac"), ("Чернит", "blackstone"),
        ("Чернитная ограда", "blackstone_wall"), ("Чернитная плита", "blackstone_slab"),
        ("Чернитные ступеньки", "blackstone_stairs"), ("Чистая карта", "map"),
        ("Чёрная глазурованная керамика", "black_glazed_terracotta"),
        ("Чёрная керамика", "black_terracotta"), ("Чёрная кровать", "black_bed"),
        ("Чёрная свеча", "black_candle"), ("Чёрная стеклянная панель", "black_stained_glass_pane"),
        ("Чёрная шерсть", "black_wool"), ("Чёрное стекло", "black_stained_glass"),
        ("Чёрный бетон", "black_concrete"), ("Чёрный ковёр", "black_carpet"),
        ("Чёрный краситель", "black_dye"), ("Чёрный мешок", "black_bundle"),
        ("Чёрный сухой бетон", "black_concrete_powder"), ("Чёрный флаг", "black_banner"),
        ("Чёрный шалкеровый ящик", "black_shulker_box"), ("Шалкеровый ящик", "shulker_box"),
        ("Щит", "shield"), ("Щиток броненосца", "armadillo_scute"), ("Элитры", "elytra"),
        ("Эндер-жемчуг", "ender_pearl"), ("Эндер-сундук", "ender_chest"), ("Эндерняк", "end_stone"),
        ("Эндерняковые кирпичи", "end_stone_bricks"), ("Энергорельсы", "powered_rail"),
        ("Яблоко", "apple"), ("Ядовитый картофель", "poisonous_potato"), ("Яйцо", "egg"),
        ("Яйцо дракона", "dragon_egg"), ("Яйцо нюхача", "sniffer_egg"),
        ("Якорь возрождения", "respawn_anchor")
    };

    private readonly System.Collections.ObjectModel.ObservableCollection<AdItem> _ads = new();
    private string _adSide = "sell";

    private static string AdIconUrl(string itemId) =>
        $"https://mcasset.cloud/26.2/assets/minecraft/textures/item/{itemId}.png";

    private static (string Name, string Id)? FindAdItem(string text)
    {
        var probe = (text ?? "").Trim().ToLowerInvariant();
        if (probe.Length == 0) return null;
        foreach (var entry in AdItems)
        {
            if (entry.Name.ToLowerInvariant() == probe || entry.Id == probe) return entry;
        }
        foreach (var entry in AdItems)
        {
            if (entry.Name.ToLowerInvariant().Contains(probe) || entry.Id.Contains(probe)) return entry;
        }
        return null;
    }

    private void ShowAdSuggest(ItemsControl target, TextBox source)
    {
        target.Items.Clear();
        var probe = source.Text.Trim().ToLowerInvariant();
        var matches = probe.Length == 0
            ? AdItems.Take(0)
            : AdItems.Where(e => e.Name.ToLowerInvariant().Contains(probe) || e.Id.Contains(probe)).Take(24);
        foreach (var entry in matches)
        {
            var button = new Button
            {
                Content = entry.Name,
                Tag = (target, source, entry.Name),
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 11,
            };
            button.Click += OnAdSuggestPick;
            target.Items.Add(button);
        }
    }

    private void OnAdSuggestPick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: var tag } button) return;
        var tuple = (ValueTuple<ItemsControl, TextBox, string>)tag;
        tuple.Item2.Text = tuple.Item3;
        tuple.Item1.Items.Clear();
        UpdateAdIconSoon();
    }

    private void OnAdItemSearch(object sender, TextChangedEventArgs e)
    {
        ShowAdSuggest(AdItemSuggest, AdItemBox);
        UpdateAdIconSoon();
    }

    private void OnAdPriceSearch(object sender, TextChangedEventArgs e)
    {
        ShowAdSuggest(AdPriceSuggest, AdPriceBox);
        UpdateAdIconSoon();
        UpdateAdPriceHint();
    }

    private void UpdateAdIcons()
    {
        var item = FindAdItem(AdItemBox.Text);
        if (item is not null)
        {
            AdItemIcon.Source = LoadCachedImage(AdIconUrl(item.Value.Id));
        }
        var price = FindAdItem(AdPriceBox.Text);
        if (price is not null)
        {
            AdPriceIcon.Source = LoadCachedImage(AdIconUrl(price.Value.Id));
        }
    }

    private CancellationTokenSource? _adIconCts;

    /// <summary>Иконки предметов подтягиваются фоном: сеть в UI-потоке вешает окно.</summary>
    private void UpdateAdIconSoon()
    {
        _adIconCts?.Cancel();
        var cts = new CancellationTokenSource();
        _adIconCts = cts;
        _ = Task.Run(async () =>
        {
            await Task.Delay(250, cts.Token).ConfigureAwait(false);
            var item = FindAdItem(AdItemBox.Text);
            var price = FindAdItem(AdPriceBox.Text);
            var itemImage = item is null ? null : await LoadImageAsync(AdIconUrl(item.Value.Id)).ConfigureAwait(false);
            var priceImage = price is null ? null : await LoadImageAsync(AdIconUrl(price.Value.Id)).ConfigureAwait(false);
            if (cts.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested) return;
                if (item is not null) AdItemIcon.Source = itemImage;
                if (price is not null) AdPriceIcon.Source = priceImage;
            });
        }, cts.Token).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private void OnAdSideClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string side }) return;
        _adSide = side == "buy" ? "buy" : "sell";
        Toast(_adSide == "buy" ? "Объявление: куплю" : "Объявление: продаю");
    }

    private void OnAdComposeClick(object sender, RoutedEventArgs e)
    {
        AdComposerCard.Visibility = AdComposerCard.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (AdComposerCard.Visibility == Visibility.Visible)
        {
            AdItemBox.Clear();
            AdPriceBox.Clear();
            AdItemCountBox.Text = "1";
            AdPriceCountBox.Text = "1";
            AdItemSuggest.Items.Clear();
            AdPriceSuggest.Items.Clear();
            AdItemIcon.Source = null;
            AdPriceIcon.Source = null;
            AdItemBox.Focus();
        }
    }

    private void OnAdCancelClick(object sender, RoutedEventArgs e)
    {
        AdComposerCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>Проверка «Мои сделки»: показываются только свои объявления.</summary>
    private bool _adsMineOnly;

    private async void OnAdsMineOnlyClick(object sender, RoutedEventArgs e)
    {
        _adsMineOnly = AdsMineOnly.IsChecked == true;
        await LoadAdsAsync();
    }

    private async void OnAdsRefreshClick(object sender, RoutedEventArgs e) => await LoadAdsAsync();

    /// <summary>Средняя цена за штуку по открытым объявлениям: предмет → цена оплаты → средняя.</summary>
    private readonly Dictionary<(string Item, string Price), double> _marketUnit =
        new(new MarketKeyComparer());

    private sealed class MarketKeyComparer : IEqualityComparer<(string Item, string Price)>
    {
        public bool Equals((string Item, string Price) x, (string Item, string Price) y) =>
            string.Equals(x.Item, y.Item, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Price, y.Price, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Item, string Price) obj) =>
            HashCode.Combine(obj.Item.ToLowerInvariant(), obj.Price.ToLowerInvariant());
    }

    /// <summary>Пересчитывает средние цены рынка и проставляет подсказки конкретным объявлениям.</summary>
    private void RebuildMarket()
    {
        _marketUnit.Clear();
        var sums = new Dictionary<(string Item, string Price), (double Value, int Count)>(new MarketKeyComparer());
        foreach (var ad in _ads)
        {
            if (ad.Status == "closed" || ad.ItemCount <= 0) continue;
            var key = (ad.Item, ad.PriceItem);
            var unit = (double)ad.PriceCount / ad.ItemCount;
            if (unit <= 0) continue;
            sums[key] = sums.TryGetValue(key, out var acc)
                ? (acc.Value + unit, acc.Count + 1)
                : (unit, 1);
        }
        foreach (var pair in sums)
        {
            _marketUnit[pair.Key] = pair.Value.Value / pair.Value.Count;
        }

        foreach (var ad in _ads)
        {
            ad.MarketHint = "";
            if (ad.Status == "closed" || ad.ItemCount <= 0) continue;
            if (!_marketUnit.TryGetValue((ad.Item, ad.PriceItem), out var average) || average <= 0) continue;
            var unit = (double)ad.PriceCount / ad.ItemCount;
            if (unit <= 0) continue;
            var delta = (unit - average) / average * 100;
            string verdict;
            if (Math.Abs(delta) < 5) verdict = "в среднем по рынку";
            else if (delta < 0) verdict = $"дешевле рынка на {Math.Abs(Math.Round(delta)):0}%";
            else verdict = $"дороже рынка на {Math.Round(delta):0}%";
            ad.MarketHint = $"Рынок: ~{average:0.##} за шт. · это {verdict}";

        }
    }

    /// <summary>Подсказка в форме: куда ставить цену по текущему рынку.</summary>
    private void UpdateAdPriceHint()
    {
        var item = FindAdItem(AdItemBox.Text);
        var price = FindAdItem(AdPriceBox.Text);
        if (item is null || price is null)
        {
            AdPriceHint.Text = "";
            return;
        }
        var unit = _marketUnit.TryGetValue((item.Value.Name, price.Value.Name), out var avg) ? avg : 0;
        if (unit <= 0)
        {
            AdPriceHint.Text = "Рынок: цен пока нет";
            return;
        }
        var itemCount = int.TryParse(AdItemCountBox.Text.Trim(), out var ic) ? Math.Max(ic, 1) : 1;
        var suggested = Math.Max(1, (int)Math.Round(unit * itemCount));
        AdPriceHint.Text = $"Рынок ~{unit:0.##} за шт. · для {itemCount} шт. это ~{suggested} {price.Value.Name}";

    }

    /// <summary>Загружает объявления с сервера и раскладывает: открытые сверху, закрытые внизу.</summary>
    private async Task LoadAdsAsync()
    {
        AdsState.Text = "Загрузка объявлений…";
        try
        {
            using var response = await ChatHttp.GetAsync(ChatBaseUrl + "/ads");
            if (!response.IsSuccessStatusCode)
            {
                AdsState.Text = "Сервер объявлений недоступен: " + (int)response.StatusCode;
                return;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var mine = _overview?.InstanceId ?? "";
            _ads.Clear();
            foreach (var item in doc.RootElement.Arr("ads"))
            {
                var ad = new AdItem
                {
                    Id = (long)item.Num("id"),
                    InstanceId = item.Str("instance_id"),
                    Username = item.Str("username"),
                    Side = item.Str("side", "sell"),
                    Item = item.Str("item"),
                    ItemCount = (int)item.Num("item_count"),
                    PriceItem = item.Str("price_item"),
                    PriceCount = (int)item.Num("price_count"),
                    Status = item.Str("status", "open"),
                    ClosedWith = item.Str("closed_with"),
                    IsMine = string.Equals(item.Str("instance_id"), mine, StringComparison.OrdinalIgnoreCase),
                };
                foreach (var offer in item.Arr("offers"))
                {
                    ad.Offers.Add(new AdOffer
                    {
                        Id = (long)offer.Num("id"),
                        AdId = ad.Id,
                        Username = offer.Str("username"),
                        InstanceId = offer.Str("instance_id"),
                        Count = (int)offer.Num("count"),
                        Price = (int)offer.Num("price"),
                        State = offer.Str("state", "pending"),
                        IsAuthor = ad.IsMine,
                    });
                }
                _ads.Add(ad);
            }

            var ordered = _ads
                .OrderBy(a => a.Status == "closed" ? 1 : 0)
                .ThenByDescending(a => a.Id)
                .ToList();
            _ads.Clear();
            foreach (var ad in ordered) _ads.Add(ad);

            RebuildMarket();
            foreach (var ad in _ads) ad.IsWatched = _watched.ContainsKey(ad.Id);
            SyncWatched();
            var visible = _ads.Where(a => !_adsMineOnly || a.IsMine).ToList();
            AdsList.ItemsSource = visible;
            var openAds = _ads.Count(a => a.Status != "closed");
            var mineDeals = _ads.Count(a => a.IsMine && a.Status == "closed");
            AdsState.Text = _adsMineOnly
                ? $"Мои сделки: закрыто {mineDeals} · моих объявлений {_ads.Count(a => a.IsMine)}"
                : $"Открытых объявлений: {openAds} · закрытых: {_ads.Count - openAds}";

            foreach (var ad in _ads)
            {
                var match = FindAdItem(ad.Item);
                if (match is null) continue;
                if (LoadCachedImage(AdIconUrl(match.Value.Id)) is { } cached) ad.ItemIcon = cached;
            }
            AdsList.Items.Refresh();
            _ = Task.Run(async () =>
            {
                foreach (var ad in ordered)
                {
                    var match = FindAdItem(ad.Item);
                    if (match is null) continue;
                    var image = await LoadImageAsync(AdIconUrl(match.Value.Id)).ConfigureAwait(false);
                    if (image is null) continue;
                    await Dispatcher.InvokeAsync(() => ad.ItemIcon = image);
                }
            });
        }
        catch (Exception exc)
        {
            AdsState.Text = "Не удалось загрузить объявления: " + exc.Message;
        }
    }

    private async void OnAdPublishClick(object sender, RoutedEventArgs e)
    {
        var item = FindAdItem(AdItemBox.Text);
        var price = FindAdItem(AdPriceBox.Text);
        if (item is null || price is null)
        {
            Toast("Выберите предмет и цену из списка", true);
            return;
        }
        var itemCount = int.TryParse(AdItemCountBox.Text.Trim(), out var ic) ? Math.Clamp(ic, 1, 100000) : 1;
        var priceCount = int.TryParse(AdPriceCountBox.Text.Trim(), out var pc) ? Math.Clamp(pc, 1, 100000) : 1;
        var payload = new
        {
            instance_id = _overview?.InstanceId ?? "",
            username = _overview?.Account?.Username ?? "",
            side = _adSide,
            item = item.Value.Name,
            item_count = itemCount,
            price_item = price.Value.Name,
            price_count = priceCount,
        };
        var answer = await PostAdAsync("/ad/create", payload);
        if (answer is null) return;
        AdComposerCard.Visibility = Visibility.Collapsed;
        Toast("Объявление опубликовано");
        await LoadAdsAsync();
    }

    private async void OnAdOfferClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AdItem ad }) return;
        var dialog = new AdOfferWindow(ad)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true) return;
        var payload = new
        {
            instance_id = _overview?.InstanceId ?? "",
            username = _overview?.Account?.Username ?? "",
            ad_id = ad.Id,
            count = dialog.OfferCount,
            price = dialog.OfferPrice,
        };
        if (await PostAdAsync("/ad/offer", payload) is null) return;
        Toast("Предложение отправлено");
        await LoadAdsAsync();
    }

    private async void OnAdDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AdItem ad }) return;
        var answer = MessageBox.Show(this, "Удалить объявление?", "Объявления",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        if (await PostAdAsync("/ad/delete", new { instance_id = _overview?.InstanceId ?? "", ad_id = ad.Id }) is null)
        {
            return;
        }
        Toast("Объявление удалено");
        await LoadAdsAsync();
    }

    private async void OnOfferAccept(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AdOffer offer }) return;
        if (await DecideOfferAsync(offer, true)) OpenAdOfferChat(offer);
    }

    private async void OnOfferDecline(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AdOffer offer }) return;
        await DecideOfferAsync(offer, false);
    }

    private async Task<bool> DecideOfferAsync(AdOffer offer, bool accept)
    {
        var payload = new
        {
            instance_id = _overview?.InstanceId ?? "",
            ad_id = offer.AdId,
            offer_id = offer.Id,
            accept,
        };
        if (await PostAdAsync("/ad/decide", payload) is null) return false;
        Toast(accept ? "Предложение принято" : "Предложение отклонено");
        await LoadAdsAsync();
        return true;
    }

    /// <summary>Слежение за чужими объявлениями: id → последняя известная цена.</summary>
    private readonly Dictionary<long, string> _watched = new();

    private static string ChatSeenPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Craftunity", "chat-seen.json");

    /// <summary>Последний id, который пользователь реально видел. Переживает перезапуск.</summary>
    private void LoadChatSeen()
    {
        try
        {
            if (!File.Exists(ChatSeenPath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(ChatSeenPath));
            if (map is null) return;
            foreach (var pair in map) _chatSeen[pair.Key] = pair.Value;
            if (_chatSeen.TryGetValue(_chatChannel, out var last))
            {
                _chatSeenHigh = last;
                if (last > _chatLastId) _chatLastId = last;
            }
        }
        catch
        {
            // повреждённый файл не должен мешать запуску
        }
    }

    private void SaveChatSeen()
    {
        try
        {
            if (_chatLastId > 0) _chatSeen[_chatChannel] = _chatLastId;
            Directory.CreateDirectory(Path.GetDirectoryName(ChatSeenPath)!);
            File.WriteAllText(ChatSeenPath, JsonSerializer.Serialize(_chatSeen));
        }
        catch
        {
            // отметка не критична
        }
    }

    private readonly Dictionary<string, long> _chatSeen = new();

    /// <summary>Самый большой id, который пользователь уже видел в этом канале.</summary>
    private long _chatSeenHigh;

    private static string WachedPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Craftunity", "watched-ads.json");

    private void LoadWatched()
    {
        _watched.Clear();
        try
        {
            if (!File.Exists(WachedPath)) return;
            var map = JsonSerializer.Deserialize<Dictionary<long, string>>(File.ReadAllText(WachedPath));
            foreach (var pair in map ?? new Dictionary<long, string>()) _watched[pair.Key] = pair.Value;
        }
        catch
        {
            // повреждённый файл не должен мешать запуску
        }
    }

    private void SaveWatched()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WachedPath)!);
            File.WriteAllText(WachedPath, JsonSerializer.Serialize(_watched));
        }
        catch
        {
            // список можно восстановить вручную
        }
    }

    private static string WatchSignature(AdItem ad) =>
        ad.Status == "closed" ? "closed" : $"{ad.ItemCount}x{ad.Item} за {ad.PriceCount} {ad.PriceItem}";

    /// <summary>Сообщает в колокольчик, если у отслеживаемого объявления изменилась цена или оно закрыто.</summary>
    private void SyncWatched()
    {
        if (_watched.Count == 0) return;
        var changed = new List<BellNote>();
        foreach (var ad in _ads)
        {
            if (!_watched.TryGetValue(ad.Id, out var was)) continue;
            var now = WatchSignature(ad);
            if (was == now) continue;
            _watched[ad.Id] = now;
            var title = ad.Status == "closed"
                ? $"«{ad.Item}» закрыто"
                : $"Цена изменилась: «{ad.Item}»";
            var body = ad.Status == "closed"
                ? $"Автор {ad.Username} закрыл объявление (договорился с {ad.ClosedWith})."
                : $"{ad.ItemCount} × {ad.Item} за {ad.PriceCount} {ad.PriceItem}.";
            changed.Add(new BellNote
            {
                Kind = "ad",
                Title = title,
                Body = body,
                TimeLabel = DateTime.Now.ToString("HH:mm"),
            });
        }
        if (changed.Count == 0) return;
        foreach (var note in changed) PushBell(note);
        SaveWatched();
    }

    private void OnAdWatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AdItem ad }) return;
        if (_watched.ContainsKey(ad.Id))
        {
            _watched.Remove(ad.Id);
            ad.IsWatched = false;
            SaveWatched();
            Toast("Больше не следим за объявлением");
        }
        else
        {
            _watched[ad.Id] = WatchSignature(ad);
            ad.IsWatched = true;
            SaveWatched();
            Toast("Следим: сообщим, если цена изменится или объявление закроют");
        }
        // Пересобираем список, чтобы подпись кнопки обновилась.
        var ordered = _ads.ToList();
        _ads.Clear();
        foreach (var item in ordered) _ads.Add(item);
        AdsList.ItemsSource = _ads.Where(a => !_adsMineOnly || a.IsMine).ToList();
    }

    private AdOffer? _notifyOffer;
    private readonly HashSet<string> _adsSeen = new(StringComparer.OrdinalIgnoreCase);
    private bool _adsSeeded;

    /// <summary>Показывает окно по центру: автору — решение по предложению, остальным — приглашение.</summary>
    /// <summary>Опрашивает объявления и показывает окно по центру при новых предложениях.</summary>
    private async Task PollAdsAsync()
    {
        using var response = await ChatHttp.GetAsync(ChatBaseUrl + "/ads");
        if (!response.IsSuccessStatusCode) return;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var mine = _overview?.InstanceId ?? "";
        var pending = new List<(AdOffer Offer, AdItem Ad)>();

        foreach (var item in doc.RootElement.Arr("ads"))
        {
            var isMine = string.Equals(item.Str("instance_id"), mine, StringComparison.OrdinalIgnoreCase);
            if (!isMine) continue;
            var ad = new AdItem
            {
                Id = (long)item.Num("id"),
                InstanceId = item.Str("instance_id"),
                Username = item.Str("username"),
                Side = item.Str("side", "sell"),
                Item = item.Str("item"),
                ItemCount = (int)item.Num("item_count"),
                PriceItem = item.Str("price_item"),
                PriceCount = (int)item.Num("price_count"),
                Status = item.Str("status", "open"),
                ClosedWith = item.Str("closed_with"),
                IsMine = true,
            };
            if (ad.Status != "open") continue;
            foreach (var offer in item.Arr("offers"))
            {
                if (offer.Str("state", "pending") != "pending") continue;
                var parsed = new AdOffer
                {
                    Id = (long)offer.Num("id"),
                    AdId = ad.Id,
                    Username = offer.Str("username"),
                    InstanceId = offer.Str("instance_id"),
                    Count = (int)offer.Num("count"),
                    Price = (int)offer.Num("price"),
                    State = "pending",
                    IsAuthor = true,
                };
                var key = ad.Id + ":" + parsed.Id;
                if (!_adsSeen.Add(key)) continue;
                pending.Add((parsed, ad));
            }
        }

        // Первый проход только запоминает уже существующие предложения.
        if (!_adsSeeded)
        {
            _adsSeeded = true;
            return;
        }

        if (pending.Count == 0) return;
        await Dispatcher.InvokeAsync(() =>
        {
            foreach (var (offer, ad) in pending)
            {
                PushBell(new BellNote
                {
                    Kind = "ad",
                    Title = $"Предложение по «{ad.Item}»",
                    Body = $"{offer.Username}: {offer.Count} шт. за {offer.Price}. Принять?",
                    TimeLabel = DateTime.Now.ToString("HH:mm"),
                });
            }
            if (AdNotification.Visibility == Visibility.Visible) return;
            ShowAdNotification(pending[0].Offer, pending[0].Ad);
        });
    }

    private LauncherUpdate.Release? _pendingRelease;

    /// <summary>Кнопка на плашке: спрашивает подтверждение и ставит обновление.</summary>
    private async void OnUpdateBannerClick(object sender, RoutedEventArgs e)
    {
        var release = _pendingRelease;
        if (release is null) return;
        var answer = MessageBox.Show(
            this,
            $"Скачать и установить версию {release.Version}? Лаунчер закроется, установщик запустится сам.",
            "Обновление Craftunity",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        string? installer = null;
        try
        {
            StatusText.Text = "Скачивание обновления:";
            installer = await LauncherUpdate.DownloadAsync(release, p =>
                Dispatcher.InvokeAsync(() => StatusText.Text = $"Скачивание обновления: {p:P0}"));
        }
        catch
        {
            installer = null;
        }
        if (installer is null)
        {
            StatusText.Text = "";
            MessageBox.Show(this,
                "Не удалось скачать обновление. Попробуйте ещё раз позже или скачайте установщик с сайта.",
                "Обновление Craftunity", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        StatusText.Text = "Запуск установщика:";
        if (LauncherUpdate.RunInstallerAfterExit(installer))
        {
            _pollCts?.Cancel();
            Close();
        }
    }

    private void ShowAdNotification(AdOffer offer, AdItem ad)
    {
        _notifyOffer = offer;
        var isAuthor = ad.IsMine;
        AdNotificationTitle.Text = isAuthor
            ? $"Предложение на «{ad.Title}»"
            : $"Предложение по «{ad.Title}» принято";
        AdNotificationText.Text = isAuthor
            ? $"{offer.Username} предлагает {offer.Count} шт. за {offer.Price}. Принять?"
            : $"{ad.Username} принял(а) ваше предложение: {offer.Count} шт. за {offer.Price}.";
        var mark = isAuthor ? Visibility.Visible : Visibility.Collapsed;
        AdNotificationYes.Visibility = mark;
        AdNotificationNo.Visibility = mark;
        AdNotification.Visibility = Visibility.Visible;
    }

    private void OnAdNotificationClose(object sender, RoutedEventArgs e)
    {
        // Уведомление прячется только решением «Да» или «Нет».
        if (_notifyOffer is null)
        {
            AdNotification.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnAdNotificationYes(object sender, RoutedEventArgs e)
    {
        var offer = _notifyOffer;
        AdNotification.Visibility = Visibility.Collapsed;
        _notifyOffer = null;
        if (offer is null) return;
        if (await DecideOfferAsync(offer, true)) OpenAdOfferChat(offer);
    }

    private async void OnAdNotificationNo(object sender, RoutedEventArgs e)
    {
        var offer = _notifyOffer;
        AdNotification.Visibility = Visibility.Collapsed;
        _notifyOffer = null;
        if (offer is null) return;
        await DecideOfferAsync(offer, false);
    }

    /// <summary>После одобрения предложения открывает личные с тем, чьё предложение приняли.</summary>
    // ------------------------------------------------------------------ профиль игрока

    private string _profileName = "";

    /// <summary>Собирает профиль по тем же данным, что видят все лаунчеры: /ads и /clients.</summary>
    private async Task<PlayerProfile?> BuildProfileAsync(string username, string instanceId)
    {
        var name = (username ?? "").Trim();
        if (name.Length == 0) return null;

        var profile = new PlayerProfile { Username = name, InstanceId = instanceId };
        var currencyHint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var response = await ChatHttp.GetAsync(ChatBaseUrl + "/ads");
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                foreach (var item in doc.RootElement.Arr("ads"))
                {
                    var author = item.Str("username").Trim();
                    var adInstance = item.Str("instance_id");
                    var isMine = string.Equals(author, name, StringComparison.OrdinalIgnoreCase);
                    var status = item.Str("status", "open");
                    if (string.IsNullOrEmpty(profile.InstanceId) && isMine) profile.InstanceId = adInstance;

                    var side = item.Str("side", "sell");
                    var adItem = item.Str("item");
                    var adCount = (int)item.Num("item_count");
                    var priceItem = item.Str("price_item");
                    var priceCount = (int)item.Num("price_count");

                    if (isMine && status == "open") profile.OpenAds++;
                    if (isMine && status == "closed")
                    {
                        if (side == "sell") profile.SoldDeals++;
                        else profile.BoughtDeals++;
                        profile.DealLines.Add(
                            $"{(side == "sell" ? "Продал" : "Купил")}: {adCount} x {adItem} за {priceCount} {priceItem} - с {item.Str("closed_with")}");
                    }

                    foreach (var offer in item.Arr("offers"))
                    {
                        var offerUser = offer.Str("username").Trim();
                        var state = offer.Str("state", "pending");
                        if (isMine)
                        {
                            if (state == "pending") profile.PendingOffers++;
                            else if (state == "accepted") profile.AcceptedOffers++;
                            else if (state == "declined") profile.DeclinedOffers++;
                        }
                        else if (string.Equals(offerUser, name, StringComparison.OrdinalIgnoreCase))
                        {
                            if (state == "pending") profile.PendingOffers++;
                            else if (state == "accepted") profile.AcceptedOffers++;
                            else if (state == "declined") profile.DeclinedOffers++;
                        }
                    }
                }
            }
        }
        catch
        {
            // профиль не критичен для работы лаунчера
        }

        try
        {
            using var response = await ChatHttp.GetAsync($"{ChatBaseUrl}/clients?light=1&token={ClientsToken}");
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("clients", out var clients)
                    && clients.ValueKind == JsonValueKind.Array)
                {
                    foreach (var client in clients.EnumerateArray())
                    {
                        if (!string.Equals(client.Str("username").Trim(), name, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (string.IsNullOrEmpty(profile.InstanceId))
                        {
                            profile.InstanceId = client.Str("instance_id");
                        }
                        var lastSeen = client.Num("last_seen");
                        var startedAt = client.Num("started_at");
                        if (lastSeen > 0)
                        {
                            var seen = DateTimeOffset.FromUnixTimeSeconds((long)lastSeen).ToLocalTime();
                            profile.LastSeenText = "был(а) в сети " + seen.ToString("dd.MM HH:mm");
                        }
                        if (lastSeen > 0 && startedAt > 0 && lastSeen >= startedAt)
                        {
                            var span = TimeSpan.FromSeconds(lastSeen - startedAt);
                            profile.PlaytimeText = "сессия " + FormatPlaytime(span);
                        }
                        break;
                    }
                }
            }
        }
        catch
        {
            // реестр необязателен
        }

        profile.DealLines.Reverse();
        while (profile.DealLines.Count > 5) profile.DealLines.RemoveAt(profile.DealLines.Count - 1);
        return profile;
    }

    /// <summary>До входа показываем карточки входа, после — профиль с кнопкой «Выйти».</summary>
    private string _profileShown = "";
    private bool _profileLoading;

    private void SyncAccountPanels()
    {
        if (LoginPanel is null || ProfilePanel is null) return;
        var loggedIn = _overview?.Account is not null;
        LoginPanel.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        ProfilePanel.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ShowProfileAsync(string username, string instanceId = "")
    {
        var name = (username ?? "").Trim();
        if (name.Length == 0) name = _overview?.Account?.Username ?? "";
        if (name.Length == 0)
        {
            ProfileName.Text = "Аккаунт не выбран";
            ProfileType.Text = "Войдите через ely.by или выберите оффлайн-ник";
            SyncAccountPanels();
            return;
        }

        _profileLoading = true;
        try
        {
        var profile = await BuildProfileAsync(name, instanceId);
        if (profile is null) return;
        _profileName = profile.Username;
        _lastProfile = profile;

        ProfileName.Text = profile.Username;
        ProfileType.Text = string.Equals(name, _overview?.Account?.Username ?? "", StringComparison.OrdinalIgnoreCase)
            ? (_overview?.Account?.Type == "ely" ? "Аккаунт ely.by" : "Оффлайн-аккаунт")
            : "Игрок Craftunity";

        ProfileOpenAds.Text = profile.OpenAds.ToString();
        ProfileSold.Text = profile.SoldDeals.ToString();
        ProfileBought.Text = profile.BoughtDeals.ToString();
        ProfilePending.Text = profile.PendingOffers.ToString();

        ProfileRating.Text = profile.AcceptedOffers + profile.DeclinedOffers == 0
            ? "нет сделок"
            : profile.Rating + "%";
        ProfilePlaytime.Text = string.IsNullOrWhiteSpace(profile.PlaytimeText)
            ? "время не учтено"
            : profile.PlaytimeText;

        ProfileDeals.ItemsSource = profile.DealLines;
        ProfileEmpty.Visibility = profile.DealLines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProfileLink.Text = profile.ShareLink;

        ProfileAvatar.Source = await LoadProfileAvatarAsync(profile.Username);
        }
        finally
        {
            _profileLoading = false;
        }
    }

    /// <summary>Голова игрока: скин из реестра клиентов, иначе по нику через ely.by.</summary>
    private async Task<System.Windows.Media.ImageSource?> LoadProfileAvatarAsync(string name)
    {
        try
        {
            if (IsOwnAuthor(name) && _heroAvatar is not null) return _heroAvatar;
            var iconUrl = IconFromRegistry(name);
            if (!string.IsNullOrEmpty(iconUrl))
            {
                var fromRegistry = await LoadImageAsync(iconUrl);
                var head = fromRegistry is BitmapSource skin ? CropHead(skin) ?? skin : null;
                if (head is not null) return head;
            }
            var fallback = await SkinForAsync(name, "https://skinsystem.ely.by/skins/" + Uri.EscapeDataString(name));
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Асинхронный вариант SkinFor: не блокирует UI-поток.</summary>
    private static async Task<System.Windows.Media.ImageSource?> SkinForAsync(string key, string skinUrl)
    {
        var cacheKey = "reg:" + key;
        lock (SkinCache)
        {
            if (SkinCache.TryGetValue(cacheKey, out var cached)) return cached;
        }
        var image = await LoadImageAsync(skinUrl);
        BitmapSource? head = image is BitmapSource source ? CropHead(source) ?? source : null;
        lock (SkinCache) SkinCache[cacheKey] = head;
        return head;
    }

    private PlayerProfile? _lastProfile;

    private static string FormatPlaytime(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "меньше минуты";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} мин";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} ч {span.Minutes} мин";
        return $"{(int)span.TotalDays} д {span.Hours} ч";
    }

    /// <summary>
    /// Копирует текст в буфер обмена. Windows может держать буфер занятым чужим процессом
    /// (CLIPBRD_E_CANT_OPEN), поэтому пробуем несколько раз с короткой паузой.
    /// </summary>
    private static bool TryCopyText(string text, int attempts = 5, int delayMs = 60)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                if (attempt == attempts - 1) return false;
                Thread.Sleep(delayMs);
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private void OnProfileShareClick(object sender, RoutedEventArgs e)
    {
        var name = _lastProfile?.Username;
        if (string.IsNullOrWhiteSpace(name)) name = _overview?.Account?.Username ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            Toast("Сначала выберите аккаунт", true);
            return;
        }
        var link = "craftunity://profile/" + Uri.EscapeDataString(name);
        if (TryCopyText(link))
        {
            Toast("Ссылка на профиль скопирована");
        }
        else
        {
            Toast("Буфер обмена занят другой программой. Попробуйте ещё раз.", true);
        }
    }

    /// <summary>Открывает профиль из ссылки craftunity://profile/ник.</summary>
    /// <summary>Вызов из App при запуске с ссылкой craftunity://profile/ник.</summary>
    public void OpenProfileLinkFromArgs(string arg) => _ = OpenProfileLinkAsync(arg);

    private async Task OpenProfileLinkAsync(string arg)
    {
        const string marker = "craftunity://profile/";
        var index = arg.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return;
        var name = Uri.UnescapeDataString(arg[(index + marker.Length)..]).Trim().TrimEnd('/');
        if (name.Length == 0) return;
        if (Nav.SelectedIndex != 5) Nav.SelectedIndex = 5;
        await ShowProfileAsync(name);
    }

    private void OpenAdOfferChat(AdOffer offer)
    {
        try
        {
            if (Nav.SelectedIndex != 6) Nav.SelectedIndex = 6;
            OpenDirectChat(offer.InstanceId, offer.Username, true);
            if (_chatChannel != "dm") return;
            Dispatcher.InvokeAsync(() =>
            {
                ChatInput.Focus();
                Toast("Личные с " + (string.IsNullOrWhiteSpace(offer.Username) ? "автором предложения" : offer.Username));
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception exc)
        {
            Toast(exc.Message, true);
        }
    }

    private async Task<JsonElement?> PostAdAsync(string path, object payload)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8, "application/json");
            using var response = await ChatHttp.PostAsync(ChatBaseUrl + path, content);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Toast("Объявления: " + text, true);
                return null;
            }
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (Exception exc)
        {
            Toast("Объявления: " + exc.Message, true);
            return null;
        }
    }

    // ------------------------------------------------------------------ чат

    private const string ChatBaseUrl = "https://craftunity-heartbeat.netlify.app";
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

    // ------------------------------------------------------------------ новости

    private readonly ObservableCollection<ChatMessage> _news = new();

    /// <summary>Новости пишет только владелец сервера.</summary>
    private bool CanPublishNews =>
        string.Equals(_overview?.Account?.Username ?? "", "Danipro914", StringComparison.OrdinalIgnoreCase);

    private void ApplyNewsRights()
    {
        var can = CanPublishNews;
        NewsComposer.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
        NewsReadOnlyNote.Visibility = can ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task LoadNewsAsync()
    {
        try
        {
            ApplyNewsRights();
            using var response = await ChatHttp.GetAsync(ChatBaseUrl + "/messages?since=0&channel=news");
            if (!response.IsSuccessStatusCode)
            {
                NewsState.Text = "Сервер недоступен: " + (int)response.StatusCode;
                return;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var rows = new List<ChatMessage>();
            foreach (var item in doc.RootElement.Arr("messages"))
            {
                rows.Add(new ChatMessage
                {
                    Id = (long)item.Num("id"),
                    InstanceId = item.Str("instance_id"),
                    Author = item.Str("username", "Гость"),
                    Text = item.Str("text"),
                    TimeLabel = FormatChatTime(item.Num("at")),
                });
            }
            rows.Sort((a, b) => b.Id.CompareTo(a.Id));
            var mine = _overview?.InstanceId ?? "";
            foreach (var row in rows)
            {
                row.NewsMine = !string.IsNullOrEmpty(mine)
                               && string.Equals(row.InstanceId, mine, StringComparison.OrdinalIgnoreCase);
            }
            _news.Clear();
            foreach (var row in rows) _news.Add(row);
            NewsList.ItemsSource = _news;
            HomeNewsList.ItemsSource = _news.Take(3).ToList();
            NewsEmpty.Visibility = _news.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HomeNewsEmpty.Visibility = _news.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            NewsState.Text = "";
        }
        catch (Exception exc)
        {
            NewsState.Text = "Новости недоступны: " + exc.Message;
        }
    }

    private async void OnNewsPublishClick(object sender, RoutedEventArgs e)
    {
        if (!CanPublishNews)
        {
            NewsState.Text = "Новости публикует только Danipro914.";
            return;
        }
        var text = NewsInput.Text.Trim();
        if (text.Length == 0) return;
        NewsState.Text = "Публикация:";
        try
        {
            var payload = new
            {
                instance_id = _overview?.InstanceId ?? "",
                username = _overview?.Account?.Username ?? "",
                text,
                channel = "news",
            };
            using var content = new StringContent(JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8, "application/json");
            using var response = await ChatSendHttp.PostAsync(ChatBaseUrl + "/message", content);
            if (!response.IsSuccessStatusCode)
            {
                NewsState.Text = "Не удалось опубликовать: " + (int)response.StatusCode;
                return;
            }
            NewsInput.Clear();
            await LoadNewsAsync();
            Toast("Новость опубликована");
        }
        catch (Exception exc)
        {
            NewsState.Text = "Не удалось опубликовать: " + exc.Message;
        }
    }

    private async void OnNewsEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessage news }) return;
        var dialog = new ChatEditWindow(news.Text) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var body = dialog.MessageText.Trim();
        if (string.IsNullOrWhiteSpace(body) || body == news.Text) return;
        if (await PostNewsAsync("/message/edit", new { id = news.Id, text = body })) Toast("Новость изменена");
    }

    private async void OnNewsDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChatMessage news }) return;
        var answer = MessageBox.Show(this,
            "Удалить эту новость? Она пропадёт у всех игроков.",
            "Удалить новость", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        if (await PostNewsAsync("/message/delete", new { id = news.Id })) Toast("Новость удалена");
    }

    /// <summary>Правка и удаление новости: тот же приёмник, что у чата, но канал news.</summary>
    private async Task<bool> PostNewsAsync(string path, object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                instance_id = _overview?.InstanceId ?? "",
                id = GetProp<long>(payload, "id"),
                text = GetProp<string>(payload, "text"),
                channel = "news",
                peer = "",
            });
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await ChatHttp.PostAsync(ChatBaseUrl + path, content);
            if (!response.IsSuccessStatusCode)
            {
                var answer = await response.Content.ReadAsStringAsync();
                NewsState.Text = response.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "Это не ваша новость"
                    : "Ошибка: " + (string.IsNullOrWhiteSpace(answer) ? ((int)response.StatusCode).ToString() : answer);
                return false;
            }
            await LoadNewsAsync();
            NewsState.Text = "";
            return true;
        }
        catch (Exception exc)
        {
            NewsState.Text = "Ошибка: " + exc.Message;
            return false;
        }
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
    private void OpenDirectChat(string? instanceId, string name, bool openOffline = false)
    {
        var id = (instanceId ?? "").Trim();
        var mine = _overview?.InstanceId ?? "";
        if (string.IsNullOrEmpty(id) || id == mine)
        {
            // Локальной записи нет - ищем по нику в реестре клиентов.
            var hit = _clientPeers.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is null && !openOffline)
            {
                ChatState.Text = "Не удалось открыть личные: игрок не в сети";
                return;
            }
            if (hit is not null) id = hit.Id;
        }
        if (string.IsNullOrEmpty(id))
        {
            // Собеседник офлайн и его id неизвестен - открываем по нику.
            id = "name:" + (name ?? "").Trim().ToLowerInvariant();
        }
        _chatChannel = "dm";
        _chatPeerId = id;
        _chatPeerName = name ?? "";
        ChatRooms.SelectedItem = null;

        // Открыли чат вручную — он снова нужен в списке.
        if (_hiddenPeers.Remove(id)) SaveHiddenPeers();

        // Собеседника из реестра может не быть (он офлайн): запоминаем его,
        // чтобы FillChatPeers добавил диалог в список личных.
        if (!string.IsNullOrEmpty(name)) _chatHistoryPeers[id] = name;

        // Список перестраиваем всегда: иначе новый собеседник не появляется
        // при первом открытии ЛС, если чат не был скрыт.
        FillChatPeers();

        ChatPeers.SelectedItem = ChatPeers.Items
            .OfType<ClientEntry>()
            .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

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
        NavAds.Content = compact ? "📢" : "📢  Объявления";
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
            // Счётчик: только те, у кого запущена игра и кто на связи.
            var inGame = 0;
            foreach (var presence in states.Values)
            {
                if (presence == PresenceState.InGame) inGame++;
            }
            var inGameText = inGame.ToString();
            await Dispatcher.InvokeAsync(() =>
            {
                if (OnlinePillText is not null) OnlinePillText.Text = inGameText;
            });

            _clientPeers = peers;
            _clientIconsAt = DateTime.UtcNow;
            await Dispatcher.InvokeAsync(FillChatPeers);
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
    /// <summary>F5 — обновить текущий экран, Ctrl+Enter — запустить игру, Esc — закрыть оверлеи.</summary>
    private async void OnGlobalKeyDown(object sender, KeyEventArgs e)
    {
        // Esc: сначала закрываем слои, потом уже что-то другое.
        if (e.Key == Key.Escape)
        {
            if (PhotoViewer.Visibility == Visibility.Visible)
            {
                OnPhotoViewerClose(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (ProjectOverlay.Visibility == Visibility.Visible)
            {
                ProjectOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            if (BellPanel.Visibility == Visibility.Visible)
            {
                BellPanel.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            return;
        }

        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await RefreshCurrentPageAsync();
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            OnPlayClick(this, new RoutedEventArgs());
        }
    }

    /// <summary>Обновляет данные того экрана, который сейчас открыт.</summary>
    private async Task RefreshCurrentPageAsync()
    {
        switch (Nav.SelectedIndex)
        {
            case 1:
                await SearchAsync(false);
                break;
            case 2:
                await ReloadInstalledAsync();
                break;
            case 3:
                await LoadFilesAsync();
                break;
            case 6:
                await LoadChatAsync(true);
                break;
            case 7:
                await LoadAdsAsync();
                break;
            case 8:
                await LoadNewsAsync();
                break;
            default:
                await RefreshAsync();
                break;
        }
        Toast("Обновлено");
    }

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

    private readonly ObservableCollection<BellNote> _bell = new();
    private bool _bellSeeded;
    private int _bellUnread;

    /// <summary>Счётчик на колокольчике: новые сообщения и новые предложения вместе.</summary>
    private void PushBell(BellNote note)
    {
        _bell.Insert(0, note);
        while (_bell.Count > 40) _bell.RemoveAt(_bell.Count - 1);
        BellList.ItemsSource = _bell;
        BellEmpty.Visibility = Visibility.Collapsed;
        _bellUnread = _bell.Count(n => !n.Seen);
        RefreshBellBadge();
    }

    /// <summary>Значок показывает только непрочитанные, а не все записи списка.</summary>
    private void RefreshBellBadge()
    {
        if (_bellUnread <= 0)
        {
            BellBadge.Visibility = Visibility.Collapsed;
            return;
        }
        BellBadgeText.Text = _bellUnread > 99 ? "99+" : _bellUnread.ToString();
        BellBadge.Visibility = Visibility.Visible;
    }

    /// <summary>Всё, что в панели, считается прочитанным.</summary>
    private void MarkBellSeen()
    {
        foreach (var note in _bell) note.Seen = true;
        _bellUnread = 0;
        RefreshBellBadge();
    }

    private void OnBellClick(object sender, RoutedEventArgs e)
    {
        BellPanel.Visibility = BellPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (BellPanel.Visibility == Visibility.Visible)
        {
            MarkBellSeen();
        }
    }

    private void OnBellClearClick(object sender, RoutedEventArgs e)
    {
        _bell.Clear();
        BellList.ItemsSource = _bell;
        BellEmpty.Visibility = Visibility.Visible;
        _bellUnread = 0;
        RefreshBellBadge();
        BellPanel.Visibility = Visibility.Collapsed;
    }

    private void OnBellItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BellNote note }) return;
        BellPanel.Visibility = Visibility.Collapsed;
        if (note.Kind == "ad")
        {
            if (Nav.SelectedIndex != 8) Nav.SelectedIndex = 8;
            return;
        }
        Nav.SelectedIndex = 6;
        OpenDirectChat(note.PeerId, note.PeerName, true);
    }

    /// <summary>Непрочитанные по собеседникам: id или name:ник -> счётчик.</summary>
    private readonly Dictionary<string, int> _peerUnread = new(StringComparer.OrdinalIgnoreCase);

    private void BumpPeerUnread(ChatMessage message)
    {
        var key = !string.IsNullOrEmpty(message.InstanceId)
            ? message.InstanceId
            : "name:" + (message.Author ?? "").ToLowerInvariant();
        var inThisChat = _chatChannel == "dm" && IsActive
                          && (string.Equals(_chatPeerId, key, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(_chatPeerName, message.Author, StringComparison.OrdinalIgnoreCase));
        if (inThisChat) return;
        _peerUnread[key] = _peerUnread.TryGetValue(key, out var was) ? was + 1 : 1;
        ApplyPeerUnread();
    }

    /// <summary>Разносит счётчики по элементам списка собеседников.</summary>
    private void ApplyPeerUnread()
    {
        foreach (var item in ChatPeers.Items)
        {
            if (item is not ClientEntry entry) continue;
            var count = _peerUnread.TryGetValue(entry.Id, out var byId) ? byId : 0;
            if (count == 0 && _peerUnread.TryGetValue("name:" + entry.Name.ToLowerInvariant(), out var byName)) count = byName;
            entry.UnreadCount = count;
        }
    }

    /// <summary>Открыли диалог — счётчик этого собеседника обнуляется.</summary>
    private void ClearPeerUnread(string? id, string? name)
    {
        var changed = false;
        if (!string.IsNullOrEmpty(id) && _peerUnread.Remove(id)) changed = true;
        if (!string.IsNullOrEmpty(name) && _peerUnread.Remove("name:" + name.ToLowerInvariant())) changed = true;
        if (changed) ApplyPeerUnread();
    }

    private void TrackUnread(ChatMessage message)
    {
        if (message.Mine) return;
        // Старое сообщение (до последнего прочитанного) новым не считается.
        if (message.Id > 0 && message.Id <= _chatSeenHigh) return;
        if (Nav.SelectedIndex == 6 && IsActive) return;
        _chatUnread++;
        BumpPeerUnread(message);
        ChatBadgeText.Text = _chatUnread > 99 ? "99+" : _chatUnread.ToString();
        ChatBadge.Visibility = Visibility.Visible;
        PushBell(new BellNote
        {
            Kind = "chat",
            Title = string.IsNullOrWhiteSpace(message.Author) ? "Новое сообщение" : message.Author,
            Body = string.IsNullOrWhiteSpace(message.Text) ? "изображение" : message.Text,
            TimeLabel = string.IsNullOrWhiteSpace(message.TimeLabel)
                ? DateTime.Now.ToString("HH:mm")
                : message.TimeLabel,
            PeerId = message.InstanceId,
            PeerName = message.Author,
        });
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

    /// <summary>Чат открыт — всё, что в нём есть, считается прочитанным и запоминается.</summary>
    private void MarkChatSeen()
    {
        if (_chatLastId > _chatSeenHigh) _chatSeenHigh = _chatLastId;
        SaveChatSeen();
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
                chatOpen = (bool)await Dispatcher.InvokeAsync(() => Nav.SelectedIndex == 6 && IsActive);
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
                if (chatOpen)
                {
                    if (id > _chatSeenHigh)
                    {
                        _chatSeenHigh = id;
                        SaveChatSeen();
                    }
                    continue;
                }
                await Dispatcher.InvokeAsync(() =>
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
                if (!reset && id > _chatSeenHigh) TrackUnread(newMessage);
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
        if (show) _ = ShowCrashDiagnosticsAsync();
    }

    /// <summary>
    /// Разбирает latest.log и crash-reports: вытаскивает причину падения
    /// понятным текстом, а не сырым стектрейсом.
    /// </summary>
    private async Task ShowCrashDiagnosticsAsync()
    {
        var logs = Path.Combine(_overview?.GameDir ?? "", "logs");
        var crashes = Path.Combine(_overview?.GameDir ?? "", "crash-reports");
        try
        {
            var text = await Task.Run(() => ReadTail(Path.Combine(logs, "latest.log"), 200))
                       ?? "Лог не найден: игра ещё не запускалась.";
            LogText.Text = text;

            var report = await Task.Run(() => NewestCrashReport(crashes));
            if (string.IsNullOrWhiteSpace(report))
            {
                LogDiag.Visibility = Visibility.Collapsed;
                return;
            }

            var cause = DetectCrashCause(report);
            LogDiag.Text = string.IsNullOrWhiteSpace(cause)
                ? "Найдён краш-репорт, но причина не распознана. Полный файл: " +
                  NewestCrashName(crashes)
                : "Причина падения: " + cause;
            LogDiag.Foreground = string.IsNullOrWhiteSpace(cause)
                ? (Brush)FindResource("MutedBrush")
                : (Brush)FindResource("DangerBrush");
            LogDiag.Visibility = Visibility.Visible;
        }
        catch (Exception exc)
        {
            LogDiag.Text = "Не удалось прочитать лог: " + exc.Message;
            LogDiag.Foreground = (Brush)FindResource("DangerBrush");
            LogDiag.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Последние строки файла: целиком читать лог на десятки мегабайт незачем.</summary>
    private static string? ReadTail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var all = File.ReadAllLines(path);
            var take = Math.Min(lines, all.Length);
            return string.Join(Environment.NewLine, all.Skip(all.Length - take)) + Environment.NewLine;
        }
        catch
        {
            return null;
        }
    }

    private static string? NewestCrashReport(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            var file = new DirectoryInfo(folder).GetFiles("*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return file is null ? null : File.ReadAllText(file.FullName);
        }
        catch
        {
            return null;
        }
    }

    private static string? NewestCrashName(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return "";
            var file = new DirectoryInfo(folder).GetFiles("*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return file?.Name ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Ищет в краш-репорте знакомые формулировки и переводит их на человеческий.</summary>
    private static string? DetectCrashCause(string report)
    {
        if (Regex.IsMatch(report, @"Duplicate mods|duplicate mod", RegexOptions.IgnoreCase))
            return "в папке модов есть дубликат одного и того же мода";

        if (Regex.IsMatch(report, @"Incompatible mods found|depends on .* which is missing|Missing or unsupported mandatory dependencies", RegexOptions.IgnoreCase))
        {
            var block = Regex.Match(report, @"Depends on [^
]+".PadLeft(0));
            return block.Success
                ? "не хватает зависимости: " + block.Value
                : "не хватает обязательного мода-зависимости";
        }

        if (Regex.IsMatch(report, @"java\.lang\.OutOfMemoryError", RegexOptions.IgnoreCase))
            return "не хватило памяти — увеличьте объём в настройках";

        if (Regex.IsMatch(report, @"NoClassDefFoundError|ClassNotFoundException", RegexOptions.IgnoreCase))
        {
            var match = Regex.Match(report, @"NoClassDefFoundError: ([^
]+)|ClassNotFoundException: ([^
]+)");
            var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return "мод обращается к отсутствующему классу " + name.Trim();
        }

        if (Regex.IsMatch(report, @"GLFW error|Pixel format not accelerated|Failed to create GLFW window", RegexOptions.IgnoreCase))
            return "видеодрайвер не может создать окно игры — обновите драйвер видеокарты";

        if (Regex.IsMatch(report, @"Mixin apply failed|Mixin transformation.*failed", RegexOptions.IgnoreCase))
            return "мод не смог примениться к коду игры (mixin) — обычно из-за несовместимой версии мода";

        var exit = Regex.Match(report, @"Exit Code: (\-?\d+)");
        if (exit.Success && exit.Groups[1].Value == "-1073741819")
            return "доступ к памяти (0xC0000005) — чаще всего мод или драйвер видеокарты";

        var line = Regex.Match(report, @"^Description: ([^\r\n]+)", RegexOptions.Multiline);
        if (line.Success) return line.Groups[1].Value.Trim();

        return null;
    }

    private void OnCopyServerClick(object sender, RoutedEventArgs e)
    {
        if (_overview is null) return;
        var address = $"{_overview.ServerHost}:{_overview.ServerPort}";
        if (TryCopyText(address))
        {
            Toast("Адрес сервера скопирован");
        }
        else
        {
            Toast("Буфер обмена занят другой программой. Попробуйте ещё раз.", true);
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
            var problems = BuildModProblems(items);
            var problemByProject = problems
                .Where(p => !string.IsNullOrEmpty(p.ProjectId))
                .GroupBy(p => p.ProjectId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
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
                    ProblemText = problemByProject.TryGetValue(item.ProjectId, out var why) ? why : "",
                    Data = item,
                });
            }
            var problemNote = problems.Count == 0
                ? ""
                : $" · проблем: {problems.Count}";
            InstalledHint.Text = $"Всего: {items.Count}"
                                 + (_installedFilter == "all" ? "" : $" · показано: {_installed.Count}")
                                 + problemNote;
            InstalledHint.ToolTip = problems.Count == 0
                ? null
                : string.Join("\n", problems.Select(p => "• " + p.Title + "\n   " + p.Detail));
            InstalledHint.Foreground = problems.Any(p => p.Kind == "error")
                ? (Brush)FindResource("DangerBrush")
                : (Brush)FindResource("MutedBrush");
        }
        catch (Exception exc)
        {
            Toast("Не удалось прочитать контент: " + exc.Message, true);
        }
    }

    /// <summary>
    /// Проверяет сборку на типичные проблемы: дубликаты по project_id,
    /// моды, файл которых пропал с диска, и выключенные моды.
    /// </summary>
    private List<ModProblem> BuildModProblems(IReadOnlyList<ContentItem> items)
    {
        var problems = new List<ModProblem>();
        var mods = items.Where(i => string.Equals(i.Kind, "mod", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var group in mods.GroupBy(m => m.ProjectId, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1)
            {
                var names = string.Join(", ", group.Select(g => g.Filename));
                problems.Add(new ModProblem
                {
                    Title = group.First().Title + " — установлен дважды",
                    Detail = names,
                    Kind = "error",
                    ProjectId = group.Key,
                });
            }
        }

        var disabled = mods.Count(m => m.Disabled);
        if (disabled > 0)
        {
            problems.Add(new ModProblem
            {
                Title = $"Выключенных модов: {disabled}",
                Detail = "Если моды пропали из игры, проверьте, не отключены ли они.",
                Kind = "warning",
            });
        }

        if (mods.Count > 250)
        {
            problems.Add(new ModProblem
            {
                Title = $"Очень много модов: {mods.Count}",
                Detail = "Больше 250 модов часто переполняет лимит загрузки Java.",
                Kind = "warning",
            });
        }

        return problems;
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

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            Toast("Проверяю обновления модов, это займёт до минуты...");
            var root = await _api.GetAsync("/api/mods/updates");
            var updates = root.ValueKind == JsonValueKind.Object
                          && root.TryGetProperty("updates", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().ToList()
                : new List<JsonElement>();
            if (updates.Count == 0)
            {
                Toast("Обновлений нет: все моды актуальны.");
                return;
            }

            var names = string.Join(", ", updates
                .Select(u => u.TryGetProperty("title", out var t) ? t.GetString() : "")
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(6));
            var more = updates.Count > 6 ? $" и ещё {updates.Count - 6}" : "";
            Toast($"Доступны обновления: {names}{more}. Нажмите «Обновить», чтобы поставить их.");
        }
        catch (Exception exc)
        {
            Toast("Не удалось проверить обновления: " + exc.Message, true);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
        }
    }

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
        ProjectOverlay.Visibility = Visibility.Collapsed;
    }

    // ---- Полноэкранный просмотр скриншотов ----

    private System.Windows.Media.ImageSource? _photoSource;
    private readonly List<System.Windows.Media.ImageSource> _photoList = new();
    private int _photoIndex;

    private void OnGalleryShotClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: System.Windows.Media.ImageSource shot }) return;
        e.Handled = true;

        // Собираем все уже загруженные кадры галереи, чтобы листать стрелками.
        _photoList.Clear();
        foreach (var child in ProjectGallery.Children)
        {
            if (child is Border { Tag: System.Windows.Media.ImageSource img }) _photoList.Add(img);
        }
        _photoIndex = Math.Max(0, _photoList.IndexOf(shot));
        ShowPhotoAt(_photoIndex);
        PhotoViewer.Visibility = Visibility.Visible;
        PhotoViewer.Focus();
    }

    /// <summary>Показывает кадр по индексу со сбросом масштаба.</summary>
    private void ShowPhotoAt(int index)
    {
        if (_photoList.Count == 0) return;
        _photoIndex = ((index % _photoList.Count) + _photoList.Count) % _photoList.Count;
        _photoSource = _photoList[_photoIndex];
        _photoScale = 1.0;
        PhotoViewerImage.Source = _photoSource;
        PhotoZoom.ScaleX = 1;
        PhotoZoom.ScaleY = 1;
        PhotoViewerScroll.ScrollToHome();
        if (PhotoCounter is not null)
        {
            PhotoCounter.Text = (_photoIndex + 1) + " / " + _photoList.Count;
        }
        var many = _photoList.Count > 1;
        PhotoPrev.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
        PhotoNext.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPhotoPrevClick(object sender, RoutedEventArgs e) => ShowPhotoAt(_photoIndex - 1);

    private void OnPhotoNextClick(object sender, RoutedEventArgs e) => ShowPhotoAt(_photoIndex + 1);

    private void OnPhotoZoomIn(object sender, RoutedEventArgs e) => ZoomPhoto(1.25);

    private void OnPhotoZoomOut(object sender, RoutedEventArgs e) => ZoomPhoto(0.8);

    private double _photoScale = 1.0;

    private void ZoomPhoto(double factor)
    {
        if (_photoSource is null) return;
        _photoScale = Math.Clamp(_photoScale * factor, 0.2, 8.0);
        PhotoZoom.ScaleX = _photoScale;
        PhotoZoom.ScaleY = _photoScale;
    }

    private void OnPhotoZoomReset(object sender, RoutedEventArgs e)
    {
        if (_photoSource is null) return;
        _photoScale = 1.0;
        PhotoZoom.ScaleX = 1;
        PhotoZoom.ScaleY = 1;
        PhotoViewerScroll.ScrollToHome();
    }

    private void OnPhotoViewerClose(object sender, RoutedEventArgs e)
    {
        PhotoViewer.Visibility = Visibility.Collapsed;
        PhotoViewerImage.Source = null;
        _photoSource = null;
    }

    private void OnPhotoWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomPhoto(e.Delta > 0 ? 1.15 : 0.87);
        e.Handled = true;
    }

    private void OnPhotoKeyDown(object sender, KeyEventArgs e)
    {
        if (PhotoViewer.Visibility != Visibility.Visible) return;
        switch (e.Key)
        {
            case Key.Escape:
                OnPhotoViewerClose(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Add:
            case Key.OemPlus:
                ZoomPhoto(1.15);
                e.Handled = true;
                break;
            case Key.Subtract:
            case Key.OemMinus:
                ZoomPhoto(0.87);
                e.Handled = true;
                break;
            case Key.Left:
            case Key.PageUp:
                ShowPhotoAt(_photoIndex - 1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.PageDown:
            case Key.Space:
                ShowPhotoAt(_photoIndex + 1);
                e.Handled = true;
                break;
        }
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
            ProjectOverlay.Visibility = Visibility.Visible;
            ProjectOverlay.Focus();
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
            if (string.IsNullOrWhiteSpace(url)) continue;

            var picture = new Image { Stretch = Stretch.UniformToFill };
            var frame = new Border
            {
                Width = 260,
                Height = 164,
                Margin = new Thickness(0, 0, 10, 0),
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Background = (Brush)FindResource("BgBrush"),
                Child = picture,
                Cursor = Cursors.Hand,
            };
            ProjectGallery.Children.Add(frame);

            // LoadImage грузит в фоне и первый раз отдаёт null: кадр добавляем сразу,
            // картинку подставляем по завершении загрузки.
            var immediate = LoadImage(url);
            if (immediate is not null)
            {
                picture.Source = immediate;
                frame.Tag = immediate;
            }
            else
            {
                _ = FillGalleryShotAsync(frame, picture, url);
            }

            frame.MouseLeftButtonUp += OnGalleryShotClick;
        }
        var hasShots = ProjectGallery.Children.Count > 0;
        ProjectGalleryHint.Visibility = hasShots ? Visibility.Visible : Visibility.Collapsed;
        ProjectGalleryScroll.Visibility = hasShots ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task FillGalleryShotAsync(Border frame, Image picture, string url)
    {
        var image = await LoadImageAsync(url);
        if (image is null) return;
        await Dispatcher.InvokeAsync(() =>
        {
            picture.Source = image;
            frame.Tag = image;
        });
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
    private static readonly HashSet<string> SkinPending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient SkinHttp = new(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Папка дискового кэша картинок: второй запуск рисует иконки без сети.</summary>
    private static string IconCacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Craftunity", "icons");

    private static string IconCachePath(string url)
    {
        var key = url.Replace("https://", "").Replace("http://", "")
            .Replace('/', '_').Replace(':', '_').Replace('?', '_').Replace('=', '_');
        if (key.Length > 120) key = key[..120];
        return Path.Combine(IconCacheDir, key + ".png");
    }

    /// <summary>Читает картинку с диска, если она уже скачивалась.</summary>
    private static BitmapSource? LoadDiskImage(string url)
    {
        try
        {
            var path = IconCachePath(url);
            if (!File.Exists(path)) return null;
            return DecodeSkin(File.ReadAllBytes(path));
        }
        catch
        {
            return null;
        }
    }

    private static void SaveDiskImage(string url, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(IconCacheDir);
            File.WriteAllBytes(IconCachePath(url), bytes);
        }
        catch
        {
            // кэш не критичен
        }
    }

    private static ImageSource? LoadCachedImage(string? url)
    {
        var full = string.IsNullOrWhiteSpace(url) ? "" : url;
        if (full.StartsWith("//", StringComparison.Ordinal)) full = "https:" + full;
        if (string.IsNullOrWhiteSpace(full)) return null;
        lock (SkinCache)
        {
            if (SkinCache.TryGetValue(full, out var hit)) return hit;
        }
        var disk = LoadDiskImage(full);
        if (disk is not null)
        {
            lock (SkinCache) SkinCache[full] = disk;
        }
        return disk;
    }

    private static async Task<ImageSource?> LoadImageAsync(string? url)
    {
        var full = string.IsNullOrWhiteSpace(url) ? "" : url;
        if (full.StartsWith("//", StringComparison.Ordinal)) full = "https:" + full;
        if (string.IsNullOrWhiteSpace(full)) return null;
        lock (SkinCache)
        {
            if (SkinCache.TryGetValue(full, out var hit)) return hit;
        }
        var fromDisk = LoadDiskImage(full);
        if (fromDisk is not null)
        {
            lock (SkinCache) SkinCache[full] = fromDisk;
            return fromDisk;
        }
        try
        {
            var bytes = await SkinHttp.GetByteArrayAsync(full).ConfigureAwait(false);
            var image = DecodeSkin(bytes);
            if (image is not null) SaveDiskImage(full, bytes);
            lock (SkinCache) SkinCache[full] = image;
            return image;
        }
        catch
        {
            lock (SkinCache) SkinCache[full] = null;
            return null;
        }
    }

    private static ImageSource? LoadImage(string? url)
    {
        var full = string.IsNullOrWhiteSpace(url) ? "" : url;
        if (full.StartsWith("//", StringComparison.Ordinal)) full = "https:" + full;
        if (string.IsNullOrWhiteSpace(full)) return null;
        lock (SkinCache)
        {
            if (SkinCache.TryGetValue(full, out var hit)) return hit;
        }
        // Грузим в фоне, но не держим UI-поток: картинка догрузится следующим вызовом из кэша.
        lock (SkinPending)
        {
            if (!SkinPending.Add(full)) return null;
        }
        _ = Task.Run(async () =>
        {
            BitmapSource? image = null;
            try
            {
                var diskVersion = LoadDiskImage(full);
                if (diskVersion is not null)
                {
                    lock (SkinCache) SkinCache[full] = diskVersion;
                    lock (SkinPending) SkinPending.Remove(full);
                    return;
                }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var bytes = await SkinHttp.GetByteArrayAsync(full, cts.Token).ConfigureAwait(false);
                image = DecodeSkin(bytes);
                if (image is not null) SaveDiskImage(full, bytes);
            }
            catch
            {
                image = null;
            }
            lock (SkinCache) SkinCache[full] = image;
            lock (SkinPending) SkinPending.Remove(full);
        });
        return null;
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

    /// <summary>Тип контента по расширению: jar — мод, zip — ресурспак.</summary>
    private static string ContentKindOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jar" => "mod",
            ".zip" => "resourcepack",
            _ => "",
        };
    }

    /// <summary>Папка шейдеров отличается тем, что внутри упаковки лежит shaders/.</summary>
    private static bool LooksLikeShaderPack(string path)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            return zip.Entries.Any(entry =>
                entry.FullName.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string[] DroppedFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return Array.Empty<string>();
        return e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
    }

    private static string[] DroppedContentFiles(DragEventArgs e) =>
        DroppedFiles(e).Where(p => ContentKindOf(p).Length > 0).ToArray();

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        var files = DroppedContentFiles(e);
        e.Effects = files.Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        var files = DroppedContentFiles(e);
        e.Handled = true;
        if (files.Length == 0)
        {
            Toast("Перетащите файл .jar (мод) или .zip (ресурспак)", true);
            return;
        }
        if (_api is null)
        {
            Toast("Лаунчер ещё не подключён к движку", true);
            return;
        }

        var mods = files.Where(p => ContentKindOf(p) == "mod").ToArray();
        var zips = files.Where(p => ContentKindOf(p) == "resourcepack").ToArray();
        var shaders = zips.Where(LooksLikeShaderPack).ToArray();
        var packs = zips.Except(shaders).ToArray();

        if (mods.Length > 0)
        {
            _kind = "mod";
            await UploadContentFilesAsync(mods);
        }
        if (packs.Length > 0)
        {
            _kind = "resourcepack";
            await UploadContentFilesAsync(packs);
        }
        if (shaders.Length > 0)
        {
            _kind = "shader";
            await UploadContentFilesAsync(shaders);
        }

        // Перетаскивание — это действие на странице контента, покажем результат.
        if (Nav.SelectedIndex != 2) Nav.SelectedIndex = 2;
        await ReloadInstalledAsync();
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
            _profileShown = "";
            _lastProfile = null;
            ProfileAvatar.Source = null;
            ProfileDeals.ItemsSource = null;
            ProfileName.Text = "Аккаунт не выбран";
            ProfileType.Text = "Войдите через ely.by или выберите оффлайн-ник";
            OfflineName.Clear();
            ElyLogin.Clear();
            ElyPassword.Clear();
            await RefreshAsync();
            SyncAccountPanels();
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
        if (sender is not Button { Tag: string theme }) return;

        ApplyTheme(theme);
        Toast("Тема: " + Themes.Get(theme).Name);

        if (_api is null) return;
        try
        {
            await _api.PostAsync("/api/settings", new { theme });
        }
        catch (Exception exc)
        {
            Toast("Тема применена, но не сохранена: " + exc.Message, true);
        }
    }

    private async void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        var next = Themes.NextAfter(_overview?.Theme ?? "dark");

        // Тема применяется сразу: интерфейс не ждёт бэкенда и не мигает.
        ApplyTheme(next);
        Toast("Тема: " + Themes.Get(next).Name);

        if (_api is null) return;
        try
        {
            await _api.PostAsync("/api/settings", new { theme = next });
        }
        catch (Exception exc)
        {
            Toast("Тема применена, но не сохранена: " + exc.Message, true);
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
