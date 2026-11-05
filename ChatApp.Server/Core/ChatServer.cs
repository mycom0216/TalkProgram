using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;
using ChatApp.Server.Services;

namespace ChatApp.Server.Core;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 서버의 "문지기". 포트를 열고 접속을 기다리다가, 새 클라이언트가 오면
//            ClientSession(접속자 1명 담당 객체)을 만들어 일을 넘긴다.
// [스레드]   - Start/Stop/Notice: 메인 스레드(Program.cs의 콘솔 입력 루프)에서 호출
//            - AcceptLoop: 전용 "Accept" 스레드 1개에서만 실행
// [연결]     Program.cs가 만든다 → 내부에서 FileService, MessageStore, PacketHandler를 만들고
//            접속마다 ClientSession을 만든다. 접속자 목록은 SessionManager, 방 목록은 RoomManager가 관리.
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   서버의 "문지기이자 관리실". 포트를 열고 새 접속을 받아 ClientSession을 만들고, 무응답 연결 정리·목록 방송·종료 순서를 관리한다.
//
// ■ 서버 시작부터 첫 메시지까지
//   Program.Main → ChatServer 생성(DB에서 방 불러오기, MessageStore의 DB 저장 스레드 시작) → Start()(포트 열기, Accept·Heartbeat 스레드 시작)
//   → AcceptLoop: 접속 기다림 → 연결 수 제한 확인 → ClientSession 생성 → 접속자 목록에 추가 → session.Start()(수신 스레드 시작)
//   → (이후는 ClientSession → PacketHandler 이야기)
//
// ■ 서버 전체 스레드 지도 (면접에서 "스레드가 몇 개인가요?"에 답하기)
//   고정: Accept(접속 받기) / Heartbeat(5초마다 무응답 연결 점검) / DbWriter(대화 DB 저장) / AuditWriter(감사 로그 저장)
//         / Retention(오래된 데이터 정리) / Logger(로그 파일 쓰기)  + 메인 스레드(콘솔 관리자 명령 입력) + 목록 방송 타이머(0.2초, 스레드 풀)
//   접속자당: 수신 Session-N 1개 + 송신 Send-N 1개  →  접속자가 N명이면 대략 "고정 6개 + 메인 + 2N개"
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - TcpListener / AcceptTcpClient(블로킹) ... "C# TcpListener AcceptTcpClient"
//   - Nagle 알고리즘과 NoDelay .............. "TCP_NODELAY Nagle 채팅"
//   - 하트비트(무응답 연결 정리) ............. "TCP half-open connection heartbeat"
//   - 의존성 주입(생성자로 받기) ............ "dependency injection constructor"
//   - 예외 필터 catch (...) when ............ "C# exception filter when"
//
// ■ 면접 예상 질문과 답
//   Q1. Accept 스레드를 따로 두고, 실제 처리는 세션 스레드에 넘기는 이유는요?
//       A. Accept가 한 사람의 처리를 하느라 오래 걸리면 그동안 다른 사람의 접속을 받을 수 없습니다. 접속 받기만 하고 바로 다음을 기다리게 해서 동시 처리를 만듭니다.
//   Q2. 서버를 어떻게 안전하게 종료하나요? 순서가 왜 중요한가요?
//       A. ① 실행 표시 끄기 → ② 리스너 멈춤(블로킹 중이던 Accept를 예외로 깨움) → ③ Accept 스레드가 끝나길 기다림(최대 2초) → ④ 모든 접속 끊기 → ⑤ 저장 못 한 대화를 마저 저장.
//          ③ 뒤에 ④를 하는 이유: 순서가 반대면 끊는 사이에 막 받아들인 접속이 목록에 남아 정리되지 않습니다. ⑤는 마지막 메시지가 유실되지 않게 하는 단계입니다.
//   Q3. 하트비트는 왜 필요하고, 왜 스레드 하나가 모두 훑나요?
//       A. 노트북 절전·와이파이 끊김은 TCP가 알려 주지 않아 서버에는 계속 "접속 중"으로 남습니다. 마지막 수신 시각이 45초(기본)를 넘으면 끊습니다.
//          세션마다 타이머를 두면 접속자 수만큼 타이머가 생기지만, 스레드 하나가 5초마다 배열을 돌면 천 명이어도 비용이 작습니다.
//   Q4. 연결 수를 왜 제한하나요?
//       A. 로그인하지 않아도 연결 하나마다 스레드 2개가 생기기 때문에, 연결만 수천 개 열어 서버를 마비시킬 수 있습니다(DoS). 전체 수(기본 2000)와 IP당 수(기본 50)를 제한합니다.
//          단, 회사 NAT 뒤에서는 여러 사람이 같은 IP로 보이므로 IP당 제한은 넉넉하게 잡고, 같은 PC(127.0.0.1)는 개발·부하 테스트용이라 제외합니다.
//   Q5. NoDelay를 켠 이유는요?
//       A. Nagle 알고리즘은 작은 패킷을 모아서 보내려고 잠깐 기다립니다. 짧은 채팅 메시지가 지연되므로 끄고 바로 보냅니다.
//   Q6. 로그인하지 않고 연결만 유지하는 클라이언트는요?
//       A. 하트비트는 "아무 패킷이나 받았는가"로 판단해서, Ping만 보내면 안 끊깁니다. 그래서 접속 후 일정 시간(LoginTimeoutSeconds, 기본 300초·최소 30초) 안에 로그인하지 않으면 따로 끊습니다.
//   Q7. 서버가 켜질 때 DB가 꺼져 있으면요?
//       A. 저장된 방을 불러오지 못하고 기본 방만 쓰며 서버는 켜집니다. 방 목록은 시작할 때 한 번 메모리(RoomManager)로 올리고, 이후 변경은 메모리와 DB에 같이 반영합니다.
//   Q8. 생성자에서 객체를 직접 만들지 않고 받는(의존성 주입) 이유는요?
//       A. ChatServer가 DB 연결 방법이나 설정 파일 위치를 몰라도 되고, 테스트 때 가짜 객체로 바꿔 끼우기 쉽습니다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 서버가 시작되자마자 꺼진다 ........ Start()의 _listener.Start(): 포트를 다른 프로그램이 쓰는 중인지(netstat -ano | findstr :9000)
//   - 직원이 접속이 안 된다 .............. 서버 로그의 "연결 거부 ... 초과" → 설정의 MaxConnections / MaxConnectionsPerIp
//   - 끊긴 사람이 목록에 계속 남는다 ..... HeartbeatLoop(HeartbeatTimeoutSeconds), 로그의 "응답 없음 ... 연결 종료"
//   - 서버를 껐다 켰더니 마지막 대화가 없다 .. Stop()의 순서, _messages.Dispose()가 실행됐는지(콘솔 /quit로 끄기)
// ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// TcpListener + Accept 스레드. 접속이 들어올 때마다 ClientSession을 만들고 수신 스레드를 시작한다.
/// </summary>
// [개념] sealed: 이 클래스를 상속하지 못하게 막는다. 상속할 이유가 없는 클래스에 붙이면 의도가 분명해지고
//        JIT가 가상 호출을 최적화하기도 쉽다.
public sealed class ChatServer
{
    // [개념] TcpListener: 지정한 IP/포트에서 TCP 접속을 "기다리는" 객체. Start()로 포트를 열고,
    //        AcceptTcpClient()를 부르면 누군가 접속할 때까지 멈춰 있다가(블로킹) 접속한 상대를 TcpClient로 돌려준다.
    private readonly TcpListener _listener;
    private readonly ServerLogger _log;
    private readonly PacketHandler _handler;
    private readonly SslStreamCertificateContext? _tls; // null = 평문
    private readonly ServerConfig _config;
    private Thread? _acceptThread;

