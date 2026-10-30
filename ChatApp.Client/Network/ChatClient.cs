using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Security;
using ChatApp.Common.Utils;

namespace ChatApp.Client.Network;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ChatClient.cs - 클라이언트의 "통신 담당" (서버 연결 + 수신 스레드 + 송신)
// - 역할: 서버에 TCP로 연결하고, 전용 수신 스레드에서 패킷을 계속 읽어 PacketReceived 이벤트로 알린다.
//         보낼 때는 Send(packet)를 부르면 된다.
// - 스레드:
//   · ConnectAsync, Disconnect → UI 스레드에서 부른다.
//   · ReceiveLoop, PacketReceived/Disconnected 이벤트 → "수신 스레드"에서 실행된다(화면을 직접 바꾸면 안 됨!).
//   · Send/SendBulk → 어느 스레드에서 불러도 큐에 넣고 바로 돌아온다. 실제 쓰기는 전용 "Send" 스레드 하나만 한다.
// - 연결: LoginForm(로그인/회원가입 응답 받기) → MainForm(이후 모든 패킷 처리).
//         패킷 형식·읽기/쓰기는 Common의 Packet, NetworkHelper를 서버와 똑같이 쓴다.
// [왜] 화면(Form)과 통신을 분리했다: 통신 코드가 WinForms를 모르므로 콘솔이나 테스트에서도 그대로 쓸 수 있고,
//      창이 바뀌어도(로그인 창 → 메인 창) 연결은 하나로 유지된다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   서버와의 TCP+TLS 연결 하나를 맡고, 스레드 4개(수신·송신·하트비트 + 호출하는 UI 스레드)를 조율하는 통신 클래스. 화면은 전혀 모른다.
//
// ■ 스레드 지도 (이 파일의 핵심)
//   UI 스레드 ........ ConnectAsync / Disconnect / ReleaseHeld 호출, 이벤트를 BeginInvoke로 받아 화면 갱신
//   Receive 스레드 ... ReadPacket에서 블로킹 → Dispatch → PacketReceived 이벤트
//   Send 스레드 ...... 큐에서 꺼내 WritePacket (소켓에 쓰는 스레드는 오직 이 하나 → 쓰기용 lock 불필요)
//   Heartbeat 스레드 . 15초마다 Ping, 45초 동안 아무것도 못 받으면 Close
//   ※ 정정: Disconnected 이벤트는 "먼저 Close를 부른 스레드"에서 발생한다. 수신 스레드뿐 아니라 송신·하트비트 스레드일 수도 있다(그래서 구독자는 어느 스레드에서든 BeginInvoke로 넘겨야 한다).
//
// ■ 읽는 순서
//   ① 필드(큐 2개, _generation, _closed, _held) → ② ConnectAsync(연결→TLS→스레드 3개 시작) → ③ ReceiveLoop·Dispatch·ReleaseHeld(받기)
//   → ④ Send·SendBulk·SendLoop(보내기) → ⑤ HeartbeatLoop → ⑥ Disconnect(내가 끊기)·Close(끊기 공통 출구)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 생산자-소비자 큐 + Monitor.Wait/PulseAll ........ "producer consumer Monitor.Wait PulseAll"
//   - 배압(backpressure): 큐가 차면 생산자를 기다리게 함 .. "backpressure bounded queue"
//   - 원자적 연산(Interlocked), volatile ................ "Interlocked.Exchange volatile memory visibility"
//   - 경쟁 상태 / 데드락 ................................ "race condition deadlock lock ordering"
//   - 세대 번호(generation)로 옛 스레드 무력화 .......... "stale thread generation counter reconnect"
//   - TLS 핸드셰이크, 인증서 고정(pinning) ............. "TLS handshake certificate pinning SslStream"
//   - Nagle 알고리즘(NoDelay), TCP 메시지 경계 없음 ..... "Nagle TCP_NODELAY length-prefixed framing"
//   - 하트비트(Ping/Pong)와 반쯤 열린 연결 .............. "heartbeat half-open connection detection"
//
// ■ 면접 예상 질문과 답
//   Q1. 왜 수신용 스레드를 따로 만들었나요? async/await로 하면 안 되나요?
//       A. NetworkHelper.ReadPacket이 동기(블로킹) 방식이라 UI 스레드에서 부르면 창이 멈춥니다. 읽기 전용 스레드를 두고 이벤트로 알리는 단순한 구조를 택했습니다. 연결 같은 일회성 작업만 async로 했습니다. 연결 수가 많은 서버라면 스레드 대신 비동기 I/O가 맞지만, 클라이언트는 연결이 하나라 스레드 하나로 충분합니다.
//   Q2. Send를 부른 스레드가 직접 소켓에 쓰지 않고 큐를 거치는 이유는요?
//       A. 예전에는 lock을 잡고 직접 썼는데, 큰 파일을 올리는 동안 UI 스레드의 채팅 한 줄이 그 lock을 기다려 창이 멈췄습니다. 지금은 큐에 넣고 바로 돌아오고, 실제 쓰기는 송신 스레드 하나만 합니다. 일반 큐를 항상 먼저 비우므로 파일 업로드 중에도 채팅이 바로 나갑니다.
//   Q3. 대량 큐 상한(16)이 있는 이유는요? (배압)
//       A. 파일 조각(256KB 단위)을 디스크에서 읽는 속도가 네트워크 전송보다 빠르면 메모리에 조각이 무한정 쌓입니다. 큐가 차면 SendBulk가 기다리게 해서 "보내는 속도에 맞춰 읽게" 만듭니다. 최대 약 4MB(16 × 256KB)만 메모리에 둡니다.
//   Q4. 로그인 직후 패킷을 보관(_holding)했다가 ReleaseHeld로 푸는 이유는요?
//       A. 서버는 로그인에 성공하면 곧바로 방 입장 결과·접속자·방 목록을 보내는데, 그 순간에는 메인 창이 아직 없어 구독자가 없습니다. 그대로면 패킷이 버려집니다. 그래서 LoginRes 성공 때 보관을 켜고 메인 창이 구독을 마친 뒤 순서대로 풀어 줍니다. 켜는 시점이 "LoginRes를 핸들러에 알리기 전"이어야 한다는 점이 실제로 겪은 경쟁 상태였습니다.
//   Q5. ReleaseHeld에서는 lock 안에서 이벤트를 부르는데, 위험하지 않나요?
//       A. 위험합니다. 그래서 규칙을 둡니다: 구독자는 Invoke(동기) 대신 BeginInvoke만 쓴다. UI 스레드가 이 lock을 잡은 채 Invoke로 UI를 기다리면 서로 영원히 기다리는 데드락이 됩니다. 반대로 순서를 지키려면 lock 안에서 부를 수밖에 없었습니다(보관된 패킷이 새 패킷에 앞서야 함). 이 트레이드오프를 알고 선택했다고 답하면 좋습니다.
//   Q6. 세대 번호(_generation)는 왜 필요한가요?
//       A. 재접속하면 옛 연결의 수신·송신 스레드가 늦게 깨어날 수 있습니다. 그 스레드가 새 연결의 큐를 가져가거나 새 연결을 Close해 버리면 안 되므로, 각 스레드가 시작 때의 세대 번호를 들고 있다가 현재와 다르면 스스로 끝납니다.
//   Q7. Close가 여러 스레드에서 동시에 불려도 한 번만 실행되는 이유는요?
//       A. Interlocked.Exchange(ref _closed, 1)가 "1로 바꾸고 이전 값을 돌려주는" 원자적 연산이라, 이전 값이 이미 1이면 다른 스레드가 먼저 닫은 것이므로 바로 반환합니다. if (_closed == 1) return; _closed = 1; 두 줄로 쓰면 두 스레드가 동시에 통과할 수 있습니다.
//   Q8. 하트비트는 왜 필요한가요? TCP가 끊김을 알려 주지 않나요?
//       A. 케이블을 뽑거나 공유기가 죽는 "반쯤 열린 연결"에서는 OS가 끊김을 몇 분 동안 모를 수 있습니다. 15초마다 Ping을 보내고 45초 동안 아무것도 못 받으면 끊김으로 처리해 빨리 재접속합니다. 서버도 같은 원리로 클라이언트를 감시합니다.
//   Q9. TLS는 어떻게 안전하게 쓰나요? 사내 자체 서명 인증서는요?
//       A. 처음 접속 때 인증서 지문(SHA-256)을 사용자에게 보여 주고 신뢰하면 저장해 두며(LoginForm), 이후에는 그 지문과 같은 인증서만 받습니다(인증서 고정). 자동 재접속도 같은 지문을 쓰므로 그 사이 가짜 서버로 바꿔치기돼도 연결되지 않습니다. 첫 접속 때 지문을 눈으로 확인하는 일은 사용자에게 달려 있다는 한계가 있습니다.
//   Q10. 한계는요? (솔직하게 답하기)
//       A. (1) Disconnect가 로그아웃 패킷이 나갈 때까지 UI 스레드를 최대 1.5초 붙잡는다. (2) 재접속용으로 비밀번호를 메모리에 들고 있다(파일에는 저장하지 않지만 메모리 덤프에는 남음) — 토큰 로그인(LastTokenLogin)으로 대체해 가는 중. (3) 구독자 예외는 Debug 출력만 하고 삼켜서 운영 중에는 원인을 알기 어렵다(로그 파일이 필요). (4) Send의 true는 "큐에 넣음"일 뿐 전달 보장이 아니다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 로그인 직후 목록이 비어 있다 ............. Dispatch의 _holding 켜는 시점, MainForm이 ReleaseHeld를 불렀는지(MainForm.cs, MainForm.Network.cs)
//   - 창이 멈춘다 ............................. 구독자가 Invoke(동기)를 쓰는지, UI 스레드에서 SendBulk/Disconnect를 부르는지
//   - 끊겼다 다시 붙는 일이 잦다 ............... 하트비트 45초 규칙(Heartbeat.cs), 서버 SendQueue 한도, 클라이언트 NormalQueueLimit(10,000)
//   - "보안 연결을 맺지 못했습니다" ............ 서버 Tls.Enabled, 서버 버전, 방화벽/프록시가 TLS를 막는지, 핸드셰이크 10초 시간 초과
//   - 다른 곳에서 로그인했다는 메시지 ........... 서버 SessionManager가 보낸 ForceLogout → KickedOut=true라 자동 재접속 안 함(의도됨)
// ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// 서버 연결 + 수신 스레드 + 송신. 화면(Form)과 분리해 두어 콘솔/테스트에서도 그대로 쓸 수 있다.
///
/// 스레드 규칙
/// - PacketReceived, Disconnected 이벤트는 "수신 스레드"에서 발생한다.
///   화면을 바꾸려면 Form에서 BeginInvoke로 UI 스레드에 넘겨야 한다(직접 건드리면 크로스 스레드 예외).
/// - 이벤트 핸들러 안에서는 Invoke(동기) 대신 BeginInvoke(비동기)만 쓴다. (ReleaseHeld와의 교착 방지)
/// - Send/SendBulk는 여러 스레드에서 불러도 안전하다(보내기 큐 + 전용 송신 스레드).
/// </summary>
// [개념] IDisposable: "다 쓰면 Dispose()로 정리해야 하는 자원(소켓, 파일 등)을 가진 클래스"라는 표시.
//   using var client = new ChatClient(); 처럼 쓰면 범위를 벗어날 때 Dispose()가 자동으로 불린다.
public sealed class ChatClient : IDisposable
{
    // [개념] TcpClient / NetworkStream
    //   - TcpClient: 서버와의 TCP 연결 하나. TCP는 보낸 바이트가 순서대로, 빠짐없이 도착하는 것을 보장한다.
    //   - NetworkStream: 그 연결 위에서 바이트를 읽고 쓰는 통로(파일 읽기/쓰기와 비슷한 Stream).
    //   - TCP는 "메시지 경계"가 없는 바이트 흐름이라, 패킷 경계는 [길이 4바이트][JSON] 형식으로 직접 구분한다(NetworkHelper).
    private TcpClient? _tcp;
    // [왜] NetworkStream이 아니라 Stream: TLS를 쓰면 실제로는 SslStream(암호화 스트림)이 들어간다.
    private Stream? _stream;
    private Thread? _receiveThread;

