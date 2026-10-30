using System.Data.Common;
using System.Net;
using System.Text;
using ChatApp.Common.Models;
using ChatApp.Server;
using ChatApp.Server.Core;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;
using ChatApp.Server.Security;
using ChatApp.Server.Services;

// [읽는 법] 콘솔 출력을 UTF-8로 맞춘다. 이 줄이 없으면 한글 로그가 깨져 보일 수 있다. 이 파일은 class 없이 바로 쓴 "최상위 문" 코드라서 위에서 아래로 실행되는 순서가 곧 프로그램의 순서다.
Console.OutputEncoding = Encoding.UTF8;

// ── 설정 읽기 ──
string configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
if (!File.Exists(configPath))
{
    Console.WriteLine("appsettings.json이 없습니다.");
    Console.WriteLine("ChatApp.Server 폴더의 appsettings.example.json을 복사해 appsettings.json을 만들고,");
    Console.WriteLine("ConnectionString의 DB 비밀번호와 AllowedEmailDomains(가입 허용 이메일 도메인)를 수정한 뒤 다시 실행하세요.");
    return;
}
// [읽는 법] 설정 파일(JSON)을 ServerConfig 객체로 바꾼다. DB 비밀번호가 들어 있어 appsettings.json은 Git에 올리지 않고(.gitignore), 형식만 보여 주는 appsettings.example.json을 올린다.
ServerConfig config = ServerConfig.Load(configPath);
// [읽는 법] 실행할 때 첫 번째 인자로 포트를 주면 설정 파일의 값보다 우선한다(같은 PC에서 서버를 두 개 띄워 시험할 때 유용). TryParse는 숫자가 아니면 예외 대신 false를 돌려준다.
if (args.Length > 0 && int.TryParse(args[0], out int port)) config.Port = port;

