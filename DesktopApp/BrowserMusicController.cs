using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Ashley.Desktop;

internal static class BrowserMusicController
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly Dictionary<string, int> Ports = new(StringComparer.OrdinalIgnoreCase)
    {
        ["brave"] = 49371, ["brave browser"] = 49371,
        ["edge"] = 49372, ["microsoft edge"] = 49372,
        ["chrome"] = 49373, ["google chrome"] = 49373
    };

    public static bool Supports(string browser) => Ports.ContainsKey(browser.Trim());

    public static async Task<string> SearchAndPlayAsync(string executable, string browser, string song)
    {
        if (string.IsNullOrWhiteSpace(song)) return "Tell Ashley the song name to search for.";
        var key = browser.Trim();
        if (!Ports.TryGetValue(key, out var port)) return "This browser can't be controlled for playback yet. I can still open its YouTube Music search results.";
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ashley", "BrowserProfiles", $"chromium-{port}");
        Directory.CreateDirectory(profile);
        var url = $"https://music.youtube.com/search?q={Uri.EscapeDataString(song)}";
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var arg in new[] { $"--remote-debugging-port={port}", "--remote-debugging-address=127.0.0.1", $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", "--new-window", url })
            start.ArgumentList.Add(arg);
        Process.Start(start);

        var deadline = DateTime.UtcNow.AddSeconds(25);
        string? webSocketUrl = null;
        while (DateTime.UtcNow < deadline && webSocketUrl is null)
        {
            try
            {
                using var targetsResponse = await Http.GetAsync($"http://127.0.0.1:{port}/json");
                using var targets = JsonDocument.Parse(await targetsResponse.Content.ReadAsStringAsync());
                webSocketUrl = targets.RootElement.EnumerateArray()
                    .Where(target => target.TryGetProperty("type", out var type) && type.GetString() == "page")
                    .Select(target => target.TryGetProperty("webSocketDebuggerUrl", out var address) ? address.GetString() : null)
                    .FirstOrDefault(address => !string.IsNullOrWhiteSpace(address));
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException) { }
            if (webSocketUrl is null) await Task.Delay(500);
        }
        if (webSocketUrl is null) return "Opened YouTube Music in the Ashley-controlled browser profile, but the browser automation connection did not start. The search page is open.";

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(webSocketUrl), CancellationToken.None);
        var expression = "new Promise(resolve => { let tries=0; const timer=setInterval(() => { const rows=[...document.querySelectorAll('ytmusic-responsive-list-item-renderer')]; const row=rows.find(r => r.querySelector('a[href*=" + "'watch?v='" + "]')); const link=row?.querySelector('a[href*=" + "'watch?v='" + "]'); if(link){clearInterval(timer); link.click(); resolve('clicked');} else if(++tries>=20){clearInterval(timer); resolve('not-found');}},500); })";
        var navResult = await EvaluateAsync(socket, 1, expression, awaitPromise: true);
        if (navResult != "clicked") return "The Ashley browser profile is open on the song search, but YouTube Music did not expose a playable result. Choose the song on the page.";
        await Task.Delay(2500);
        var playResult = await EvaluateAsync(socket, 2, "(() => { const button=document.querySelector('ytmusic-player-bar .play-pause-button'); if(!button) return 'opened-song'; const label=(button.getAttribute('aria-label')||'').toLowerCase(); if(label.includes('play')) button.click(); return 'play-requested'; })()");
        return playResult == "play-requested"
            ? $"I opened the first YouTube Music result for {song} and asked it to play. If this Ashley browser profile is not signed in or autoplay is blocked, finish playback in the open window."
            : $"I opened the first YouTube Music result for {song}. Press Play in the open window if playback did not start.";
    }

    private static async Task<string> EvaluateAsync(ClientWebSocket socket, int id, string expression, bool awaitPromise = false)
    {
        var json = JsonSerializer.Serialize(new { id, method = "Runtime.evaluate", @params = new { expression, awaitPromise, returnByValue = true, userGesture = true } });
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[32 * 1024];
        while (true)
        {
            using var received = new MemoryStream();
            WebSocketReceiveResult frame;
            do
            {
                frame = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (frame.MessageType == WebSocketMessageType.Close) return "";
                received.Write(buffer, 0, frame.Count);
                if (received.Length > 2 * 1024 * 1024) throw new InvalidDataException("The browser sent an unexpectedly large response.");
            } while (!frame.EndOfMessage);
            using var response = JsonDocument.Parse(received.ToArray());
            var root = response.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
            if (root.TryGetProperty("result", out var result) && result.TryGetProperty("result", out var remote) &&
                remote.TryGetProperty("value", out var value)) return value.GetString() ?? "";
            return "";
        }
    }
}