    // [개념] volatile: 여러 스레드가 읽고 쓰는 bool 값을 CPU 캐시/컴파일러 최적화 때문에 "옛 값"으로 보지 않도록 한다.
    // [스레드] 메인 스레드(Stop)가 false로 쓰고, Accept 스레드가 while 조건에서 읽는다.
    //         lock까지 쓸 필요는 없는 단순 플래그라 volatile로 충분하다.
    private volatile bool _running;
    private int _stopped; // Stop()이 이미 불렸는지 (1이면 다시 하지 않음)
    private readonly MessageStore _messages;
    private readonly FileService _files;
    private readonly RetentionService? _retention;

    /// <summary>보관 기간 정리를 지금 한 번 실행 (서버 콘솔 /cleanup).</summary>
    public void RunCleanup() => _retention?.RunOnce();

    // 하트비트 감시 스레드 (무응답 연결 정리)
    private readonly int _heartbeatTimeoutMs;
    private Thread? _heartbeatThread;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>접속자 목록 (스레드 안전). 서버 콘솔의 /users 명령에서도 쓴다.</summary>
    public SessionManager Sessions { get; } = new();

    /// <summary>관리자 기능 (클라이언트 관리자 창과 서버 콘솔 명령이 같이 쓴다).</summary>
    public AdminService Admin { get; }