string logPath = Path.Combine(
    AppContext.BaseDirectory,

//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   서버의 "시작 → 점검 → 운영 → 종료" 전체 순서를 담은 진입점. 점검에 하나라도 실패하면 서버를 켜지 않고 이유를 알려 준 뒤 끝낸다(fail-fast).
//
// ■ 시작 순서와 이유 (위에서 아래로 그대로 읽으면 된다)
//   ① appsettings.json 읽기(없으면 안내 후 종료) → ② 로그 파일 열기(이후 모든 오류를 남기려고 가장 먼저) → ③ DB 연결 확인
//   ④ 저장소·서비스 만들기, 기본 방 보장(EnsureLobby) → ⑤ DB 스키마 버전 확인(오래되면 필요한 SQL 파일 목록을 알려 주고 종료)
//   ⑥ TLS 인증서 준비(읽지 못하면 평문으로 몰래 열지 않고 종료) → ⑦ ChatServer 생성·시작(포트가 사용 중이면 원인을 알리고 종료)
//   ⑧ 관리자 없음·승인 대기 안내 → ⑨ 종료 신호 등록 → ⑩ 콘솔 입력 스레드 시작 → ⑪ 메인 스레드는 관리자 명령 처리 루프
//   ⑫ 루프를 빠져나오면(Ctrl+C, /quit) server.Stop()으로 정상 종료
//
// ■ 스레드 지도 (이 서버가 도는 동안)
//   메인(콘솔 명령 처리) / Console(입력 읽기) / Accept / Heartbeat / DbWriter / AuditWriter / Retention / Logger + 연결마다 Session-N, Send-N
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 최상위 문(top-level statements) ......................... "C# top-level statements"
//   - fail-fast(빨리 실패하기), secure by default ............ "fail fast principle secure by default"
//   - 정상 종료(graceful shutdown), ProcessExit, CancelKeyPress . "graceful shutdown .NET Console.CancelKeyPress ProcessExit"
//   - 의존성 조립(composition root) .......................... "composition root dependency injection manual"
//   - BlockingCollection으로 스레드 간 명령 전달 .............. "BlockingCollection producer consumer console"
//   - DB 스키마 버전 관리(마이그레이션) ....................... "database migration schema version"
//
// ■ 면접 예상 질문과 답
//   Q1. Program.cs의 역할은 무엇이고, 왜 모든 객체를 여기서 만드나요?
//       A. 서버가 쓰는 부품(DB, 저장소, 서비스, 서버)을 한곳에서 만들어 서로 연결하는 "조립 지점"입니다. 각 클래스는 필요한 부품을 생성자로 받기만 하므로, 어떤 구현을 쓸지는 이 파일만 보면 알 수 있고 테스트 때 다른 부품으로 바꾸기 쉽습니다.
//   Q2. 시작할 때 DB 스키마 버전을 확인하는 이유는요?
//       A. 새 기능은 새 표·칸이 필요한데, SQL을 적용하지 않고 켜면 메시지 저장이 "칸 없음" 오류로 계속 실패해 대화가 조용히 사라질 수 있습니다. 그래서 필요한 버전보다 낮으면 어떤 SQL 파일을 순서대로 적용해야 하는지 알려 주고 서버를 켜지 않습니다. 조용히 잘못 동작하는 것보다 시끄럽게 멈추는 편이 안전합니다.
//   Q3. TLS 인증서를 못 읽으면 평문으로라도 켜지 않고 종료하는 이유는요?
//       A. 사용자는 암호화된 줄 알고 쓰는데 실제로는 평문이면 가장 위험하기 때문입니다. 보안 기능은 실패하면 "안전한 쪽으로" 멈추게 했고, 평문은 설정에서 Tls.Enabled=false로 일부러 꺼야만 되며 그때도 시작 로그에 경고를 남깁니다.
//   Q4. Ctrl+C로 끌 때 남은 대화가 사라지지 않게 하는 방법은요?
//       A. CancelKeyPress에서 e.Cancel = true로 즉시 종료를 막고 종료 신호만 세웁니다. 메인 루프가 신호를 보고 빠져나와 server.Stop()을 부르면, 새 접속 차단 → 기존 접속 종료 → 남은 대화를 DB에 저장(MessageStore.Dispose) 순서로 끝납니다. 서비스 중지(SIGTERM)는 ProcessExit에서 같은 정리를 하고, Stop은 여러 번 불려도 한 번만 실행되게 만들었습니다.
//   Q5. 콘솔 입력을 별도 스레드에서 읽는 이유는요?
//       A. Console.ReadLine은 입력이 올 때까지 멈추는 블로킹 호출이라 메인 스레드에서 쓰면 종료 신호를 확인하지 못합니다. 입력 스레드는 명령을 BlockingCollection에 넣고, 메인은 0.2초마다 TryTake로 명령 또는 종료 신호를 번갈아 확인합니다. 입력이 없는 환경(서비스)에서는 ReadLine이 null을 줘서 입력 스레드만 끝나고 서버는 계속 돕니다.
//   Q6. 첫 관리자는 어떻게 만드나요?
//       A. 관리자가 한 명도 없으면 관리자 창을 열 사람이 없으므로, 서버 PC의 콘솔에서 /grant 사원번호로 지정합니다. 서버 PC에 접근할 수 있는 사람이 곧 최고 권한자라는 신뢰 모델이고, 이 동작도 행위자 "CONSOLE"로 감사 로그에 남습니다.
//   Q7. 이 파일의 개선점은요? (솔직하게 답하기)
//       A. (1) "/quit"는 Trim 전에 비교하므로 뒤에 공백이 붙으면 종료되지 않는다. (2) files 테이블 안내 문구에 개발 PC의 경로(C:\Users\...)가 직접 들어 있어 다른 환경에서는 맞지 않는다. (3) 명령 처리 switch가 길어서 명령별 함수나 명령 표로 나누면 읽기 쉽다. (4) 설정 오류(JSON 형식 오류)는 예외로 종료되므로 사람이 읽기 쉬운 메시지로 바꾸면 좋다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 서버가 바로 꺼진다 ....................... 콘솔·logs 폴더 마지막 줄: appsettings.json 없음 / DB 연결 실패 / 스키마 오래됨 / 인증서 오류 / 포트 사용 중 중 하나
//   - "DB 스키마가 오래되었습니다" ............. 출력된 db\migrate_0NN_*.sql을 CMD에서 순서대로 실행(009는 미리 백업·확인)
//   - "포트를 이미 쓰고 있습니다" ............... 서버가 두 개 떴는지(Visual Studio에서 ▶ 중복 실행), 다른 프로그램의 9000 포트 사용
//   - Ctrl+C 후 오래 걸린다 ..................... Stop의 Join(2초)·MessageStore.Dispose(최대 5초) 대기, DB 응답 지연
//   - 관리자 창을 열 수 없다 ................... 콘솔에서 /grant 사원번호 (관리자 0명 경고가 시작 때 나온다)
// ───────────────────────────────────────────────────────────────────────────
    "logs",
    $"server-{DateTime.Now:yyyyMMdd}-pid{Environment.ProcessId}.log"); // 로그 파일 날짜도 서버 PC 날짜로