    // ── 보내기 큐 + 전용 송신 스레드 ──
    // [왜] 예전에는 Send를 부른 스레드가 lock을 잡고 소켓에 직접 썼다. 그러면 큰 파일을 올리는 동안(조각 수천 개)
    //      UI 스레드의 채팅 한 줄이 그 lock을 기다리느라 창이 멈칫하고, 네트워크가 막히면 UI 스레드까지 멈췄다.
    //      이제 Send는 큐에 넣고 바로 돌아오며, 실제 쓰기는 "Send" 스레드 하나만 한다(쓰는 스레드가 하나라 섞일 일도 없다).
    // [개념] 두 줄 큐: 일반(채팅·명령)이 항상 먼저, 대량(파일 조각)은 일반 큐가 비었을 때만 → 파일을 올리는 중에도 채팅이 바로 나간다.
    // [개념] Monitor.Wait/PulseAll: lock을 잠깐 내려놓고 "누가 깨워 줄 때까지" 잠든다. 큐에 넣는 쪽이 PulseAll로 깨운다.
    private readonly object _queueLock = new();
    private readonly Queue<Packet> _normalQueue = new();
    private readonly Queue<Packet> _bulkQueue = new();
    /// <summary>대량 큐가 이만큼 차면 SendBulk가 기다린다(파일 조각을 메모리에 무한정 쌓지 않는다 = 배압).</summary>
    private const int BulkQueueCapacity = 16;
    /// <summary>일반 큐가 이만큼 쌓이면 네트워크가 막힌 것으로 보고 끊는다(메모리 보호).</summary>
    private const int NormalQueueLimit = 10_000;
    /// <summary>소켓 쓰기가 이 시간 넘게 막히면 끊김으로 본다.</summary>
    private const int SendTimeoutMs = 30_000;
    private Thread? _sendThread;
    // [개념] 연결 세대 번호: 재접속하면 1 늘어난다. 이전 연결의 송신 스레드는 자기 세대가 아니면 스스로 끝난다.
    private int _generation;
    private readonly ManualResetEventSlim _logoutSent = new(false);

