; Inno Setup script for Elledi Transfer.
; Built by build-installer.ps1, which publishes the app into ..\publish\app first.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; Never change AppId: it lets new versions upgrade the existing installation
AppId={{6E4B1A52-3C2D-4E8F-9A1B-7D5C0E2F8A41}
AppName=Elledi Transfer
AppVersion={#AppVersion}
AppVerName=Elledi Transfer {#AppVersion}
AppPublisher=Elledi
; Per-user install in %LOCALAPPDATA%\Programs, no administrator rights needed;
; the setup offers "install for all users" when started by an administrator
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Elledi Transfer
DefaultGroupName=Elledi Transfer
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=EllediTransfer-Setup-{#AppVersion}
SetupIconFile=..\EllediTransfer.Gui\Assets\app.ico
UninstallDisplayIcon={app}\EllediTransfer.Gui.exe
UninstallDisplayName=Elledi Transfer
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Close a running copy before replacing its files
CloseApplications=yes

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Executables and libraries of versions built before the rename to EllediTransfer
Type: files; Name: "{app}\WinScp.*"

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Elledi Transfer"; Filename: "{app}\EllediTransfer.Gui.exe"
Name: "{autodesktop}\Elledi Transfer"; Filename: "{app}\EllediTransfer.Gui.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\EllediTransfer.Gui.exe"; Description: "{cm:LaunchProgram,Elledi Transfer}"; Flags: nowait postinstall skipifsilent