// [읽는 법] 로그 파일 이름에 날짜와 프로세스 번호(pid)를 넣어, 서버를 여러 번 켜도 파일이 섞이지 않는다. using var로 선언해 프로그램 끝에서 자동으로 닫히고, 로거는 내부에 전용 스레드(Logger)를 가진다.
using var log = new ServerLogger(logPath);

// ── DB 연결 확인 ──
var db = new Db(config.ConnectionString);
try
{
    // [읽는 법] 실제로 한 번 연결해 보는 점검. 실패(DbException)하면 원인 후보(MariaDB 실행 여부, 연결 문자열)를 알리고 return으로 프로그램을 끝낸다 — 이 아래 코드는 DB가 살아 있다는 전제에서 쓰였다.
    db.TestConnection();
    log.Info("MariaDB 연결 확인");
}
catch (DbException ex)
{
    log.Error($"MariaDB에 연결할 수 없습니다: {ex.Message}");
    log.Error("MariaDB가 실행 중인지, appsettings.json의 ConnectionString이 맞는지 확인하세요.");
    return;
}

var userRepository = new UserRepository(db);
var auth = new AuthService(userRepository, config);
var auditRepository = new AuditRepository(db);
// [읽는 법] 감사 로그는 전용 스레드(AuditWriter)가 큐에서 꺼내 DB에 쓴다. 호출하는 쪽은 큐에 넣기만 하므로 느린 DB가 요청 처리를 막지 않는다. using var 덕분에 프로그램이 끝날 때 남은 기록을 쓰고 정리한다.
using var audit = new AuditLog(auditRepository, log); // 감사 로그 기록 스레드 시작
var roomRepository = new RoomRepository(db);
// [읽는 법] 기본 방 "전체 채팅"(1번)이 DB에 없으면 만든다. 이미 있으면 아무것도 하지 않으므로 서버를 몇 번 켜도 안전하다(멱등).
roomRepository.EnsureLobby(); // 기본 방 "전체 채팅"(1번)

