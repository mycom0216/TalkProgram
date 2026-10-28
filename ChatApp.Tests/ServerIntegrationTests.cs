using System.Net;
using System.Net.Sockets;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Security;
using ChatApp.Common.Utils;
using ChatApp.Server;
using ChatApp.Server.Core;
using ChatApp.Server.Data;
using ChatApp.Server.Services;

namespace ChatApp.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 서버 통합 테스트: 진짜 ChatServer(Accept 스레드, 세션 스레드, TLS)를 띄우고 소켓으로 붙어 본다.
// DB는 일부러 "없는 주소"를 준다 → DB 없이도 확인할 수 있는 것들과, DB가 죽었을 때 서버가 버티는지를 본다.
// (로그인·관리자 기능처럼 DB가 필요한 흐름은 실제 MariaDB로 시나리오 테스트를 했다. docs/TESTING.md 참고)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ServerFixture : IDisposable
{
    public int Port { get; }
    public string Fingerprint { get; }
    private readonly ChatServer _server;
    private readonly TlsFixture _tls = new();
    private readonly AuditLog _audit;

    public ServerFixture()
    {
        // 포트 0으로 잠깐 열어 비어 있는 포트 번호를 얻는다
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        // 127.0.0.1:1 = 아무도 듣지 않는 포트 → DB 연결은 즉시 실패한다
        var db = new Db("Server=127.0.0.1;Port=1;Database=x;User ID=x;Password=x;Connection Timeout=2;");
        var config = new ServerConfig { UploadFolder = Path.Combine(_tls.Folder, "uploads") };
        var users = new UserRepository(db);
        var auditRepository = new AuditRepository(db);
        _audit = new AuditLog(auditRepository, _tls.Log);
        _server = new ChatServer(IPAddress.Loopback, Port, new AuthService(users, config), new RoomRepository(db),
                                 new FileRepository(db), new MessageRepository(db), users, auditRepository, _audit,
                                 config, _tls.Log, _tls.Tls.Context);
        _server.Start();
        Fingerprint = _tls.Tls.Fingerprint;
    }

    public void Dispose()
    {
        _server.Stop();
        _audit.Dispose();
        _tls.Dispose();
    }
}

public class ServerIntegrationTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Ping을_보내면_Pong이_온다()
    {
        using var c = await TestClient.ConnectAsync(server);
        c.Send(Packet.Create(PacketType.Ping));
        Assert.Equal(PacketType.Pong, c.Read()?.Type);
    }

    [Fact]
    public async Task 로그인_전에는_채팅할_수_없다()
    {
        using var c = await TestClient.ConnectAsync(server);
        c.Send(Packet.Create(PacketType.ChatMsg, "몰래 보내기"));
        Packet? reply = c.Read();
        Assert.Equal(PacketType.Error, reply?.Type);
        Assert.Contains("로그인", reply?.Body);
    }

    [Fact]
    public async Task DB가_죽어_있어도_서버는_오류로_답하고_계속_동작한다()
    {
        using var c = await TestClient.ConnectAsync(server);
        c.Send(Packet.Create(PacketType.LoginReq).WithData(new LoginRequest { EmployeeNo = "A0001", Password = "Passw0rd1", ProtocolVersion = ProtocolInfo.Version }));
        Packet? reply = c.Read();
        Assert.Equal(PacketType.LoginRes, reply?.Type);
        Assert.False(reply!.Success);
        Assert.Contains("DB", reply.Body);

        // 같은 연결이 여전히 살아 있다 (한 요청의 오류가 연결·서버를 죽이지 않는다)
        c.Send(Packet.Create(PacketType.Ping));
        Assert.Equal(PacketType.Pong, c.Read()?.Type);
    }

    [Fact]
    public async Task 여러_클라이언트가_동시에_접속해도_각자_응답을_받는다()
    {
        // 접속자마다 수신 스레드가 따로 있어서 서로 기다리지 않는다 (멀티스레드 서버의 핵심)
        var clients = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => TestClient.ConnectAsync(server)));
        try
        {
            foreach (TestClient c in clients) c.Send(Packet.Create(PacketType.Ping));
            Assert.All(clients, c => Assert.Equal(PacketType.Pong, c.Read()?.Type));
        }
        finally
        {
            foreach (TestClient c in clients) c.Dispose();
        }
    }

    [Fact]
    public async Task TLS_서버에_평문으로_붙으면_연결이_끊긴다()
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        NetworkStream stream = tcp.GetStream();
        stream.ReadTimeout = 5000;
        NetworkHelper.WritePacket(stream, Packet.Create(PacketType.Ping));
        // 서버는 이 바이트를 TLS 인사말로 읽으려다 실패하고 연결을 닫는다 → 끝(0바이트) 또는 연결 오류
        try
        {
            Assert.Null(NetworkHelper.ReadPacket(stream));
        }
        catch (IOException) { /* 연결이 강제로 끊김 - 이것도 정상 */ }
    }

    /// <summary>TLS로 접속한 테스트용 클라이언트.</summary>
    private sealed class TestClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly Stream _stream;

        private TestClient(TcpClient tcp, Stream stream) { _tcp = tcp; _stream = stream; }

        public static async Task<TestClient> ConnectAsync(ServerFixture server)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
            tcp.ReceiveTimeout = 5000; // 응답이 안 오면 테스트가 영원히 멈추지 않게
            Stream ssl = await TlsHelper.AuthenticateAsClientAsync(tcp.GetStream(), "127.0.0.1", server.Fingerprint);
            return new TestClient(tcp, ssl);
        }

        public void Send(Packet packet) => NetworkHelper.WritePacket(_stream, packet);

        /// <summary>목록 방송 같은 "부탁하지 않은" 패킷은 건너뛰고 다음 응답을 읽는다.</summary>
        public Packet? Read()
        {
            while (NetworkHelper.ReadPacket(_stream) is Packet p)
                if (p.Type is not (PacketType.UserList or PacketType.RoomList)) return p;
            return null;
        }

        public void Dispose()
        {
            _stream.Dispose();
            _tcp.Dispose();
        }
    }
}
