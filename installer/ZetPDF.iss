; Instalator ZetPDF (Inno Setup 6). Budowanie z katalogu repozytorium:
;   pwsh tools\publish\publish.ps1        (tworzy dist\)
;   pwsh installer\build-installer.ps1    (tworzy installer\Output\ZetPDF-Setup-<wersja>.exe)
; Instalacja domyślnie dla bieżącego użytkownika (bez uprawnień administratora);
; w oknie startowym można wybrać instalację dla wszystkich użytkowników.

#define AppName "ZetPDF"
#ifndef AppVersion
  #define AppVersion "0.1.0-test"
#endif
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.1.0.0"
#endif
#define AppExe "ZetPDF.exe"
#define ProgId "ZetPDF.Document"

[Setup]
AppId={{6E0B7B52-3C1D-4D8E-9A51-2F7C0B9E4A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Marek Zettel
VersionInfoVersion={#AppVersionNumeric}
VersionInfoProductVersion={#AppVersionNumeric}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=ZetPDF-Setup-{#AppVersion}
SetupIconFile=..\src\ZetPDF.App\Assets\ZetPDF.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes
ShowLanguageDialog=auto
UsePreviousLanguage=no

[Languages]
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
pl.AssocPdf=Dodaj ZetPDF do menu „Otwórz za pomocą” dla plików PDF
en.AssocPdf=Add ZetPDF to the "Open with" menu for PDF files
pl.ReadmeName=Przeczytaj (README)
en.ReadmeName=Read me (README)
pl.LicensesName=Licencje bibliotek
en.LicensesName=Third-party licenses

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "assocpdf"; Description: "{cm:AssocPdf}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\README.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "..\dist\LICENSES\*"; DestDir: "{app}\LICENSES"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:ReadmeName}"; Filename: "{app}\README.txt"
Name: "{group}\{cm:LicensesName}"; Filename: "{app}\LICENSES"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Rejestracja jako program obsługujący PDF ("Otwórz za pomocą") – bez przejmowania domyślnego programu.
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "PDF – {#AppName}"; Flags: uninsdeletekey; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: none; ValueName: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assocpdf
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Flags: uninsdeletekey; Tasks: assocpdf

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Ustawienia użytkownika (%APPDATA%\ZetPDF) i pliki robocze (%LOCALAPPDATA%\ZetPDF) zostają po odinstalowaniu.
