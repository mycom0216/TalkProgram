using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ChatApp.Common.Security;
using ChatApp.Server.Logging;

namespace ChatApp.Server.Security;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 서버 TLS 인증서를 읽거나, 없으면 "자체 서명 인증서"를 새로 만들어 저장한다.
// [스레드]   Program.cs(메인 스레드)에서 서버 시작 전에 한 번만 부른다.
// [개념]     인증서(X.509) = "이 공개 키의 주인은 누구"라는 증명서. 짝이 되는 개인 키는 서버만 갖고 있다.
//            TLS 핸드셰이크에서 서버는 인증서를 보여 주고, 개인 키로만 할 수 있는 서명을 해서 "진짜 주인"임을 증명한다.
// [개념]     자체 서명(self-signed): 공인 인증기관(CA) 대신 자기가 자기 인증서에 서명한 것.
//            돈이 들지 않고 인터넷이 없는 사내망에서도 쓸 수 있지만, 운영체제가 자동으로 믿어 주지 않는다.
//            → 클라이언트가 처음 접속할 때 지문을 확인받고 저장하는 방식(TlsHelper의 TOFU)으로 보완한다.
// ═══════════════════════════════════════════════════════════════════════════

public static class ServerCertificate
{
    /// <summary>
    /// 인증서 파일을 읽는다. 없으면 새로 만들어 저장한 뒤 읽는다.
    /// </summary>
    // [개념] SslStreamCertificateContext: 인증서 + 인증서 체인을 미리 묶어 둔 객체.
    //        접속마다 체인을 다시 계산하지 않아도 되어, 동시 접속이 많을 때 핸드셰이크가 빨라진다.
    public static ServerTls LoadOrCreate(TlsConfig config, ServerLogger log)
    {
        string path = config.CertificateFullPath;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, CreateSelfSigned(config.CertificatePassword));
            log.Info($"TLS 인증서가 없어 새로 만들었습니다: {path}");
        }

        // [개념] X509CertificateLoader(.NET 9+): .pfx를 읽는 새 API. 예전 new X509Certificate2(파일) 생성자는 더 이상 권장되지 않는다.
        // [주의] 윈도우의 TLS 구현(SChannel)은 "메모리에만 있는 임시 키"(EphemeralKeySet)를 쓰지 못한다.
        //        그래서 기본 플래그로 읽어 운영체제 키 저장소를 거치게 한다.
        X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(path, config.CertificatePassword);
        if (!certificate.HasPrivateKey)
            throw new InvalidDataException($"인증서에 개인 키가 없습니다: {path}");

        string fingerprint = TlsHelper.Format(TlsHelper.Fingerprint(certificate));
        log.Info($"TLS 사용: {certificate.Subject}, 만료 {certificate.NotAfter:yyyy-MM-dd}");
        log.Info($"TLS 인증서 지문(SHA-256): {fingerprint}");
        log.Info("  └ 클라이언트가 처음 접속할 때 이 지문이 화면에 보입니다. 같은지 확인하고 '신뢰'를 누르게 안내하세요.");
        if (certificate.NotAfter < DateTime.Now.AddDays(30))
            log.Error($"TLS 인증서가 곧 만료됩니다({certificate.NotAfter:yyyy-MM-dd}). 파일을 지우고 다시 실행하면 새로 만들어집니다(클라이언트는 지문을 다시 확인해야 함).");

        return new ServerTls(certificate, fingerprint);
    }

    /// <summary>자체 서명 인증서를 만들어 .pfx 바이트로 돌려준다 (RSA 2048, SHA-256, 5년).</summary>
    private static byte[] CreateSelfSigned(string password)
    {
        // [왜] RSA 2048: 윈도우·리눅스 어느 TLS 구현에서나 문제없이 동작하는 가장 무난한 선택.
        //      (ECDSA P-256이 더 가볍지만, 오래된 윈도우 버전과의 호환성을 우선했다)
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={Environment.MachineName}, O=사내톡", key,
                                             HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // [개념] 확장(extension): 이 인증서를 "어디에 써도 되는지" 적는 칸.
        //   - BasicConstraints(CA 아님): 이 인증서로 다른 인증서를 발급할 수 없다.
        //   - KeyUsage: 서명·키 교환용.
        //   - EnhancedKeyUsage 1.3.6.1.5.5.7.3.1 = "TLS 서버 인증"용.
        //   - SubjectAlternativeName(SAN): 이 인증서가 쓰일 서버 이름/주소. 요즘 TLS는 CN 대신 SAN을 본다.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        // [왜] 시작 시각을 하루 전으로: 서버와 클라이언트 PC의 시계가 조금 어긋나 있어도 "아직 유효하지 않음" 오류가 나지 않게.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
        return certificate.Export(X509ContentType.Pfx, password);
    }
}

/// <summary>서버 인증서와 핸드셰이크용 컨텍스트를 함께 들고 다닌다.</summary>
public sealed class ServerTls(X509Certificate2 certificate, string fingerprint)
{
    public X509Certificate2 Certificate { get; } = certificate;

    /// <summary>"AB:CD:..." 형태의 SHA-256 지문 (서버 콘솔 /tls 명령에서 다시 보여 준다).</summary>
    public string Fingerprint { get; } = fingerprint;

    // [왜] offline: true → 체인을 만들 때 인터넷에서 중간 인증서를 내려받으려 하지 않는다(망분리 환경).
    public System.Net.Security.SslStreamCertificateContext Context { get; } =
        System.Net.Security.SslStreamCertificateContext.Create(certificate, null, offline: true);
}
