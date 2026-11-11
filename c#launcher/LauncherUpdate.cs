using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace CraftunityLauncher;

/// <summary>Проверка манифеста и автоматическая установка новой версии лаунчера.</summary>
public static class LauncherUpdate
{
    private const string Repo = "daniprocrut-ui/craftunity";
    private const string ManifestUrl = "https://daniprocrut-ui.github.io/craftunity/manifest.json";

    public sealed record Release(string Version, string Url, string Sha256);

    public static async Task<Release?> CheckAsync(string currentVersion)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        var text = await http.GetStringAsync(ManifestUrl);
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("launcher", out var launcher)) return null;
        if (!launcher.TryGetProperty("version", out var v) || !launcher.TryGetProperty("url", out var u)) return null;
        if (!launcher.TryGetProperty("sha256", out var s)) return null;
        var version = v.GetString() ?? "";
        var url = u.GetString() ?? "";
        var sha = (s.GetString() ?? "").ToLowerInvariant();
        if (string.IsNullOrEmpty(version) || !url.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase)) return null;
        if (Compare(version, currentVersion) <= 0) return null;
        return new Release(version, url, sha);
    }

    public static async Task<string?> DownloadAsync(Release release, Action<double>? progress = null)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Craftunity", "update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"Craftunity_Setup_{release.Version}.exe");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var file = File.Create(target))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Invoke((double)done / total);
            }
        }
        if (!string.IsNullOrEmpty(release.Sha256))
        {
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant();
            if (!string.Equals(actual, release.Sha256, StringComparison.Ordinal))
            {
                File.Delete(target);
                return null;
            }
        }
        return target;
    }

    public static bool RunInstaller(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                Arguments = "/SILENT /NOCANCEL /NORESTART /SP- /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS",
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Запускает установщик через отдельный cmd, который сначала дожидается выхода лаунчера,
    /// и только потом ставит обновление. Так установщик не конкурирует за python.exe.
    /// </summary>
    public static bool RunInstallerAfterExit(string installerPath, int exitTimeoutSeconds = 90)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Craftunity", "update");
            Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, "apply_setup.cmd");
            var body =
                "@echo off\r\n" +
                "chcp 65001 >nul\r\n" +
                "setlocal\r\n" +
                "set /a TRIES=0\r\n" +
                ":wait\r\n" +
                "tasklist /FI \"IMAGENAME eq CraftunityLauncher.exe\" | find /I \"CraftunityLauncher.exe\" >nul\r\n" +
                "if errorlevel 1 goto run\r\n" +
                "set /a TRIES+=1\r\n" +
                $"if %TRIES% GEQ {exitTimeoutSeconds} goto run\r\n" +
                "ping -n 2 127.0.0.1 >nul\r\n" +
                "goto wait\r\n" +
                ":run\r\n" +
                $"start \"\" \"{installerPath}\" /SILENT /NOCANCEL /NORESTART /SP- /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS\r\n" +
                "del \"%~f0\" >nul 2>&1\r\n";
            File.WriteAllText(script, body, System.Text.Encoding.UTF8);
            Process.Start(new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"/c \"{script}\"",
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int Compare(string left, string right)
    {
        static string[] Parts(string value) =>
            value.Trim().TrimStart('v', 'V').Split(new[] { '.', '-', '+' }, StringSplitOptions.RemoveEmptyEntries);
        var a = Parts(left);
        var b = Parts(right);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            _ = int.TryParse(i < a.Length ? a[i] : "0", out var x);
            _ = int.TryParse(i < b.Length ? b[i] : "0", out var y);
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }
}
