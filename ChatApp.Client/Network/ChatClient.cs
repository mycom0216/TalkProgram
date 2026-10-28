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
//   · Send → UI 스레드(채팅 전송)와 파일 올리기 작업 스레드가 동시에 부를 수 있다 → _sendLock으로 보호.
// - 연결: LoginForm(로그인/회원가입 응답 받기) → MainForm(이후 모든 패킷 처리).
//         패킷 형식·읽기/쓰기는 Common의 Packet, NetworkHelper를 서버와 똑같이 쓴다.
// [왜] 화면(Form)과 통신을 분리했다: 통신 코드가 WinForms를 모르므로 콘솔이나 테스트에서도 그대로 쓸 수 있고,
//      창이 바뀌어도(로그인 창 → 메인 창) 연결은 하나로 유지된다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 서버 연결 + 수신 스레드 + 송신. 화면(Form)과 분리해 두어 콘솔/테스트에서도 그대로 쓸 수 있다.
///
/// 스레드 규칙
/// - PacketReceived, Disconnected 이벤트는 "수신 스레드"에서 발생한다.
///   화면을 바꾸려면 Form에서 BeginInvoke로 UI 스레드에 넘겨야 한다(직접 건드리면 크로스 스레드 예외).
/// - 이벤트 핸들러 안에서는 Invoke(동기) 대신 BeginInvoke(비동기)만 쓴다. (ReleaseHeld와의 교착 방지)
/// - Send는 여러 스레드에서 불러도 안전하다(_sendLock).
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

    // [개념] lock 객체: lock(_sendLock) { ... } 블록에는 한 번에 한 스레드만 들어갈 수 있다(상호 배제).
    //   [주의] 두 스레드가 동시에 같은 소켓에 쓰면 패킷 A의 앞부분과 패킷 B가 섞여 서버가 해석하지 못한다.
    //   예) UI 스레드가 채팅을 보내는 도중 파일 올리기 스레드가 조각을 보내는 경우 → lock으로 한 번에 하나씩만 쓰게 한다.
    private readonly object _sendLock = new();

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
        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "Receive" };
        _receiveThread.Start();

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
    private void ReceiveLoop()
    {
        string reason = "서버와 연결이 끊어졌습니다.";
        try
        {
            while (true)
            {
                Packet? packet = NetworkHelper.ReadPacket(_stream!);
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
            Close(reason);
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
        PacketReceived?.Invoke(packet);
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
            foreach (Packet packet in _held)
                PacketReceived?.Invoke(packet);
            _held.Clear();
            _holding = false;
        }
    }

    /// <summary>패킷 전송. 연결이 끊겨 있으면 false.</summary>
    // [스레드] 여러 스레드(UI, 파일 올리기 작업 스레드)가 동시에 불러도 안전하다.
    public bool Send(Packet packet)
    {
        if (!IsConnected) return false;
        try
        {
            lock (_sendLock)
            {
                NetworkHelper.WritePacket(_stream!, packet);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // [왜] Close는 lock 밖에서 부른다. Close가 Disconnected 이벤트를 발생시키는데,
            //   그 구독자가 다시 Send를 부르면 lock 안에서 같은 lock을 기다리는 꼴이 될 수 있어서다.
            Close("메시지를 보내는 중 연결이 끊어졌습니다.");
            return false;
        }
    }

    /// <summary>사용자가 직접 종료(로그아웃). Disconnected 이벤트는 발생하지 않는다.</summary>
    // [왜] 내가 닫은 것까지 "연결이 끊어졌습니다" 경고 창을 띄우면 이상하므로 _userClosing으로 구분한다.
    public void Disconnect()
    {
        if (!IsConnected) return;
        _userClosing = true;
        Send(Packet.Create(PacketType.Logout)); // 서버가 바로 접속 종료 처리를 하도록 알림
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

        // 스트림을 닫으면 수신 스레드의 ReadPacket이 예외로 깨어나 ReceiveLoop가 끝난다
        _stream?.Close();
        _tcp?.Close();

        if (!_userClosing)
            Disconnected?.Invoke(reason);
    }

    public void Dispose() => Disconnect();
}
