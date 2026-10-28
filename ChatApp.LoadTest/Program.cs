using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Security;
using ChatApp.Common.Utils;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ChatApp.LoadTest - 부하 테스트 도구
// 가상 사용자 N명이 서버에 동시에 접속해 "전체 채팅"에서 일정 간격으로 메시지를 보내고,
// 모든 사용자가 모든 메시지를 빠짐없이, 순서대로, 얼마나 빨리 받는지 측정한다.
//
// 측정 항목
//  - 접속·로그인: 성공 수, 걸린 시간
//  - 메시지: 보낸 수, 받아야 할 수(보낸 수 × 접속자 수), 받은 수 → 유실률, 순서 어긋남
//  - 지연 시간: 보낸 순간 ~ 받은 순간 (p50 / p95 / p99 / 최대). 보내는 쪽과 받는 쪽이 같은 프로세스라 같은 시계로 잰다
//  - 서버가 같은 PC에서 돌고 있으면: 메모리(최대), 스레드 수(최대), CPU 사용률(평균)
//
// 사용법: dotnet run --project ChatApp.LoadTest -- [서버주소] [포트] [사용자수] [보내는시간초] [메시지간격ms] [--plain]
//   기본은 TLS로 접속한다(인증서는 확인하지 않음 - 시험 도구라서). 평문 서버면 --plain.
//   예)  dotnet run --project ChatApp.LoadTest -- 127.0.0.1 9000 200 30 2000
// 가상 사용자 계정(LT0001...)은 처음 실행할 때 자동으로 가입한다. (서버의 AllowedEmailDomains가 ["*"]여야 함)
// 테스트가 끝난 뒤 기록 정리:  DELETE FROM messages WHERE sender_no LIKE 'LT%';
// ─────────────────────────────────────────────────────────────────────────────

Console.OutputEncoding = Encoding.UTF8;
bool useTls = !args.Contains("--plain");
args = args.Where(a => !a.StartsWith("--")).ToArray();
string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 9000;
int userCount = args.Length > 2 ? int.Parse(args[2]) : 100;
int seconds = args.Length > 3 ? int.Parse(args[3]) : 30;
int intervalMs = args.Length > 4 ? int.Parse(args[4]) : 2000;
const string Password = "LoadTest1234";

var report = new StringBuilder();
void Say(string line) { Console.WriteLine(line); report.AppendLine(line); }

Say($"부하 테스트: {host}:{port} ({(useTls ? "TLS" : "평문")}), 가상 사용자 {userCount}명, {seconds}초 동안 각자 {intervalMs}ms마다 1개씩 전송");
Say($"  예상 전송 {userCount * 1000.0 / intervalMs:N0}개/초 → 서버가 전달할 패킷 {userCount * userCount * 1000.0 / intervalMs:N0}개/초 (모두 같은 방)");

// ── 1. 계정 준비 (없으면 가입) ──
// [왜] 가입은 비밀번호 해시(PBKDF2 10만 번) 때문에 서버 CPU를 많이 쓴다. 동시에 8개씩만 보낸다.
var sw = Stopwatch.StartNew();
int created = 0;
await Parallel.ForEachAsync(Enumerable.Range(1, userCount), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) =>
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(host, port);
    Stream s = await Net.OpenAsync(tcp, host, useTls);
    NetworkHelper.WritePacket(s, Packet.Create(PacketType.RegisterReq).WithData(new RegisterRequest
    {
        EmployeeNo = No(i), Email = $"{No(i).ToLower()}@loadtest.example", Name = $"부하{i}", Department = "부하테스트", Position = "봇", Password = Password,
        ProtocolVersion = ProtocolInfo.Version,
    }));
    while (NetworkHelper.ReadPacket(s) is Packet p)
    {
        if (p.Type != PacketType.RegisterRes) continue;
        if (p.Success) Interlocked.Increment(ref created);
        else if (!(p.Body ?? "").Contains("이미")) Console.WriteLine($"  가입 실패 {No(i)}: {p.Body}");
        break;
    }
});
Say($"[1] 계정 준비: 새로 가입 {created}명 ({sw.Elapsed.TotalSeconds:N1}초)");

// ── 2. 동시 접속 + 로그인 ──
// [왜] 실제 출근 시간처럼 한꺼번에 몰리는 상황. 로그인도 비밀번호 확인(PBKDF2) 때문에 서버 CPU를 쓴다.
using var monitor = ServerMonitor.TryStart();
var clients = Enumerable.Range(1, userCount).Select(i => new VirtualUser(i, No(i))).ToArray();
sw.Restart();
var loginTimes = new ConcurrentBag<double>();
await Parallel.ForEachAsync(clients, new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (c, _) =>
{
    var t = Stopwatch.StartNew();
    if (await c.ConnectAndLoginAsync(host, port, Password, useTls)) loginTimes.Add(t.Elapsed.TotalMilliseconds);
});
int online = clients.Count(c => c.LoggedIn);
Say($"[2] 접속·로그인: {online}/{userCount}명 성공, 전체 {sw.Elapsed.TotalSeconds:N1}초" +
    (loginTimes.IsEmpty ? "" : $", 1명당 평균 {loginTimes.Average():N0}ms / 최대 {loginTimes.Max():N0}ms"));
