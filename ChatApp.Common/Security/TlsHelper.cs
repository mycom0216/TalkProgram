using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ChatApp.Common.Security;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] TLS(전송 구간 암호화) 공통 도우미. 클라이언트(WinForms·콘솔·부하 테스트)가 서버에 붙을 때 쓴다.
// [왜]       TLS가 없으면 비밀번호·대화·파일이 "평문"으로 네트워크를 지나간다. 같은 와이파이/스위치에 있는 사람이
//            Wireshark 같은 도구로 그대로 읽을 수 있다. 회사에 파는 메신저라면 반드시 막아야 하는 부분이다.
// [개념]     TLS(Transport Layer Security): HTTPS의 "S". TCP 위에 한 겹을 더 씌워
//            ① 암호화(엿보기 방지) ② 무결성(중간에서 바꿔치기 방지) ③ 서버 인증(가짜 서버 방지)을 해 준다.
//            .NET에서는 SslStream이 이 일을 한다. NetworkStream을 SslStream으로 "감싸기만" 하면
//            그 위의 Read/Write는 그대로 쓰면서 실제로 오가는 바이트는 암호화된다(NetworkHelper 코드는 그대로).
// [개념]     인증서 지문(fingerprint): 인증서 전체 바이트의 SHA-256 해시. 인증서마다 다르고 위조가 사실상 불가능하다.
//            사내 서버는 공인 인증서 대신 "자체 서명 인증서"를 쓰는 경우가 많아서, 운영체제가 신뢰하지 않는다.
//            그래서 SSH처럼 "처음 접속 때 지문을 확인하고 저장 → 다음부터는 저장한 지문과 같은지 비교" 방식(TOFU)을 쓴다.
//            (TOFU = Trust On First Use, 처음 한 번 신뢰)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 서버 인증서를 신뢰할 수 없어서 연결을 거부했다. 화면은 지문을 보여 주고 사용자가 신뢰하면 저장 후 다시 연결한다.
/// </summary>
public sealed class UntrustedCertificateException : AuthenticationException
{
    /// <summary>서버가 보낸 인증서의 SHA-256 지문 (콜론 없는 16진수 64글자).</summary>
    public string Fingerprint { get; }

    /// <summary>인증서 주체 이름 (예: "CN=SERVER-PC, O=사내톡").</summary>
    public string Subject { get; }

    /// <summary>
    /// true = 예전에 저장한 지문과 "다르다". 서버 인증서를 새로 만들었거나, 누군가 중간에서 가짜 서버로 가로채는 중(중간자 공격)일 수 있다.
    /// false = 처음 보는 서버라 저장한 지문이 아직 없다.
    /// </summary>
    public bool Changed { get; }

    public UntrustedCertificateException(string fingerprint, string subject, bool changed)
        : base(changed ? "서버 인증서가 예전과 다릅니다." : "처음 접속하는 서버의 인증서입니다.")
    {
        Fingerprint = fingerprint;
        Subject = subject;
        Changed = changed;
    }
}

public static class TlsHelper
{
    /// <summary>TLS 핸드셰이크(암호화 협상) 제한 시간. 이 안에 끝나지 않으면 연결을 포기한다.</summary>
    // [왜] 상대가 TCP 연결만 하고 아무것도 보내지 않으면 핸드셰이크가 영원히 끝나지 않는다. 시간 제한으로 자원을 지킨다.
    public const int HandshakeTimeoutMs = 10_000;

    /// <summary>인증서의 SHA-256 지문 (콜론 없는 대문자 16진수).</summary>
    // [개념] GetRawCertData(): 인증서 자체의 DER 바이트. 이것을 해시하면 인증서 "한 장"을 고유하게 가리키는 값이 된다.
    // [왜] SHA-1(옛 Thumbprint 속성) 대신 SHA-256: SHA-1은 충돌 공격이 알려져 있어 보안 용도로는 쓰지 않는다.
    public static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>사람이 읽기 쉽게 "AB:CD:EF..." 형태로 바꾼다 (서버 로그와 화면에서 같은 모양으로 보여 주려고).</summary>
    public static string Format(string fingerprint)
    {
        string hex = Normalize(fingerprint);
        // [개념] Chunk(2): 문자열을 2글자씩 자른다 → ["AB","CD",...] → ":"로 이어 붙인다.
        return string.Join(':', hex.Chunk(2).Select(c => new string(c)));
    }

