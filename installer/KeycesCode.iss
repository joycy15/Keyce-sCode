; =====================================================================
;  Installateur de Keyce'sCode (Membre 5 : distribution)
;  Compilé avec Inno Setup 6 par installer\build-installer.ps1
;  Résultat : installer\Output\KeycesCode-Setup-1.0.0.exe
; =====================================================================

#define MyAppName "Keyce'sCode"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Groupe Keyce'sCode (Keyce B2)"
#define MyAppExeName "KeycesCode.App.exe"

[Setup]
; Identifiant unique de l'application : ne plus le changer (sert aux mises à jour)
AppId={{8F1C2E57-3B6A-4D8E-9C41-6A2B7E5D9F10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\KeycesCode
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Le fichier .exe d'installation est créé dans installer\Output
OutputDir=Output
OutputBaseFilename=KeycesCode-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Permet d'installer pour soi seul (sans droits administrateur) ou pour tous
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Tout le contenu publié par « dotnet publish » (dossier publish à la racine du dépôt)
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
