using System.Net.Sockets;
using System.Text;
using ChatApp.Common.Protocol;
using ChatApp.Common.Utils;

// 1단계 검증용 콘솔 클라이언트. 3단계에서 WinForms 클라이언트(ChatApp.Client)로 대체한다.
// 구조는 WinForms에서도 같다: 메인(UI) 스레드 = 입력/송신, 수신 스레드 = ReadPacket 루프.

Console.OutputEncoding = Encoding.UTF8;

string host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 && int.TryParse(args[1], out int p) ? p : 9000;

using var client = new TcpClient();
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
NetworkStream stream = client.GetStream();

// 메인 스레드와 (나중에 추가할) 하트비트 스레드가 동시에 보낼 수 있으므로 송신은 lock
object sendLock = new();
void Send(Packet packet)
{
    lock (sendLock) NetworkHelper.WritePacket(stream, packet);
}

// ── 1. 로그인 (수신 스레드 시작 전이라 여기서 직접 응답을 읽는다) ──
string myName;
while (true)
{
    Console.Write("닉네임: ");
    string? input = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(input)) continue;

    Send(Packet.Create(PacketType.LoginReq, body: input));

    Packet? res = NetworkHelper.ReadPacket(stream);
    if (res is null)
    {
        Console.WriteLine("서버와 연결이 끊어졌습니다.");
        return;
    }
    Console.WriteLine(res.Body);
    if (res.Type == PacketType.LoginRes && res.Success)
    {
        myName = input;
        break;
    }
}

// ── 2. 수신 스레드 시작 ──
bool quitting = false; // 내가 /quit 한 경우엔 "연결 끊김" 안내를 띄우지 않음
var receiveThread = new Thread(() =>
{
    try
    {
        while (true)
        {
            Packet? packet = NetworkHelper.ReadPacket(stream);
            if (packet is null) break;
            Print(packet);
        }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }

    if (!Volatile.Read(ref quitting))
        Console.WriteLine("[서버와 연결이 끊어졌습니다. Enter를 누르면 종료합니다]");
})
{ IsBackground = true, Name = "Receive" };
receiveThread.Start();

Console.WriteLine("명령어: /w 닉네임 내용 (귓속말), /quit (종료)");

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
            // "/w 닉네임 내용"
            string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                Console.WriteLine("사용법: /w 닉네임 내용");
                continue;
            }
            Send(Packet.Create(PacketType.Whisper, body: parts[2], target: parts[1]));
        }
        else
        {
            Send(Packet.Create(PacketType.ChatMsg, body: line));
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

void Print(Packet packet)
{
    string time = packet.Timestamp.ToString("HH:mm");
    string text = packet.Type switch
    {
        PacketType.ChatMsg => $"[{time}] {packet.Sender}: {packet.Body}",
        PacketType.Whisper when packet.Sender == myName => $"[{time}] (귓속말 → {packet.Target}) {packet.Body}",
        PacketType.Whisper => $"[{time}] (귓속말 ← {packet.Sender}) {packet.Body}",
        PacketType.SystemMsg => $"*** {packet.Body}",
        PacketType.UserList => $"[접속자] {packet.Body}",
        PacketType.Error => $"[오류] {packet.Body}",
        _ => "",
    };
    if (text.Length > 0) Console.WriteLine(text);
}