    // [개념] Interlocked / Volatile: 여러 스레드가 함께 보는 int 값을 안전하게 읽고 바꾸는 도구.
    //   _closed를 0→1로 바꾸는 일을 Interlocked.Exchange로 하면, 두 스레드가 동시에 Close를 불러도 정확히 한 번만 닫힌다.
    private int _closed;           // 0 = 열림, 1 = 닫힘

    // [개념] volatile: 다른 스레드가 바꾼 값을 CPU 캐시 때문에 늦게 보는 일 없이 항상 최신 값을 읽게 한다.
    //   UI 스레드가 true로 바꾸고, 수신 스레드가 읽는다.
    private volatile bool _userClosing;

    // ── 하트비트 (Heartbeat.cs 참고) ──
    // [개념] Environment.TickCount64: 컴퓨터가 켜진 뒤 지난 밀리초. 시계(DateTime.Now)와 달리 사용자가 시간을 바꾸거나
    //   서머타임이 바뀌어도 뒤로 가지 않으므로 "얼마나 지났나"를 잴 때 쓴다.
    // [스레드] 수신 스레드가 쓰고 하트비트 스레드가 읽는다. long은 32비트 환경에서 한 번에 읽고 쓰지 못할 수 있으므로
    //   Interlocked.Read / Interlocked.Exchange로 다룬다(값이 반쯤 바뀐 상태를 읽는 것 방지).
    private long _lastReceivedTicks;
    private Thread? _heartbeatThread;
    private CancellationTokenSource? _heartbeatStop;

