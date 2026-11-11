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
    private readonly System.Net.Http.HttpClient _chatHttp = new() { Timeout = TimeSpan.FromSeconds(12) };
    private string _kind = "mod";
    private string _installedFilter = "all";
    private int _offset;
    private long _total;
    private string _query = "";
    private byte[]? _pendingSkin;
    private Overview? _overview;
    private double _progressShown;

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
        if (LauncherUpdate.RunInstaller(installer))
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
                }
                catch
                {
                    // пропускаем тик
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

        Themes.Apply(overview.Theme);
        _ = LoadSkinAsync();
    }

    // ---------------------------------------------------------------- навигация

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PagePlay is null) return;
        var index = Nav.SelectedIndex;
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
        PageSettings.Visibility = index == 7 ? Visibility.Visible : Visibility.Collapsed;

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
            case 6:
                _ = LoadChatAsync(true);
                break;
        }
    }

    private void Toast(string message, bool bad = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(bad ? "DangerBrush" : "MutedBrush");
    }

    // ------------------------------------------------------------------ чат

    private const string ChatBaseUrl = "https://craftunity-heartbeat.netlify.app";

    private async void OnChatRefreshClick(object sender, RoutedEventArgs e) => await LoadChatAsync(true);

    private async void OnChatSendClick(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async void OnChatInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SendChatAsync();
    }

    private async Task SendChatAsync()
    {
        var body = ChatInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(body)) return;
        ChatState.Text = "Отправка…";
        try
        {
            var payload = new
            {
                instance_id = _overview?.InstanceId ?? "",
                username = _overview?.Account?.Username ?? "Гость",
                text = body,
            };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await _chatHttp.PostAsync(ChatBaseUrl + "/message", content);
            var answer = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                ChatState.Text = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? "Слишком часто, подождите пару секунд"
                    : "Не удалось отправить: " + answer;
                return;
            }
            ChatInput.Text = "";
            ChatState.Text = "Отправлено";
            await LoadChatAsync(false);
        }
        catch (Exception exc)
        {
            ChatState.Text = "Ошибка сети: " + exc.Message;
        }
    }

    private async Task LoadChatAsync(bool reset)
    {
        try
        {
            var since = reset ? 0 : _chatLastId;
            using var response = await _chatHttp.GetAsync($"{ChatBaseUrl}/messages?since={since}");
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
                _chat.Add(new ChatMessage
                {
                    Id = id,
                    Author = item.Str("username", "Гость"),
                    Text = item.Str("text"),
                    IconUrl = item.Str("icon"),
                    TimeLabel = FormatChatTime(item.Num("at")),
                });
                if (id > _chatLastId) _chatLastId = id;
            }
            var count = (long)root.Num("count");
            ChatState.Text = count == 0 ? "Сообщений пока нет" : $"Сообщений: {count}";
            ChatScroll.ScrollToEnd();
        }
        catch (Exception exc)
        {
            ChatState.Text = "Ошибка сети: " + exc.Message;
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

    private static ImageSource? LoadImage(string? url)
    {
        var full = string.IsNullOrWhiteSpace(url) ? "" : url;
        if (full.StartsWith("//", StringComparison.Ordinal)) full = "https:" + full;
        if (string.IsNullOrWhiteSpace(full)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(full, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
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
            AvatarImage.Source = CropHead(decoded);
            SkinHint.Text = "Локальный скин применён.";
        }
        catch
        {
            // скин не критичен для работы
        }
    }

    /// <summary>Вырезает из скина 64×64 только голову вместе с волосами, а не весь скин.</summary>
    private static BitmapSource? CropHead(BitmapSource? skin)
    {
        if (skin is null) return null;
        try
        {
            var size = skin.PixelWidth >= 64 ? 8 : 4;
            var scale = skin.PixelWidth / (skin.PixelWidth >= 64 ? 64 : 32);
            var head = new CroppedBitmap(skin, new Int32Rect(8 * scale, 8 * scale, size * scale, size * scale));
            var side = new CroppedBitmap(skin, new Int32Rect(0, 8 * scale, size * scale, size * scale));
            var output = new DrawingVisual();
            using (var ctx = output.RenderOpen())
            {
                ctx.DrawImage(head, new Rect(0, 0, 12, 12));
                ctx.DrawImage(side, new Rect(12, 0, 12, 12));
            }
            var result = new RenderTargetBitmap(24, 12, 96, 96, PixelFormats.Pbgra32);
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