var fileRepository = new FileRepository(db);
// [왜] 새 기능(영구 방·읽음 표시·답장 등)은 DB에 새 표·칸이 필요하다. 스크립트를 안 돌린 채 켜면
//      기능이 이상하게 실패하므로, 시작할 때 분명히 알려 준다.
// [읽는 법] DB에 기록된 스키마 버전과 코드가 요구하는 버전(Db.RequiredSchemaVersion)을 비교한다. 아래 migrations 표에서 "현재 버전 다음부터 필요 버전까지"의 SQL 파일 이름만 골라 출력한다.
int schema = db.SchemaVersion();
if (schema < Db.RequiredSchemaVersion)
{
    log.Error($"DB 스키마가 오래되었습니다(현재 {schema}, 필요 {Db.RequiredSchemaVersion}). 현재 버전 다음부터 순서대로 적용하세요:");
    var migrations = new Dictionary<int, string>
    {
        [2] = "migrate_002_files.sql", [3] = "migrate_003_status.sql", [4] = "migrate_004_admin.sql",
        [5] = "migrate_005_rooms_messages.sql", [6] = "migrate_006_room_notices.sql", [7] = "migrate_007_message_edit.sql",
        [8] = "migrate_008_login_tokens.sql", [9] = "migrate_009_utc_timestamps.sql", [10] = "migrate_010_device_login_tokens.sql", [11] = "migrate_011_friends.sql", [12] = "migrate_012_profile_photo.sql", [13] = "migrate_013_profile_background.sql",
    };
    for (int version = Math.Max(schema + 1, 2); version <= Db.RequiredSchemaVersion; version++)
    {
        if (version == 9)
        {
            log.Error(@"  009 전에 db\preview_009_utc_timestamps.sql을 확인하고 전체 백업을 만드세요.");
            log.Error(@"  009는 기존 시각대에 맞게 offset/확인 변수를 설정하지 않으면 적용되지 않습니다.");
        }
        log.Error($"  db\\{migrations[version]}");
    }
    log.Error(@"명령 프롬프트(CMD)에서 프로젝트 폴더로 이동한 뒤 위 파일을 한 개씩 순서대로 실행하세요. PowerShell은 < 입력 리디렉션을 지원하지 않습니다.");
    // [왜] 그대로 켜면 메시지 저장이 칸 없음 오류로 계속 실패한다(대화가 조용히 사라짐). 여기서 멈추는 편이 안전하다.
    // [읽는 법] 종료 코드 1 = 실패. 윈도우 서비스나 배치 파일이 "정상 종료가 아니다"를 알아챌 수 있다(0이 성공). 출력만 하고 return하면 종료 코드가 0이라 실패를 모른다.
    Environment.ExitCode = 1;
    return;
}

if (!fileRepository.TableExists())
{
    log.Error("DB에 files 테이블이 없어 파일 전송을 쓸 수 없습니다. (채팅은 정상 동작)");
    log.Error(@"cmd에서 실행: ""C:\Program Files\MariaDB 13.0\bin\mysql.exe"" -u root -p chatapp < ""C:\Users\LMS\Downloads\ChatApp\ChatApp\db\migrate_002_files.sql""");
}

// ── TLS 인증서 ──
ServerTls? tls = null;
if (config.Tls.Enabled)
{
    try
    {
        // [읽는 법] 인증서(.pfx)가 있으면 읽고, 없으면 자체 서명 인증서를 만든다. 아래 catch는 "읽기·권한·암호·형식 오류"만 잡아 서버를 멈춘다(예상한 실패만 잡고 나머지는 그대로 드러냄).
        tls = ServerCertificate.LoadOrCreate(config.Tls, log);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or InvalidDataException)
    {
        // [왜] 인증서를 못 읽으면 평문으로 "몰래" 열지 않고 서버를 멈춘다. 암호화된 줄 알고 쓰는 것이 가장 위험하다.
        log.Error($"TLS 인증서를 읽을 수 없습니다: {ex.Message}");
        log.Error("appsettings.json의 Tls.CertificatePath / CertificatePassword를 확인하세요. (평문으로 쓰려면 Tls.Enabled를 false로)");
        return;
    }
}
else
{
    log.Error("TLS가 꺼져 있습니다(Tls.Enabled=false). 비밀번호와 대화가 암호화되지 않고 전송됩니다. 테스트 용도로만 쓰세요.");
}

