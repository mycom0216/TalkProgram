using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using ChatApp.Common.Protocol;
using ChatApp.Common.Security;
using ChatApp.Common.Utils;
using ChatApp.Server;
using ChatApp.Server.Logging;
using ChatApp.Server.Security;

namespace ChatApp.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// TLS 테스트: 실제로 내 PC 안(127.0.0.1)에서 서버·클라이언트 소켓을 열어 TLS 핸드셰이크를 해 본다.
// - 자체 서명 인증서 자동 생성
// - 지문 고정(pinning): 처음 보는 인증서 / 저장한 지문과 같은 인증서 / 다른 인증서
// [개념] IClassFixture<T>: 이 클래스의 테스트들이 T 객체 하나를 같이 쓴다. 인증서 만들기(RSA 키 생성)는 느리므로 한 번만.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class TlsFixture : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "chatapp-test-" + Guid.NewGuid().ToString("N"));
    public ServerTls Tls { get; }
    public ServerLogger Log { get; }

    public TlsFixture()
    {
        Directory.CreateDirectory(Folder);
        Log = new ServerLogger(Path.Combine(Folder, "test.log"));
        Tls = ServerCertificate.LoadOrCreate(new TlsConfig { CertificatePath = Path.Combine(Folder, "server.pfx") }, Log);
    }

    public void Dispose()
    {
        Log.Dispose();
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
    }
}

public class TlsTests(TlsFixture fixture) : IClassFixture<TlsFixture>
{
    // 이 테스트는 “지문은 SHA256 64자이고 보기용 형식과 서로 바꿀 수 있다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 지문은_SHA256_64자이고_보기용_형식과_서로_바꿀_수_있다()
    {
        string raw = TlsHelper.Fingerprint(fixture.Tls.Certificate);
        Assert.Equal(64, raw.Length);
        Assert.Equal(raw, TlsHelper.Normalize(fixture.Tls.Fingerprint)); // 서버 로그의 "AB:CD:.." 형식
        Assert.Equal(raw, TlsHelper.Normalize(TlsHelper.Format(raw).ToLowerInvariant().Replace(":", " ")));
    }

    // 이 테스트는 “자동 생성 인증서는 개인 키가 있고 5년 유효하다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 자동_생성_인증서는_개인_키가_있고_5년_유효하다()
    {
        Assert.True(fixture.Tls.Certificate.HasPrivateKey);
        Assert.True(fixture.Tls.Certificate.NotAfter > DateTime.Now.AddYears(4));
        Assert.True(File.Exists(Path.Combine(fixture.Folder, "server.pfx")));
    }

    // 이 테스트는 “처음 보는 자체 서명 인증서는 사용자 확인이 필요하다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public async Task 처음_보는_자체_서명_인증서는_사용자_확인이_필요하다()
    {
        var ex = await Assert.ThrowsAsync<UntrustedCertificateException>(() => ConnectAsync(pinned: null));
        Assert.False(ex.Changed);
        Assert.Equal(TlsHelper.Fingerprint(fixture.Tls.Certificate), ex.Fingerprint);
    }

    // 이 테스트는 “저장한 지문과 같으면 연결되고 패킷이 암호화되어 오간다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public async Task 저장한_지문과_같으면_연결되고_패킷이_암호화되어_오간다()
    {
        string pinned = fixture.Tls.Fingerprint; // 사용자가 "신뢰"를 눌러 저장한 값
        using SslStream ssl = await ConnectAsync(pinned);

        Assert.True(ssl.IsEncrypted);
        NetworkHelper.WritePacket(ssl, Packet.Create(PacketType.Ping, "핑"));
        Assert.Equal("핑", NetworkHelper.ReadPacket(ssl)?.Body); // 테스트 서버는 받은 패킷을 그대로 돌려준다
    }

    // 이 테스트는 “저장한 지문과 다르면 중간자 공격으로 보고 거부한다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public async Task 저장한_지문과_다르면_중간자_공격으로_보고_거부한다()
    {
        var ex = await Assert.ThrowsAsync<UntrustedCertificateException>(() => ConnectAsync(new string('A', 64)));
        Assert.True(ex.Changed);
    }

    /// <summary>127.0.0.1에 TLS 에코 서버를 띄우고 클라이언트로 접속한다.</summary>
    private async Task<SslStream> ConnectAsync(string? pinned)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); // 0 = 비어 있는 포트를 운영체제가 골라 줌
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task server = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            using var ssl = new SslStream(accepted.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = fixture.Tls.Context });
                while (NetworkHelper.ReadPacket(ssl) is Packet p) NetworkHelper.WritePacket(ssl, p); // 에코
            }
            catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException) { }
        });

        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var timeout = new CancellationTokenSource(TlsHelper.HandshakeTimeoutMs);
            return await TlsHelper.AuthenticateAsClientAsync(client.GetStream(), "127.0.0.1", pinned, cancel: timeout.Token);
        }
        finally
        {
            listener.Stop(); // 더 이상 새 접속은 받지 않는다 (이미 받은 연결과 에코 작업은 클라이언트가 닫히면 끝난다)
        }
    }
}