    // 로그인 직후 ~ 채팅 화면이 준비될 때까지 들어온 패킷(입장 알림, 접속자 목록)을 잠시 보관
    // [왜] 로그인에 성공하면 서버는 곧바로 "기본 방 입장 결과, 접속자 목록, 방 목록"을 보낸다.
    //   그런데 그 순간에는 아직 메인 창(MainForm)이 만들어지지 않아 PacketReceived를 구독한 쪽이 없다.
    //   그대로 두면 이 패킷들이 아무도 안 받은 채 버려져, 메인 창의 목록이 비어 보인다.
    //   그래서 메인 창이 준비될 때까지(ReleaseHeld 호출) 받아 둔다.
    private readonly object _holdLock = new();
    private readonly List<Packet> _held = [];
    private bool _holding;

    /// <summary>패킷 수신 (수신 스레드에서 발생).</summary>
    // [개념] event Action<Packet>: "패킷을 받으면 이 함수들을 불러 줘"라고 등록(+=)해 두는 알림 장치(옵저버 패턴).
    //   ChatClient는 누가 듣고 있는지 몰라도 되고, 화면 쪽은 필요할 때 구독/해제만 하면 된다.
    public event Action<Packet>? PacketReceived;

    /// <summary>서버와 연결이 끊김 (수신 스레드에서 발생). 사용자가 직접 종료한 경우는 발생하지 않음.</summary>
    public event Action<string>? Disconnected;

    // [개념] Volatile.Read: 다른 스레드가 쓴 _closed 값을 최신으로 읽는다.
    public bool IsConnected => _tcp is not null && Volatile.Read(ref _closed) == 0;

    // [개념] { get; private set; }: 바깥에서는 읽기만, 값 변경은 이 클래스 안에서만.
    public string Host { get; private set; } = "";
    public int Port { get; private set; }

    /// <summary>로그인한 내 정보. 로그인 전에는 null.</summary>
    public UserInfo? Me { get; set; }

    /// <summary>
    /// 마지막으로 성공한 로그인 정보. 자동 재접속 때 다시 로그인하는 데 쓴다.
    /// [주의] 비밀번호를 메모리에만 들고 있고 파일에는 절대 저장하지 않는다(프로그램을 끄면 사라짐).
    ///        더 안전하게 하려면 로그인 때 서버가 "재접속 토큰"을 발급하는 방식으로 바꿀 수 있다(서버 재시작에는 약함).
    /// </summary>
    public LoginRequest? LastLogin { get; set; }
    public TokenLoginRequest? LastTokenLogin { get; set; }

    /// <summary>서버가 ForceLogout을 보내 끊은 경우 true (다른 곳에서 로그인). 이때는 자동 재접속을 하면 안 된다.</summary>
    public bool KickedOut { get; private set; }

    /// <summary>true면 TLS(암호화)로 접속한다. client.json의 UseTls 값을 LoginForm이 넣어 준다.</summary>
    public bool UseTls { get; set; } = true;

    /// <summary>
    /// 이 서버에 대해 사용자가 "신뢰"한 인증서 지문(SHA-256). 있으면 이 지문의 인증서만 받는다(인증서 고정, pinning).
    /// 자동 재접속도 같은 값을 쓰므로, 재접속하는 사이 가짜 서버로 바꿔치기돼도 연결되지 않는다.
    /// </summary>
    public string? PinnedFingerprint { get; set; }

    /// <summary>현재 연결이 TLS로 암호화되어 있는지 (화면 표시용).</summary>
    public bool IsEncrypted => _stream is System.Net.Security.SslStream;

