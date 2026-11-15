using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Ashley.Desktop;

internal sealed class AssistantBrain
{
    internal const string CredentialTarget = "Ashley.Gemini.ApiKey";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private const string ToolDefinitions = """
    [
      {"type":"function","name":"open_app","description":"Open an installed desktop app by its Start menu name.","parameters":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}},
      {"type":"function","name":"close_app","description":"Ask an open desktop app to close gracefully by name.","parameters":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}},
      {"type":"function","name":"set_volume","description":"Set, increase, decrease, mute, or unmute the Windows master volume.","parameters":{"type":"object","properties":{"value":{"type":"string","description":"0-100, up, down, mute, or unmute"}},"required":["value"],"additionalProperties":false}},
      {"type":"function","name":"play_music","description":"Search YouTube Music in the selected browser's normal profile and attempt to select and play a matching result. Playback may not be confirmed.","parameters":{"type":"object","properties":{"song":{"type":"string"},"browser":{"type":"string","description":"Browser name, or default"}},"required":["song","browser"],"additionalProperties":false}},
      {"type":"function","name":"search_web","description":"Search the web for information.","parameters":{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}},
      {"type":"function","name":"open_folder","description":"Open one of the standard user folders.","parameters":{"type":"object","properties":{"name":{"type":"string","enum":["downloads","documents","desktop","pictures","music","videos"]}},"required":["name"],"additionalProperties":false}},
      {"type":"function","name":"open_settings","description":"Open a Windows Settings page: sound, display, bluetooth, wifi, or settings.","parameters":{"type":"object","properties":{"page":{"type":"string","enum":["sound","display","bluetooth","wifi","settings"]}},"required":["page"],"additionalProperties":false}},
      {"type":"function","name":"computer_status","description":"Read basic computer status such as available memory, uptime, and disk space.","parameters":{"type":"object","properties":{},"additionalProperties":false}},
      {"type":"function","name":"list_files","description":"List names of files and folders in one of the standard user folders, optionally filtering by a simple name fragment. Do not expose file contents.","parameters":{"type":"object","properties":{"folder":{"type":"string","enum":["downloads","documents","desktop","pictures","music","videos"]},"contains":{"type":"string"}},"required":["folder","contains"],"additionalProperties":false}},
      {"type":"function","name":"read_text_file","description":"Read a small text file inside Downloads, Documents, or Desktop. Never read credentials, browser profiles, or system files.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"A path under Downloads, Documents, or Desktop"}},"required":["path"],"additionalProperties":false}}
    ]
    """;

    public static bool HasApiKey => CredentialStore.Read() is not null;

