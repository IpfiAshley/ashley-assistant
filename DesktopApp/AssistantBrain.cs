using System.Net.Http.Headers;
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
    private const string CredentialTarget = "Ashley.OpenAI.ApiKey";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(45) };
    private const string ToolDefinitions = """
    [
      {"type":"function","name":"open_app","description":"Open an installed desktop app by its Start menu name.","parameters":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}},
      {"type":"function","name":"close_app","description":"Ask an open desktop app to close gracefully by name.","parameters":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}},
      {"type":"function","name":"set_volume","description":"Set, increase, decrease, mute, or unmute the Windows master volume.","parameters":{"type":"object","properties":{"value":{"type":"string","description":"0-100, up, down, mute, or unmute"}},"required":["value"],"additionalProperties":false}},
      {"type":"function","name":"play_music","description":"Search YouTube Music for a song and open the results in a selected browser. Do not claim it is already playing.","parameters":{"type":"object","properties":{"song":{"type":"string"},"browser":{"type":"string","description":"Browser name, or default"}},"required":["song","browser"],"additionalProperties":false}},
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
            Title = "Ashley AI setup", Owner = owner, Width = 460, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = System.Windows.Media.Brushes.White, ShowInTaskbar = false
        };
        var stack = new StackPanel { Margin = new Thickness(22) };
        stack.Children.Add(new TextBlock { Text = "Connect Ashley to OpenAI", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(new TextBlock { Text = "Enter an OpenAI API key. API usage may be billed separately from ChatGPT. Ashley stores the key in Windows Credential Manager.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        var key = new PasswordBox { Height = 30, Padding = new Thickness(6), Password = CredentialStore.Read() ?? "" };
        stack.Children.Add(key);
        var model = new TextBox { Text = "gpt-6-luna", Height = 30, Padding = new Thickness(6), Margin = new Thickness(0, 10, 0, 0), IsReadOnly = true };
        stack.Children.Add(model);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var save = new Button { Content = "Save key", IsDefault = true, MinWidth = 90, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(12, 6, 12, 6) };
        buttons.Children.Add(save); buttons.Children.Add(cancel); stack.Children.Add(buttons); dialog.Content = stack;
        save.Click += (_, _) =>
        {
            var candidate = key.Password.Trim();
            if (!candidate.StartsWith("sk-", StringComparison.Ordinal) || candidate.Length < 20)
            {
                MessageBox.Show(dialog, "That doesn't look like an OpenAI API key.", "Check the key", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Ashley AI is not connected yet. Open AI setup and add an OpenAI API key.");

        var input = new List<object> { new { role = "user", content = userText } };
        var tools = JsonSerializer.Deserialize<JsonElement>(ToolDefinitions);
        for (var turn = 0; turn < 4; turn++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "gpt-6-luna",
                store = false,
                instructions = "You are Ashley, a helpful Windows laptop voice assistant. Use tools only for actions the user clearly asked for. Never claim an action succeeded unless its tool result says it did. Music opens YouTube Music search results; do not claim playback started. For destructive or sensitive tasks, explain that this version does not perform them. For ordinary questions, answer concisely and naturally for text to speech.",
                input,
                tools,
                tool_choice = "auto"
            }), Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                var detail = "The online AI service returned an error.";
                try { detail = JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString() ?? detail; } catch { }
                throw new InvalidOperationException(detail);
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var calls = root.GetProperty("output").EnumerateArray().Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call").ToArray();
            if (calls.Length == 0)
            {
                var answer = root.TryGetProperty("output_text", out var outputText) ? outputText.GetString() : null;
                return string.IsNullOrWhiteSpace(answer) ? "I couldn't form a response to that." : answer.Trim();
            }
            foreach (var call in calls)
            {
                var name = call.GetProperty("name").GetString() ?? "";
                using var argsDoc = JsonDocument.Parse(call.GetProperty("arguments").GetString() ?? "{}");
                var result = await executeTool(name, argsDoc.RootElement);
                input.Add(new { type = "function_call", call_id = call.GetProperty("call_id").GetString(), name, arguments = call.GetProperty("arguments").GetString() });
                input.Add(new { type = "function_call_output", call_id = call.GetProperty("call_id").GetString(), output = result });
            }
        }
        return "I reached the limit for actions in one request. Please split that into a couple of commands.";
    }
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
        if (!CredRead("Ashley.OpenAI.ApiKey", Generic, 0, out var pointer)) return null;
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
                Type = Generic, TargetName = "Ashley.OpenAI.ApiKey", CredentialBlob = blob,
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
