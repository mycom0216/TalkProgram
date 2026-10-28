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
    private Thread? _acceptThread;

    // [개념] volatile: 여러 스레드가 읽고 쓰는 bool 값을 CPU 캐시/컴파일러 최적화 때문에 "옛 값"으로 보지 않도록 한다.
    // [스레드] 메인 스레드(Stop)가 false로 쓰고, Accept 스레드가 while 조건에서 읽는다.
    //         lock까지 쓸 필요는 없는 단순 플래그라 volatile로 충분하다.
    private volatile bool _running;
    private readonly MessageStore _messages;

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

    // [왜] 필요한 객체(AuthService, 각종 Repository, 설정, 로거)를 생성자에서 "받아서" 쓴다(의존성 주입).
    //      ChatServer가 직접 DB 연결 문자열을 알 필요가 없고, 테스트 때 다른 객체로 바꿔 끼우기도 쉽다.
    public ChatServer(IPAddress address, int port, AuthService auth, RoomRepository roomRepository,
                      FileRepository fileRepository, MessageRepository messageRepository,
                      UserRepository userRepository, AuditRepository auditRepository, AuditLog audit,
                      ServerConfig config, ServerLogger log, SslStreamCertificateContext? tls = null)
    {
        // [개념] IPAddress.Any(0.0.0.0) = 이 PC의 모든 네트워크 카드(유선/무선/가상)로 들어오는 접속을 받는다.
        _listener = new TcpListener(address, port);
        _log = log;
        _tls = tls;
        var files = new FileService(Sessions, Rooms, fileRepository, config, log, audit);
        Admin = new AdminService(userRepository, auditRepository, auth, Sessions, audit, log);
        // [개념] files.ReadThumbnail처럼 "메서드 이름만" 넘기면 Func<string, byte[]?> 대리자(delegate)로 바뀐다.
        //        MessageStore는 FileService를 몰라도 "경로 → 미리보기 바이트" 함수만 받아서 쓸 수 있다.
        _messages = new MessageStore(messageRepository, files.ReadThumbnail, log); // DB 작업 스레드 시작
        _handler = new PacketHandler(Sessions, Rooms, auth, roomRepository, files, _messages, Admin, audit, log);
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

            // [개념] NoDelay = true: Nagle 알고리즘 끄기. Nagle은 작은 패킷을 모아서 한 번에 보내려고 잠깐 기다리는데,
            //        채팅처럼 짧은 메시지가 바로 가야 하는 프로그램에서는 지연(최대 수백 ms)이 생기므로 끈다.
            client.NoDelay = true; // 짧은 채팅 메시지를 지연 없이 보냄

            var session = new ClientSession(client, _handler, _tls); // TLS 핸드셰이크는 세션의 수신 스레드가 한다
            Sessions.Add(session);
            _log.Info($"접속: {session} (현재 {Sessions.ConnectedCount}명)");
            // [주의] 목록에 먼저 넣고(Add) 나서 Start한다. 반대로 하면, 시작하자마자 끊긴 세션이
            //        목록에서 지워진 "뒤에" 목록에 들어가 유령 접속자가 남을 수 있다.
            session.Start(); // 이 세션의 수신 스레드 + 송신 스레드 시작
        }
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
        while (!_stopping.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
        {
            foreach (ClientSession session in Sessions.GetAllSessions()) // 스냅샷이라 도는 중에 누가 나가도 안전
            {
                long silent = session.SilentMilliseconds;
                if (silent <= _heartbeatTimeoutMs || session.IsClosed) continue;

                _log.Info($"응답 없음 ({silent / 1000}초) → 연결 종료: {session}");
                session.Close(); // OnDisconnected가 불려 접속자 목록·방에서 빠지고 다른 사람에게 퇴장 알림이 간다
            }
        }
    }

    /// <summary>서버 종료 (메인 스레드에서 /quit 입력 시). 순서가 중요하다.</summary>
    public void Stop()
    {
        _running = false;          // ① Accept 루프가 다시 돌지 않게 표시
        _stopping.Cancel();        //    하트비트 감시 스레드도 깨워서 끝낸다
        _handler.StopListBroadcaster();
        _listener.Stop();         // 블로킹 중인 AcceptTcpClient를 깨운다
        Sessions.CloseAll();      // ③ 모든 접속 끊기 (각 세션 스레드가 스스로 끝난다)
        // [개념] Join(시간): 그 스레드가 끝날 때까지 최대 2초 기다린다. 무한정 기다리지 않도록 시간 제한을 둔다.
        _acceptThread?.Join(TimeSpan.FromSeconds(2));
        _messages.Dispose();      // 아직 저장 못 한 대화를 마저 저장하고 DB 작업 스레드 종료
        _log.Info("서버 종료");
    }

    /// <summary>서버 콘솔에서 보내는 공지.</summary>
    public void Notice(string message) => _handler.BroadcastSystem($"[공지] {message}");
}
