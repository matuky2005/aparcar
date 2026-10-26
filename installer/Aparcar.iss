; Aparcar - instalador de pruebas para WinUI 3 x64 unpackaged.
; /DAppSourceDir="ruta absoluta de dotnet publish"
; /DInstallerOutputDir="ruta absoluta de salida del instalador"
#ifndef AppSourceDir
  #error Se requiere /DAppSourceDir
#endif
#ifndef InstallerOutputDir
  #error Se requiere /DInstallerOutputDir
#endif
#define AppVersion "0.24.0"

[Setup]
AppId={{B9A646DB-96D1-4685-848A-54D46646A74A}
AppName=Aparcar
AppVersion={#AppVersion}
AppVerName=Aparcar {#AppVersion} (versión de pruebas)
AppPublisher=Aparcar
AppPublisherURL=https://github.com/matuky2005/aparcar
DefaultDirName={localappdata}\Programs\Aparcar
DefaultGroupName=Aparcar
DisableProgramGroupPage=yes
OutputDir={#InstallerOutputDir}
OutputBaseFilename=Aparcar-Setup-{#AppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0
UninstallDisplayIcon={app}\Aparcar.Desktop.exe
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
VersionInfoVersion=0.24.0.0
SetupLogging=yes

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Aparcar"; Filename: "{app}\Aparcar.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Aparcar"; Filename: "{app}\Aparcar.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Crear acceso directo en el escritorio"; GroupDescription: "Accesos directos:"; Flags: unchecked

[Run]
Filename: "{app}\Aparcar.Desktop.exe"; Description: "Abrir Aparcar"; Flags: nowait postinstall skipifsilent
