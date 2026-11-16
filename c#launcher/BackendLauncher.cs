using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace CraftunityLauncher;

/// <summary>Поднимает урезанный Python-лаунчер рядом с exe и ждёт, пока появится локальный API.</summary>
public static class BackendLauncher
{
    private static Process? _process;
    private static string _baseUrl = "";

    public static string BaseUrl => _baseUrl;

    /// <summary>Адрес API из lock-файла, если его пишет уже запущенный лаунчер.</summary>
    public static string? UrlFromLock()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var lockPath = Path.Combine(appData, "Craftunity", "launcher.lock");
            if (!File.Exists(lockPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(lockPath));
            if (!doc.RootElement.TryGetProperty("port", out var portValue)) return null;
            var port = portValue.ToString();
            return string.IsNullOrWhiteSpace(port) ? null : $"http://127.0.0.1:{port}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Гарантирует запущенный API: свой Python, иначе системный, иначе ждём lock.</summary>
    public static async Task<string> EnsureAsync(string? hint = null, int timeoutSeconds = 40)
    {
        var lockUrl = UrlFromLock();
        if (lockUrl is not null && await AliveAsync(lockUrl)) return lockUrl;
        if (hint is not null && await AliveAsync(hint)) return hint;

        var root = AppContext.BaseDirectory;
        var python = Path.Combine(root, "python", "python.exe");
        var script = Path.Combine(root, "run.py");

        if (File.Exists(python) && File.Exists(script))
        {
            Start(python, script, root);
        }

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var url = UrlFromLock();
            if (url is not null && await AliveAsync(url))
            {
                _baseUrl = url;
                return url;
            }
            if (!string.IsNullOrEmpty(_baseUrl) && await AliveAsync(_baseUrl)) return _baseUrl;
            await Task.Delay(500);
        }

        return hint ?? "http://127.0.0.1:8765";
    }

    private static void Start(string python, string script, string root)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = python,
                ArgumentList = { script, "--port", "8765", "--browser" },
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.Environment["CRAFTUNITY_HEADLESS"] = "1";
            info.Environment["PYTHONHOME"] = Path.Combine(root, "python");
            info.Environment["PYTHONPATH"] = root;
            _process = Process.Start(info);
            _baseUrl = "http://127.0.0.1:8765";
        }
        catch
        {
            _process = null;
        }
    }

    private static async Task<bool> AliveAsync(string url)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync(url.TrimEnd('/') + "/api/state");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Останавливает поднятый Python при закрытии окна.</summary>
    public static void Shutdown()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // процесс мог завершиться сам
        }
    }
}
