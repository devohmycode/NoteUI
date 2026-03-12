# NoteUI

A modern sticky notes application for Windows, built with **WinUI 3** (Windows App SDK 1.8) and **.NET 8**.

![Windows](https://img.shields.io/badge/Windows-10%2F11-blue) ![.NET 8](https://img.shields.io/badge/.NET-8.0-purple) ![WinUI 3](https://img.shields.io/badge/WinUI-3-green)

## Features

### Notes
- Create, edit, delete, and duplicate notes
- Rich text editing (bold, italic, underline, strikethrough, bullet lists)
- 14 color themes (Yellow, Green, Mint, Teal, Blue, Lavender, Purple, Pink, Coral, Orange, Peach, Sand, Gray, Charcoal)
- Pin notes to the top of the list
- Search and filter by title or content
- Inline title editing
- Auto-save on every change

### Task Lists
- Dedicated task list note type with checkboxes
- Add, remove, and reorder tasks
- Task completion tracking with progress display (e.g. "3/5")
- Per-task reminders with date and time picker
- Enter to add a new task, Backspace on empty to delete

### Reminders
- Set reminders on individual tasks with a date and time picker (keyboard input, minute precision)
- Windows toast notifications when a reminder fires
- Visual bell indicator on tasks with active reminders
- Automatic cleanup after notification

### Voice Notes
- Speech-to-text recording with real-time transcription
- Two recognition engines: **Vosk** (lightweight) and **Whisper** (OpenAI, higher accuracy)
- French and English language support
- In-app model download with progress tracking
- Live waveform visualization during recording
- Transcription is saved as a regular note

#### Available Models

| Model | Engine | Size | Languages |
|-------|--------|------|-----------|
| Vosk small | Vosk | 40 MB | FR, EN |
| Whisper tiny | Whisper | 75 MB | FR, EN |
| Whisper base | Whisper | 140 MB | FR, EN |
| Whisper small | Whisper | 460 MB | FR, EN |

### Cloud Sync
- **Firebase Realtime Database** with email/password and Google Sign-In
- **WebDAV / Nextcloud** with username/password authentication
- Automatic conflict resolution (most recent version wins)
- Auto-reconnect on startup from saved credentials

### Customization
- Light, Dark, and System theme
- Acrylic backdrop with custom tint color, opacity, and luminosity controls
- Configurable notes storage folder
- Always-on-top (pin) for any window

### UI & Animations
- Borderless custom window chrome with shadow
- GPU-accelerated Composition animations (fade, slide, scale)
- Smooth compact/expand transitions
- Card shadows for depth
- System tray icon (close to tray, reopen from tray)

## Installation

Download the latest **NoteUI-Setup.exe** from the [Releases](../../releases) page and run the installer.

Options during installation:
- Create a desktop shortcut
- Launch at Windows startup

## Build from Source

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Windows App SDK 1.8](https://learn.microsoft.com/windows/apps/windows-app-sdk/)
- Windows 10 (build 19041) or later

### Build
\`\`\`bash
dotnet build
\`\`\`

### Publish
\`\`\`bash
dotnet publish -c Release -r win-x64 --self-contained true
\`\`\`

Output: \`bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/\`

### Create Installer
Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php):
\`\`\`bash
"C:\Users\Dev\AppData\Local\Programs\Inno Setup 6\ISCC.exe" installer.iss
\`\`\`

Output: \`installer/NoteUI-Setup.exe\`

## Project Structure

\`\`\`
NoteUI/
  App.xaml.cs                    # Application entry point
  MainWindow.xaml/.cs            # Main window (note list, search, settings)
  NoteWindow.xaml/.cs            # Note editor / task list editor
  VoiceNoteWindow.xaml/.cs       # Voice recording and transcription
  NotesManager.cs                # Note data model and persistence
  SpeechRecognitionService.cs    # Vosk/Whisper STT engines and model downloader
  ReminderService.cs             # Background reminder checker and toast notifications
  FirebaseSync.cs                # Firebase Realtime Database sync
  WebDavSync.cs                  # WebDAV/Nextcloud sync
  AppSettings.cs                 # Settings persistence (theme, backdrop, cloud)
  ActionPanel.cs                 # Flyout menus and color picker
  AnimationHelper.cs             # GPU Composition animations
  AcrylicSettingsWindow.xaml/.cs # Custom acrylic backdrop editor
  WindowHelper.cs                # Borderless window utilities
  WindowShadow.cs                # DWM shadow effects
  TrayIcon.cs                    # System tray integration
  installer.iss                  # Inno Setup installer script
\`\`\`

## Data Storage

- **Notes**: \`<notes_folder>/notes.json\` (default: \`%LocalAppData%/NoteUI/notes/\`)
- **Settings**: \`%LocalAppData%/NoteUI/\` (theme, backdrop, cloud credentials)
- **Voice models**: \`%LocalAppData%/NoteUI/models/<model_id>/\`
- **Voice settings**: \`%LocalAppData%/NoteUI/voice_settings.json\`

## Tech Stack

- **Framework**: WinUI 3 / Windows App SDK 1.8
- **Runtime**: .NET 8 (self-contained, win-x64)
- **Audio**: NAudio 2.2.1
- **Speech-to-text**: Vosk 0.3.38, Whisper.net 1.7.4
- **Cloud**: Firebase REST API, WebDAV protocol
- **Auth**: Firebase email/password, Google OAuth2 PKCE
- **Animations**: Windows.UI.Composition (DirectX GPU-accelerated)
- **Installer**: Inno Setup 6

## License

All rights reserved.