    /// <summary>
    /// 서버 접속. UI가 멈추지 않도록 비동기로 연결하고, 연결되면 수신 스레드를 시작한다.
    /// 실패하면 SocketException(거부 등) 또는 TimeoutException.
    /// </summary>
    // [개념] async / await
    //   - 서버 연결은 네트워크 상태에 따라 몇 초가 걸릴 수 있다. UI 스레드에서 그냥 기다리면 그동안 창이 "응답 없음"이 된다.
    //   - await를 만나면 메서드가 잠시 멈추고 UI 스레드는 다른 일(화면 그리기, 클릭 처리)을 계속한다.
    //     연결이 끝나면 await 다음 줄부터 이어서 실행된다(WinForms에서는 다시 UI 스레드에서 이어짐).
    //   - Task: "나중에 끝나는 작업"을 나타내는 객체. 부르는 쪽도 await로 끝날 때까지 기다릴 수 있다.
    public async Task ConnectAsync(string host, int port, int timeoutMs = 3000)
    {
        if (IsConnected) return;

        // [개념] NoDelay = true: Nagle 알고리즘 끄기.
        //   Nagle은 작은 데이터를 모아서 한 번에 보내려고 잠깐(수십 ms) 기다리는데, 채팅처럼 짧은 메시지를
        //   바로바로 보내야 하는 프로그램에서는 그 지연이 체감되므로 끈다.
        var tcp = new TcpClient { NoDelay = true };
        // [개념] SendTimeout: 상대가 받지 않아 보내기 버퍼가 꽉 차면 Write가 영원히 멈출 수 있다. 시간을 넘기면 예외로 깨운다.
        tcp.Client.SendTimeout = SendTimeoutMs;

        // [개념] CancellationTokenSource(timeoutMs): 지정한 시간이 지나면 자동으로 "취소" 신호를 보낸다.
        //   ConnectAsync에 그 토큰을 넘기면, 서버가 응답하지 않을 때 윈도우 기본값(약 20초)보다 훨씬 빨리 포기할 수 있다.
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 시간 초과로 취소됨 → 사용자가 이해하기 쉬운 TimeoutException으로 바꿔서 던진다
            tcp.Dispose();
            throw new TimeoutException($"{timeoutMs / 1000}초 안에 서버가 응답하지 않습니다.");
        }
        catch
        {
            // 연결 거부(서버 꺼짐, 방화벽) 등: 소켓을 정리하고 원래 예외를 그대로 다시 던진다(throw;)
            tcp.Dispose();
            throw;
        }

        // [개념] TLS 핸드셰이크: TCP 연결 위에서 "어떤 암호를 쓸지, 서버가 진짜인지"를 먼저 주고받는다.
        //   이게 끝나야 그 뒤의 로그인(비밀번호)이 암호화되어 나간다. 그래서 로그인보다 반드시 먼저 한다.
        Stream stream = tcp.GetStream();
        if (UseTls)
        {
            using var handshakeTimeout = new CancellationTokenSource(TlsHelper.HandshakeTimeoutMs);
            try
            {
                stream = await TlsHelper.AuthenticateAsClientAsync(stream, host, PinnedFingerprint, cancel: handshakeTimeout.Token);
            }
            catch (UntrustedCertificateException)
            {
                tcp.Dispose();
                throw; // 화면(LoginForm)이 지문을 보여 주고 신뢰 여부를 묻는다
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
            {
                // 서버가 TLS를 쓰지 않는 옛 버전이거나(평문 서버는 우리 TLS 인사말을 이상한 패킷으로 보고 끊는다), 시간 초과
                tcp.Dispose();
                throw new AuthenticationException(
                    "보안 연결(TLS)을 맺지 못했습니다. 서버가 TLS를 쓰는지(Tls.Enabled), 서버 프로그램이 최신인지 확인하세요.", ex);
            }
        }

        int generation;
        lock (_queueLock)
        {
            _normalQueue.Clear();
            _bulkQueue.Clear();
            generation = ++_generation;
        }
        _logoutSent.Reset();

        _tcp = tcp;
        _stream = stream;
        Host = host;
        Port = port;
        Volatile.Write(ref _closed, 0);
        _userClosing = false;
        KickedOut = false;

        // [주의] 이전 연결이 "로그인 직후 보관 중"(_holding) 상태에서 끊겼다면, 새 연결의 LoginRes까지 보관돼
        //        영영 전달되지 않는다. 새로 연결할 때마다 보관 상태를 초기화한다.
        lock (_holdLock)
        {
            _held.Clear();
            _holding = false;
        }

        // [개념] 수신 전용 스레드
        //   - ReadPacket은 데이터가 올 때까지 "블로킹"(그 자리에서 멈춰 기다림)한다. UI 스레드에서 하면 창이 멈춘다.
        //   - 그래서 읽기만 하는 스레드를 따로 만든다. 이 스레드는 프로그램이 끝날 때까지 서버 메시지를 기다린다.
        //   - IsBackground = true: 메인(UI) 스레드가 끝나면 이 스레드도 같이 종료된다(프로그램이 안 꺼지는 문제 방지).
        //   - Name: 디버거의 "스레드" 창에서 어떤 스레드인지 알아보기 쉽게 이름을 붙인다.
        // [왜] 스트림과 세대 번호를 넘겨 둔다: 이전 연결의 수신 스레드가 늦게 깨어나도 새 연결을 읽거나 닫지 않게.
        Stream receiveStream = stream;
        _receiveThread = new Thread(() => ReceiveLoop(receiveStream, generation)) { IsBackground = true, Name = "Receive" };
        _receiveThread.Start();

        Stream sendStream = stream;
        _sendThread = new Thread(() => SendLoop(sendStream, generation)) { IsBackground = true, Name = "Send" };
        _sendThread.Start();

        // 하트비트 스레드: 일정 간격으로 Ping을 보내고, 서버가 오래 조용하면 끊김으로 처리한다.
        // [왜] 로그인 화면에 오래 머물러 있어도 서버가 연결을 끊지 않도록 "접속 직후"부터 돌린다.
        Interlocked.Exchange(ref _lastReceivedTicks, Environment.TickCount64);
        _heartbeatStop = new CancellationTokenSource();
        _heartbeatThread = new Thread(() => HeartbeatLoop(_heartbeatStop.Token)) { IsBackground = true, Name = "Heartbeat" };
        _heartbeatThread.Start();
    }

