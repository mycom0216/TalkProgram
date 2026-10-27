using System.Net;
using System.Net.Sockets;
using ChatApp.Server.Logging;
using ChatApp.Server.Services;

namespace ChatApp.Server.Core;

/// <summary>
/// TcpListener + Accept 스레드. 접속이 들어올 때마다 ClientSession을 만들고 수신 스레드를 시작한다.
/// </summary>
public sealed class ChatServer
{
    private readonly TcpListener _listener;
    private readonly ServerLogger _log;
    private readonly PacketHandler _handler;
    private Thread? _acceptThread;
    private volatile bool _running;

    public SessionManager Sessions { get; } = new();

    public ChatServer(IPAddress address, int port, ServerLogger log)
    {
        _listener = new TcpListener(address, port);
        _log = log;
        _handler = new PacketHandler(Sessions, log);
    }

    public void Start()
    {
        _listener.Start();
        _running = true;

        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "Accept" };
        _acceptThread.Start();

        _log.Info($"서버 시작: {_listener.LocalEndpoint}");
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient(); // 접속이 올 때까지 블로킹
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (!_running) break; // Stop()에서 listener를 멈춘 경우
                _log.Error($"Accept 실패: {ex.Message}");
                continue;
            }

            client.NoDelay = true; // 짧은 채팅 메시지를 지연 없이 보냄

            var session = new ClientSession(client, _handler);
            Sessions.Add(session);
            _log.Info($"접속: {session} (현재 {Sessions.ConnectedCount}명)");
            session.Start();
        }
    }

    public void Stop()
    {
        _running = false;
        _listener.Stop();         // 블로킹 중인 AcceptTcpClient를 깨운다
        Sessions.CloseAll();
        _acceptThread?.Join(TimeSpan.FromSeconds(2));
        _log.Info("서버 종료");
    }

    /// <summary>서버 콘솔에서 보내는 공지.</summary>
    public void Notice(string message) => _handler.BroadcastSystem($"[공지] {message}");
}
