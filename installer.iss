[Setup]
AppId={{B8A3D1E7-4F2C-4B9A-8E6D-1A5C3F7E9D2B}
AppName=Notes
AppVersion=1.0.0
AppPublisher=NoteUI
DefaultDirName={localappdata}\Programs\NoteUI
DefaultGroupName=Notes
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

[Tasks]
Name: "desktopicon"; Description: "Cr&#233;er un raccourci sur le bureau"; GroupDescription: "Raccourcis:"
Name: "startup"; Description: "Lancer au d&#233;marrage de Windows"; GroupDescription: "Options:"

[Files]
Source: "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Notes"; Filename: "{app}\NoteUI.exe"
Name: "{autodesktop}\Notes"; Filename: "{app}\NoteUI.exe"; Tasks: desktopicon
Name: "{userstartup}\Notes"; Filename: "{app}\NoteUI.exe"; Tasks: startup

[Run]
Filename: "{app}\NoteUI.exe"; Description: "Lancer Notes"; Flags: nowait postinstall skipifsilent
