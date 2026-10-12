#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
[Setup]
AppId={{A02DE67B-3DA8-48A3-95B7-3CBA32F4CB0D}
AppName=Wysyłka masowa z medycyny pracy
AppVersion={#AppVersion}
AppPublisher=MP
AppPublisherURL=https://github.com/Pawel-sp9pw/mp-mail
DefaultDirName={localappdata}\Programs\MP Mail
DefaultGroupName=Wysyłka masowa z medycyny pracy
OutputDir=..\artifacts\installer
OutputBaseFilename=MpMail-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
UninstallDisplayIcon={app}\MpMail.exe
[Languages]
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
[Tasks]
Name: "desktopicon"; Description: "Utwórz skrót na pulpicie"; Flags: unchecked
[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Wysyłka masowa z medycyny pracy"; Filename: "{app}\MpMail.exe"
Name: "{autodesktop}\Wysyłka masowa z medycyny pracy"; Filename: "{app}\MpMail.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\MpMail.exe"; Description: "Uruchom Wysyłkę masową z medycyny pracy"; Flags: nowait postinstall skipifsilent
[Code]
function GetCurrentUserSid: String;
var
  Locator, Services, Account, Accounts: Variant;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Accounts := Services.ExecQuery('SELECT SID FROM Win32_UserAccount WHERE Name = ''' +
      GetUserNameString + ''' AND Domain = ''' + GetEnv('USERDOMAIN') + '''');
    if Accounts.Count > 0 then begin
      Account := Accounts.ItemIndex(0);
      Result := Account.SID;
    end;
  except
    Result := '';
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Sid: String;
begin
  if CurUninstallStep = usUninstall then begin
    Sid := GetCurrentUserSid;
    if Sid <> '' then
      Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "MP-Mail-' + Sid + '" /F',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
