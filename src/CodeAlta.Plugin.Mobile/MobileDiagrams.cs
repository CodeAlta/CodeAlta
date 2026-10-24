using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace CodeAlta.Plugin.Mobile;

/// <summary>The diagrams opened in a tab of their own: a page each, at a random id, for two hours (64 at most).</summary>
internal sealed class MobileDiagrams
{
    private static readonly TimeSpan Life = TimeSpan.FromHours(2);
    private readonly ConcurrentDictionary<string, (string Page, DateTime Until)> _pages = new(StringComparer.Ordinal);

    internal string Add(string svg, string? background, string? text, bool dark)
    {
        var now = DateTime.UtcNow;
        foreach (var old in _pages.Where(p => p.Value.Until < now).Select(p => p.Key).ToList()) _pages.TryRemove(old, out _);
        while (_pages.Count >= 64 && _pages.OrderBy(p => p.Value.Until).Select(p => p.Key).FirstOrDefault() is { } first) _pages.TryRemove(first, out _);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var bg = background ?? (dark ? "#2f343c" : "#f6f7f9");
        var fg = text ?? (dark ? "#f6f7f9" : "#1c2127");
        // Pinch to zoom: the page allows it, and the diagram starts whole: its own width, or the screen's if that is narrower.
        var page = $$"""
            <!doctype html><html lang="en" style="color-scheme:{{(dark ? "dark" : "light")}}"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1, minimum-scale=0.25, maximum-scale=10, user-scalable=yes">
            <meta name="theme-color" content="{{bg}}"><title>Diagram</title>
            <style>html,body{margin:0;background:{{bg}};color:{{fg}}}body{padding:16px;box-sizing:border-box;min-height:100vh}
            svg{display:block;margin:0 auto;max-width:none!important;width:min(100%,var(--w,100%));height:auto}</style></head>
            <body>{{svg}}</body></html>
            """;
        _pages[id] = (page, now + Life);
        return id;
    }

    internal string? Get(string id)
        => _pages.TryGetValue(id, out var p) && p.Until >= DateTime.UtcNow ? p.Page : null;
}