// [읽는 법] 지금까지 만든 부품을 모두 ChatServer에 넘겨 조립한다. IPAddress.Any = 이 PC의 모든 네트워크 카드에서 오는 접속을 받는다(0.0.0.0).
var server = new ChatServer(IPAddress.Any, config.Port, auth, roomRepository, fileRepository, new MessageRepository(db),
                            userRepository, auditRepository, audit, config, log, tls?.Context, db);
try
{
    // [읽는 법] 여기서 Accept·Heartbeat 등 스레드가 시작되고 포트가 열린다. 포트가 이미 사용 중이면 SocketException(AddressAlreadyInUse)이 나므로, 예외로 죽는 대신 원인을 알리고 끝낸다.
    server.Start();
}
catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
{
    // [왜] 같은 포트를 다른 프로그램(대개 이미 켜져 있는 서버)이 쓰고 있으면 여기서 실패한다.
    //      예외로 죽는 대신 원인을 알려 주고 끝낸다. (Visual Studio에서 ▶를 한 번 더 눌러 서버가 두 개 뜨는 경우가 흔하다)
    log.Error($"포트 {config.Port}을(를) 이미 다른 프로그램이 쓰고 있습니다. 서버가 이미 실행 중인지 확인하세요.");
    log.Error("사내톡 창만 하나 더 띄우려면: 솔루션 탐색기에서 ChatApp.Client 오른쪽 클릭 → 디버그 → 새 인스턴스 시작");
    server.Stop();
    return;
}
// [읽는 법] 서버가 켜진 사실도 감사 로그에 남긴다(행위자 "CONSOLE", 상세에 평문/TLS 구분). 나중에 "언제부터 평문으로 운영됐는가"를 추적할 수 있다.
audit.Record(AuditActions.ServerStart, AdminService.ConsoleActor, detail: tls is null ? "평문" : "TLS");
log.Info($"가입 허용 이메일: {auth.GetServerInfo().DomainsText}, 가입 즉시 승인: {config.AutoApprove}");
log.Info($"파일 저장 폴더: {config.UploadRoot} (최대 {config.MaxFileSizeMB}MB)");

// ── 관리자 안내 ──
// [왜] 관리자가 한 명도 없으면 아무도 관리자 창을 열 수 없다. 첫 관리자는 서버 콘솔(서버 PC에 접근할 수 있는 사람)이 지정한다.
if (userRepository.CountActiveAdmins() == 0)
    log.Error("관리자 계정이 없습니다. 서버 콘솔에서 /grant 사원번호 로 첫 관리자를 지정하세요.");
int pendingCount = userRepository.CountPending();
if (pendingCount > 0) log.Info($"승인 대기 중인 가입 신청 {pendingCount}건 (/pending 으로 확인)");

PrintHelp();

// ── 종료 신호 처리 ──
// [왜] Ctrl+C, 창 닫기, Windows 서비스 중지(SIGTERM) 때도 "남은 대화·감사 기록 저장 → 접속 정리" 순서로 끝내야 한다.
//      처리하지 않으면 프로세스가 바로 죽어서, 아직 큐에 있던(저장 전) 대화가 사라진다.
// [개념] ManualResetEventSlim: 스레드 사이의 "신호등". Set()을 부르면 켜지고, IsSet으로 확인한다. 여기서는 "이제 종료하라"는 신호로 쓴다. 초기값 false = 꺼진 상태.
var shutdown = new ManualResetEventSlim(false);
// [읽는 법] Ctrl+C를 눌렀을 때 실행할 함수를 등록한다(+=는 이벤트에 함수 추가, (_, e) =>는 람다). e.Cancel = true로 "운영체제의 즉시 종료"를 막고 신호만 세워, 아래 메인 루프가 정리 후 끝내게 한다.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;   // 바로 죽지 말고 아래 정상 종료 경로로
    shutdown.Set();
};
// [읽는 법] 프로세스가 끝나기 직전(서비스 중지, 창 닫기 등)에 한 번 더 정리 기회를 얻는다. Ctrl+C 경로와 겹쳐도 Stop은 한 번만 실행되고 audit.Dispose도 여러 번 불러도 안전하게 만들어져 있다.
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    // 서비스 중지·SIGTERM 등. Stop은 두 번 불려도 한 번만 실행되고, 감사 로그 정리도 여러 번 불려도 안전하다.
    server.Stop();
    audit.Dispose();
};

