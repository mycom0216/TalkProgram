; ─────────────────────────────────────────────────────────────────────────────
; 사내톡 클라이언트 설치 파일 (Inno Setup 6 스크립트)
; - publish.bat이 publish\client 를 만든 뒤 이 스크립트를 컴파일한다 → publish\사내톡-설치-<버전>.exe
; - 관리자 권한 없이 사용자 폴더(%LocalAppData%\Programs\사내톡)에 설치한다 → 회사 PC에서도 바로 설치 가능
; - 바탕화면·시작 메뉴 바로 가기, 제거 프로그램 등록, (선택) 윈도우 시작 시 자동 실행
; - 버전은 exe 파일의 버전(csproj <Version>)을 그대로 읽는다
; ─────────────────────────────────────────────────────────────────────────────
#define AppName "사내톡"
#define AppExe "ChatApp.Client.exe"
#define SourceDir "..\publish\client"
#define AppVersion GetVersionNumbersString(SourceDir + "\" + AppExe)

[Setup]
; AppId는 바꾸지 말 것 (바꾸면 업데이트가 아니라 다른 프로그램으로 설치된다)
AppId={{6E1B7C52-3F0A-4D7B-9B2E-5A1C2D3E4F60}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=basiclike
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\publish
OutputBaseFilename=사내톡-설치-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
; 설치·업데이트 전에 실행 중인 사내톡을 닫는다 (파일이 잠겨 덮어쓰지 못하는 문제 방지)
CloseApplications=yes
RestartApplications=no
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 바로 가기 만들기"; GroupDescription: "추가 작업:"
Name: "autostart"; Description: "윈도우를 시작할 때 사내톡 자동 실행"; GroupDescription: "추가 작업:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{#AppName} 제거"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "사내톡 실행"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 제거 전에 트레이에 떠 있는 사내톡을 끈다
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillChatApp"
