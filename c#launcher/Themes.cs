using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace CraftunityLauncher;

/// <summary>Палитры тем лаунчера и их применение к ресурсам WPF.</summary>
public static class Themes
{
    public sealed record Palette(
        string Id, string Name, string Bg, string Panel, string Card,
        string Text, string Muted, string Accent, string AccentFg, string Border);

    public static readonly List<Palette> All = new()
    {
        new("dark", "Тёмная", "#090b14", "#141a2c", "#1c2338", "#e9eefb", "#8b95b2", "#fb923c", "#1c0f02", "#2a3350"),
        new("light", "Светлая", "#f3f4f8", "#ffffff", "#eef1f7", "#171a24", "#626a80", "#ea580c", "#ffffff", "#d7dbe8"),
        new("midnight", "Полуночная", "#04060e", "#0f1628", "#16203a", "#e6ecfb", "#7f8cad", "#38bdf8", "#04121d", "#22304f"),
        new("forest", "Лесная", "#07100c", "#102018", "#16291f", "#e8f6ed", "#88a695", "#4ade80", "#04140b", "#24402f"),
        new("sunset", "Закатная", "#150811", "#2a1226", "#381a31", "#fdeaf6", "#b48ba6", "#fb7185", "#2a0712", "#4d2545"),
        new("ocean", "Океанская", "#04121b", "#0b2b3d", "#0f3850", "#e3f6fd", "#7fa6b8", "#2dd4bf", "#03201f", "#1b526d"),
        new("sakura", "Сакура", "#1a0f16", "#301a28", "#3d2233", "#fdeaf4", "#b48ea3", "#f472b6", "#2b0a1c", "#57304a"),
        new("neon", "Неоновая", "#050208", "#120a22", "#1a0f30", "#f0e8ff", "#9080b0", "#a855f7", "#16042b", "#2d1a4d"),
        new("graphite", "Графитовая", "#0e1013", "#1d2126", "#262b32", "#eef1f5", "#8b95a1", "#9aa7b8", "#14171b", "#363d46"),
        new("crimson", "Багровая", "#120508", "#261014", "#32161b", "#fde8ec", "#b58a95", "#ef4444", "#240507", "#4a2028"),
        new("coffee", "Кофейная", "#14100c", "#261f18", "#322920", "#f6eee4", "#a8947d", "#c1814a", "#1f1308", "#4a3d2e"),
        new("sand", "Песочная", "#f6efe2", "#fffdf7", "#f3ead9", "#2a2117", "#7d6a52", "#b45309", "#2a1a05", "#dccdb2"),
        new("lavender", "Лаврндовая", "#f3f2fb", "#ffffff", "#f0eefb", "#1e1a33", "#6b6490", "#7c3aed", "#ffffff", "#d8d3ef"),
        new("arctic", "Арктическая", "#eef4f8", "#ffffff", "#eaf2f8", "#10222e", "#5e7b8d", "#0284c7", "#ffffff", "#cfdee8"),
    };

    public static Palette Get(string? id)
    {
        foreach (var item in All)
            if (string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) return item;
        return All[0];
    }

    public static string NextAfter(string? id)
    {
        var index = All.FindIndex(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        return All[(index + 1 + All.Count) % All.Count].Id;
    }

    /// <summary>Перекрашивает глобальные кисти и возвращает применённую палитру.</summary>
    public static Palette Apply(string? id, bool forceContrast = true)
    {
        var palette = Get(id);
        if (forceContrast) palette = WithContrast(palette);
        Set("BgBrush", palette.Bg);
        Set("PanelBrush", palette.Panel);
        Set("CardBrush", palette.Card);
        Set("CardHoverBrush", Mix(palette.Card, palette.Text, 0.08));
        Set("LineBrush", palette.Border);
        Set("TextBrush", palette.Text);
        Set("MutedBrush", palette.Muted);
        Set("AccentBrush", palette.Accent);
        Set("AccentFgBrush", palette.AccentFg);
        Set("DangerBrush", "#f87171");

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        gradient.GradientStops.Add(new GradientStop(Parse(Mix(palette.Accent, "#ffffff", 0.45)), 0));
        gradient.GradientStops.Add(new GradientStop(Parse(palette.Accent), 0.55));
        gradient.GradientStops.Add(new GradientStop(Parse(Mix(palette.Accent, "#000000", 0.22)), 1));
        gradient.Freeze();
        Application.Current.Resources["AccentGradient"] = gradient;
        return palette;
    }

    private static void Set(string key, string color)
    {
        var brush = new SolidColorBrush(Parse(color));
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private static Color Parse(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 3)
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
        var value = Convert.ToUInt32(hex, 16);
        return Color.FromRgb((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
    }

    /// <summary>На светлых темах текст чёрный, на тёмных — светлый.</summary>
    public static Palette WithContrast(Palette palette)
    {
        var light = Luminance(palette.Bg) >= 0.5;
        var text = light ? "#0b0d12" : "#f2f5fa";
        var muted = light ? Mix(text, palette.Bg, 0.35) : Mix(text, palette.Panel, 0.30);
        return palette with { Text = text, Muted = muted };
    }

    private static double Luminance(string hex)
    {
        var c = Parse(hex);
        double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static string Mix(string first, string second, double amount)
    {
        var a = Parse(first);
        var b = Parse(second);
        byte Blend(byte x, byte y) => (byte)Math.Clamp(x + (y - x) * amount, 0, 255);
        return $"#{Blend(a.R, b.R):X2}{Blend(a.G, b.G):X2}{Blend(a.B, b.B):X2}";
    }
}
