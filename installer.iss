[Setup]
AppId={{B8A3D1E7-4F2C-4B9A-8E6D-1A5C3F7E9D2B}
AppName=NoteUI
AppVersion=0.0.1
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

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le bureau"; GroupDescription: "Raccourcis:"
Name: "startup"; Description: "Lancer au démarrage de Windows"; GroupDescription: "Options:"

[Files]
Source: "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Notes"; Filename: "{app}\NoteUI.exe"
Name: "{autodesktop}\Notes"; Filename: "{app}\NoteUI.exe"; Tasks: desktopicon
Name: "{userstartup}\Notes"; Filename: "{app}\NoteUI.exe"; Tasks: startup

[Run]
Filename: "{app}\NoteUI.exe"; Description: "Lancer Notes"; Flags: nowait postinstall skipifsilent
