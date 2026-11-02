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
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   서버가 TLS(암호화 통신)에 쓸 인증서(.pfx)를 읽는다. 파일이 없으면 이 서버 PC용 "자체 서명 인증서"를 만들어 저장한 뒤 읽고, 지문(SHA-256)을 로그에 보여 준다.
//
// ■ 흐름
//   Program.cs → LoadOrCreate(설정, 로그) → pfx 파일 있나? 없으면 CreateSelfSigned로 만들어 저장 → LoadPkcs12FromFile로 읽기 → 개인 키 확인
//   → 지문과 만료일을 로그에 출력(만료 30일 전이면 경고) → ServerTls(인증서 + 핸드셰이크용 Context) 반환
//   이후: 접속자가 들어오면 ClientSession이 이 Context로 SslStream.AuthenticateAsServer를 한다(Program이 tls?.Context를 넘김).
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 자체 서명 인증서 vs 공인 CA 인증서 .............. "self-signed certificate vs CA signed"
//   - TLS 핸드셰이크와 개인 키의 역할 ................. "TLS handshake server certificate private key"
//   - TOFU(처음 한 번 지문 확인 후 신뢰) ............... "trust on first use certificate pinning"
//   - CertificateRequest / CreateSelfSigned ........... "C# CertificateRequest CreateSelfSigned"
//   - SAN(Subject Alternative Name) .................. "certificate SAN vs CN"
//   - pfx(PKCS#12)와 비밀번호 ......................... "pfx PKCS12 private key password"
//   - SslStreamCertificateContext ..................... "SslStreamCertificateContext.Create offline"
//
// ■ 면접 예상 질문과 답
//   Q1. 자체 서명 인증서를 쓴 이유와 위험은 무엇인가요?
//       A. 사내망에서는 공인 인증서(비용·도메인·인터넷 필요)가 부담이어서 자체 서명을 택했습니다. 위험은 운영체제가 믿어 주지 않아 "중간에서 가짜 서버가 끼어드는 공격"을 자동으로는 못 막는다는 점입니다. 그래서 클라이언트가 처음 접속할 때 지문을 화면에 보여 확인받고 저장하는 방식(TOFU)으로 보완했습니다. 운영자는 서버 로그의 지문과 화면의 지문이 같은지 안내해야 합니다.
//   Q2. 인증서를 새로 만들면 클라이언트는 어떻게 되나요?
//       A. 지문이 바뀌므로 클라이언트는 "지문이 달라졌다"는 경고를 보게 됩니다(저장된 지문과 다르므로). 의도한 교체인지 공격인지 사람이 확인해야 하므로, 만료 경고 로그에도 "클라이언트는 지문을 다시 확인해야 함"이라고 적어 두었습니다.
//   Q3. pfx 비밀번호는 어디에 있나요? 안전한가요?
//       A. 설정(config.Tls)의 CertificatePassword로 읽고 씁니다. pfx 파일과 비밀번호가 함께 유출되면 개인 키가 노출되므로, 설정 파일의 접근 권한 관리가 중요합니다(어디에 어떻게 저장하는지는 설정 파일을 확인할 것).
//   Q4. 개인 키(HasPrivateKey) 확인은 왜 하나요?
//       A. 인증서만 있고 키가 없으면 서버가 "내가 주인"이라는 서명을 할 수 없어 핸드셰이크가 접속마다 실패합니다. 접속이 들어온 뒤에야 알면 원인 찾기가 어려우므로, 시작할 때 바로 예외로 알립니다(빨리 실패하기).
//   Q5. 유효 시작을 "하루 전"으로 만든 이유는요?
//       A. 서버와 클라이언트 PC의 시계가 조금 어긋나 있으면, 방금 만든 인증서가 클라이언트 입장에서 "아직 유효하지 않음"으로 보일 수 있습니다. 하루 여유를 두어 막습니다.
//   Q6. SAN에 이름을 여러 개 넣은 이유는요?
//       A. 요즘 TLS 검증은 CN이 아니라 SAN을 봅니다. 서버 PC 이름, localhost, 127.0.0.1을 넣어 같은 PC에서의 시험과 PC 이름으로 접속하는 경우를 모두 맞췄습니다(IP 주소로 접속하는 경우는 SAN에 그 IP가 없으므로 일반 검증은 이름 불일치가 날 수 있다 — 클라이언트가 지문 방식으로 확인하는지는 TlsHelper를 볼 것).
//
// ■ 디버깅할 때 먼저 볼 곳
//   - 서버가 시작할 때 인증서 예외 ....... 비밀번호가 맞는지, pfx가 깨지지 않았는지, 파일 경로(config.CertificateFullPath)
//   - 클라이언트가 "지문이 다르다" ........ 서버 PC가 바뀌었거나 pfx를 지우고 다시 만들었는지(로그의 지문과 비교)
//   - 윈도우에서 키 오류 ................. 위 [주의]처럼 임시 키 옵션을 쓰지 않았는지(코드는 기본 플래그)
//
// ■ 개선 아이디어 (코드는 바꾸지 않음)
//   - 만료 전 자동 갱신과 "지문 두 개 허용" 같은 교체 절차, 서버 시작 후에도 만료 점검
//   - 인증서 비밀번호를 설정 파일 평문이 아닌 OS 보안 저장소/환경 변수에서 읽기
// ───────────────────────────────────────────────────────────────────────────

/// <summary>서버 TLS 인증서(.pfx)를 읽거나 자체 서명으로 만들어 주는 정적 도우미.</summary>
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
    /// <summary>서버 인증서 (개인 키 포함).</summary>
    public X509Certificate2 Certificate { get; } = certificate;

    /// <summary>"AB:CD:..." 형태의 SHA-256 지문 (서버 콘솔 /tls 명령에서 다시 보여 준다).</summary>
    public string Fingerprint { get; } = fingerprint;

    /// <summary>접속마다 쓰는 TLS 핸드셰이크용 인증서 컨텍스트 (인증서 체인을 미리 만들어 둔 것).</summary>
    // [왜] offline: true → 체인을 만들 때 인터넷에서 중간 인증서를 내려받으려 하지 않는다(망분리 환경).
    public System.Net.Security.SslStreamCertificateContext Context { get; } =
        System.Net.Security.SslStreamCertificateContext.Create(certificate, null, offline: true);
}
