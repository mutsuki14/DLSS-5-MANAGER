#ifndef SourceDir
  #error SourceDir must point to the verified publish folder
#endif
#ifndef OutputDir
  #error OutputDir must be supplied
#endif
#ifndef AppVersion
  #error AppVersion must be supplied
#endif
#ifndef FileVersion
  #error FileVersion must be supplied
#endif

[Setup]
AppId={{A51F7959-3448-4C79-9A34-CECE90051C57}
AppName=DLSS 5 MANAGER
AppVersion={#AppVersion}
AppVerName=DLSS 5 MANAGER {#AppVersion} (fork preview)
AppPublisher=NODIX TECH
AppPublisherURL=https://github.com/NODIX-TECH/DLSS-5-MANAGER
AppSupportURL=https://github.com/mutsuki14/DLSS-5-MANAGER/issues
AppUpdatesURL=https://github.com/mutsuki14/DLSS-5-MANAGER/releases
AppCopyright=Copyright (c) 2026 Numidia Studios. Built by NODIX TECH.
DefaultDirName={localappdata}\Programs\DLSS5Manager-Fork
DefaultGroupName=DLSS 5 MANAGER (fork preview)
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=DLSS-5-MANAGER-{#AppVersion}-win-x64-setup
SetupIconFile=..\Assets\logo.ico
UninstallDisplayIcon={app}\DLSS 5 MANAGER.exe
UninstallDisplayName=DLSS 5 MANAGER {#AppVersion} (fork preview)
VersionInfoVersion={#FileVersion}
VersionInfoCompany=NODIX TECH
VersionInfoProductName=DLSS 5 MANAGER
VersionInfoProductVersion={#AppVersion}
LicenseFile=..\Copyright.txt
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DLSS 5 MANAGER"; Filename: "{app}\DLSS 5 MANAGER.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\DLSS 5 MANAGER.exe"; Description: "Launch DLSS 5 MANAGER"; Flags: nowait postinstall skipifsilent
