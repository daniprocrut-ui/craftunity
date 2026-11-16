using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CraftunityLauncher;

public sealed class AccountInfo
{
    public string Username { get; init; } = "";
    public string Type { get; init; } = "";
}

public sealed class ProgressInfo
{
    public string Status { get; init; } = "";
    public string Message { get; init; } = "";
    public string Error { get; init; } = "";
    public double Progress { get; init; }
    public bool Running { get; init; }
}

public sealed class Overview
{
    public string LauncherVersion { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string ManifestError { get; init; } = "";
    public AccountInfo? Account { get; init; }
    public ProgressInfo Progress { get; init; } = new();
    public int InstalledMods { get; init; }
    public string GameDir { get; init; } = "";
    public string Java { get; init; } = "";
    public string BuildVersion { get; init; } = "";
    public string BuildUpdated { get; init; } = "";
    public string ManifestSource { get; init; } = "";
    public string ManifestMessage { get; init; } = "";
    public string ManifestUrl { get; init; } = "";
    public int PackMods { get; init; }
    public List<string> PackModIds { get; init; } = new();
    public string ServerHost { get; init; } = "";
    public int ServerPort { get; init; }
    public bool AutoJoin { get; init; }
    public string Theme { get; init; } = "dark";
    public int MemoryMb { get; init; }
    public string JavaPath { get; init; } = "";
    public bool AutoJava { get; init; }
    public bool ReduceMotion { get; init; }
    public bool HolidayTheme { get; init; }
    public bool ChatSound { get; init; } = true;
    public string CurseforgeKey { get; init; } = "";

    public static Overview From(JsonElement root)
    {
        var manifest = root.Obj("manifest");
        var game = manifest.Obj("game");
        var status = root.Obj("status");
        var accountJson = root.Obj("account");
        return new Overview
        {
            LauncherVersion = root.Str("launcher_version"),
            GameVersion = game.Str("version"),
            Loader = game.Str("loader"),
            InstanceId = root.Str("instance_id"),
            ManifestError = root.Str("manifest_error"),
            Account = accountJson.ValueKind == JsonValueKind.Object && accountJson.Str("username").Length > 0
                ? new AccountInfo { Username = accountJson.Str("username"), Type = accountJson.Str("type") }
                : null,
            Progress = new ProgressInfo
            {
                Status = status.Str("status"),
                Message = status.Str("message"),
                Error = status.Str("error"),
                Progress = status.Num("progress"),
                Running = status.Bool("running"),
            },
            InstalledMods = (int)root.Num("installed_mods"),
            GameDir = root.Str("game_dir"),
            Java = root.Str("java"),
            BuildVersion = manifest.Str("version"),
            BuildUpdated = manifest.Str("updated_at"),
            ManifestSource = root.Str("manifest_source"),
            ManifestMessage = manifest.Str("message"),
            ManifestUrl = root.Obj("settings").Str("manifest_url"),
            PackMods = manifest.Arr("mods").Count(),
            PackModIds = manifest.Arr("mods").Select(m => m.Str("project_id")).Where(id => id.Length > 0).ToList(),
            ServerHost = manifest.Obj("server").Str("host", root.Obj("settings").Str("server_host")),
            ServerPort = (int)manifest.Obj("server").Num("port", root.Obj("settings").Num("server_port", 25565)),
            AutoJoin = manifest.Obj("server").Bool("auto_join", root.Obj("settings").Bool("auto_join", true)),
            Theme = root.Obj("settings").Str("theme", "dark"),
            MemoryMb = (int)root.Obj("settings").Num("memory_mb", 4096),
            JavaPath = root.Obj("settings").Str("java_path"),
            AutoJava = root.Obj("settings").Bool("auto_java", true),
            ReduceMotion = root.Obj("settings").Bool("reduce_motion"),
            HolidayTheme = root.Obj("settings").Bool("holiday_theme", true),
            ChatSound = root.Obj("settings").Bool("chat_sound", true),
            CurseforgeKey = root.Obj("settings").Str("curseforge_key"),
        };
    }
}

public sealed class ContentItem
{
    public string Title { get; init; } = "";
    public string Kind { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string Filename { get; init; } = "";
    public bool Disabled { get; init; }
    public string StateText => Disabled ? "выключен" : "включён";
    public bool Untracked { get; init; }
    public string Icon { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Removable => !Untracked && !ProjectId.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
}

public sealed class ModCard
{
    public string ProjectId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public string DownloadsLabel { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string AuthorLabel { get; set; } = "";
    public string ActionLabel { get; set; } = "Установить";
    public bool CanInstall { get; set; } = true;
}

public sealed class InstalledCard
{
    public string Title { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string KindLabel { get; set; } = "";
    public string StateLabel { get; set; } = "";
    public string ToggleLabel { get; set; } = "Выключить";
    public bool CanToggle { get; set; } = true;
    public bool CanRemove { get; set; }
    public ContentItem Data { get; set; } = new();
}

public sealed class FileRow
{
    public string Name { get; set; } = "";
    public string SizeLabel { get; set; } = "";
    public string Dest { get; set; } = "";
}

public sealed class SearchHit
{
    public string ProjectId { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public long Downloads { get; init; }
    public string Source { get; init; } = "modrinth";
    public string Author { get; init; } = "";
}

public static class Models
{
    public static List<ContentItem> ContentItems(JsonElement root)
    {
        var items = new List<ContentItem>();
        foreach (var item in root.Arr("items"))
        {
            items.Add(new ContentItem
            {
                Title = item.Str("title", item.Str("filename")),
                Kind = item.Str("kind", "mod"),
                ProjectId = item.Str("project_id"),
                Filename = item.Str("filename"),
                Disabled = item.Bool("disabled"),
                Untracked = item.Bool("untracked"),
                Icon = item.Str("icon_hd", item.Str("icon")),
                Description = item.Str("description"),
            });
        }
        items.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        return items;
    }

    public static List<SearchHit> SearchHits(JsonElement root)
    {
        var hits = new List<SearchHit>();
        foreach (var hit in root.Arr("hits"))
        {
            hits.Add(new SearchHit
            {
                ProjectId = hit.Str("project_id", hit.Str("id")),
                Slug = hit.Str("slug"),
                Title = hit.Str("title", hit.Str("slug")),
                Description = hit.Str("description"),
                IconUrl = hit.Str("icon_url", hit.Str("icon")),
                Downloads = (long)hit.Num("downloads"),
                Source = hit.Str("source", "modrinth"),
                Author = hit.Str("author"),
            });
        }
        return hits;
    }
}

/// <summary>Состояние игрока: зелёный — в игре, жёлтый — открыт лаунчер, красный — не в сети.</summary>
public enum PresenceState
{
    Offline = 0,
    Launcher = 1,
    InGame = 2,
}

public sealed class ClientEntry : System.ComponentModel.INotifyPropertyChanged
{
    private System.Windows.Media.ImageSource? _icon;
    private PresenceState _presence = PresenceState.Offline;

    public ClientEntry(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }

    public System.Windows.Media.ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    /// <summary>Присутствие по данным реестра клиентов.</summary>
    public PresenceState Presence
    {
        get => _presence;
        set
        {
            if (_presence == value) return;
            _presence = value;
            foreach (var name in new[]
                     {
                         nameof(Presence), nameof(StatusColor), nameof(StatusText),
                         nameof(IsInGame), nameof(IsInLauncher), nameof(IsOffline),
                     })
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }
        }
    }

    public System.Windows.Media.Brush StatusColor => Presence switch
    {
        PresenceState.InGame => System.Windows.Media.Brushes.LimeGreen,
        PresenceState.Launcher => System.Windows.Media.Brushes.Goldenrod,
        _ => System.Windows.Media.Brushes.IndianRed,
    };

    public string StatusText => Presence switch
    {
        PresenceState.InGame => "в игре",
        PresenceState.Launcher => "в лаунчере",
        _ => "не в сети",
    };

    public bool IsInGame => Presence == PresenceState.InGame;
    public bool IsInLauncher => Presence == PresenceState.Launcher;
    public bool IsOffline => Presence == PresenceState.Offline;

    public override string ToString() => Name;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}


/// <summary>Предложение на объявление: у автора объявления есть «Да» и «Нет».</summary>
public sealed class AdOffer
{
    public long Id { get; init; }
    public long AdId { get; init; }
    public string Username { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public int Count { get; init; }
    public int Price { get; init; }
    public string State { get; set; } = "pending";

    public string Text => $"{Username}: {Count} шт. за {Price}";
    public bool IsPending => State == "pending";
    public bool IsAccepted => State == "accepted";

    public string BorderColor => State switch
    {
        "accepted" => "#22C55E",
        "declined" => "#EF4444",
        _ => "#00000000",
    };

    /// <summary>Словесный итог для закрытых предложений.</summary>
    public string StateText => State switch
    {
        "accepted" => "принято",
        "declined" => "отклонено",
        _ => "ждёт решения",
    };

    public System.Windows.Visibility StateVisibility =>
        State == "pending"
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;

    /// <summary>Кнопки «Да»/«Нет» видит только автор объявления и только для ждущих предложений.</summary>
    public System.Windows.Visibility AuthorVisibility =>
        IsAuthor && IsPending ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public bool IsAuthor { get; set; }
}

/// <summary>Объявление: продаю/куплю, что, сколько, за что.</summary>
public sealed class AdItem
{
    public long Id { get; init; }
    public string InstanceId { get; init; } = "";
    public string Username { get; init; } = "";
    public string Side { get; init; } = "sell";
    public string Item { get; init; } = "";
    public int ItemCount { get; init; }
    public string PriceItem { get; init; } = "";
    public int PriceCount { get; init; }
    public string Status { get; set; } = "open";
    public string ClosedWith { get; set; } = "";
    public List<AdOffer> Offers { get; init; } = new();

    public bool IsMine { get; set; }

    public System.Windows.Media.ImageSource? ItemIcon { get; set; }

    public string SideLabel => Side == "buy" ? "Куплю" : "Продаю";

    public string Title => $"{SideLabel} {ItemCount} × {Item}";

    public string Subtitle => Status == "closed"
        ? $"Закрыто: договорились с {ClosedWith}"
        : $"За {PriceCount} × {PriceItem} · автор {Username} · предложений: {Offers.Count}";

    public string StatusText => Status == "closed" ? "закрыто" : "открыто";

    public string BorderColor => Status == "closed" ? "#22C55E" : "#00000000";

    public System.Windows.Visibility AuthorDeleteVisibility =>
        IsMine ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>«Предложить» только на чужих открытых объявлениях.</summary>
    public System.Windows.Visibility OfferVisibility =>
        !IsMine && Status != "closed"
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
}

public sealed class ChatMessage : System.ComponentModel.INotifyPropertyChanged
{
    public long Id { get; set; }
    public string InstanceId { get; set; } = "";
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public string AvatarData { get; set; } = "";
    public string ImageData { get; set; } = "";

    private System.Windows.Media.ImageSource? _image;

    public System.Windows.Media.ImageSource? Image
    {
        get => _image;
        set
        {
            _image = value;
            Raise(nameof(Image));
            Raise(nameof(HasImage));
        }
    }

    public bool HasImage => _image is not null;
    public string TimeLabel { get; set; } = "";
    public bool Edited { get; set; }
    public bool Mine { get; set; }
    public string ReactionsLabel { get; set; } = "";

    /// <summary>Своя новость: кнопки «Изменить» и «Удалить».</summary>
    public System.Windows.Visibility NewsMineVisibility =>
        NewsMine ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public bool NewsMine { get; set; }
    public List<string> Reactions { get; set; } = new();
    public string QuoteText { get; set; } = "";
    public string ReplyToText { get; set; } = "";
    public bool HasReply => !string.IsNullOrEmpty(ReplyToText);
    public string LikeLabel { get; set; } = "👍";
    public string HeartLabel { get; set; } = "❤️";
    public string FireLabel { get; set; } = "🔥";
    public string SmileLabel { get; set; } = "😄";
    public string SadLabel { get; set; } = "😢";
    public System.Windows.Media.ImageSource? Avatar { get; set; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>Запись в панели уведомлений: сообщение чата или предложение по объявлению.</summary>
public sealed class BellNote
{
    public string Kind { get; init; } = "chat";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string TimeLabel { get; init; } = "";
    public string AccentColor => Kind == "ad" ? "#FB923C" : "#38BDF8";

    /// <summary>Чат — открыть ленту, объявление — открыть вкладку объявлений.</summary>
    public string PeerId { get; init; } = "";
    public string PeerName { get; init; } = "";
    public string PeerAuthor { get; init; } = "";
}
