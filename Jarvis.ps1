Add-Type -AssemblyName System.Speech

$culture = [System.Globalization.CultureInfo]::GetCultureInfo('en-US')
$installedRecognizers = [System.Speech.Recognition.SpeechRecognitionEngine]::InstalledRecognizers()
if (-not ($installedRecognizers | Where-Object { $_.Culture.Name -eq $culture.Name })) {
    Write-Error 'Windows has speech features installed, but this prototype uses the older System.Speech interface and cannot see a compatible English (United States) recognizer. Do not reinstall the language features; this prototype needs a different speech backend.'
    exit 1
}

$recognizer = [System.Speech.Recognition.SpeechRecognitionEngine]::new($culture)
$synthesizer = [System.Speech.Synthesis.SpeechSynthesizer]::new()
$recognizer.LoadGrammar([System.Speech.Recognition.DictationGrammar]::new())
$recognizer.SetInputToDefaultAudioDevice()

$script:awaitingCommandUntil = [DateTime]::MinValue
$script:running = $true

function Say([string] $message) {
    Write-Host "Ashley: $message" -ForegroundColor Cyan
    $synthesizer.SpeakAsync($message) | Out-Null
}

function Handle-Command([string] $command) {
    $normalized = $command.Trim().ToLowerInvariant()
    if (-not $normalized) { return }

    Write-Host "You: $command" -ForegroundColor Gray
    if ($normalized -match '^(what time is it|tell me the time|time)$') {
        Say ("It's " + (Get-Date -Format 'h:mm tt'))
    }
    elseif ($normalized -match '^(help|what can you do)$') {
        Say 'You can ask for the time or date, open Notepad or Calculator, or ask me to search the web.'
    }
    elseif ($normalized -match '^(what is the date|what day is it|tell me the date|date)$') {
        Say (Get-Date -Format 'dddd, MMMM d')
    }
    elseif ($normalized -match '^(open )?(notepad|text editor)$') {
        Start-Process notepad.exe
        Say 'Opening Notepad.'
    }
    elseif ($normalized -match '^(open )?(calculator|calc)$') {
        Start-Process calc.exe
        Say 'Opening Calculator.'
    }
    elseif ($normalized -match '^(open )?(browser|web browser|edge)$') {
        Start-Process 'https://www.google.com'
        Say 'Opening your browser.'
    }
    elseif ($normalized -match '^(search for|google)\s+(.+)$') {
        $query = [Uri]::EscapeDataString($Matches[2])
        Start-Process "https://www.google.com/search?q=$query"
        Say "Searching for $($Matches[2])."
    }
    elseif ($normalized -match '^(stop listening|goodbye|exit|quit)$') {
        Say 'Okay. Talk to you later.'
        $script:running = $false
    }
    else {
        Say "I heard $command. I don't know how to do that yet."
    }
}

$recognizer.add_SpeechRecognized({
    param($sender, $eventArgs)
    $heard = $eventArgs.Result.Text.Trim()
    if (-not $heard) { return }

    if ((Get-Date) -le $script:awaitingCommandUntil) {
        $script:awaitingCommandUntil = [DateTime]::MinValue
        Handle-Command $heard
        return
    }

    if ($heard -match '(?i)\bhey[\s,]+ashley\b\s*(.*)$') {
        $command = $Matches[1].Trim()
        if ($command) {
            Handle-Command $command
        }
        else {
            $script:awaitingCommandUntil = (Get-Date).AddSeconds(8)
            Say 'I am listening.'
        }
    }
})

Write-Host 'Ashley is listening for "Hey Ashley". Say "stop listening" to close it.' -ForegroundColor Green
Write-Host 'Press Ctrl+C to exit at any time.' -ForegroundColor DarkGray
try {
    $recognizer.RecognizeAsync([System.Speech.Recognition.RecognizeMode]::Multiple)
    while ($script:running) { Start-Sleep -Milliseconds 200 }
}
finally {
    $recognizer.RecognizeAsyncCancel()
    $recognizer.Dispose()
    $synthesizer.Dispose()
}