    public static bool Configure(Window owner)
    {
        var dialog = new Window
        {
            Title = "Ashley AI setup", Owner = owner, Width = 460, Height = 330,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.White, ShowInTaskbar = false
        };
        var stack = new StackPanel { Margin = new Thickness(22) };
        stack.Children.Add(new TextBlock { Text = "Connect Ashley to Gemini", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBlock { Text = "Get a Gemini API key from Google AI Studio. Free-tier limits vary, and Google may use free-tier content to improve its products. Avoid sending sensitive information. Ashley stores the key in Windows Credential Manager.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        var key = new PasswordBox { Height = 30, Padding = new Thickness(6), Password = CredentialStore.Read() ?? "" };
        stack.Children.Add(key);
        var model = new TextBox { Text = "Gemini 3.8 Flash", Height = 30, Padding = new Thickness(6), Margin = new Thickness(0, 10, 0, 0), IsReadOnly = true };
        stack.Children.Add(model);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var save = new Button { Content = "Save key", IsDefault = true, MinWidth = 90, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(12, 6, 12, 6) };
        buttons.Children.Add(save); buttons.Children.Add(cancel); stack.Children.Add(buttons); dialog.Content = stack;
        save.Click += (_, _) =>
        {
            var candidate = key.Password.Trim();
            if (candidate.Length < 20)
            {
                MessageBox.Show(dialog, "That key looks too short. Copy the full Gemini API key from Google AI Studio.", "Check the key", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            CredentialStore.Write(candidate);
            dialog.DialogResult = true;
            dialog.Close();
        };
        return dialog.ShowDialog() == true;
    }

    public async Task<string> RespondAsync(string userText, Func<string, JsonElement, Task<string>> executeTool)
    {
        var apiKey = CredentialStore.Read();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Ashley AI is not connected yet. Open AI setup and add a Gemini API key.");

        var contents = new List<object> { new { role = "user", parts = new[] { new { text = userText } } } };
        using var toolDoc = JsonDocument.Parse(ToolDefinitions);
        var declarations = toolDoc.RootElement.EnumerateArray().Select(tool => new
        {
            name = tool.GetProperty("name").GetString(),
            description = tool.GetProperty("description").GetString(),
            parameters = ToGeminiSchema(tool.GetProperty("parameters"))
        }).ToArray();
        for (var turn = 0; turn < 4; turn++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent");
            request.Headers.Add("x-goog-api-key", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                systemInstruction = new { parts = new[] { new { text = "You are Ashley, a helpful Windows laptop voice assistant. Use tools only for actions the user clearly asked for. Never claim an action succeeded unless its tool result says it did. Music playback may require the user to finish in the browser. For destructive or sensitive tasks, explain that this version does not perform them. For ordinary questions, answer concisely and naturally for text to speech." } } },
                contents,
                tools = new[] { new { functionDeclarations = declarations } },
                toolConfig = new { functionCallingConfig = new { mode = "AUTO" } }
            }), Encoding.UTF8, "application/json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await Client.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var detail = "The Gemini API returned an error.";
                try { detail = JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString() ?? detail; } catch { }
                throw new InvalidOperationException(detail);
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                throw new InvalidOperationException("Gemini returned no response. It may have blocked the request or reached a quota limit.");
            var candidate = candidates[0];
            if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
                throw new InvalidOperationException("Gemini returned an empty response.");
            var functionParts = parts.EnumerateArray().Where(part => part.TryGetProperty("functionCall", out _)).ToArray();
            var responseText = string.Join(" ", parts.EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text))).Trim();
            if (functionParts.Length == 0)
            {
                return string.IsNullOrWhiteSpace(responseText) ? "I couldn't form a response to that." : responseText;
            }
            contents.Add(content.Clone());
            var functionResponses = new List<object>();
            foreach (var part in functionParts)
            {
                var call = part.GetProperty("functionCall");
                var name = call.GetProperty("name").GetString() ?? "";
                var result = await executeTool(name, call.GetProperty("args"));
                var callId = call.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                functionResponses.Add(new { functionResponse = new { name, response = new { result }, id = callId } });
            }
            contents.Add(new { role = "user", parts = functionResponses });
        }
        return "I reached the limit for actions in one request. Please split that into a couple of commands.";
    }

    private static object? ToGeminiSchema(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .Where(property => property.Name != "additionalProperties")
            .ToDictionary(property => property.Name, property => ToGeminiSchema(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ToGeminiSchema).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}

internal static class CredentialStore
{
    private const uint Generic = 1;
    private const uint PersistLocalMachine = 2;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags; public uint Type; public string TargetName; public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten; public uint CredentialBlobSize;
        public IntPtr CredentialBlob; public uint Persist; public uint AttributeCount; public IntPtr Attributes;
        public string? TargetAlias; public string? UserName;
    }
    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("Advapi32.dll")] private static extern void CredFree(IntPtr buffer);

    public static string? Read()
    {
        if (!CredRead(AssistantBrain.CredentialTarget, Generic, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        finally { CredFree(pointer); }
    }

    public static void Write(string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = Generic, TargetName = AssistantBrain.CredentialTarget, CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length, Persist = PersistLocalMachine, UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows could not save Ashley's AI key.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            var zero = new byte[bytes.Length];
            Marshal.Copy(zero, 0, blob, zero.Length);
            Marshal.FreeHGlobal(blob);
        }
    }
}
