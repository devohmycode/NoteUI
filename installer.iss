[Setup]
AppId={{B8A3D1E7-4F2C-4B9A-8E6D-1A5C3F7E9D2B}
AppName=NoteUI
AppVersion=0.4.0
AppPublisher=NoteUI
DefaultDirName={localappdata}\Programs\NoteUI
DefaultGroupName=NoteUI
PrivilegesRequired=lowest
OutputDir=installer
OutputBaseFilename=NoteUI-Setup
SetupIconFile=app.ico
UninstallDisplayIcon={app}\NoteUI.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=NoteUI.exe
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "Launch at Windows startup"; GroupDescription: "Options:"; Languages: english
Name: "startup"; Description: "Lancer au démarrage de Windows"; GroupDescription: "Options:"; Languages: french

[Files]
Source: "bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "runtimes\linux-*\*"

[Icons]
Name: "{group}\NoteUI"; Filename: "{app}\NoteUI.exe"
Name: "{autodesktop}\NoteUI"; Filename: "{app}\NoteUI.exe"; Tasks: desktopicon
Name: "{userstartup}\NoteUI"; Filename: "{app}\NoteUI.exe"; Tasks: startup

[Run]
Filename: "{app}\NoteUI.exe"; Description: "{cm:LaunchProgram,NoteUI}"; Flags: nowait postinstall skipifsilent
