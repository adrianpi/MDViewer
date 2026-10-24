#define MyAppName "MDViewer"
#define MyAppVersion "1.0.2"
#define MyAppPublisher "MDViewer"
#define MyAppExeName "MDViewer.exe"

[Setup]
AppId={{3A8D7231-08F3-4BD9-9EC3-582F648092D4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
OutputDir=Output
OutputBaseFilename=MDViewer-Setup-{#MyAppVersion}
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
ChangesAssociations=yes
SetupLogging=yes

[Tasks]
Name: "associate_md"; Description: "Make MDViewer the default app for Markdown files (.md)"; GroupDescription: "File associations:"; Flags: checkedonce
Name: "desktop_icon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MDViewer"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\MDViewer"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktop_icon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch MDViewer"; Flags: postinstall nowait skipifsilent
Filename: "ms-settings:defaultapps?registeredAppUser=MDViewer"; Description: "Choose MDViewer for .md files in Windows Settings"; Flags: postinstall shellexec skipifsilent; Tasks: associate_md

[Code]
const
  MarkdownExtensionKey = 'Software\Classes\.md';
  MarkdownProgId = 'MDViewer.Markdown';
  MarkdownProgIdKey = 'Software\Classes\' + MarkdownProgId;
  ApplicationCapabilitiesKey = 'Software\MDViewer\Capabilities';
  RegisteredApplicationsKey = 'Software\RegisteredApplications';
  InstallerStateKey = 'Software\MDViewer\Installer';

procedure RegisterMarkdownAssociation;
var
  PreviousProgId: String;
  PreviousAssociationExists: Boolean;
begin
  // Keep the original per-user handler so uninstall can put it back.
  if not RegQueryStringValue(HKCU, InstallerStateKey, 'AssociationConfigured', PreviousProgId) then
  begin
    PreviousAssociationExists := RegQueryStringValue(
      HKCU, MarkdownExtensionKey, '', PreviousProgId);
    if PreviousAssociationExists and (PreviousProgId = MarkdownProgId) then
      PreviousAssociationExists := False;

    if PreviousAssociationExists then
    begin
      RegWriteStringValue(HKCU, InstallerStateKey, 'HadPreviousAssociation', 'yes');
      RegWriteStringValue(HKCU, InstallerStateKey, 'PreviousProgId', PreviousProgId);
    end
    else
      RegWriteStringValue(HKCU, InstallerStateKey, 'HadPreviousAssociation', 'no');

    RegWriteStringValue(HKCU, InstallerStateKey, 'AssociationConfigured', 'yes');
  end;

  RegWriteStringValue(HKCU, MarkdownProgIdKey, '', 'Markdown document');
  RegWriteStringValue(
    HKCU, MarkdownProgIdKey + '\DefaultIcon', '',
    '"' + ExpandConstant('{app}\{#MyAppExeName}') + '",0');
  RegWriteStringValue(
    HKCU, MarkdownProgIdKey + '\shell\open\command', '',
    '"' + ExpandConstant('{app}\{#MyAppExeName}') + '" "%1"');
  RegWriteStringValue(
    HKCU, MarkdownExtensionKey + '\OpenWithProgids', MarkdownProgId, '');
  RegWriteStringValue(HKCU, MarkdownExtensionKey, '', MarkdownProgId);
  RegWriteStringValue(HKCU, ApplicationCapabilitiesKey, 'ApplicationName', 'MDViewer');
  RegWriteStringValue(
    HKCU, ApplicationCapabilitiesKey, 'ApplicationDescription',
    'Markdown document viewer');
  RegWriteStringValue(
    HKCU, ApplicationCapabilitiesKey + '\FileAssociations', '.md', MarkdownProgId);
  RegWriteStringValue(
    HKCU, RegisteredApplicationsKey, 'MDViewer', ApplicationCapabilitiesKey);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('associate_md') then
    RegisterMarkdownAssociation;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  CurrentProgId: String;
  HadPreviousAssociation: String;
  PreviousProgId: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  if RegQueryStringValue(HKCU, InstallerStateKey, 'AssociationConfigured', CurrentProgId) then
  begin
    // Do not overwrite a different application chosen after MDViewer was installed.
    if RegQueryStringValue(HKCU, MarkdownExtensionKey, '', CurrentProgId) and
       (CurrentProgId = MarkdownProgId) then
    begin
      if RegQueryStringValue(HKCU, InstallerStateKey, 'HadPreviousAssociation', HadPreviousAssociation) and
         (HadPreviousAssociation = 'yes') and
         RegQueryStringValue(HKCU, InstallerStateKey, 'PreviousProgId', PreviousProgId) then
        RegWriteStringValue(HKCU, MarkdownExtensionKey, '', PreviousProgId)
      else
        RegDeleteValue(HKCU, MarkdownExtensionKey, '');
    end;

    RegDeleteValue(HKCU, MarkdownExtensionKey + '\OpenWithProgids', MarkdownProgId);
    RegDeleteValue(HKCU, RegisteredApplicationsKey, 'MDViewer');
    RegDeleteKeyIncludingSubkeys(HKCU, ApplicationCapabilitiesKey);
    RegDeleteKeyIncludingSubkeys(HKCU, MarkdownProgIdKey);
    RegDeleteKeyIncludingSubkeys(HKCU, InstallerStateKey);
  end;
end;
