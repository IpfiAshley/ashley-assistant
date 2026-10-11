# Ashley

Ashley is a voice-controlled Windows desktop assistant prototype with a futuristic HUD. She can launch and close apps, search the web, open common folders and websites, change the system volume, and answer simple time and date questions.

## Features

- A desktop interface with two switchable HUD views.
- Online English speech recognition by default, with an optional local English speech model.
- The “Hey Ashley” wake phrase and a 30-second command window for follow-up requests.
- Typed commands when speech is unavailable.
- Native Windows actions for apps, folders, websites, settings, and volume.
- Optional launch at Windows sign-in.

## Start Ashley

On a fresh checkout, build the desktop app first. You need the .NET 10 SDK, an internet connection for NuGet packages, and the Microsoft Edge WebView2 Runtime. Then run these from this folder:

```powershell
.\Build-Ashley.ps1
.\Start-Ashley.ps1
```

The build creates `AshleyApp-HUD-Updated\Ashley.exe`. Once built, double-click that file or use `Start-Ashley.ps1` to open Ashley again. The app also needs the .NET 10 Desktop Runtime.

## Voice and privacy

Ashley starts in **Online** speech mode. Online recognition needs internet access and sends microphone audio to Edge's speech service for transcription. Ashley requests English (South Africa) and falls back to English (US) if that service does not support it. Choose **Offline** in the HUD to use the bundled Vosk English model locally. The offline model is included in this repository; offline recognition itself does not send microphone audio online.

Speech recognition only turns speech into text. Ashley's commands are a defined set of desktop actions; she does not yet have a general-purpose AI reasoning service. Ashley pauses listening while speaking.

## Commands

Say “Hey Ashley,” followed by a command, or give follow-up commands during the next 30 seconds. Examples:

- “What time is it?” / “Could you tell me the date?”
- “Open Calculator,” “launch Spotify,” or “close Brave.”
- “Open my Downloads” or “show the Desktop folder.”
- “Open YouTube” or “go to wikipedia.org.”
- “Open Sound settings” or “show Wi-Fi settings.”
- “Mute,” “turn the volume up,” or “set volume to fifty percent.”
- “Search for Call of Duty Mobile.”
- “Help” / “What can you do?”

Ashley can open apps that appear in a Windows Start menu shortcut. Closing an app sends a normal close request and does not force-terminate it. Other requests need an explicit command ability before Ashley can perform them.

## Project files

- `DesktopApp\` contains the native Windows desktop app source.
- `DesktopApp\models\` contains the bundled offline Vosk model.
- `Jarvis.html` contains the HUD and command recognition logic.
- `Build-Ashley.ps1` builds the Windows app; `Start-Ashley.ps1` starts it.
- `JarvisServer.js`, `Jarvis.ps1`, and `Start-Jarvis.ps1` are the earlier browser-based prototype.
