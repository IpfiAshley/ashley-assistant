# Ashley

Ashley is a voice-controlled Windows desktop assistant prototype with a futuristic HUD. She can launch and close apps, search the web, open common folders and websites, change the system volume, and answer simple time and date questions.

## Features

- A desktop interface with two switchable HUD views.
- Online English speech recognition by default, with an optional local English speech model.
- The “Hey Ashley” wake phrase and unlimited follow-up commands until you say “stop listening” or press Stop.
- YouTube Music search in the default browser, with a first-result playback attempt in an Ashley-controlled Brave, Edge, or Chrome profile when you name one.
- Local command matching for common laptop actions; unrecognized phrases get a quick example instead of an online AI request.
- Typed commands when speech is unavailable.
- Native Windows actions for apps, folders, websites, settings, and volume.
- Optional launch at Windows sign-in.

## Start Ashley

On a fresh checkout, build the desktop app first. You need the .NET 10 SDK, an internet connection for NuGet packages, and the Microsoft Edge WebView2 Runtime. Then run these from this folder:

```powershell
.\Build-Ashley.ps1
.\Start-Ashley.ps1
```

The build creates `AshleyApp-HUD-Latest\Ashley.exe`. Once built, double-click that file or use `Start-Ashley.ps1` to open Ashley again. The app also needs the .NET 10 Desktop Runtime.

## Voice and privacy

Ashley starts in **Online** speech mode. Online recognition needs internet access and sends microphone audio to Edge's speech service for transcription. Ashley requests English (South Africa) and falls back to English (US) if that service does not support it. Choose **Offline** in the HUD to use the bundled Vosk English model locally. The offline model is included in this repository; offline recognition itself does not send microphone audio online.

Speech recognition turns speech into text. Basic commands are matched and run on the laptop without a Gemini request or API key. Online recognition still sends microphone audio to Edge's speech service; choose **Offline** if you want speech recognition to stay on the device. Conversation and online AI command understanding are paused while the basic voice commands are being improved.

## Commands

Say “Hey Ashley,” followed by a command, then continue giving follow-up commands for as long as you like. Say “stop listening” or press Stop to end the listening session. Examples:

- “What time is it?” / “Could you tell me the date?”
- “Open Calculator,” “launch Spotify,” or “close Brave.”
- “Open my Downloads” or “show the Desktop folder.”
- “Open YouTube” or “go to wikipedia.org.”
- “Open Sound settings” or “show Wi-Fi settings.”
- “Mute,” “turn the volume up,” or “set volume to fifty percent.”
- “Search for Call of Duty Mobile.”
- “Play music on Brave,” “play Blinding Lights on Opera GX,” or “play this song” and then say the title.
- “Help” / “What can you do?”

For Brave, Edge, or Chrome, a named song request opens YouTube Music in that browser's normal profile, searches for the song, and uses Windows accessibility controls to select a matching result and press Play when those controls are available. This preserves the browser's existing sign-in. YouTube Music updates or accessibility/autoplay restrictions can prevent Ashley from confirming playback; in that case use the open results tab. A request using the system default browser or a different browser opens search results without the playback attempt.

Ashley supports the commands listed above. Closing an app sends a normal close request and does not force-terminate it. Unrecognized wording receives examples of supported commands; Ashley does not send it to a model while online AI is paused.

## Project files

- `DesktopApp\` contains the native Windows desktop app source.
- `DesktopApp\BrowserMusicController.cs` handles the limited Chromium DevTools playback flow.
- `DesktopApp\models\` contains the bundled offline Vosk model.
- `Jarvis.html` contains the HUD and command recognition logic.
- `Build-Ashley.ps1` builds the Windows app; `Start-Ashley.ps1` starts it.
- `JarvisServer.js`, `Jarvis.ps1`, and `Start-Jarvis.ps1` are the earlier browser-based prototype.