// 콘솔 입력은 별도 스레드에서 읽는다. 입력이 없는 환경(서비스, 백그라운드 실행)에서는 ReadLine이 바로 null을 돌려주는데,
// 그렇다고 서버를 끄면 안 되므로 그때는 종료 신호(Ctrl+C 등)만 기다린다.
// [읽는 법] 입력 스레드(생산자)가 넣고 메인 스레드(소비자)가 꺼내는 스레드 안전 큐. 서버의 DbWriter와 같은 생산자-소비자 패턴이다.
var commands = new System.Collections.Concurrent.BlockingCollection<string>();
// [읽는 법] 콘솔 입력 전용 스레드. while (Console.ReadLine() is string input) 은 "한 줄을 읽어 null이 아니면(string이면) 계속"이라는 뜻이고, 입력이 끝나면(null) 반복이 끝나 스레드만 종료된다. IsBackground = true라 서버가 끝나면 같이 끝난다.
new Thread(() =>
{
    while (Console.ReadLine() is string input) commands.Add(input);
    // 입력 끝(null) = 콘솔이 없음 → 명령은 더 받지 않지만 서버는 계속 돈다
}) { IsBackground = true, Name = "Console" }.Start();

// 메인 스레드는 관리자 명령 처리만 담당한다. (Accept·수신·로그는 각자 다른 스레드)
// [읽는 법] 메인 스레드의 일: 종료 신호가 꺼져 있는 동안 반복. 명령이 있으면 처리하고 없으면(TryTake가 0.2초 뒤 false) 처음으로 돌아가 종료 신호를 다시 본다. 그래서 Ctrl+C가 최대 0.2초 안에 반영된다.
while (!shutdown.IsSet)
{
    // [개념] TryTake(out x, 200): 0.2초 동안 명령을 기다린다. 없으면 false → 다시 종료 신호를 확인한다.
    if (!commands.TryTake(out string? line, 200)) continue;
    if (line == "/quit") break;
    line = line.Trim();
    if (line.Length == 0) continue;

    // "/suspend A0001 무단 결근" → command = "/suspend", arg = "A0001", rest = "무단 결근"
    // [읽는 법] 공백으로 최대 3조각으로 나눈다: 명령, 첫 인자, 나머지 전부. 세 번째 인자를 3으로 제한해야 사유처럼 공백이 들어간 문장("무단 결근")이 한 덩어리(rest)로 남는다.
    string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    string command = parts[0].ToLowerInvariant();
    string arg = parts.Length > 1 ? parts[1] : "";
    string? rest = parts.Length > 2 ? parts[2] : null;

    // [읽는 법] 명령어별 갈래. 앞쪽은 보기 전용(/users /rooms /tls /pending /members /audit), /notice /cleanup은 서버에 작업 요청, 마지막 default는 /approve 같은 "회원 관리" 명령을 표(AdminCommands)에서 찾아 AdminService.Execute로 보낸다.
    switch (command)
    {
        case "/users":
            var users = server.Sessions.GetUsers();
            Console.WriteLine($"접속 {server.Sessions.ConnectedCount}명 / 로그인 {users.Count}명");
            foreach (var u in users)
                Console.WriteLine($"  [{u.Department}] {u} · {u.Presence}{(u.IsAdmin ? " · 관리자" : "")}{(u.StatusMessage.Length > 0 ? $" \"{u.StatusMessage}\"" : "")}");
            break;

        case "/rooms":
            foreach (var r in server.Rooms.GetRoomInfos())
                Console.WriteLine($"  #{r.RoomId} {r.Name}{(r.IsPrivate ? " [비공개]" : "")} (멤버 {r.CapacityText}, 접속 {r.OnlineCount}){(r.OwnerName is null ? "" : $" 만든 사람: {r.OwnerName}")}");
            break;

        case "/notice":
            string notice = line.Length > "/notice".Length ? line["/notice".Length..].Trim() : "";
            if (notice.Length == 0) { Console.WriteLine("  사용법: /notice 내용"); break; }
            server.Notice(notice);
            audit.Record(AuditActions.Notice, AdminService.ConsoleActor, detail: notice);
            break;

        case "/tls":
            Console.WriteLine(tls is null ? "  TLS 꺼짐 (평문)" : $"  {tls.Certificate.Subject}\n  SHA-256: {tls.Fingerprint}\n  만료: {tls.Certificate.NotAfter:yyyy-MM-dd}");
            break;

        // ── 회원 관리 ──
        case "/pending":
        case "/members":
            string? filter = command == "/pending" ? "PENDING" : arg.Length > 0 ? arg.ToUpperInvariant() : null;
            try
            {
                var list = server.Admin.ListUsers(filter);
                Console.WriteLine($"  {list.Count}명");
                foreach (var m in list)
                    Console.WriteLine($"  {m.EmployeeNo,-10} {m.Name} {m.Position} [{m.Department}] {AdminUserInfo.StatusText(m.Status)}" +
                                      $"{(m.Role == "ADMIN" ? " · 관리자" : "")}{(m.Online ? " · 접속 중" : "")}{(m.LockMinutesLeft > 0 ? $" · 잠김({m.LockMinutesLeft}분)" : "")}");
            }
            catch (DbException ex) { log.Error($"DB 오류: {ex.Message}"); }
            break;

        case "/audit":
            // /audit [개수] [사원번호]  예) /audit 50 A0001
            // [읽는 법] /audit의 인자 해석: 첫 인자가 숫자면 그 개수(없으면 30), 숫자가 아니면 사원번호로 본다("/audit 50 A0001"과 "/audit A0001" 둘 다 가능). 조회 결과는 최근 것부터 오므로 Reverse()로 뒤집어 오래된 것 → 최근 순으로 보여 준다.
            int limit = int.TryParse(arg, out int n) ? n : 30;
            string? who = int.TryParse(arg, out _) ? rest : (arg.Length > 0 ? arg : null);
            try
            {
                var logs = server.Admin.QueryAudit(new AuditQuery { Limit = limit, EmployeeNo = who });
                foreach (var a in logs.AsEnumerable().Reverse()) // 오래된 것 → 최근 것 순으로 보기 좋게
                    Console.WriteLine($"  {a.Time:MM-dd HH:mm:ss} {a.Action,-15} {a.ActorNo ?? "-",-10} → {a.TargetNo ?? "-",-10} {a.Detail} {a.ClientIp}");
            }
            catch (DbException ex) { log.Error($"DB 오류: {ex.Message}"); }
            break;

        case "/cleanup":
            // 보관 기간 정리를 지금 바로 실행 (보통은 하루 한 번 자동)
            try { server.RunCleanup(); Console.WriteLine("  정리를 마쳤습니다. (자세한 내용은 위 로그)"); }
            catch (DbException ex) { log.Error($"DB 오류: {ex.Message}"); }
            break;

        case "/help":
            PrintHelp();
            break;

        default:
            // [읽는 법] 명령어 문자열 → 동작 종류 표 조회. 관리자 화면과 같은 Execute를 부르되 행위자만 "CONSOLE"이라 규칙(마지막 관리자 보호, 감사 기록)이 화면과 똑같이 적용된다. DB 오류는 잡아 로그로 남겨 콘솔 루프가 죽지 않게 한다.
            if (AdminCommands.TryGetValue(command, out AdminActionType type))
            {
                if (arg.Length == 0) { Console.WriteLine($"  사용법: {command} 사원번호{(type is AdminActionType.Suspend or AdminActionType.Kick ? " [사유]" : "")}"); break; }
                try
                {
                    AdminOutcome result = server.Admin.Execute(new AdminAction { Type = type, EmployeeNo = arg, Reason = rest },
                                                               AdminService.ConsoleActor, actorIp: null);
                    Console.WriteLine($"  {(result.Success ? "완료" : "실패")}: {result.Message}");
                    if (result.TempPassword is not null) Console.WriteLine($"  임시 비밀번호: {result.TempPassword}");
                }
                catch (DbException ex) { log.Error($"DB 오류: {ex.Message}"); }
                break;
            }
            Console.WriteLine("알 수 없는 명령어입니다. /help 로 목록을 보세요.");
            break;
    }
}

