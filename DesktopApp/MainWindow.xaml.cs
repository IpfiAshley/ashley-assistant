using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vosk;

namespace Ashley.Desktop;

public partial class MainWindow : Window
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    private readonly object _recognizerLock = new();
    private Model? _speechModel;
    private VoskRecognizer? _recognizer;
    private WaveIn? _microphone;
    private volatile bool _recognitionPaused;
    private string? _startupRegistrationWarning;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!IsPackagedApp())
            {
                try { RegisterForCurrentUserStartup(); }
                catch (Exception error) { _startupRegistrationWarning = error.Message; }
            }

            var webViewData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ashley", "WebView2");
            Directory.CreateDirectory(webViewData);
            var webViewOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-features=msSpeechRecognitionServiceUseCetoService"
            };
            var webViewEnvironment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: webViewData,
                options: webViewOptions);
            await AssistantView.EnsureCoreWebView2Async(webViewEnvironment);
            var browser = AssistantView.CoreWebView2;
            browser.Settings.AreDefaultContextMenusEnabled = false;
            browser.Settings.IsStatusBarEnabled = false;
            browser.PermissionRequested += (_, request) =>
            {
                if (request.PermissionKind == CoreWebView2PermissionKind.Microphone &&
                    Uri.TryCreate(request.Uri, UriKind.Absolute, out var source) &&
                    source.Host.Equals("ashley.local", StringComparison.OrdinalIgnoreCase))
                    request.State = CoreWebView2PermissionState.Allow;
            };
            browser.WebMessageReceived += OnWebMessageReceived;
            browser.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                    browser.PostWebMessageAsJson(JsonSerializer.Serialize(new
                    {
                        type = "autoStartListening",
                        startupWarning = _startupRegistrationWarning
                    }));
            };

            var page = Path.Combine(AppContext.BaseDirectory, "Jarvis.html");
            browser.SetVirtualHostNameToFolderMapping(
                "ashley.local", AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.Allow);
            if (!File.Exists(page))
                throw new FileNotFoundException("Ashley.html is missing from the app folder.", page);
            AssistantView.Source = new Uri("https://ashley.local/Jarvis.html");
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Ashley could not start", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsPackagedApp()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        return result != AppModelErrorNoPackage;
    }

    private static void RegisterForCurrentUserStartup()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("Windows could not find Ashley's executable path.");

        using var runKey = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (runKey is null)
            throw new InvalidOperationException("Windows could not open the current user's startup settings.");

        runKey.SetValue("Ashley", $"\"{executable}\"", RegistryValueKind.String);
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            var action = root.TryGetProperty("action", out var actionValue) ? actionValue.GetString() : "";
            var query = root.TryGetProperty("query", out var queryValue) ? queryValue.GetString() ?? "" : "";

            switch (action)
            {
                case "startListening":
                    await StartListeningAsync();
                    break;
                case "stopListening":
                    StopListening();
                    break;
                case "pauseListening":
                    _recognitionPaused = true;
                    break;
                case "resumeListening":
                    _recognitionPaused = false;
                    break;
                case "notepad":
                    Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
                    break;
                case "calculator":
                    Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true });
                    await BringCalculatorForward();
                    break;
                case "files":
                    Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                    break;
                case "openFolder" when !string.IsNullOrWhiteSpace(query):
                    OpenFolder(query);
                    break;
                case "openWebsite" when !string.IsNullOrWhiteSpace(query):
                    OpenWebsite(query);
                    break;
                case "playMusic":
                    await OpenMusicAsync(query);
                    break;
                case "aiCommand" when !string.IsNullOrWhiteSpace(query):
                    await HandleAiCommandAsync(query);
                    break;
                case "aiSetup":
                    if (AssistantBrain.Configure(this))
                        SendActionFeedback("actionResult", "Online AI is connected. You can ask Ashley in your own words.");
                    break;
                case "openSettings" when !string.IsNullOrWhiteSpace(query):
                    OpenWindowsSettings(query);
                    break;
                case "volume" when !string.IsNullOrWhiteSpace(query):
                    SetVolume(query);
                    break;
                case "browser":
                    Process.Start(new ProcessStartInfo("https://www.google.com") { UseShellExecute = true });
                    break;
                case "search" when !string.IsNullOrWhiteSpace(query):
                    var url = "https://www.google.com/search?q=" + Uri.EscapeDataString(query.Trim());
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    break;
                case "openApp" when !string.IsNullOrWhiteSpace(query):
                    OpenInstalledApp(query.Trim());
                    break;
                case "closeApp" when !string.IsNullOrWhiteSpace(query):
                    CloseInstalledApp(query.Trim());
                    break;
            }
        }
        catch (Exception error)
        {
            SendActionFeedback("actionError", $"I couldn't complete that: {error.Message}");
        }
    }

    private async Task HandleAiCommandAsync(string command)
    {
        if (!AssistantBrain.HasApiKey && !AssistantBrain.Configure(this))
        {
            SendActionFeedback("actionError", "Online AI is not connected yet. Use the AI setup button when you are ready.");
            return;
        }

        SendStatus("Thinking online…");
        var brain = new AssistantBrain();
        var answer = await brain.RespondAsync(command, ExecuteAiToolAsync);
        SendActionFeedback("actionResult", answer);
    }

    private async Task<string> ExecuteAiToolAsync(string name, JsonElement args)
    {
        string Arg(string key) => args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";
        switch (name)
        {
            case "open_app": return OpenInstalledApp(Arg("name"), report: false);
            case "close_app": return CloseInstalledApp(Arg("name"), report: false);
            case "set_volume": return SetVolume(Arg("value"), report: false);
            case "play_music":
                var song = Arg("song"); var browser = Arg("browser");
                var request = JsonSerializer.Serialize(new { song, browser = browser.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : browser });
                return await OpenMusicAsync(request, report: false);
            case "search_web":
                var search = Arg("query");
                if (search.Length is 0 or > 500) return "Search query is empty or too long.";
                Process.Start(new ProcessStartInfo("https://www.google.com/search?q=" + Uri.EscapeDataString(search)) { UseShellExecute = true });
                return $"Opened a web search for {search}.";
            case "open_folder":
                var folder = Arg("name"); return OpenFolder(folder, report: false);
            case "open_settings":
                var page = Arg("page"); return OpenWindowsSettings(page, report: false);
            case "computer_status": return GetComputerStatus();
            case "list_files": return ListStandardFolder(Arg("folder"), Arg("contains"));
            case "read_text_file": return await ReadUserTextFileAsync(Arg("path"));
            default: return "That action is not available to Ashley.";
        }
    }

    private static string GetComputerStatus()
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var drive = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!);
        var freeGb = Math.Round(drive.AvailableFreeSpace / 1024d / 1024 / 1024, 1);
        var totalGb = Math.Round(drive.TotalSize / 1024d / 1024 / 1024, 1);
        var physicalMemoryGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024, 1);
        return $"Uptime: {(int)uptime.TotalHours} hours {uptime.Minutes} minutes. C: free space: {freeGb} GB of {totalGb} GB. Installed memory: approximately {physicalMemoryGb} GB.";
    }

    private static string? GetStandardFolder(string name) => name.Trim().ToLowerInvariant() switch
    {
        "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        _ => null
    };

    private static string ListStandardFolder(string name, string contains)
    {
        var root = GetStandardFolder(name);
        if (root is null || !Directory.Exists(root)) return "That standard folder is not available.";
        try
        {
            var results = Directory.EnumerateFileSystemEntries(root)
                .Select(Path.GetFileName)
                .Where(item => item is not null && item.Contains(contains, StringComparison.OrdinalIgnoreCase))
                .Take(40).ToArray();
            return results.Length == 0 ? "No matching files or folders were found." : string.Join("; ", results);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "Windows did not allow Ashley to list that folder."; }
    }

    private static async Task<string> ReadUserTextFileAsync(string candidate)
    {
        if (candidate.Length == 0 || candidate.Length > 500) return "The file path is empty or too long.";
        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
            var roots = new[] { "downloads", "documents", "desktop" }.Select(GetStandardFolder).Where(path => path is not null).Cast<string>().Select(Path.GetFullPath).ToArray();
            var allowedRoot = roots.FirstOrDefault(root => fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (allowedRoot is null)
                return "Ashley can only read files located under Downloads, Documents, or Desktop.";
            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length > 32_000) return "That file is missing or larger than the 32 KB reading limit.";
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0) return "Ashley cannot read a linked or redirected file.";
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(fullPath)!); directory is not null && directory.FullName.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase); directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return "Ashley cannot read through a linked or redirected folder.";
            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            if (extension is not (".txt" or ".md" or ".csv" or ".json" or ".log")) return "Ashley can read plain text files with .txt, .md, .csv, .json, or .log extensions.";
            return await File.ReadAllTextAsync(fullPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return "Ashley could not read that file."; }
    }

    private string OpenFolder(string requestedFolder, bool report = true)
    {
        var folder = requestedFolder.Trim().ToLowerInvariant();
        var path = folder switch
        {
            "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            if (report) SendActionFeedback("actionError", $"I couldn't find your {requestedFolder} folder.");
            return $"I couldn't find your {requestedFolder} folder.";
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        return $"Opened the {requestedFolder} folder.";
    }

    private void OpenWebsite(string address)
    {
        var candidate = address.Trim();
        if (!candidate.Contains(".", StringComparison.Ordinal))
        {
            SendActionFeedback("actionError", $"I don't recognize the website {address}. Try saying its full web address.");
            return;
        }

        if (!candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            candidate = "https://" + candidate;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            SendActionFeedback("actionError", "That doesn't look like a valid website address.");
            return;
        }

        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    private async Task<string> OpenMusicAsync(string serializedRequest, bool report = true)
    {
        try
        {
            using var request = JsonDocument.Parse(serializedRequest);
            var root = request.RootElement;
            var song = root.TryGetProperty("song", out var songValue) ? songValue.GetString()?.Trim() ?? "" : "";
            var browser = root.TryGetProperty("browser", out var browserValue) && browserValue.ValueKind == JsonValueKind.String
                ? browserValue.GetString()
                : null;
            if (song.Length > 300)
                song = song[..300];

            if (!string.IsNullOrWhiteSpace(browser) && BrowserMusicController.Supports(browser) && song.Length > 0)
            {
                var chromiumExecutable = FindBrowserExecutable(browser);
                if (chromiumExecutable is null)
                {
                    var result = $"I couldn't find {browser} installed on this laptop.";
                    if (report) SendActionFeedback("actionError", result);
                    return result;
                }
                var automated = await BrowserMusicController.SearchAndPlayAsync(chromiumExecutable, browser, song);
                if (report) SendActionFeedback("actionResult", automated);
                return automated;
            }

            var url = song.Length == 0
                ? "https://music.youtube.com/"
                : $"https://music.youtube.com/search?q={Uri.EscapeDataString(song)}";

            if (string.IsNullOrWhiteSpace(browser))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                var result = song.Length == 0 ? "Opened YouTube Music in your default browser." : $"Opened YouTube Music search results for {song} in your default browser. Choose a result to start playback.";
                if (report) SendActionFeedback("actionResult", result);
                return result;
            }

            var executable = FindBrowserExecutable(browser);
            if (executable is null)
            {
                var shortcut = FindBrowserShortcut(browser);
                if (shortcut is null)
                {
                    if (report) SendActionFeedback("actionError", $"I couldn't find {browser} in the installed apps on this laptop.");
                    return $"I couldn't find {browser} in the installed apps on this laptop.";
                }

                Process.Start(new ProcessStartInfo(shortcut)
                {
                    UseShellExecute = true,
                    Arguments = $"--new-window \"{url}\""
                });
                var result = song.Length == 0 ? $"Opened YouTube Music in {browser}." : $"Opened YouTube Music search results for {song} in {browser}. Choose a result to start playback.";
                if (report) SendActionFeedback("actionResult", result);
                return result;
            }

            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            start.ArgumentList.Add("--new-window");
            start.ArgumentList.Add(url);
            Process.Start(start);
            var opened = song.Length == 0 ? $"Opened YouTube Music in {browser}." : $"Opened YouTube Music search results for {song} in {browser}. Choose a result to start playback.";
            if (report) SendActionFeedback("actionResult", opened);
            return opened;
        }
        catch (Exception error)
        {
            var message = $"I couldn't open YouTube Music: {error.Message}";
            if (report) SendActionFeedback("actionError", message);
            return message;
        }
    }

    private static string? FindBrowserExecutable(string browser)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var normalized = browser.Trim().ToLowerInvariant();
        var candidates = normalized switch
        {
            "brave" or "brave browser" => new[]
            {
                Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(programFilesX86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
            },
            "edge" or "microsoft edge" => new[]
            {
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe")
            },
            "chrome" or "google chrome" => new[]
            {
                Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe")
            },
            "firefox" => new[]
            {
                Path.Combine(programFiles, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(programFilesX86, "Mozilla Firefox", "firefox.exe")
            },
            _ => Array.Empty<string>()
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindBrowserShortcut(string browser)
    {
        var requested = NormalizeAppName(browser);
        if (requested.Length == 0)
            return null;

        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var match = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
                    .FirstOrDefault(path => NormalizeAppName(Path.GetFileNameWithoutExtension(path)) == requested);
                if (match is not null)
                    return match;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return null;
    }

    private string OpenWindowsSettings(string page, bool report = true)
    {
        var settingsPage = page.Trim().ToLowerInvariant() switch
        {
            "sound" => "sound",
            "display" => "display",
            "bluetooth" => "bluetooth",
            "wifi" or "wi-fi" or "network" => "network-wifi",
            "settings" or "windows settings" => "",
            _ => null
        };

        if (settingsPage is null)
        {
            if (report) SendActionFeedback("actionError", "I can open sound, display, Bluetooth, Wi-Fi, or the main Windows Settings page.");
            return "That Settings page is not supported.";
        }

        var uri = settingsPage.Length == 0 ? "ms-settings:" : $"ms-settings:{settingsPage}";
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        return $"Opened Windows {page} settings.";
    }

    private string SetVolume(string command, bool report = true)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var volume = device.AudioEndpointVolume;
        var normalized = command.Trim().ToLowerInvariant();

        if (normalized == "mute")
            volume.Mute = true;
        else if (normalized == "unmute")
            volume.Mute = false;
        else if (normalized is "up" or "down")
        {
            var delta = normalized == "up" ? 0.10f : -0.10f;
            volume.MasterVolumeLevelScalar = Math.Clamp(volume.MasterVolumeLevelScalar + delta, 0f, 1f);
            volume.Mute = false;
        }
        else if (float.TryParse(normalized, System.Globalization.NumberStyles.Number,
                     System.Globalization.CultureInfo.InvariantCulture, out var percent) && percent is >= 0 and <= 100)
        {
            volume.MasterVolumeLevelScalar = percent / 100f;
            volume.Mute = false;
        }
        else
        {
            if (report) SendActionFeedback("actionError", "Say volume up, volume down, mute, unmute, or set volume to a number from 0 to 100.");
            return "That volume setting is invalid. Use a value from 0 to 100, up, down, mute, or unmute.";
        }
        return normalized switch
        {
            "mute" => "Muted the system volume.",
            "unmute" => "Unmuted the system volume.",
            "up" => "Turned the system volume up.",
            "down" => "Turned the system volume down.",
            _ => $"Set the system volume to {normalized} percent."
        };
    }

    private string OpenInstalledApp(string requestedName, bool report = true)
    {
        var requested = NormalizeAppName(requestedName);
        var startMenuRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")
        };

        var shortcuts = startMenuRoots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            .Select(path => new { Path = path, Name = NormalizeAppName(Path.GetFileNameWithoutExtension(path)) })
            .ToArray();

        var match = shortcuts.FirstOrDefault(item => item.Name == requested)
            ?? shortcuts.FirstOrDefault(item => item.Name.StartsWith(requested, StringComparison.Ordinal))
            ?? shortcuts.FirstOrDefault(item => item.Name.Contains(requested, StringComparison.Ordinal));

        if (match is null)
        {
            if (report) SendActionFeedback("actionError", $"I couldn't find {requestedName} in the Start menu. Try the app's full name.");
            return $"I couldn't find {requestedName} in the Start menu.";
        }

        try
        {
            Process.Start(new ProcessStartInfo(match.Path) { UseShellExecute = true });
            var displayName = Path.GetFileNameWithoutExtension(match.Path);
            if (report) SendActionFeedback("actionResult", $"Opening {displayName}.");
            return $"Opened {displayName}.";
        }
        catch (Exception error)
        {
            if (report) SendActionFeedback("actionError", $"I found {Path.GetFileNameWithoutExtension(match.Path)}, but Windows couldn't open it: {error.Message}");
            return $"Windows could not open {Path.GetFileNameWithoutExtension(match.Path)}.";
        }
    }

    private string CloseInstalledApp(string requestedName, bool report = true)
    {
        var key = NormalizeAppName(requestedName);
        var processName = key switch
        {
            "brave" or "bravebrowser" => "brave",
            "edge" or "microsoftedge" => "msedge",
            "chrome" or "googlechrome" => "chrome",
            "firefox" or "mozillafirefox" => "firefox",
            "word" or "microsoftword" => "WINWORD",
            "excel" or "microsoftexcel" => "EXCEL",
            "powerpoint" or "microsoftpowerpoint" => "POWERPNT",
            "spotify" => "Spotify",
            "discord" => "Discord",
            "vlc" or "vlcmediaplayer" => "vlc",
            "notepad" => "notepad",
            "calculator" or "calc" => "Calculator",
            _ => new string(requestedName.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray())
        };

        if (string.IsNullOrWhiteSpace(processName))
        {
            if (report) SendActionFeedback("actionError", "Tell me the name of the app you want me to close.");
            return "Tell me the name of the app you want to close.";
        }

        var closeRequests = 0;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow())
                        closeRequests++;
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        if (report)
        {
            if (closeRequests > 0) SendActionFeedback("actionResult", $"Asked {requestedName} to close.");
            else SendActionFeedback("actionError", $"I couldn't find an open {requestedName} window to close.");
        }
        return closeRequests > 0 ? $"Asked {requestedName} to close." : $"I couldn't find an open {requestedName} window.";
    }

    private static string NormalizeAppName(string name)
    {
        var words = name.ToLowerInvariant()
            .Split(new[] { ' ', '-', '_', '.', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word is not ("microsoft" or "application" or "app" or "browser" or "the"));
        return string.Concat(words.Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray())));
    }

    private void SendActionFeedback(string type, string text)
    {
        AssistantView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type, text }));
    }

    private static async Task BringCalculatorForward()
    {
        await Task.Delay(900);
        var window = FindWindow("ApplicationFrameWindow", "Calculator");
        if (window == IntPtr.Zero)
            window = FindWindow(null, "Calculator");
        if (window != IntPtr.Zero)
        {
            ShowWindowAsync(window, 9);
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
    }

    private async Task StartListeningAsync()
    {
        if (_microphone is not null)
            return;

        SendStatus("Loading the offline speech model. This may take a moment the first time.");
        var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "vosk-model-small-en-us-0.15");
        if (!Directory.Exists(modelPath))
        {
            SendStatus("The offline speech model is missing. Rebuild Ashley to restore it.");
            return;
        }

        try
        {
            _speechModel ??= await Task.Run(() =>
            {
                Vosk.Vosk.SetLogLevel(-1);
                return new Model(modelPath);
            });

            lock (_recognizerLock)
            {
                _recognizer = new VoskRecognizer(_speechModel, 16000.0f);
                _recognizer.SetMaxAlternatives(3);
            }

            var capture = new WaveIn
            {
                DeviceNumber = 0,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };
            capture.DataAvailable += OnMicrophoneData;
            _microphone = capture;
            _recognitionPaused = false;
            capture.StartRecording();
            SendStatus("Listening offline for Hey Ashley.");
        }
        catch (Exception error)
        {
            StopListening();
            SendStatus($"Could not start the offline microphone: {error.Message}");
        }
    }

    private void OnMicrophoneData(object? sender, WaveInEventArgs eventArgs)
    {
        if (_recognitionPaused)
            return;

        string? text = null;
        var alternatives = new List<string>();
        lock (_recognizerLock)
        {
            if (_recognitionPaused || _recognizer is null)
                return;

            if (_recognizer.AcceptWaveform(eventArgs.Buffer, eventArgs.BytesRecorded))
            {
                using var result = JsonDocument.Parse(_recognizer.Result());
                if (result.RootElement.TryGetProperty("text", out var value))
                    text = value.GetString();
                if (result.RootElement.TryGetProperty("alternatives", out var candidates) &&
                    candidates.ValueKind == JsonValueKind.Array)
                {
                    foreach (var candidate in candidates.EnumerateArray())
                    {
                        if (candidate.TryGetProperty("text", out var candidateText) &&
                            !string.IsNullOrWhiteSpace(candidateText.GetString()))
                            alternatives.Add(candidateText.GetString()!);
                    }
                    if (string.IsNullOrWhiteSpace(text))
                        text = alternatives.FirstOrDefault();
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            var recognized = text;
            Dispatcher.BeginInvoke(() => AssistantView.CoreWebView2?.PostWebMessageAsJson(
                JsonSerializer.Serialize(new { type = "transcript", text = recognized, alternatives })));
        }
    }

    private void StopListening()
    {
        _recognitionPaused = true;
        if (_microphone is not null)
        {
            _microphone.DataAvailable -= OnMicrophoneData;
            try { _microphone.StopRecording(); } catch { }
            _microphone.Dispose();
            _microphone = null;
        }

        lock (_recognizerLock)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
        _recognitionPaused = false;
        SendStatus("Stopped listening.");
    }

    private void SendStatus(string text)
    {
        if (AssistantView.CoreWebView2 is not null)
            AssistantView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "status", text }));
    }

    protected override void OnClosed(EventArgs e)
    {
        StopListening();
        _speechModel?.Dispose();
        base.OnClosed(e);
    }
}