    /// <summary>비교용으로 콜론·공백을 없애고 대문자로 맞춘다 (사용자가 복사해 붙인 값도 비교할 수 있게).</summary>
    public static string Normalize(string? fingerprint) =>
        (fingerprint ?? "").Replace(":", "").Replace(" ", "").Trim().ToUpperInvariant();

    /// <summary>
    /// 이미 연결된 TCP 스트림 위에서 TLS 핸드셰이크를 하고, 암호화된 스트림(SslStream)을 돌려준다.
    /// </summary>
    /// <param name="inner">TcpClient.GetStream()으로 얻은 평문 스트림.</param>
    /// <param name="host">접속한 서버 주소 (SNI·인증서 이름 확인용).</param>
    /// <param name="pinnedFingerprint">예전에 신뢰한 지문. 있으면 이 값과 같은 인증서만 받는다.</param>
    /// <param name="acceptAny">true면 인증서를 확인하지 않는다. 부하 테스트 같은 "시험용"에서만 쓴다(실사용 금지).</param>
    /// <exception cref="UntrustedCertificateException">신뢰할 수 없는 인증서 (화면에서 지문 확인 필요).</exception>
    /// <exception cref="AuthenticationException">TLS 협상 실패 (서버가 TLS를 쓰지 않는 등).</exception>
    public static async Task<SslStream> AuthenticateAsClientAsync(Stream inner, string host, string? pinnedFingerprint,
                                                                 bool acceptAny = false, CancellationToken cancel = default)
    {
        string pinned = Normalize(pinnedFingerprint);
        UntrustedCertificateException? rejected = null;

        // [개념] leaveInnerStreamOpen: false → SslStream을 닫으면 안쪽 NetworkStream(소켓)도 같이 닫힌다.
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            // [개념] SslProtocols.None = "운영체제가 안전하다고 보는 버전 중 가장 좋은 것"(TLS 1.3, 안 되면 1.2).
            //        버전을 코드에 못 박아 두면 나중에 더 좋은 버전이 나와도 못 쓰고, 낡은 버전을 실수로 켤 수도 있다.
            EnabledSslProtocols = SslProtocols.None,
            // [왜] 인증서 폐기 목록(CRL) 확인을 끈다: 망분리된 사내망은 인터넷에 있는 폐기 목록 서버에 갈 수 없어서
            //      확인을 켜 두면 접속할 때마다 수십 초씩 멈춘다. 대신 지문 고정(pinning)으로 인증서를 확인한다.
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,

            // [개념] 인증서 확인 콜백: 서버 인증서를 받은 순간 .NET이 이 함수를 부른다. true면 통과, false면 연결 거부.
            //        errors에는 운영체제가 검사한 결과(신뢰할 수 없는 발급자, 이름 불일치 등)가 들어 있다.
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null) return false;
                if (acceptAny) return true;

                string actual = Fingerprint(certificate);
                if (pinned.Length > 0)
                {
                    // 저장한 지문이 있으면 "그것과 같은가"만 본다. 자체 서명 인증서라도 같은 인증서면 안전하다.
                    if (actual == pinned) return true;
                    rejected = new UntrustedCertificateException(actual, certificate.Subject, changed: true);
                    return false;
                }

                // 저장한 지문이 없을 때: 회사가 공인/사내 CA 인증서를 쓰면 운영체제 검사만으로 통과한다.
                if (errors == SslPolicyErrors.None) return true;

                // 자체 서명 인증서 → 사용자에게 지문을 보여 주고 물어봐야 한다
                rejected = new UntrustedCertificateException(actual, certificate.Subject, changed: false);
                return false;
            },
        };

        try
        {
            await ssl.AuthenticateAsClientAsync(options, cancel);
            return ssl;
        }
        // [왜] 콜백이 false를 돌려주면 .NET은 일반 AuthenticationException을 던진다. 이유(지문)를 화면에 전하려고
        //      우리가 만든 UntrustedCertificateException으로 바꿔서 던진다.
        catch (AuthenticationException) when (rejected is not null)
        {
            ssl.Dispose();
            throw rejected;
        }
        catch
        {
            ssl.Dispose();
            throw;
        }
    }
}