    /// <summary>채팅방 목록 (스레드 안전). 서버 콘솔의 /rooms 명령에서도 쓴다.</summary>
    public RoomManager Rooms { get; } = new();

    /// <summary>서버를 만든다. 서비스·저장소를 조립하고 DB의 채팅방을 불러오며, 접속 대기는 <see cref="Start"/>에서 시작한다.</summary>
    // [왜] 필요한 객체(AuthService, 각종 Repository, 설정, 로거)를 생성자에서 "받아서" 쓴다(의존성 주입).
    //      ChatServer가 직접 DB 연결 문자열을 알 필요가 없고, 테스트 때 다른 객체로 바꿔 끼우기도 쉽다.
    public ChatServer(IPAddress address, int port, AuthService auth, RoomRepository roomRepository,
                      FileRepository fileRepository, MessageRepository messageRepository,
                      UserRepository userRepository, AuditRepository auditRepository, AuditLog audit,
                      ServerConfig config, ServerLogger log, SslStreamCertificateContext? tls = null, Db? db = null)
    {
        // [개념] IPAddress.Any(0.0.0.0) = 이 PC의 모든 네트워크 카드(유선/무선/가상)로 들어오는 접속을 받는다.
        _listener = new TcpListener(address, port);
        _log = log;
        _tls = tls;
        _config = config;
        var files = new FileService(Sessions, Rooms, fileRepository, config, log, audit, userRepository.FindActiveUser);
        Admin = new AdminService(userRepository, auditRepository, auth, Sessions, audit, log, db, config.UploadRoot);
        // [개념] files.ReadThumbnail처럼 "메서드 이름만" 넘기면 Func<string, byte[]?> 대리자(delegate)로 바뀐다.
        //        MessageStore는 FileService를 몰라도 "경로 → 미리보기 바이트" 함수만 받아서 쓸 수 있다.
        _messages = new MessageStore(messageRepository, files.ReadThumbnail, log); // DB 작업 스레드 시작
        _handler = new PacketHandler(Sessions, Rooms, auth, roomRepository, messageRepository, userRepository,
                                     files, _messages, Admin, audit, config, log);
        _files = files;
        // DB에 저장된 단체방과 멤버를 불러온다 (서버를 다시 켜도 방이 그대로)
        try
        {
            // [읽는 법] DB(rooms, room_members)에서 "열려 있는" 방과 멤버를 읽어 RoomManager(메모리)에 올린다. 서버를 다시 켜도 방이 그대로 있게 하는 부분.
            List<Room> saved = roomRepository.LoadOpenRooms();
            Rooms.Load(saved);
            if (saved.Count > 0) log.Info($"채팅방 {saved.Count}개를 불러왔습니다.");
        }
        catch (System.Data.Common.DbException ex)
        {
            log.Error($"채팅방을 불러오지 못했습니다(기본 방만 사용): {ex.Message}");
        }
        // 보관 기간 정리 (db가 없으면 테스트 등 → 끔)
        if (db is not null) _retention = new RetentionService(db, files, config, log);
        // [읽는 법] 설정은 "초", 내부 계산은 "밀리초"라 1000을 곱한다. Math.Max(10, ...)는 설정을 너무 짧게(0 등) 잘못 적어도 최소 10초는 기다리게 하는 안전장치.
        _heartbeatTimeoutMs = Math.Max(10, config.HeartbeatTimeoutSeconds) * 1000;
    }

