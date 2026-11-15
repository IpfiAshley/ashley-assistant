using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

namespace Ashley.Desktop;

internal static class BrowserMusicController
{
    private static readonly HashSet<string> SupportedBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "brave", "brave browser", "edge", "microsoft edge", "chrome", "google chrome"
    };

    public static bool Supports(string browser) => SupportedBrowsers.Contains(browser.Trim());

    public static async Task<string> SearchAndPlayAsync(string executable, string browser, string song)
    {
        if (string.IsNullOrWhiteSpace(song)) return "Tell Ashley the song name to search for.";
        var url = $"https://music.youtube.com/search?q={Uri.EscapeDataString(song)}";
        try
        {
            // Pass the URL as a real argument and omit profile/debug flags so the browser reuses its normal profile.
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            start.ArgumentList.Add("--new-tab");
            start.ArgumentList.Add(url);
            Process.Start(start);
        }
        catch (Exception error)
        {
            return $"I couldn't open {browser}: {error.Message}";
        }

        PlaybackResult completed;
        try
        {
            completed = await Task.Run(() => TryChooseAndPlay(executable, song, TimeSpan.FromSeconds(15)))
                .WaitAsync(TimeSpan.FromSeconds(18));
        }
        catch (TimeoutException) { completed = PlaybackResult.NotFound; }
        return completed switch
        {
            PlaybackResult.Started => $"I opened and started the first matching YouTube Music result for {song} in your signed-in {browser} profile.",
            PlaybackResult.ResultSelected => $"I selected a matching result for {song} in your {browser} profile. YouTube Music did not confirm playback, so press Play in the browser if needed.",
            _ => $"I opened the YouTube Music search in your normal {browser} profile, but couldn't reliably identify a matching result to click. Choose the song in that tab to play it."
        };
    }

    private enum PlaybackResult { NotFound, ResultSelected, Started }

    private static PlaybackResult TryChooseAndPlay(string executable, string song, TimeSpan timeout)
    {
        var processName = Path.GetFileNameWithoutExtension(executable);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var browserIds = Process.GetProcessesByName(processName).Select(process =>
                {
                    using (process) return process.Id;
                }).ToHashSet();
                var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
                AutomationElement? targetWindow = null;
                foreach (AutomationElement window in windows)
                {
                    if (browserIds.Contains(window.Current.ProcessId) && window.Current.Name.Contains("YouTube Music", StringComparison.OrdinalIgnoreCase))
                    {
                        targetWindow = window;
                        break;
                    }
                }

                if (targetWindow is not null)
                {
                    var buttons = targetWindow.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    var normalizedSong = Normalize(song);
                    AutomationElement? matchingPlayButton = null;
                    foreach (AutomationElement button in buttons)
                    {
                        var buttonName = Normalize(button.Current.Name);
                        // Never click an unlabeled/general Play control: those can start a playlist
                        // that is already on screen instead of the requested search result.
                        if (buttonName.StartsWith("play ", StringComparison.Ordinal) &&
                            buttonName.Contains(normalizedSong, StringComparison.Ordinal))
                        {
                            matchingPlayButton = button;
                            break;
                        }
                    }
                    if (matchingPlayButton is not null &&
                        matchingPlayButton.TryGetCurrentPattern(InvokePattern.Pattern, out var playPattern))
                    {
                        ((InvokePattern)playPattern).Invoke();
                        return PlaybackResult.Started;
                    }
                }
            }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            Thread.Sleep(500);
        }
        return PlaybackResult.NotFound;
    }

    private static string Normalize(string value)
    {
        var cleaned = new string(value.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray());
        return string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
