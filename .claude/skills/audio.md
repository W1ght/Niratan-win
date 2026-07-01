---
name: audio
description: Popup audio playback verification — audio sources, autoplay, template expansion, local audio (docs/VERIFICATION.md §4)
---

# Popup Audio Test

Verify the popup audio playback chain end-to-end:

```
AudioSettings → PopupHtmlGenerator injects settings → popup.js
  → click audio button → fetchAudioUrl / expandAudioTemplate
  → postPopupMessage(playWordAudio) → C# DictionaryLookupPopup
  → IAudioService.PlayAsync → WinUI MediaPlayer
```

## Affected Files

When modifying any of these, this skill is mandatory:
- `Hoshi/Services/Audio/AudioService.cs`
- `Hoshi/Services/Audio/IAudioService.cs`
- `Hoshi/Models/Settings/AudioSettings.cs`
- `Hoshi/Views/Dictionary/DictionaryLookupPopup.cs` (playWordAudio handler)
- `Hoshi/Services/Dictionary/PopupHtmlGenerator.cs` (SerializeAudioSources, audio injection)
- `Hoshi/Web/DictionaryPopup/popup.js` (fetchAudioUrl, expandAudioTemplate, playWordAudio, autoplay)
- `Hoshi/ViewModels/Pages/AudioSettingsPageViewModel.cs`

## Build and Launch

```powershell
.\build-and-run.ps1
```

## Manual Verification Checklist

### 1. Audio Settings Persistence
- Open Settings → Audio
- Add a custom audio source (name + URL)
- Toggle sources on/off
- Change playback mode (interrupt / duck / mix)
- Toggle autoplay
- Restart app, confirm settings persist

### 2. Default Audio Source
- Open a book, look up a word with known audio (e.g. 食べる)
- Click the audio icon in the popup
- Audio should play via `https://hoshi-reader.manhhaoo-do.workers.dev/`
- Check logs: `[Audio] Playing '...' (... bytes) mode=...`

### 3. URL Template Expansion
- In popup.js, the template `{term}` and `{reading}` must be replaced:
  ```
  https://hoshi-reader.manhhaoo-do.workers.dev/?term=食べる&reading=たべる
  ```
- Check DevTools console for `[Audio] expandAudioTemplate:` logs

### 4. Autoplay
- Enable autoplay in audio settings
- Look up a word — audio should play immediately without clicking
- Disable autoplay — audio should NOT play on lookup

### 5. Local Audio (requires AnkiConnect + local audio server)
- Enable local audio in settings
- Look up a word — should try `http://localhost:8765/localaudio/get/?term=...`
- If local server is offline, should fallback gracefully

### 6. Playback Modes
- **Interrupt**: Play audio A, then B during A → A stops, B plays
- **Duck**: audio behavior depends on Windows MediaPlayer ducking support
- **Mix**: Play audio A, then B during A → both may overlap

### 7. Nested Popup Audio
- Open a popup, then nested lookup inside it
- Click audio in child popup — should play for child popup's word
- Close child, click audio in parent — should play for parent popup's word

## Automated Test Filter (when tests exist)

```powershell
dotnet test Hoshi.Tests/Hoshi.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Audio"
```

## Log Keywords

Watch for these in Serilog output:
```
[Audio] Playing '...'
[Audio] Playback ended
[Audio] Playback failed
[Audio] Download timeout
[Audio] Download failed
```
