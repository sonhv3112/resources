; Inno Setup script for GpmLite (Windows). Produces a single Setup.exe installer.
; Build: run package-windows.ps1 first (creates dist\GpmLite-win-x64\), then compile this with
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" scripts\installer.iss
; Output: dist\GpmLite-Setup.exe

#define MyAppName "GpmLite"
#define MyAppVersion "0.1.0"
#define MyAppExe "GpmLite.exe"

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExe}
OutputDir=..\dist
OutputBaseFilename=GpmLite-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Files]
; Everything produced by package-windows.ps1 (app + camoufox\ + sidecar\).
Source: "..\dist\GpmLite-win-x64\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Tạo lối tắt trên màn hình"; GroupDescription: "Lối tắt:"

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Mở {#MyAppName}"; Flags: nowait postinstall skipifsilent