// [읽는 법] 정상 종료의 최종 지점. Stop 안에서 새 접속 차단 → 모든 접속 종료 → 보관 정리 스레드 종료 → 남은 대화 저장 순서로 진행된다. 이 줄을 지나면 using var로 만든 로거·감사 로그가 정리된다.
server.Stop();

// ── 콘솔 명령 → 관리 동작 ──
// [개념] """ ... """ 는 원시 문자열 리터럴: 여러 줄과 따옴표를 이스케이프 없이 그대로 쓸 수 있다. 이 함수는 최상위 문 아래에 있지만 "로컬 함수"로 컴파일되어 위쪽 코드에서도 부를 수 있다.
static void PrintHelp()
{
    Console.WriteLine("""
        명령어
          /users  /rooms  /notice 내용  /tls  /quit
          /pending                 승인 대기 목록      /members [상태]   회원 목록 (PENDING/ACTIVE/SUSPENDED/RESIGNED)
          /approve 사원번호        가입 승인           /reject 사원번호  가입 거절
          /suspend 사원번호 [사유] 정지               /resign 사원번호  퇴사 처리      /activate 사원번호  정지·퇴사 해제
          /unlock 사원번호         로그인 잠금 해제     /resetpw 사원번호 임시 비밀번호 발급
          /kick 사원번호 [사유]    강제 로그아웃        /grant 사원번호   관리자 지정    /revoke 사원번호   관리자 해제
          /audit [개수] [사원번호] 감사 로그         /cleanup          보관 기간 지난 대화·파일·로그 지금 정리
        """);
}