    /// <summary>포트를 열고 Accept 스레드를 시작한다. (메인 스레드에서 1번 호출)</summary>
    public void Start()
    {
        _listener.Start(); // [주의] 포트를 이미 다른 프로그램이 쓰고 있으면 여기서 SocketException이 난다.
        _running = true;

        // [개념] new Thread: 운영체제 스레드를 직접 하나 만든다.
        //        Accept처럼 "평생 블로킹하며 기다리는" 일은 ThreadPool(짧은 작업용 공용 스레드)에 맡기면
        //        풀의 스레드를 계속 점유하므로, 전용 스레드를 따로 만드는 것이 맞다.
        // [개념] IsBackground = true: 메인 스레드(프로그램)가 끝나면 이 스레드도 같이 강제 종료된다.
        //        false(기본값)면 이 스레드가 끝날 때까지 프로세스가 종료되지 않는다.
        // [개념] Name: 디버거·로그에서 어떤 스레드인지 알아보기 쉽게 붙이는 이름.
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "Accept" };
        _acceptThread.Start();

        _handler.StartListBroadcaster(); // 접속자·방 목록을 200ms마다 묶어서 보내는 타이머

        _heartbeatThread = new Thread(HeartbeatLoop) { IsBackground = true, Name = "Heartbeat" };
        _heartbeatThread.Start();
        _retention?.Start();