if (online == 0) return;

await Task.Delay(3000); // 입장 알림·접속자 목록이 다 퍼지길 기다림
foreach (var c in clients) c.ResetStats();
monitor?.Reset();

// ── 3. 채팅 폭주 ──
sw.Restart();
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
var senders = clients.Where(c => c.LoggedIn).Select(c => c.SendLoopAsync(intervalMs, stop.Token)).ToArray();
await Task.WhenAll(senders);
double sendSeconds = sw.Elapsed.TotalSeconds;
long sent = clients.Sum(c => c.Sent);
int receivers = clients.Count(c => c.LoggedIn);
long expected = sent * receivers;

// ── 4. 남은 메시지가 다 도착할 때까지 기다림 (3초 동안 더 오지 않으면 끝) ──
long last = -1;
var drain = Stopwatch.StartNew();
while (drain.Elapsed < TimeSpan.FromSeconds(30))
{
    long now = clients.Sum(c => c.Received);
    if (now >= expected) break;
    if (now == last && drain.Elapsed > TimeSpan.FromSeconds(3)) break;
    last = now;
    await Task.Delay(1000);
}
double totalSeconds = sw.Elapsed.TotalSeconds;

long received = clients.Sum(c => c.Received);
long outOfOrder = clients.Sum(c => c.OutOfOrder);
int disconnected = clients.Count(c => c.Disconnected);
double[] lat = clients.SelectMany(c => c.Latencies).Order().ToArray();
double P(double q) => lat.Length == 0 ? 0 : lat[Math.Min(lat.Length - 1, (int)(lat.Length * q))];

Say($"[3] 메시지: 보냄 {sent:N0}개 ({sent / sendSeconds:N0}개/초), 받아야 함 {expected:N0}, 받음 {received:N0}");
Say($"    유실 {expected - received:N0}개 ({(expected == 0 ? 0 : 100.0 * (expected - received) / expected):N3}%), 순서 어긋남 {outOfOrder:N0}개, 도중 끊긴 사용자 {disconnected}명");
Say($"    서버 전달 처리량 {received / totalSeconds:N0}개/초");
Say($"[4] 지연 시간(ms): 중앙값 {P(0.50):N1} / 95% {P(0.95):N1} / 99% {P(0.99):N1} / 최대 {(lat.Length == 0 ? 0 : lat[^1]):N1}");
if (monitor is not null) Say($"[5] 서버(같은 PC): {monitor.Summary()}");
else Say("[5] 서버 프로세스(ChatApp.Server)가 이 PC에 없어 메모리·CPU는 측정하지 않음");

foreach (var c in clients) c.Close();
string file = $"loadtest-{DateTime.Now:yyyyMMdd-HHmmss}-{userCount}명.txt";
File.WriteAllText(file, report.ToString());
Console.WriteLine($"결과 저장: {Path.GetFullPath(file)}");

static string No(int i) => $"LT{i:D4}";

/// <summary>
/// 가상 사용자 1명. 실제 클라이언트처럼 수신 전용 스레드를 하나 갖는다.
/// [스레드] 통계(받은 수, 지연, 순서)는 그 사용자의 수신 스레드만 쓰므로 lock이 필요 없고, 합계를 낼 때만 다른 스레드가 읽는다.
/// </summary>
sealed class VirtualUser(int index, string employeeNo)
{
    private TcpClient? _tcp;
    private Stream? _stream;
    private readonly object _sendLock = new();
    private readonly TaskCompletionSource<bool> _login = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, int> _lastSeq = []; // 보낸 사람별 마지막으로 받은 번호 (순서 확인)
    private volatile bool _counting;
    private int _seq;

    public bool LoggedIn { get; private set; }
    public bool Disconnected { get; private set; }
    public long Sent;
    public long Received;
    public long OutOfOrder;
    public List<double> Latencies { get; } = [];

    public async Task<bool> ConnectAndLoginAsync(string host, int port, string password, bool useTls)
    {
        try
        {
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(host, port);
            _stream = await Net.OpenAsync(_tcp, host, useTls);
            new Thread(ReceiveLoop) { IsBackground = true, Name = $"LT-{index}" }.Start();
            Send(Packet.Create(PacketType.LoginReq).WithData(new LoginRequest { EmployeeNo = employeeNo, Password = password, ProtocolVersion = ProtocolInfo.Version }));
            LoggedIn = await _login.Task.WaitAsync(TimeSpan.FromSeconds(60));
            if (LoggedIn) _ = HeartbeatLoopAsync(); // 실제 클라이언트처럼 15초마다 Ping (안 보내면 서버가 45초 뒤 끊는다)
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException
                                      or System.Security.Authentication.AuthenticationException)
        {
            LoggedIn = false;
        }
        return LoggedIn;
    }

