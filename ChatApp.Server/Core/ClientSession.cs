using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Text.Json;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Security;
using ChatApp.Common.Utils;
using ChatApp.Server.Services;

namespace ChatApp.Server.Core;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 접속한 클라이언트 1명을 담당. 이 프로젝트 "멀티스레드"의 핵심 클래스.
//            소켓(TcpClient)과, 그 소켓을 위한 수신 스레드·송신 스레드·송신 큐를 가진다.
// [스레드]   - ReceiveLoop: 이 세션 전용 수신 스레드 ("Session-N")
//            - SendLoop   : 이 세션 전용 송신 스레드 ("Send-N")
//            - Send/SendBulk: 아무 스레드(다른 사람의 수신 스레드, 내려받기 스레드, 서버 콘솔)에서나 호출됨
//            - Close: 수신/송신 스레드 또는 다른 스레드에서 "동시에" 불릴 수 있음 → Interlocked로 1번만 실행
// [연결]     ChatServer(Accept 스레드)가 만든다 → 받은 패킷은 PacketHandler.Handle로 넘긴다
//            → 끊기면 PacketHandler.OnDisconnected로 알린다.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 접속한 클라이언트 1명. 클라이언트 1명당 스레드 2개를 가진다.
/// - 수신 스레드: ReadPacket으로 블로킹 대기 → PacketHandler에 넘김
/// - 송신 스레드: 송신 큐에서 꺼내 소켓에 쓴다.
///   다른 사람의 수신 스레드(방 메시지 전달 등)는 큐에 넣기만 하고 바로 돌아가므로,
///   느린 클라이언트 한 명 때문에 다른 사람의 처리가 멈추지 않는다(파일 전송이 생기면서 필요해짐).
///
/// 송신 큐는 두 개다.
/// - _queue: 채팅·알림 등 일반 패킷 (우선)
/// - _bulk : 파일 조각. 용량 제한이 있어 꽉 차면 넣는 쪽(내려받기 스레드)이 기다린다(배압, backpressure).
/// 송신 스레드는 일반 큐를 먼저 보므로 큰 파일을 받는 중에도 채팅이 바로 전달된다.
/// </summary>
// [왜] 예전에는 Send()에서 lock을 잡고 소켓에 "직접" 썼다. 그러면 A가 B에게 메시지를 보낼 때,
//      B의 네트워크가 느리면 A의 수신 스레드가 B 소켓 쓰기에서 멈춰 버린다(남 때문에 내가 멈춤).
//      지금은 "큐에 넣기"만 하므로 A는 즉시 돌아가고, 실제 쓰기는 B 전용 송신 스레드가 한다.
public sealed class ClientSession
{
    // [개념] const: 컴파일할 때 값이 정해지는 상수. 바꿀 일이 없는 설정값에 쓴다.
    private const int BulkQueueCapacity = 8;     // 파일 조각 8개(약 2MB)까지만 메모리에 쌓는다
    private const int MaxQueuedPackets = 20000;   // 일반 큐가 이만큼 밀리면 응답 없는 클라이언트로 보고 끊는다

    // [개념] static 필드: 객체마다가 아니라 클래스 전체에 1개. 모든 세션이 함께 쓰는 "번호 발급기".
    private static int _nextId;

    private readonly TcpClient _client;

    // [개념] NetworkStream: 소켓을 "파일처럼" Read/Write 할 수 있게 해 주는 스트림.
    //        TCP는 메시지 경계가 없는 바이트 흐름(stream)이라, 패킷 경계는 NetworkHelper의 [길이 4바이트] 헤더로 구분한다.
    // [왜] 타입이 NetworkStream이 아니라 Stream인 이유: TLS를 켜면 핸드셰이크 뒤에 SslStream(암호화 스트림)으로 바뀐다.
    //      둘 다 Stream의 자식이라 ReadPacket/WritePacket은 어느 쪽이든 그대로 쓸 수 있다.
    // [개념] volatile: 수신 스레드가 바꾼 값을 Close()를 부르는 다른 스레드가 곧바로 보게 한다.
    private volatile Stream _stream;

    // TLS를 쓰면 서버 인증서 컨텍스트, 평문이면 null
    private readonly SslStreamCertificateContext? _tls;
    private readonly PacketHandler _handler;
    private readonly Thread _receiveThread;
    private readonly Thread _sendThread;

