using System.Net.Sockets;
using System.Text.Json;
using ChatApp.Common.Protocol;
using ChatApp.Common.Utils;
using ChatApp.Server.Services;

namespace ChatApp.Server.Core;

/// <summary>
/// 접속한 클라이언트 1명. 클라이언트 1명당 수신 스레드 1개를 가진다.
/// - 수신: 전용 스레드가 ReadPacket으로 블로킹 대기 → PacketHandler에 넘김
/// - 송신: 여러 스레드(다른 사람의 수신 스레드, 서버 콘솔)가 동시에 Send를 부를 수 있으므로 _sendLock으로 보호
/// </summary>
public sealed class ClientSession
{
    private static int _nextId;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly PacketHandler _handler;
    private readonly Thread _receiveThread;
    private readonly object _sendLock = new();
    private int _closed; // 0 = 열림, 1 = 닫힘 (Interlocked로 한 번만 닫히게 보장)

    public int Id { get; }
    public string RemoteEndPoint { get; }

    /// <summary>로그인 전에는 null.</summary>
    public string? Nickname { get; internal set; }
    public bool IsLoggedIn => Nickname is not null;

    public ClientSession(TcpClient client, PacketHandler handler)
    {
        Id = Interlocked.Increment(ref _nextId);
        _client = client;
        _stream = client.GetStream();
        _handler = handler;
        RemoteEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

        _receiveThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,          // 메인 스레드가 끝나면 같이 종료
            Name = $"Session-{Id}",
        };
    }

    public void Start() => _receiveThread.Start();

    private void ReceiveLoop()
    {
        try
        {
            while (true)
            {
                Packet? packet = NetworkHelper.ReadPacket(_stream);
                if (packet is null) break; // 클라이언트가 정상 종료

                _handler.Handle(this, packet);
            }
        }
        catch (IOException) { /* 강제 종료, 네트워크 끊김 */ }
        catch (ObjectDisposedException) { /* 서버가 먼저 닫음 */ }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            _handler.Log.Error($"[{Id}] 잘못된 패킷 수신, 연결 종료: {ex.Message}");
        }
        catch (Exception ex)
        {
            // 한 클라이언트의 오류가 서버 전체를 죽이지 않도록 여기서 막는다(NF-05).
            _handler.Log.Error($"[{Id}] 처리 중 예외: {ex}");
        }
        finally
        {
            Close();
        }
    }

    public void Send(Packet packet)
    {
        if (Volatile.Read(ref _closed) == 1) return;

        try
        {
            lock (_sendLock) // 한 소켓에 두 스레드가 동시에 쓰면 패킷이 섞인다(NF-04)
            {
                NetworkHelper.WritePacket(_stream, packet);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close(); // lock 밖에서 닫는다
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return; // 이미 닫힘

        _stream.Close();
        _client.Close();
        _handler.OnDisconnected(this);
    }

    public override string ToString() => $"#{Id}({Nickname ?? "미로그인"}, {RemoteEndPoint})";
}