    public void ResetStats()
    {
        Sent = Received = OutOfOrder = 0;
        _lastSeq.Clear(); // 이 시점 이후 수신 스레드만 건드림 (아직 아무도 보내지 않는 중)
        Latencies.Clear();
        _counting = true;
    }

    /// <summary>시작 시각을 사람마다 흩뜨린 뒤 일정 간격으로 전체 채팅에 보낸다. 본문 = "LT|보낸사람|번호|보낸시각"</summary>
    public async Task SendLoopAsync(int intervalMs, CancellationToken stop)
    {
        try
        {
            await Task.Delay(Random.Shared.Next(intervalMs), stop);
            while (!stop.IsCancellationRequested)
            {
                int seq = ++_seq;
                Send(Packet.Create(PacketType.ChatMsg, $"LT|{index}|{seq}|{Stopwatch.GetTimestamp()}", target: RoomInfo.LobbyId.ToString()));
                Interlocked.Increment(ref Sent);
                await Task.Delay(intervalMs, stop);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { Disconnected = true; }
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!Disconnected && _tcp?.Connected == true)
            {
                await Task.Delay(TimeSpan.FromSeconds(Heartbeat.IntervalSeconds));
                Send(Packet.Create(PacketType.Ping));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private void Send(Packet packet)
    {
        lock (_sendLock) NetworkHelper.WritePacket(_stream!, packet);
    }

    private void ReceiveLoop()
    {
        try
        {
            while (NetworkHelper.ReadPacket(_stream!) is Packet p)
            {
                if (p.Type == PacketType.LoginRes) { _login.TrySetResult(p.Success); continue; }
                if (p.Type != PacketType.ChatMsg || !_counting || p.Body is not string body || !body.StartsWith("LT|")) continue;

                string[] parts = body.Split('|');
                int from = int.Parse(parts[1]), seq = int.Parse(parts[2]);
                long sentAt = long.Parse(parts[3]);
                Latencies.Add(Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds);
                if (_lastSeq.TryGetValue(from, out int prev) && seq <= prev) OutOfOrder++;
                _lastSeq[from] = seq;
                Interlocked.Increment(ref Received);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException) { }
        if (LoggedIn) Disconnected = true;
        _login.TrySetResult(false);
    }

    public void Close()
    {
        LoggedIn = false; // 닫는 것은 "도중 끊김"으로 세지 않음
        _tcp?.Close();
    }
}

/// <summary>
/// 서버가 같은 PC에 있으면 1초마다 메모리·스레드 수·CPU를 잰다.
/// [개념] CPU 사용률 = (그 프로세스가 쓴 CPU 시간 증가분) ÷ (흐른 시간 × CPU 코어 수)
/// </summary>
sealed class ServerMonitor : IDisposable
{
    private readonly Process _process;
    private readonly Timer _timer;
    private TimeSpan _cpuStart;
    private DateTime _wallStart;
    private long _peakMemory;
    private int _peakThreads;

    private ServerMonitor(Process process)
    {
        _process = process;
        Reset();
        _timer = new Timer(_ => Sample(), null, 0, 1000);
    }

    public static ServerMonitor? TryStart()
    {
        // Visual Studio·exe로 실행하면 프로세스 이름이 ChatApp.Server다 (dotnet ChatApp.Server.dll로 실행했다면 찾지 못함)
        Process? p = Process.GetProcessesByName("ChatApp.Server").FirstOrDefault();
        return p is null ? null : new ServerMonitor(p);
    }

    public void Reset()
    {
        _process.Refresh();
        _cpuStart = _process.TotalProcessorTime;
        _wallStart = DateTime.UtcNow;
        _peakMemory = _process.WorkingSet64;
        _peakThreads = _process.Threads.Count;
    }

    private void Sample()
    {
        try
        {
            _process.Refresh();
            _peakMemory = Math.Max(_peakMemory, _process.WorkingSet64);
            _peakThreads = Math.Max(_peakThreads, _process.Threads.Count);
        }
        catch (InvalidOperationException) { } // 서버가 꺼짐
    }

    public string Summary()
    {
        Sample();
        double cpu = (_process.TotalProcessorTime - _cpuStart).TotalSeconds / (DateTime.UtcNow - _wallStart).TotalSeconds / Environment.ProcessorCount;
        return $"메모리 최대 {_peakMemory / 1024.0 / 1024:N0}MB, 스레드 최대 {_peakThreads}개, CPU 평균 {cpu:P0} (코어 {Environment.ProcessorCount}개 기준)";
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>TCP 스트림을 (필요하면) TLS로 감싼다.</summary>
static class Net
{
    // [주의] acceptAny: true = 인증서를 확인하지 않는다. 내 PC에서 내 서버를 시험하는 도구라서만 허용한다.
    //        실제 클라이언트에서 이렇게 하면 TLS를 쓰는 의미(가짜 서버 차단)가 사라진다.
    public static async Task<Stream> OpenAsync(TcpClient tcp, string host, bool useTls) =>
        useTls ? await TlsHelper.AuthenticateAsClientAsync(tcp.GetStream(), host, null, acceptAny: true)
               : tcp.GetStream();
}
