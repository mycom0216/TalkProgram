using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Security;
using ChatApp.Common.Transfer;
using ChatApp.Common.Utils;

// 검증용 콘솔 클라이언트. 3단계에서 WinForms 클라이언트(ChatApp.Client)로 대체한다.
// 구조는 WinForms에서도 같다: 메인(UI) 스레드 = 입력/송신, 수신 스레드 = ReadPacket 루프.

Console.OutputEncoding = Encoding.UTF8;

// 사용법: ChatApp.ConsoleClient [서버주소] [포트] [--plain]   (--plain = TLS 끄기, 평문 서버용)
bool useTls = !args.Contains("--plain");
args = args.Where(a => !a.StartsWith("--")).ToArray();
string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 && int.TryParse(args[1], out int p) ? p : 9000;

TcpClient client = new(); // TLS 지문 확인 후 다시 연결할 수 있어 using var(재할당 불가) 대신 일반 변수
try
{
    client.Connect(host, port);
}
catch (SocketException ex)
{
    Console.WriteLine($"서버({host}:{port})에 연결할 수 없습니다: {ex.Message}");
    return;
}
client.NoDelay = true;
Stream stream = client.GetStream();

// ── TLS: 서버 인증서 지문을 보여 주고 확인을 받는다 (콘솔판 TOFU, 저장은 하지 않음) ──
if (useTls)
{
    try
    {
        stream = await TlsHelper.AuthenticateAsClientAsync(stream, host, pinnedFingerprint: null);
    }
    catch (UntrustedCertificateException ex)
    {
        Console.WriteLine($"서버 인증서: {ex.Subject}");
        Console.WriteLine($"SHA-256 지문: {TlsHelper.Format(ex.Fingerprint)}");
        Console.Write("서버 콘솔(/tls)의 지문과 같습니까? 신뢰하고 연결할까요? (y/n): ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) return;

        // 처음 연결은 거부했으므로 새로 연결하고, 이번에는 방금 확인한 지문으로 고정한다
        client.Dispose();
        client = new TcpClient { NoDelay = true };
        client.Connect(host, port);
        stream = await TlsHelper.AuthenticateAsClientAsync(client.GetStream(), host, ex.Fingerprint);
    }
    catch (Exception ex) when (ex is AuthenticationException or IOException)
    {
        Console.WriteLine($"보안 연결(TLS) 실패: {ex.Message}  (평문 서버라면 --plain 옵션으로 실행)");
        return;
    }
    Console.WriteLine($"TLS 연결됨: {((System.Net.Security.SslStream)stream).SslProtocol}");
}

// 메인 스레드와 (나중에 추가할) 하트비트 스레드가 동시에 보낼 수 있으므로 송신은 lock
object sendLock = new();
void Send(Packet packet)
{
    lock (sendLock) NetworkHelper.WritePacket(stream, packet);
}

// ── 1. 로그인 / 회원가입 (수신 스레드 시작 전이라 여기서 직접 응답을 읽는다) ──
UserInfo? me = null;
while (me is null)
{
    Console.WriteLine();
    Console.WriteLine("1) 로그인   2) 회원가입   q) 종료");
    Console.Write("선택: ");
    string? choice = Console.ReadLine()?.Trim();
    if (choice is null || choice.Equals("q", StringComparison.OrdinalIgnoreCase)) return;

    try
    {
        if (choice == "1") me = Login();
        else if (choice == "2") Register();
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
    {
        Console.WriteLine("서버와 연결이 끊어졌습니다.");
        return;
    }
}

// ── 2. 수신 스레드 시작 ──
// 여러 방에 동시에 참여할 수 있으므로, 채팅을 보낼 "현재 방"을 따로 기억한다.
// 수신 스레드(입장/퇴장 결과)와 메인 스레드(입력)가 함께 쓰므로 lock으로 보호한다.
object roomLock = new();
int activeRoom = RoomInfo.LobbyId;
var joinedRooms = new HashSet<int>();
var roomNames = new Dictionary<int, string> { [RoomInfo.LobbyId] = "전체 채팅" };

// 파일 전송 엔진 (WinForms 클라이언트와 같은 코드). 조각 패킷은 수신 스레드에서 바로 처리된다.
var transfers = new FileTransferService(packet =>
{
    try { Send(packet); return true; }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return false; }
});
transfers.UploadProgress += (id, percent) => { if (percent % 25 == 0) Console.WriteLine($"[파일] 보내는 중 {percent}% ({id[..6]})"); };
transfers.UploadFailed += (id, reason) => Console.WriteLine($"[파일] 보내기 실패 ({id[..6]}): {reason}");
transfers.DownloadProgress += (id, percent) => { if (percent % 25 == 0) Console.WriteLine($"[파일] 받는 중 {percent}% ({id[..6]})"); };
transfers.DownloadCompleted += (id, path) => Console.WriteLine($"[파일] 저장 완료: {path}");
transfers.DownloadFailed += (id, reason) => Console.WriteLine($"[파일] 받기 실패 ({id[..6]}): {reason}");

bool quitting = false; // 내가 /quit 한 경우엔 "연결 끊김" 안내를 띄우지 않음
var receiveThread = new Thread(() =>
{
    try
    {
        while (true)
        {
            Packet? packet = NetworkHelper.ReadPacket(stream);
            if (packet is null) break;
            if (transfers.TryHandle(packet)) continue; // 파일 조각 등
            Print(packet);
        }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }

    if (!Volatile.Read(ref quitting))
        Console.WriteLine("[서버와 연결이 끊어졌습니다. Enter를 누르면 종료합니다]");
})
{ IsBackground = true, Name = "Receive" };
receiveThread.Start();

// ── 하트비트 스레드: 15초마다 Ping (안 보내면 서버가 45초 뒤 응답 없음으로 연결을 끊는다, Heartbeat.cs 참고) ──
// [스레드] 메인 스레드(입력)와 이 스레드가 동시에 Send를 부를 수 있으므로 Send 안의 lock(sendLock)이 꼭 필요하다.
var heartbeatThread = new Thread(() =>
{
    while (!Volatile.Read(ref quitting))
    {
        Thread.Sleep(TimeSpan.FromSeconds(Heartbeat.IntervalSeconds));
        try { Send(Packet.Create(PacketType.Ping)); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { break; }
    }
})
{ IsBackground = true, Name = "Heartbeat" };
heartbeatThread.Start();

Console.WriteLine("명령어: /w 사원번호 내용 (귓속말), /rooms (방 목록), /create 방이름 [최대인원], /join 방번호 (입장 또는 말할 방 전환),");
Console.WriteLine("        /leave [방번호] (방 나가기), /myrooms (참여 중인 방), /quit (종료)");
Console.WriteLine("        /file 경로 (말하는 방에 파일 보내기), /dm 사원번호 경로 (1:1 파일), /get 파일ID [저장경로] (파일 받기), /history 사원번호 (1:1 지난 대화)");

// ── 3. 메인 스레드: 입력 → 송신 ──
while (true)
{
    string? line = Console.ReadLine();
    if (line is null || line == "/quit") break;
    if (string.IsNullOrWhiteSpace(line)) continue;
    if (!client.Connected) break;

    try
    {
        if (line.StartsWith("/w "))
        {
            // "/w 사원번호 내용"
            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                Console.WriteLine("사용법: /w 사원번호 내용");
                continue;
            }
            Send(Packet.Create(PacketType.Whisper, body: parts[2], target: parts[1]));
        }
        else if (line == "/rooms")
        {
            Send(Packet.Create(PacketType.RoomListReq));
        }
        else if (line.StartsWith("/create "))
        {
            // "/create 개발팀 회의 10" → 마지막이 숫자면 최대 인원
            string rest = line["/create ".Length..].Trim();
            int max = 10;
            int lastSpace = rest.LastIndexOf(' ');
            if (lastSpace > 0 && int.TryParse(rest[(lastSpace + 1)..], out int n))
            {
                max = n;
                rest = rest[..lastSpace].Trim();
            }
            Send(Packet.Create(PacketType.RoomCreate).WithData(new CreateRoomRequest { Name = rest, MaxUsers = max }));
        }
        else if (line.StartsWith("/join "))
        {
            string arg = line["/join ".Length..].Trim();
            bool alreadyJoined;
            lock (roomLock)
            {
                alreadyJoined = int.TryParse(arg, out int id) && joinedRooms.Contains(id);
                if (alreadyJoined) activeRoom = id; // 이미 참여 중이면 말할 방만 바꾼다
            }
            if (alreadyJoined) Console.WriteLine($"=== 이제 #{arg} {RoomName(int.Parse(arg))} 방에 말합니다 ===");
            else Send(Packet.Create(PacketType.RoomJoin, target: arg));
        }
        else if (line == "/leave" || line.StartsWith("/leave "))
        {
            string arg = line.Length > "/leave".Length ? line["/leave".Length..].Trim() : "";
            int target;
            lock (roomLock) target = int.TryParse(arg, out int id) ? id : activeRoom;
            Send(Packet.Create(PacketType.RoomLeave, target: target.ToString()));
        }
        else if (line == "/myrooms")
        {
            lock (roomLock)
                Console.WriteLine("[참여 중] " + string.Join(" / ", joinedRooms.Order().Select(id =>
                    $"#{id} {roomNames.GetValueOrDefault(id, "?")}{(id == activeRoom ? " ←말하는 방" : "")}")));
        }
        else if (line.StartsWith("/file "))
        {
            int target;
            lock (roomLock) target = activeRoom;
            try { Console.WriteLine($"[파일] 보내기 시작 (취소: /cancel {transfers.StartUpload(line["/file ".Length..].Trim().Trim('"'), target, null)})"); }
            catch (ArgumentException ex) { Console.WriteLine($"[파일] {ex.Message}"); }
        }
        else if (line.StartsWith("/dm "))
        {
            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) { Console.WriteLine("사용법: /dm 사원번호 경로"); continue; }
            try { transfers.StartUpload(parts[2].Trim('"'), null, parts[1]); }
            catch (ArgumentException ex) { Console.WriteLine($"[파일] {ex.Message}"); }
        }
        else if (line.StartsWith("/cancel "))
        {
            // 파일 ID 앞 몇 글자만 알 때가 많으므로(진행률 표시에 앞 6글자) 그대로 받는다: 보내기/받기 둘 다 시도
            string id = line["/cancel ".Length..].Trim();
            bool ok = transfers.CancelUpload(id) | transfers.CancelDownload(id);
            Console.WriteLine(ok ? $"[파일] 취소했습니다 ({id[..Math.Min(6, id.Length)]})" : "[파일] 진행 중인 전송이 없습니다. (전체 파일 ID를 입력하세요)");
        }
        else if (line.StartsWith("/history "))
        {
            Send(Packet.Create(PacketType.HistoryReq, target: line["/history ".Length..].Trim()));
        }
        else if (line.StartsWith("/get "))
        {
            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            string fileId = parts[1];
            string dest = parts.Length > 2 ? parts[2].Trim('"') : Path.Combine(AppContext.BaseDirectory, "downloads", fileId);
            if (!transfers.StartDownload(fileId, dest)) Console.WriteLine("[파일] 이미 받는 중이거나 연결이 끊겼습니다.");
        }
        else if (line.StartsWith('/'))
        {
            Console.WriteLine("알 수 없는 명령어입니다. /w, /rooms, /create, /join, /leave, /myrooms, /file, /dm, /get, /quit 중에서 입력하세요.");
        }
        else
        {
            int target;
            lock (roomLock) target = activeRoom;
            Send(Packet.Create(PacketType.ChatMsg, body: line, target: target.ToString()));
        }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
    {
        break;
    }
}

Volatile.Write(ref quitting, true);
try { Send(Packet.Create(PacketType.Logout)); } catch { /* 이미 끊긴 경우 무시 */ }
client.Close();

// ───────────────────────────── 아래는 도우미 함수 ─────────────────────────────

UserInfo? Login()
{
    var req = new LoginRequest
    {
        EmployeeNo = Ask("사원번호: "),
        Password = AskPassword("비밀번호: "),
    };
    Send(Packet.Create(PacketType.LoginReq).WithData(req));

    Packet res = WaitFor(PacketType.LoginRes);
    Console.WriteLine(res.Body);
    return res.Success ? res.GetData<UserInfo>() : null;
}

void Register()
{
    // 가입 가능한 이메일 도메인을 서버에서 받아 와 안내
    Send(Packet.Create(PacketType.ServerInfoReq));
    ServerInfo? info = WaitFor(PacketType.ServerInfo).GetData<ServerInfo>();
    Console.WriteLine($"── 회원가입 (가입 가능한 이메일: {info?.DomainsText ?? "?"}) ──");
    var req = new RegisterRequest
    {
        EmployeeNo = Ask("사원번호 (영문·숫자 4~20자): "),
        Email = Ask(info is { AnyEmailDomain: false, AllowedEmailDomains.Count: > 0 }
            ? $"사내 이메일 (예: hong@{info.AllowedEmailDomains[0]}): "
            : "이메일: "),
        Name = Ask("이름: "),
        Department = Ask("부서: "),
        Position = Ask("직급: "),
    };

    string password = AskPassword("비밀번호 (영문+숫자 8자 이상): ");
    if (AskPassword("비밀번호 확인: ") != password)
    {
        Console.WriteLine("비밀번호가 일치하지 않습니다.");
        return;
    }
    req.Password = password;

    Send(Packet.Create(PacketType.RegisterReq).WithData(req));
    Console.WriteLine(WaitFor(PacketType.RegisterRes).Body);
}

// 원하는 응답이 올 때까지 읽는다(로그인 전에는 다른 패킷이 거의 오지 않음).
Packet WaitFor(PacketType type)
{
    while (true)
    {
        Packet packet = NetworkHelper.ReadPacket(stream) ?? throw new IOException("서버가 연결을 끊었습니다.");
        if (packet.Type == type) return packet;
        if (packet.Type == PacketType.Error) Console.WriteLine($"[오류] {packet.Body}");
    }
}

static string Ask(string label)
{
    Console.Write(label);
    return Console.ReadLine()?.Trim() ?? "";
}

// 비밀번호는 화면에 * 로 표시
static string AskPassword(string label)
{
    Console.Write(label);
    if (Console.IsInputRedirected) return Console.ReadLine() ?? ""; // 자동 테스트용

    var sb = new StringBuilder();
    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return sb.ToString();
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0)
            {
                sb.Length--;
                Console.Write("\b \b");
            }
            continue;
        }
        if (!char.IsControl(key.KeyChar))
        {
            sb.Append(key.KeyChar);
            Console.Write('*');
        }
    }
}