    // [개념] BlockingCollection<T>: 스레드 안전한 큐 + "비어 있으면 꺼내는 쪽이 기다림"/"꽉 차면 넣는 쪽이 기다림" 기능.
    //        생산자-소비자 패턴: 여러 생산자(Send를 부르는 스레드들)가 넣고, 소비자 1명(송신 스레드)이 꺼낸다.
    //        new() = 용량 제한 없음, new(8) = 최대 8개까지(그 이상 넣으려면 기다려야 함).
    private readonly BlockingCollection<Packet> _queue = new();
    private readonly BlockingCollection<Packet> _bulk = new(BulkQueueCapacity);

    // [개념] CancellationTokenSource: "이제 그만!" 신호를 보내는 장치. Cancel()을 부르면
    //        이 토큰으로 기다리던 작업(TakeFromAny)이 OperationCanceledException을 던지며 깨어난다.
    private readonly CancellationTokenSource _closing = new();
    private int _closed; // 0 = 열림, 1 = 닫힘 (Interlocked로 한 번만 닫히게 보장)

    /// <summary>세션 번호 (로그 구분용, 1부터 증가).</summary>
    public int Id { get; }

    /// <summary>상대 주소 "IP:포트" (로그용).</summary>
    public string RemoteEndPoint { get; }

    /// <summary>로그인한 사용자 정보. 로그인 전에는 null.</summary>
    // [개념] internal set: 읽기는 누구나, 쓰기는 같은 프로젝트 안(SessionManager.TryLogin)에서만.
    public UserInfo? User { get; internal set; }
    public bool IsLoggedIn => User is not null;

    /// <summary>
    /// 임시 비밀번호로 로그인해서 아직 비밀번호를 바꾸지 않은 상태. 이 동안은 비밀번호 변경·핑·로그아웃만 할 수 있다.
    /// [스레드] 이 세션의 수신 스레드가 쓰고, 다른 스레드(목록 방송)도 읽으므로 volatile.
    /// </summary>
    public bool MustChangePassword { get => _mustChangePassword; internal set => _mustChangePassword = value; }
    private volatile bool _mustChangePassword;

    // [개념] Volatile.Read: 다른 스레드가 Interlocked로 바꾼 최신 값을 확실히 읽어 온다(캐시된 옛 값 방지).
    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    /// <summary>참여 중인 방들(여러 개 가능). RoomManager의 lock 안에서만 읽고 쓴다.</summary>
    internal HashSet<Room> JoinedRooms { get; } = [];

    /// <summary>진행 중인 내려받기 수 (Interlocked로 증감).</summary>
    // [왜] 속성이 아니라 "필드"인 이유: Interlocked.Increment(ref x)는 필드의 메모리 위치(ref)가 필요하다.
    //      속성은 get/set 메서드라 ref로 넘길 수 없다.
    internal int ActiveDownloads;

    // 마지막으로 이 클라이언트에게서 패킷을 받은 시각 (Environment.TickCount64, 밀리초).
    // [스레드] 수신 스레드가 쓰고, 서버의 하트비트 감시 스레드(ChatServer)가 읽는다 → Interlocked로 읽고 쓴다.
    private long _lastReceivedTicks = Environment.TickCount64;

    /// <summary>마지막으로 패킷을 받은 뒤 지난 시간(밀리초). 하트비트 감시 스레드가 무응답 연결을 찾을 때 쓴다.</summary>
    public long SilentMilliseconds => Environment.TickCount64 - Interlocked.Read(ref _lastReceivedTicks);

