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

public sealed class ChatMessage
{
    public long Id { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public string TimeLabel { get; set; } = "";
}
