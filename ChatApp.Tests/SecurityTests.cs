using ChatApp.Common.Models;
using ChatApp.Server;
using ChatApp.Server.Security;

namespace ChatApp.Tests;

// 비밀번호 해시, 설정 파일, 관리자·감사 로그 이름 규칙.
public class PasswordHasherTests
{
// ===============
    // 이 테스트는 “맞는 비밀번호만 통과한다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 맞는_비밀번호만_통과한다()
    {
        var (hash, salt) = PasswordHasher.Hash("Passw0rd!");
        Assert.True(PasswordHasher.Verify("Passw0rd!", hash, salt));
        Assert.False(PasswordHasher.Verify("passw0rd!", hash, salt)); // 대소문자도 구분
        Assert.False(PasswordHasher.Verify("", hash, salt));
    }

// ===============
    // 이 테스트는 “같은 비밀번호라도 사람마다 해시가 다르다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 같은_비밀번호라도_사람마다_해시가_다르다()
    {
        // salt(무작위 값) 덕분에, DB가 유출돼도 "같은 비밀번호를 쓰는 사람들"을 한눈에 찾을 수 없다
        var a = PasswordHasher.Hash("SamePass1");
        var b = PasswordHasher.Hash("SamePass1");
        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Hash, b.Hash);
    }

// ===============
    // 이 테스트는 “해시에는 원래 비밀번호가 들어 있지 않다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 해시에는_원래_비밀번호가_들어_있지_않다()
    {
        var (hash, salt) = PasswordHasher.Hash("VisibleSecret1");
        Assert.DoesNotContain("VisibleSecret1", hash + salt);
    }

// ===============
    // 이 테스트는 “새 해시에는 반복 횟수가 적혀 있고 다시 만들 필요가 없다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 새_해시에는_반복_횟수가_적혀_있고_다시_만들_필요가_없다()
    {
        var (hash, _) = PasswordHasher.Hash("Passw0rd!");
        Assert.StartsWith("pbkdf2-sha256$600000$", hash);
        Assert.True(hash.Length <= 100, "DB 칸(VARCHAR(100))에 들어가야 한다");
        Assert.False(PasswordHasher.NeedsRehash(hash));
    }

// ===============
    // 이 테스트는 “옛 10만 번 해시도 로그인되고 다시 만들 대상이 된다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 옛_해시도_로그인되고_다시_만들_대상이_된다()
    {
        // 반복 횟수를 저장하기 전 방식: 접두사 없는 Base64, 10만 번
        byte[] salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        string legacy = Convert.ToBase64String(System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            "Passw0rd!", salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32));
        string saltText = Convert.ToBase64String(salt);

        Assert.True(PasswordHasher.Verify("Passw0rd!", legacy, saltText));
        Assert.False(PasswordHasher.Verify("Wrong0rd!", legacy, saltText));
        Assert.True(PasswordHasher.NeedsRehash(legacy));
    }

// ===============
    // 이 테스트는 “망가진 해시는 예외 없이 거절한다” 상황이 제대로 처리되는지 확인합니다.
    [Theory]
    [InlineData("pbkdf2-sha256$abc$AAAA")]
    [InlineData("pbkdf2-sha256$0$AAAA")]
    [InlineData("pbkdf2-sha256$999999999$AAAA")]
    [InlineData("pbkdf2-sha256$600000")]
    [InlineData("not base64 !!")]
    [InlineData("")]
    public void 망가진_해시는_예외_없이_거절한다(string broken)
    {
        string salt = Convert.ToBase64String(new byte[16]);
        Assert.False(PasswordHasher.Verify("Passw0rd!", broken, salt));
        Assert.False(PasswordHasher.NeedsRehash(broken));
    }
}

public class ServerConfigTests
{
// ===============
    // 이 테스트는 “이메일 도메인을 정리한다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 이메일_도메인을_정리한다()
    {
        var config = new ServerConfig { AllowedEmailDomains = [" @Company.KR ", "basiclike.kr", "company.kr", ""] };
        Assert.False(config.AnyEmailDomain);
        Assert.Equal(new[] { "company.kr", "basiclike.kr" }, config.EmailDomains);
    }

// ===============
    // 이 테스트는 “별표면 모든 도메인을 허용한다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 별표면_모든_도메인을_허용한다()
        => Assert.True(new ServerConfig { AllowedEmailDomains = ["*"] }.AnyEmailDomain);

// ===============
    // 이 테스트는 “환경 변수가 있으면 DB·인증서 비밀번호를 덮어쓴다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 환경_변수가_있으면_DB와_인증서_비밀번호를_덮어쓴다()
    {
        var config = new ServerConfig { ConnectionString = "Server=file;Password=from-file;" };
        config.Tls.CertificatePassword = "file-pass";

        ServerConfig.ApplyEnvironmentOverrides(config, name => name switch
        {
            "CHATAPP_CONNECTION_STRING" => "Server=env;Password=from-env;",
            "CHATAPP_TLS_CERT_PASSWORD" => "env-pass",
            _ => null,
        });

        Assert.Equal("Server=env;Password=from-env;", config.ConnectionString);
        Assert.Equal("env-pass", config.Tls.CertificatePassword);
    }

// ===============
    // 이 테스트는 “환경 변수가 없거나 비어 있으면 설정 파일 값을 그대로 둔다” 상황이 제대로 처리되는지 확인합니다.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 환경_변수가_없거나_비어_있으면_설정_파일_값을_그대로_둔다(string? value)
    {
        var config = new ServerConfig { ConnectionString = "Server=file;" };
        config.Tls.CertificatePassword = "file-pass";

        ServerConfig.ApplyEnvironmentOverrides(config, _ => value);

        Assert.Equal("Server=file;", config.ConnectionString);
        Assert.Equal("file-pass", config.Tls.CertificatePassword);
    }

// ===============
    // 이 테스트는 “설정 파일의 주석과 빠진 항목을 처리한다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 설정_파일의_주석과_빠진_항목을_처리한다()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {
                  // 주석이 있어도 읽힌다
                  "port": 9100,
                  "ConnectionString": "Server=x;",
                }
                """);
            ServerConfig config = ServerConfig.Load(path);
            Assert.Equal(9100, config.Port);                    // 대소문자 무시
            Assert.True(config.Tls.Enabled);                    // 없는 항목 = 기본값 (TLS는 기본 켜짐)
            Assert.Equal(3072, config.MaxFileSizeMB);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class AdminModelTests
{
// ===============
    // 이 테스트는 “모든 관리 동작은 감사 로그 이름과 한국어 이름이 있다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 모든_관리_동작은_감사_로그_이름과_한국어_이름이_있다()
    {
        foreach (AdminActionType type in Enum.GetValues<AdminActionType>())
        {
            string action = AuditActions.Of(type);
            Assert.Contains(action, AuditActions.All);               // 관리자 창의 동작 필터에 나온다
            Assert.True(action.Length <= 30, action);                 // DB 칸(VARCHAR(30))에 들어간다
            Assert.NotEqual(action, AuditActions.Text(action));       // 화면에 영어 코드가 그대로 보이지 않는다
            Assert.NotEqual(type.ToString(), AdminAction.Text(type));
        }
    }

// ===============
    // 이 테스트는 “감사 로그 필터 목록에 중복이 없다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 감사_로그_필터_목록에_중복이_없다()
        => Assert.Equal(AuditActions.All.Length, AuditActions.All.Distinct().Count());
}