        _log.Info($"서버 시작: {_listener.LocalEndpoint} ({(_tls is null ? "평문 - 암호화 안 함" : "TLS 암호화")})");
    }

    /// <summary>
    /// [스레드] Accept 스레드 전용. 접속을 기다렸다가 → 세션 생성 → 세션의 스레드 시작 → 다시 기다리기를 반복.
    /// [왜] Accept 스레드는 "받기만" 하고 실제 대화 처리는 세션 스레드에게 넘긴다.
    ///      그래야 한 사람의 처리가 오래 걸려도 다른 사람의 접속을 계속 받을 수 있다(1:N 동시 처리의 핵심).
    /// </summary>
    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient(); // 접속이 올 때까지 블로킹
            }
            // [개념] catch (...) when (조건): 조건이 참인 예외만 잡는다(예외 필터). 나머지 예외는 위로 그대로 전달된다.
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // [왜] Stop()에서 _listener.Stop()을 부르면 블로킹 중이던 AcceptTcpClient가 예외를 던지며 깨어난다.
                //      이것이 Accept 스레드를 끝내는 정상 경로다.
                if (!_running) break; // Stop()에서 listener를 멈춘 경우
                _log.Error($"Accept 실패: {ex.Message}");
                continue;
            }

            // ── 연결 수 제한 (DoS 방어) ──
            // [왜] 로그인하지 않아도 연결 하나마다 스레드 2개가 생긴다. 제한이 없으면 연결만 수천 개 열어 서버를 마비시킬 수 있다.
            if (RejectByLimit(client)) continue;

            // [개념] NoDelay = true: Nagle 알고리즘 끄기. Nagle은 작은 패킷을 모아서 한 번에 보내려고 잠깐 기다리는데,
            //        채팅처럼 짧은 메시지가 바로 가야 하는 프로그램에서는 지연(최대 수백 ms)이 생기므로 끈다.
            client.NoDelay = true; // 짧은 채팅 메시지를 지연 없이 보냄

            var session = new ClientSession(client, _handler, _tls, // TLS 핸드셰이크는 세션의 수신 스레드가 한다
                                            // [읽는 법] 설정(MB)을 바이트로 바꾼다(1024 × 1024). 1024L처럼 L을 붙인 이유: int로 계산하면 2GB 근처에서 넘쳐 음수가 될 수 있어 long으로 계산한다.
                                            //           Math.Max(1, ...)는 0·음수 같은 잘못된 설정을 막는다.
                                            Math.Max(1, _config.MaxSendQueueMB) * 1024L * 1024,
                                            Math.Max(5, _config.SendTimeoutSeconds) * 1000);
            Sessions.Add(session);
            _log.Info($"접속: {session} (현재 {Sessions.ConnectedCount}명)");
            // [주의] 목록에 먼저 넣고(Add) 나서 Start한다. 반대로 하면, 시작하자마자 끊긴 세션이
            //        목록에서 지워진 "뒤에" 목록에 들어가 유령 접속자가 남을 수 있다.
            session.Start(); // 이 세션의 수신 스레드 + 송신 스레드 시작
        }
    }

    /// <summary>최대 연결 수·IP당 연결 수를 넘으면 바로 끊는다. 끊었으면 true.</summary>
    // [스레드] Accept 스레드에서만 부른다. 세는 동안 다른 세션이 끊길 수 있지만, 조금 어긋나도 문제없는 "대략적인" 제한이다.
    private bool RejectByLimit(TcpClient client)
    {
        string? reason = null;
        if (Sessions.ConnectedCount >= _config.MaxConnections)
            reason = $"최대 연결 수({_config.MaxConnections}) 초과";
        else if (client.Client.RemoteEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address)
                 // [읽는 법] 같은 IP에서 온 연결이 몇 개인지 세려고 전체 세션을 한 번 훑는다(접속자 수만큼 걸리는 O(N) 작업).
                 //           수백 명 규모에서는 충분히 빠르지만, 접속이 수천 명 규모로 몰리면 IP별 개수를 따로 들고 있는 방식으로 바꿀 수 있다.
                 && Sessions.GetAllSessions().Count(s => ep.Address.Equals(s.RemoteAddress)) >= _config.MaxConnectionsPerIp)
            // [왜] 같은 PC(127.0.0.1)는 부하 테스트·개발용이라 제외한다. 회사 NAT 뒤라 여러 사람이 같은 IP로 보일 수 있으니 값은 넉넉히.
            reason = $"IP당 연결 수({_config.MaxConnectionsPerIp}) 초과";

        if (reason is null) return false;
        _log.Error($"연결 거부: {client.Client.RemoteEndPoint} - {reason}");
        try { client.Close(); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        return true;
    }

    /// <summary>
    /// [스레드] 하트비트 감시 스레드 전용. 5초마다 모든 연결을 훑어서, 정해진 시간(기본 45초) 동안
    /// 아무 패킷도 보내지 않은 연결을 끊는다.
    /// [왜] 노트북 절전·와이파이 끊김처럼 "조용히 사라진" 클라이언트는 TCP가 알려 주지 않는다(Heartbeat.cs 참고).
    ///      끊지 않으면 접속자 목록에 계속 "온라인"으로 남고, 그 사람의 송신 큐·스레드도 계속 자원을 차지한다.
    /// [왜] 세션마다 타이머를 두지 않고 스레드 하나가 모두 훑는다: 접속자가 1000명이어도 스레드는 1개면 되고,
    ///      5초에 한 번 배열을 도는 비용은 아주 작다.
    /// </summary>
    private void HeartbeatLoop()
    {
        // [개념] WaitHandle.WaitOne(5초): 5초 동안 잠들되, Stop()에서 Cancel()하면 즉시 깨어나 true → 루프 종료.
        int round = 0;
        while (!_stopping.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
        {
            // 1분에 한 번: 멈춘 업로드 정리, 오래된 속도 제한 기록 정리
            // [읽는 법] 이 루프는 5초마다 한 바퀴. round를 세어서 12번에 한 번(= 5초 × 12 = 1분)만 무거운 정리 작업을 한다.
            if (++round % 12 == 0)
            {
                try
                {
                    _files.AbortStalled(TimeSpan.FromMinutes(2));
                    _handler.Maintenance();
                }
                catch (Exception ex) { _log.Error($"주기 정리 중 오류: {ex.Message}"); }
            }

            long loginTimeoutMs = Math.Max(30, _config.LoginTimeoutSeconds) * 1000L;
            foreach (ClientSession session in Sessions.GetAllSessions()) // 스냅샷이라 도는 중에 누가 나가도 안전
            {
                // [주의] 이 스레드에서 예외가 새어 나가면 서버 전체가 죽는다. 세션 하나의 문제는 그 세션에서 끝낸다.
                try
                {
                    if (session.IsClosed) continue;
                    long silent = session.SilentMilliseconds;
                    if (silent > _heartbeatTimeoutMs)
                    {
                        _log.Info($"응답 없음 ({silent / 1000}초) → 연결 종료: {session}");
                        session.Close(); // OnDisconnected가 불려 접속자 목록·방에서 빠지고 다른 사람에게 퇴장 알림이 간다
                    }
                    // [왜] Ping만 보내며 로그인하지 않는 연결은 하트비트로는 안 끊긴다. 로그인 제한 시간을 따로 둔다.
                    else if (!session.IsLoggedIn && Environment.TickCount64 - session.ConnectedTicks > loginTimeoutMs)
                    {
                        _log.Info($"로그인 제한 시간({loginTimeoutMs / 1000}초) 초과 → 연결 종료: {session}");
                        session.Close();
                    }
                }
                catch (Exception ex)
                {
                    _log.Error($"하트비트 점검 중 오류 ({session}): {ex.Message}");
                }
            }
        }
    }

    /// <summary>서버 종료 (메인 스레드에서 /quit 입력 시). 순서가 중요하다.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return; // Ctrl+C와 /quit이 겹쳐도 한 번만
        _running = false;          // ① Accept 루프가 다시 돌지 않게 표시
        _stopping.Cancel();        //    하트비트 감시 스레드도 깨워서 끝낸다
        _handler.StopListBroadcaster();
        _listener.Stop();         // ② 블로킹 중인 AcceptTcpClient를 깨운다
        // [개념] Join(시간): 그 스레드가 끝날 때까지 최대 2초 기다린다. 무한정 기다리지 않도록 시간 제한을 둔다.
        // [주의] Accept 스레드가 "완전히 끝난 뒤에" 모든 접속을 끊는다. 순서가 반대면, 끊는 사이 막 받아들인 접속이
        //        목록에 새로 들어와 정리되지 않고 남을 수 있다.
        _acceptThread?.Join(TimeSpan.FromSeconds(2));
        Sessions.CloseAll();      // ③ 모든 접속 끊기 (각 세션 스레드가 스스로 끝난다)
        _retention?.Dispose();
        _messages.Dispose();      // 아직 저장 못 한 대화를 마저 저장하고 DB 작업 스레드 종료
        _log.Info("서버 종료");
    }

    /// <summary>서버 콘솔에서 보내는 공지.</summary>
    public void Notice(string message) => _handler.BroadcastSystem($"[공지] {message}");
}