// [개념] 최상위 문(top-level statements): 이 파일처럼 class 없이 바로 쓴 코드는 컴파일러가 Program 클래스의 Main으로 감싼다.
//        partial class Program을 쓰면 그 자동 생성 클래스에 필드를 덧붙일 수 있다(명령어 표를 매번 새로 만들지 않으려고 static 필드로).
// [개념] 최상위 문을 쓰면 컴파일러가 숨은 Program 클래스를 만든다. partial로 같은 이름의 클래스를 선언해 필드(명령어 표)를 덧붙인 것이다. static readonly라 한 번만 만들어 계속 쓴다.
static partial class Program
{
    private static readonly Dictionary<string, AdminActionType> AdminCommands = new()
    {
        ["/approve"] = AdminActionType.Approve,
        ["/reject"] = AdminActionType.Reject,
        ["/suspend"] = AdminActionType.Suspend,
        ["/resign"] = AdminActionType.Resign,
        ["/activate"] = AdminActionType.Activate,
        ["/unlock"] = AdminActionType.Unlock,
        ["/resetpw"] = AdminActionType.ResetPassword,
        ["/kick"] = AdminActionType.Kick,
        ["/grant"] = AdminActionType.GrantAdmin,
        ["/revoke"] = AdminActionType.RevokeAdmin,
    };
}
