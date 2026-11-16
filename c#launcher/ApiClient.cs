using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CraftunityLauncher;

/// <summary>Клиент локального API лаунчера (craftunity/server.py).</summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;

    public ApiClient(string baseUrl)
    {
        _base = baseUrl.TrimEnd('/');
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = true };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
    }

    private static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<JsonElement> GetAsync(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(_base + path, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ApiException(ExtractError(text, (int)response.StatusCode));
        return Parse(text);
    }

    public async Task<JsonElement> PostAsync(string path, object? payload, CancellationToken ct = default)
    {
        var body = payload is null ? "{}" : JsonSerializer.Serialize(payload, Json);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(_base + path, content, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ApiException(ExtractError(text, (int)response.StatusCode));
        return Parse(text);
    }

    public async Task<JsonElement> PostRawAsync(string path, byte[] body, params (string Name, string Value)[] headers)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Post, _base + path) { Content = content };
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ApiException(ExtractError(text, (int)response.StatusCode));
        return Parse(text);
    }

    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(_base + "/api/state", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static JsonElement Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static string ExtractError(string text, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                return error.GetString() ?? $"Ошибка {status}";
        }
        catch
        {
            // ответ не JSON — отдаём как есть
        }
        return string.IsNullOrWhiteSpace(text) ? $"Ошибка {status}" : text.Trim();
    }

    public void Dispose() => _http.Dispose();
}

public sealed class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}

/// <summary>Обёртки над ответами API.</summary>
public static class ApiJson
{
    public static string Str(this JsonElement element, string name, string fallback = "")
    {
        if (element.ValueKind != JsonValueKind.Object) return fallback;
        if (!element.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Null or JsonValueKind.Undefined => fallback,
            _ => value.ToString(),
        };
    }

    public static double Num(this JsonElement element, string name, double fallback = 0)
    {
        if (element.ValueKind != JsonValueKind.Object) return fallback;
        if (!element.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return fallback;
    }

    public static bool Bool(this JsonElement element, string name, bool fallback = false)
    {
        if (element.ValueKind != JsonValueKind.Object) return fallback;
        if (!element.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    public static JsonElement Obj(this JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
            return value;
        return default;
    }

    public static IEnumerable<JsonElement> Arr(this JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) yield return item;
        }
    }
}