    // [스레드] Accept 스레드에서 실행된다. 여기서는 스레드 객체만 만들고, 실제 시작은 Start()에서.
    public ClientSession(TcpClient client, PacketHandler handler, SslStreamCertificateContext? tls = null)
    {
        // [개념] Interlocked.Increment: "읽고 +1 하고 쓰기"를 쪼갤 수 없는 한 동작(원자적)으로 한다.
        //        _nextId++ 는 읽기/더하기/쓰기 3단계라, 두 스레드가 동시에 하면 같은 번호가 나올 수 있다.
        //        (지금은 Accept 스레드 하나만 부르지만, 공유 static 값은 습관적으로 안전하게 다룬다)
        Id = Interlocked.Increment(ref _nextId);
        _client = client;
        _stream = client.GetStream();
        _tls = tls;
        _handler = handler;
        // [개념] ?. 와 ?? : RemoteEndPoint가 null이면 ToString()을 부르지 않고 "unknown"을 쓴다.
        RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"Session-{Id}" };
        _sendThread = new Thread(SendLoop) { IsBackground = true, Name = $"Send-{Id}" };
    }

    /// <summary>수신 스레드를 시작한다. 송신 스레드는 수신 스레드가 TLS 핸드셰이크를 마친 뒤에 시작한다.</summary>
    // [왜] 송신 스레드를 핸드셰이크 "뒤에" 시작: 암호화가 준비되기 전에 송신 스레드가 평문 스트림에 패킷을 써 버리면
    //      상대는 그것을 TLS 데이터로 읽으려다 실패한다. 그 사이 Send()로 들어온 패킷은 큐에 쌓여 있다가 나간다.
    public void Start() => _receiveThread.Start();

    /// <summary>
    /// [스레드] 수신 스레드 전용. 패킷 1개 읽기(블로킹) → 처리 → 반복.
    /// 이 스레드가 끝나는 경우: 상대가 정상 종료, 네트워크 끊김, 서버가 Close, 잘못된 데이터.
    /// 어떤 경우든 finally에서 Close()로 정리한다.
    /// </summary>
    private void ReceiveLoop()
    {
        try
        {
            // ① TLS 핸드셰이크 (켜져 있을 때). 실패하면 바로 연결을 끊는다(finally의 Close).
            if (_tls is not null && !AuthenticateTls()) return;

            // ② 이제부터 보내도 된다
            _sendThread.Start();

            bool firstPacket = _tls is null; // 평문 서버면 첫 패킷이 TLS 인사말인지 확인한다

            while (true)
            {
                // [개념] 블로킹 I/O: 데이터가 올 때까지 이 줄에서 스레드가 멈춰 기다린다(CPU는 쓰지 않음).
                //        그래서 접속자마다 스레드를 하나씩 두는 구조(thread-per-client)가 된다.
                Packet? packet = NetworkHelper.ReadPacket(_stream, firstPacket);
                firstPacket = false;
                if (packet is null) break; // 클라이언트가 정상 종료
                // 어떤 패킷이든 받았으면 살아 있는 것 (Ping뿐 아니라 채팅·파일 조각도)
                Interlocked.Exchange(ref _lastReceivedTicks, Environment.TickCount64);

                // [스레드] 처리도 이 수신 스레드에서 한다. 그래서 PacketHandler 안의 코드는
                //         여러 사람의 수신 스레드에서 "동시에" 실행될 수 있다 → 공유 데이터는 스레드 안전해야 함.
                _handler.Handle(this, packet);
            }
        }
        catch (IOException) { /* 강제 종료, 네트워크 끊김 */ }
        catch (ObjectDisposedException) { /* 서버가 먼저 닫음 */ }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            // [주의] 길이 헤더가 이상하거나 JSON이 깨졌다 = 정상 클라이언트가 아니거나 데이터가 손상됨.
            //        이후 바이트의 패킷 경계를 믿을 수 없으므로 연결을 끊는 것이 안전하다.
            _handler.Log.Error($"[{Id}] 잘못된 패킷 수신, 연결 종료: {ex.Message}");
        }
        catch (Exception ex)
        {
            // 한 클라이언트의 오류가 서버 전체를 죽이지 않도록 여기서 막는다(NF-05).
            // [개념] 스레드 안에서 잡히지 않은 예외는 .NET에서 프로세스 전체를 종료시킨다. 그래서 마지막 방어선이 필요하다.
            _handler.Log.Error($"[{Id}] 처리 중 예외: {ex}");
        }
        finally
        {
            // [개념] finally: 정상 종료든 예외든 "반드시" 실행되는 블록. 정리 코드를 두기 좋은 곳.
            Close();
        }
    }

    /// <summary>
    /// [스레드] 수신 스레드에서 실행. 평문 스트림을 SslStream으로 감싸고 TLS 핸드셰이크를 한다.
    /// 성공하면 _stream을 암호화 스트림으로 바꾸고 true.
    /// </summary>
    // [왜] 핸드셰이크를 Accept 스레드가 아니라 "세션의 수신 스레드"에서 한다:
    //      핸드셰이크는 몇 번의 왕복 + RSA 연산이 필요하다. Accept 스레드에서 하면, 접속만 하고 아무것도 보내지 않는
    //      클라이언트 하나 때문에 최대 10초 동안 다른 사람이 아무도 접속하지 못한다(슬로로리스 공격과 비슷).
    private bool AuthenticateTls()
    {
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        // [개념] CreateLinkedTokenSource: 두 가지 중 하나라도 일어나면 취소 → "시간 초과" 또는 "서버가 Close()함"
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        cancel.CancelAfter(TlsHelper.HandshakeTimeoutMs);
        try
        {
            var options = new SslServerAuthenticationOptions
            {
                ServerCertificateContext = _tls,
                ClientCertificateRequired = false,               // 사용자 확인은 인증서가 아니라 사원번호+비밀번호로 한다
                EnabledSslProtocols = SslProtocols.None,          // 운영체제가 고르는 안전한 버전(TLS 1.2/1.3)
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            };
            // [개념] .GetAwaiter().GetResult(): 비동기 메서드를 "끝날 때까지 기다리며" 동기로 부른다.
            //        보통 UI 스레드에서는 교착 위험이 있어 피하지만, 여기는 이 세션 전용 스레드라 기다려도 문제없다.
            //        비동기 버전을 쓴 이유는 CancellationToken으로 시간 제한을 걸 수 있어서다.
            ssl.AuthenticateAsServerAsync(options, cancel.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException
                                      or ObjectDisposedException or InvalidOperationException)
        {
            ssl.Dispose();
            // 평문 클라이언트(옛 버전)가 붙거나, 시간 안에 협상을 못 끝낸 경우. 운영 중에 흔할 수 있어 Info로 남긴다.
            string why = ex is OperationCanceledException ? $"{TlsHelper.HandshakeTimeoutMs / 1000}초 안에 협상을 끝내지 않음"
                                                          : ex.GetBaseException().Message;
            if (!IsClosed) _handler.Log.Info($"[{Id}] TLS 협상 실패, 연결 종료 ({RemoteEndPoint}): {why}");
            return false;
        }

        _stream = ssl;
        _handler.Log.Info($"[{Id}] TLS 연결: {ssl.SslProtocol}, {ssl.NegotiatedCipherSuite}");
        return true;
    }

    /// <summary>송신 스레드: 큐에서 꺼내 소켓에 쓴다. 소켓에 쓰는 스레드는 이것 하나뿐이라 lock이 필요 없다.</summary>
    // [주의] 소켓 하나에 두 스레드가 동시에 Write하면 두 패킷의 바이트가 섞여 상대가 해석할 수 없게 된다.
    //        "쓰는 스레드를 하나로 정한다"는 것이 lock 없이 이 문제를 없애는 방법이다.
    private void SendLoop()
    {
        BlockingCollection<Packet>[] queues = [_queue, _bulk]; // 앞쪽(일반 큐)을 먼저 확인한다
        try
        {
            while (true)
            {
                // [개념] TakeFromAny: 여러 큐 중 "꺼낼 것이 있는 큐"에서 하나를 꺼낸다. 모두 비었으면 기다린다.
                //        배열 앞쪽 큐부터 확인하므로 일반 큐(채팅)가 파일 큐보다 우선 처리된다(간단한 우선순위 큐).
                //        _closing.Token이 취소되면 기다리던 중이라도 OperationCanceledException으로 빠져나온다.
                BlockingCollection<Packet>.TakeFromAny(queues, out Packet? packet, _closing.Token);
                // [개념] packet! : "여기서 null이 아님을 내가 보장한다"고 컴파일러에게 알리는 표시(null-forgiving).
                NetworkHelper.WritePacket(_stream, packet!);
                // SendAndClose로 넣은 "마지막 패킷"을 보냈으면 연결을 닫는다
                if (ReferenceEquals(packet, Volatile.Read(ref _lastPacket))) break;
            }
        }
        catch (OperationCanceledException) { /* Close() */ }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        catch (Exception ex)
        {
            _handler.Log.Error($"[{Id}] 송신 중 예외: {ex}");
        }
        finally
        {
            Close();
        }
    }

    // SendAndClose로 지정한 마지막 패킷 (송신 스레드가 이것을 보낸 뒤 연결을 닫는다)
    private Packet? _lastPacket;

    /// <summary>
    /// 이 패킷을 "마지막으로" 보내고 연결을 닫는다 (예: ForceLogout).
    /// [왜] Send() 후 바로 Close()를 부르면 큐에 든 패킷이 나가기 전에 연결이 닫혀 상대가 이유를 모른다.
    ///      그래서 송신 스레드가 이 패킷을 실제로 소켓에 쓴 다음에 닫게 한다(그 앞에 쌓인 패킷도 순서대로 먼저 나간다).
    /// [주의] 상대가 이미 응답이 없다면 보내기가 끝나지 않을 수 있으므로, 5초 뒤에는 무조건 닫는다.
    /// </summary>
    public void SendAndClose(Packet packet)
    {
        Volatile.Write(ref _lastPacket, packet);
        Send(packet);
        // [개념] Task.Delay(...).ContinueWith(...): 스레드를 잡아 두지 않고 5초 뒤에 실행할 일을 예약한다(타이머).
        Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Close());
    }

    /// <summary>일반 패킷 전송. 큐에 넣고 바로 돌아온다(어느 스레드에서 불러도 안전).</summary>
    // [스레드] 방 채팅이면 보낸 사람의 수신 스레드가, 공지면 메인 스레드가, DB 기록은 DbWriter 스레드가 부른다.
    public void Send(Packet packet)
    {
        if (IsClosed) return;
        try
        {
            _queue.Add(packet); // 용량 제한이 없는 큐라 기다리지 않고 바로 들어간다
        }
        catch (InvalidOperationException) { return; } // 닫히는 중

        // [주의] 상대가 데이터를 전혀 읽지 않으면(프로그램이 멈춤 등) 큐가 끝없이 커져 서버 메모리가 바닥난다.
        //        일정 개수를 넘으면 "응답 없는 클라이언트"로 보고 끊어서 서버를 보호한다.
        if (_queue.Count > MaxQueuedPackets)
        {
            _handler.Log.Error($"[{Id}] 송신 대기 패킷이 {MaxQueuedPackets}개를 넘어 연결을 끊습니다(응답 없음).");
            Close();
        }
    }

    /// <summary>
    /// 파일 조각처럼 큰 패킷 전송. 큐가 꽉 차 있으면 자리가 날 때까지 기다린다.
    /// 같은 파일의 시작·조각·끝을 모두 이 메서드로 보내야 순서가 지켜진다. 연결이 끊기면 false.
    /// </summary>
    // [개념] 배압(backpressure): 받는 쪽이 느리면 보내는 쪽 속도를 강제로 늦추는 것.
    //        내려받기 스레드가 디스크에서 3GB를 읽는 속도는 네트워크보다 훨씬 빠르다. 제한이 없으면
    //        3GB가 전부 메모리 큐에 쌓인다. 큐를 8칸으로 묶어 두면 송신 스레드가 하나 보낼 때마다 하나씩만 더 읽는다.
    // [주의] 일반 큐(Send)와 섞어 쓰면 송신 스레드가 일반 큐를 먼저 꺼내므로, "끝" 패킷이 "조각"보다 먼저 나가는
    //        순서 뒤바뀜이 생긴다. 그래서 한 파일의 패킷은 모두 _bulk 한 곳으로만 보낸다.
    public bool SendBulk(Packet packet)
    {
        try
        {
            while (!IsClosed)
            {
                // [왜] Add(무한 대기) 대신 TryAdd(250ms)를 반복: 기다리는 도중 연결이 끊기면
                //      0.25초 안에 알아채고 빠져나올 수 있다(무한 대기하면 스레드가 영원히 멈출 수 있음).
                if (_bulk.TryAdd(packet, 250)) return true;
            }
        }
        catch (InvalidOperationException) { }
        return false;
    }

    /// <summary>
    /// 연결 종료 + 정리. 여러 스레드가 거의 동시에 부를 수 있지만 실제 정리는 딱 1번만 한다.
    /// </summary>
    public void Close()
    {
        // [개념] Interlocked.Exchange(ref x, 1): x를 1로 바꾸고 "바꾸기 전 값"을 돌려준다. 이 전체가 원자적이다.
        //        두 스레드가 동시에 불러도 0을 돌려받는 스레드는 정확히 하나 → 그 스레드만 아래 정리를 한다.
        // [주의] if (_closed == 0) { _closed = 1; ... } 처럼 쓰면, 두 스레드가 동시에 0을 보고 둘 다 정리해
        //        OnDisconnected(퇴장 알림 등)가 두 번 실행되는 경쟁 상태가 생긴다.
        if (Interlocked.Exchange(ref _closed, 1) == 1) return; // 이미 닫힘

        _closing.Cancel(); // 송신 스레드를 깨워 끝낸다
        _stream.Close();   // 수신 스레드의 ReadPacket을 깨워 끝낸다
        _client.Close();
        _handler.OnDisconnected(this); // 접속자 목록·방에서 빼고 다른 사람에게 퇴장 알림
    }

    /// <summary>로그 출력용 "#3(홍길동 대리(A0001), 10.0.0.5:51234)".</summary>
    public override string ToString() => $"#{Id}({User?.ToString() ?? "미로그인"}, {RemoteEndPoint})";
}