    /// <summary>
    /// 하트비트 스레드 본체: Heartbeat.IntervalSeconds마다 깨어나서
    /// ① 서버에서 Heartbeat.TimeoutSeconds 넘게 아무것도 안 왔으면 → 끊김 처리 (Disconnected 이벤트)
    /// ② 아니면 Ping을 보낸다 (서버가 Pong으로 답해서 서버 쪽에서도 내가 살아 있음을 안다).
    /// </summary>
    // [스레드] 하트비트 스레드에서만 실행된다.
    private void HeartbeatLoop(CancellationToken stop)
    {
        // [개념] WaitHandle.WaitOne(시간): 그 시간만큼 잠들되, Close()가 _heartbeatStop.Cancel()을 부르면 바로 깨어나 true를 돌려준다.
        //   Thread.Sleep(15000)을 쓰면 연결을 닫아도 최대 15초 동안 스레드가 남아 있게 된다.
        while (!stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(Heartbeat.IntervalSeconds)))
        {
            long silentMs = Environment.TickCount64 - Interlocked.Read(ref _lastReceivedTicks);
            if (silentMs > Heartbeat.TimeoutSeconds * 1000L)
            {
                // 소켓을 닫으면 수신 스레드도 예외로 깨어나 끝난다. 끊긴 이유는 먼저 부른 Close의 것이 쓰인다.
                Close($"서버가 {Heartbeat.TimeoutSeconds}초 동안 응답하지 않아 연결을 끊었습니다. (네트워크 끊김)");
                return;
            }
            Send(Packet.Create(PacketType.Ping)); // 실패하면 Send 안에서 Close된다
        }
    }

    /// <summary>수신 스레드 본체: 연결이 끊길 때까지 패킷을 읽어 Dispatch로 넘긴다.</summary>
    // [스레드] 수신 스레드에서만 실행된다.
    private void ReceiveLoop(Stream stream, int generation)
    {
        string reason = "서버와 연결이 끊어졌습니다.";
        try
        {
            while (true)
            {
                Packet? packet = NetworkHelper.ReadPacket(stream);
                if (packet is null) break; // 서버가 정상 종료

                // 무엇이든 받았으면 서버가 살아 있다 (하트비트 스레드가 이 시각을 본다)
                Interlocked.Exchange(ref _lastReceivedTicks, Environment.TickCount64);
                if (packet.Type == PacketType.Pong) continue; // 연결 확인용 응답은 화면에 넘길 필요가 없다
                if (packet.Type == PacketType.ForceLogout)
                {
                    // 다른 곳에서 로그인함 → 이유를 남기고 끊는다 (화면은 Disconnected에서 KickedOut을 보고 재접속하지 않음)
                    KickedOut = true;
                    reason = packet.Body ?? "다른 곳에서 로그인하여 연결이 끊어졌습니다.";
                    break;
                }
                Dispatch(packet);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 네트워크 끊김 또는 내가 닫음
            // [개념] 다른 스레드(UI)가 _stream.Close()를 하면, 블로킹 중인 ReadPacket이 예외를 던지며 깨어난다.
            //   이것이 "블로킹 중인 수신 스레드를 끝내는" 일반적인 방법이다.
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            // 길이가 이상하거나 JSON이 깨진 패킷 → 더 읽어도 경계를 맞출 수 없으므로 연결을 끊는다
            reason = $"서버에서 잘못된 데이터를 받아 연결을 종료했습니다. ({ex.Message})";
        }
        finally
        {
            // [개념] finally: 정상 종료든 예외든 반드시 실행된다. 연결 정리를 빠뜨리지 않기 위해 여기에 둔다.
            if (generation == Volatile.Read(ref _generation)) Close(reason); // 이미 새 연결이면 건드리지 않는다
        }
    }

    /// <summary>받은 패킷을 구독자에게 전달한다. 로그인 직후에는 화면이 준비될 때까지 보관한다.</summary>
    // [스레드] 수신 스레드.
    private void Dispatch(Packet packet)
    {
        lock (_holdLock)
        {
            // [왜] 비밀번호 변경 응답은 보관하지 않는다: 임시 비밀번호로 로그인하면 채팅 화면이 뜨기 "전에"
            //      로그인 창 위에서 비밀번호 변경 창이 이 응답을 기다린다. 보관하면 영영 받지 못한다.
            if (_holding && packet.Type != PacketType.ChangePasswordRes)
            {
                _held.Add(packet);
                return;
            }

            // 로그인 성공 직후부터는 채팅 화면이 ReleaseHeld()를 부를 때까지 보관.
            // 반드시 핸들러에 LoginRes를 알리기 "전에" 켜야 한다. 나중에 켜면, 핸들러가 바로 채팅 화면을 열어
            // ReleaseHeld()를 먼저 부른 뒤 보관이 시작되어 이후 패킷이 영영 전달되지 않는다(실제로 발견된 경쟁 상태).
            // [주의] 경쟁 상태(race condition): 결과가 "어느 스레드가 먼저 실행되느냐"에 따라 달라지는 버그.
            //   잘못된 순서: ① LoginRes 전달 → ② (UI 스레드) 메인 창 열고 ReleaseHeld() → ③ (수신 스레드) _holding = true
            //   → 이후 패킷이 모두 _held에 쌓이기만 하고 아무도 꺼내지 않는다. 그래서 ①보다 먼저 _holding을 켠다.
            if (packet.Type == PacketType.LoginRes && packet.Success)
                _holding = true;
        }

        // [왜] 이벤트 호출은 lock 밖에서 한다. lock 안에서 남의 코드(구독자)를 부르면,
        //   그 코드가 오래 걸리거나 다른 lock을 기다릴 때 교착 상태가 생기기 쉽다.
        // [주의] 구독자에서 예외가 나면 수신 스레드가 죽고 프로그램 전체가 꺼진다. 패킷 하나만 버리고 계속 받는다.
        try { PacketReceived?.Invoke(packet); }
        // [읽는 법] "메모리 부족만 빼고 모두 잡는다"는 필터. 구독자(화면) 코드의 버그가 수신 스레드를 죽여 프로그램 전체가 꺼지는 것을 막되, 정말 치명적인 오류는 숨기지 않는다. 단점: 오류가 Debug 출력으로만 남는다.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"패킷 처리 중 오류 ({packet.Type}): {ex}");
        }
    }

    /// <summary>
    /// 보관해 둔 패킷을 순서대로 전달하고 보관을 끝낸다. 채팅 화면이 이벤트를 구독한 뒤 호출한다.
    /// lock 안에서 전달해야 그사이 새로 도착한 패킷이 앞질러 가지 않는다.
    /// </summary>
    // [스레드] UI 스레드(MainForm.OnLoad)에서 부른다.
    // [주의] 여기서는 예외적으로 lock 안에서 구독자를 부른다. 그동안 수신 스레드는 Dispatch의 lock에서 기다리므로
    //   "보관된 패킷 → 새 패킷" 순서가 지켜진다. 대신 구독자(MainForm)는 BeginInvoke만 써야 한다
    //   (Invoke로 UI 스레드를 기다리면, UI 스레드가 바로 이 lock을 잡고 있으므로 서로 영원히 기다린다 = 데드락).
    public void ReleaseHeld()
    {
        lock (_holdLock)
        {
            try
            {
                foreach (Packet packet in _held)
                {
                    try { PacketReceived?.Invoke(packet); }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        System.Diagnostics.Debug.WriteLine($"보관 패킷 처리 중 오류 ({packet.Type}): {ex}");
                    }
                }
            }
            finally
            {
                // [주의] 무슨 일이 있어도 보관을 끝낸다. 안 그러면 이후 패킷이 영영 전달되지 않는다.
                _held.Clear();
                _holding = false;
            }
        }
    }

    /// <summary>
    /// 패킷 전송(일반 큐). 큐에 넣고 바로 돌아온다. 연결이 끊겨 있으면 false.
    /// [주의] true는 "보내기로 했다"는 뜻이지 서버가 받았다는 뜻이 아니다. 받았는지는 서버의 응답·방송으로 안다.
    /// </summary>
    // [스레드] 어느 스레드에서 불러도 안전하다.
    public bool Send(Packet packet)
    {
        if (!IsConnected) return false;
        bool overflow;
        lock (_queueLock)
        {
            overflow = _normalQueue.Count >= NormalQueueLimit;
            if (!overflow)
            {
                _normalQueue.Enqueue(packet);
                Monitor.PulseAll(_queueLock);
            }
        }
        if (overflow) Close("네트워크가 너무 느려 보내지 못한 메시지가 쌓였습니다. 연결을 다시 맺습니다.");
        return !overflow;
    }

    /// <summary>
    /// 대량 전송(파일 조각). 대량 큐가 가득 차 있으면 자리가 날 때까지 기다린다(배압).
    /// 연결이 끊기거나 cancel이 오면 false. [스레드] 파일 올리기 작업 스레드에서만 부른다(UI 스레드에서 부르면 창이 멈출 수 있다).
    /// </summary>
    public bool SendBulk(Packet packet, CancellationToken cancel = default)
    {
        lock (_queueLock)
        {
            while (_bulkQueue.Count >= BulkQueueCapacity)
            {
                if (!IsConnected || cancel.IsCancellationRequested) return false;
                Monitor.Wait(_queueLock, 200); // 송신 스레드가 하나 꺼낼 때마다 깨워 준다 (200ms마다 취소도 확인)
            }
            if (!IsConnected || cancel.IsCancellationRequested) return false;
            _bulkQueue.Enqueue(packet);
            Monitor.PulseAll(_queueLock);
        }
        return true;
    }

    /// <summary>송신 스레드 본체: 큐에서 꺼내 소켓에 쓴다. 일반 큐가 항상 먼저.</summary>
    // [스레드] "Send" 스레드에서만 실행된다. 이 스레드만 _stream에 쓰므로 쓰기용 lock이 필요 없다.
    private void SendLoop(Stream stream, int generation)
    {
        try
        {
            while (true)
            {
                Packet packet;
                lock (_queueLock)
                {
                    while (_normalQueue.Count == 0 && _bulkQueue.Count == 0)
                    {
                        if (generation != _generation || Volatile.Read(ref _closed) == 1) return;
                        // [읽는 법] 큐가 비어 있으면 최대 1초 잠든다(Send가 PulseAll로 깨우면 즉시 일어남). 1초마다 한 번씩은 일부러 깨어나 "세대가 바뀌었나/닫혔나"를 확인해, 아무도 깨워 주지 않아도 스레드가 영원히 남지 않게 한다.
                        Monitor.Wait(_queueLock, 1000);
                    }
                    if (generation != _generation) return;
                    // [읽는 법] 우선순위 큐의 단순 구현: 일반 큐에 뭐든 있으면 그것을 먼저, 없을 때만 대량 큐에서 꺼낸다. 위쪽 while 조건을 통과했으므로 둘 중 하나는 반드시 비어 있지 않다.
                    packet = _normalQueue.Count > 0 ? _normalQueue.Dequeue() : _bulkQueue.Dequeue();
                    Monitor.PulseAll(_queueLock); // SendBulk에서 자리를 기다리는 스레드를 깨운다
                }
                try
                {
                    NetworkHelper.WritePacket(stream, packet);
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException)
                {
                    // 너무 크거나 직렬화할 수 없는 패킷 하나 → 그 패킷만 버리고 계속 보낸다
                    // [주의] 여기서 예외가 새면 송신 스레드가 죽고 프로그램 전체가 꺼진다.
                    System.Diagnostics.Debug.WriteLine($"보낼 수 없는 패킷을 버림 ({packet.Type}): {ex.Message}");
                    continue;
                }
                if (packet.Type == PacketType.Logout)
                {
                    _logoutSent.Set();
                    return; // 로그아웃 뒤에는 아무것도 보내지 않는다
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (generation == Volatile.Read(ref _generation))
                Close("메시지를 보내는 중 연결이 끊어졌습니다.");
        }
    }

    /// <summary>
    /// 사용자가 직접 종료(로그아웃). Disconnected 이벤트는 발생하지 않는다.
    /// 아직 안 나간 채팅은 보내고, 마지막으로 Logout을 보낸 뒤(최대 1.5초 기다림) 닫는다. 보내던 파일 조각은 버린다.
    /// </summary>
    // [왜] 내가 닫은 것까지 "연결이 끊어졌습니다" 경고 창을 띄우면 이상하므로 _userClosing으로 구분한다.
    public void Disconnect()
    {
        if (!IsConnected) return;
        _userClosing = true;
        lock (_queueLock)
        {
            _bulkQueue.Clear();
            _normalQueue.Enqueue(Packet.Create(PacketType.Logout)); // 서버가 바로 접속 종료 처리를 하도록 알림 (맨 마지막 패킷)
            Monitor.PulseAll(_queueLock);
        }
        // [주의] UI 스레드에서 최대 1.5초 기다린다. 서버가 로그아웃을 바로 알게 하려는 배려이지만, 네트워크가 막혀 있으면 창 닫기가 1.5초 느려진다(시간 초과 후에는 그냥 Close).
        _logoutSent.Wait(1500);
        Close("로그아웃");
    }

    /// <summary>연결 정리. 여러 스레드에서 동시에 불려도 딱 한 번만 실행된다.</summary>
    private void Close(string reason)
    {
        // [개념] Interlocked.Exchange(ref _closed, 1): _closed를 1로 바꾸고 "바꾸기 전 값"을 돌려준다. 이 동작은 원자적이다
        //   (중간에 다른 스레드가 끼어들 수 없음). 이전 값이 이미 1이면 다른 스레드가 먼저 닫은 것이므로 그냥 돌아간다.
        //   if (_closed == 1) return; _closed = 1; 처럼 두 줄로 쓰면 두 스레드가 동시에 검사를 통과할 수 있다.
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;

        _heartbeatStop?.Cancel(); // 하트비트 스레드를 깨워서 끝낸다
        lock (_queueLock)
        {
            _bulkQueue.Clear();
            Monitor.PulseAll(_queueLock); // 송신 스레드와 SendBulk에서 기다리는 스레드를 깨운다
        }

        // 스트림을 닫으면 수신 스레드의 ReadPacket이 예외로 깨어나 ReceiveLoop가 끝난다
        try { _stream?.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _tcp?.Close();

        if (!_userClosing)
        {
            // [주의] 구독자(화면) 코드에서 예외가 나도 이 스레드(수신/송신/하트비트)가 죽어 프로그램이 꺼지지 않게 한다.
            try { Disconnected?.Invoke(reason); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Disconnected 처리 중 오류: {ex}"); }
        }
    }

    public void Dispose() => Disconnect();
}