string RoomName(int id)
{
    lock (roomLock) return roomNames.GetValueOrDefault(id, $"#{id}");
}

string RoomTag(string? target) => int.TryParse(target, out int id) ? $"[{RoomName(id)}] " : "";

void Print(Packet packet)
{
    // 방 입장/퇴장 결과는 상태(참여 방, 말하는 방)를 먼저 갱신한다 (수신 스레드)
    if (packet.Type == PacketType.RoomRes && packet.Success && packet.GetData<RoomInfo>() is RoomInfo joined)
    {
        lock (roomLock)
        {
            joinedRooms.Add(joined.RoomId);
            roomNames[joined.RoomId] = joined.Name;
            activeRoom = joined.RoomId;
        }
    }
    else if (packet.Type == PacketType.RoomLeft && int.TryParse(packet.Target, out int leftId))
    {
        lock (roomLock)
        {
            joinedRooms.Remove(leftId);
            if (activeRoom == leftId) activeRoom = RoomInfo.LobbyId;
        }
    }
    else if (packet.Type == PacketType.RoomList)
    {
        lock (roomLock)
            foreach (RoomInfo r in packet.GetData<List<RoomInfo>>() ?? []) roomNames[r.RoomId] = r.Name;
    }

    string time = packet.Timestamp.ToString("HH:mm");
    string text = packet.Type switch
    {
        PacketType.ChatMsg => $"[{time}] {RoomTag(packet.Target)}{packet.Sender}: {packet.Body}",
        PacketType.Whisper when packet.SenderId == me!.EmployeeNo => $"[{time}] (귓속말 → {packet.GetData<UserInfo>()?.ToString() ?? packet.Target}) {packet.Body}",
        PacketType.Whisper => $"[{time}] (귓속말 ← {packet.Sender}({packet.SenderId})) {packet.Body}",
        PacketType.SystemMsg => $"*** {RoomTag(packet.Target)}{packet.Body}",
        PacketType.UserList => FormatUserList(packet.GetData<List<UserInfo>>() ?? []),
        PacketType.RoomRes => packet.Success ? $"=== {packet.Body} (이제 이 방에 말합니다) ===" : $"[방] {packet.Body}",
        PacketType.RoomLeft => $"=== {packet.Body} ===",
        PacketType.RoomList => FormatRoomList(packet.GetData<List<RoomInfo>>() ?? []),
        PacketType.FileMsg when packet.GetData<SharedFile>() is SharedFile f =>
            $"[{time}] {(f.RoomId is null ? "(1:1) " : RoomTag(packet.Target))}{packet.Sender}: [파일] {f.FileName} ({f.SizeText}) → /get {f.FileId}",
        PacketType.History when packet.GetData<HistoryBatch>() is HistoryBatch h => FormatHistory(h),
        PacketType.Error => $"[오류] {packet.Body}",
        _ => "",
    };
    if (text.Length > 0) Console.WriteLine(text);
}

string FormatHistory(HistoryBatch h)
{
    if (h.Items.Count == 0) return "";
    string where = h.RoomId is int id ? RoomName(id) : $"1:1 {h.PeerNo}";
    var lines = h.Items.Select(i => $"  ({i.Time:MM-dd HH:mm}) {i.SenderName}: {i.Body}{(i.File is null ? "" : $" → /get {i.File.FileId}")}");
    return $"--- [{where}] 지난 대화 {h.Items.Count}개 ---\n{string.Join("\n", lines)}\n--- 여기까지 ---";
}

static string FormatRoomList(List<RoomInfo> rooms)
    => "[방 목록] " + string.Join(" / ", rooms.Select(r => $"#{r.RoomId} {r.Name}({r.CapacityText})"));

// 부서별로 묶어서 표시 (CH-03)
static string FormatUserList(List<UserInfo> users)
{
    var groups = users.GroupBy(u => u.Department)
                      .Select(g => $"{g.Key}: {string.Join(", ", g.Select(u => u.ToString()))}");
    return $"[접속자 {users.Count}명] {string.Join(" / ", groups)}";
}
