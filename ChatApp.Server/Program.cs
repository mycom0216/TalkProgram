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
ServerConfig config = ServerConfig.Load(configPath);
if (args.Length > 0 && int.TryParse(args[0], out int port)) config.Port = port;

string logPath = Path.Combine(AppContext.BaseDirectory, "logs", $"server-{DateTime.Now:yyyyMMdd}.log");
using var log = new ServerLogger(logPath);

// ── DB 연결 확인 ──
var db = new Db(config.ConnectionString);
try
{
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
using var audit = new AuditLog(auditRepository, log); // 감사 로그 기록 스레드 시작
var roomRepository = new RoomRepository(db);
roomRepository.EnsureLobby(); // 기본 방 "전체 채팅"(1번)

var fileRepository = new FileRepository(db);
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

var server = new ChatServer(IPAddress.Any, config.Port, auth, roomRepository, fileRepository, new MessageRepository(db),
                            userRepository, auditRepository, audit, config, log, tls?.Context);
server.Start();
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

// 메인 스레드는 관리자 콘솔 입력만 담당한다. (Accept·수신·로그는 각자 다른 스레드)
while (true)
{
    string? line = Console.ReadLine();
    if (line is null || line == "/quit") break;
    line = line.Trim();
    if (line.Length == 0) continue;

    // "/suspend A0001 무단 결근" → command = "/suspend", arg = "A0001", rest = "무단 결근"
    string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    string command = parts[0].ToLowerInvariant();
    string arg = parts.Length > 1 ? parts[1] : "";
    string? rest = parts.Length > 2 ? parts[2] : null;

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
                Console.WriteLine($"  #{r.RoomId} {r.Name} ({r.CapacityText}){(r.OwnerName is null ? "" : $" 방장: {r.OwnerName}")}");
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

        case "/help":
            PrintHelp();
            break;

        default:
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

server.Stop();

// ── 콘솔 명령 → 관리 동작 ──
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
          /audit [개수] [사원번호] 감사 로그
        """);
}

// [개념] 최상위 문(top-level statements): 이 파일처럼 class 없이 바로 쓴 코드는 컴파일러가 Program 클래스의 Main으로 감싼다.
//        partial class Program을 쓰면 그 자동 생성 클래스에 필드를 덧붙일 수 있다(명령어 표를 매번 새로 만들지 않으려고 static 필드로).
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
