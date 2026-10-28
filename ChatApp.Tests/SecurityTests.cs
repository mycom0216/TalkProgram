using ChatApp.Common.Models;
using ChatApp.Server;
using ChatApp.Server.Security;

namespace ChatApp.Tests;

// 비밀번호 해시, 설정 파일, 관리자·감사 로그 이름 규칙.
public class PasswordHasherTests
{
    [Fact]
    public void 맞는_비밀번호만_통과한다()
    {
        var (hash, salt) = PasswordHasher.Hash("Passw0rd!");
        Assert.True(PasswordHasher.Verify("Passw0rd!", hash, salt));
        Assert.False(PasswordHasher.Verify("passw0rd!", hash, salt)); // 대소문자도 구분
        Assert.False(PasswordHasher.Verify("", hash, salt));
    }

    [Fact]
    public void 같은_비밀번호라도_사람마다_해시가_다르다()
    {
        // salt(무작위 값) 덕분에, DB가 유출돼도 "같은 비밀번호를 쓰는 사람들"을 한눈에 찾을 수 없다
        var a = PasswordHasher.Hash("SamePass1");
        var b = PasswordHasher.Hash("SamePass1");
        Assert.NotEqual(a.Salt, b.Salt);
        Assert.NotEqual(a.Hash, b.Hash);
    }

    [Fact]
    public void 해시에는_원래_비밀번호가_들어_있지_않다()
    {
        var (hash, salt) = PasswordHasher.Hash("VisibleSecret1");
        Assert.DoesNotContain("VisibleSecret1", hash + salt);
    }
}

public class ServerConfigTests
{
    [Fact]
    public void 이메일_도메인을_정리한다()
    {
        var config = new ServerConfig { AllowedEmailDomains = [" @Company.KR ", "basiclike.kr", "company.kr", ""] };
        Assert.False(config.AnyEmailDomain);
        Assert.Equal(new[] { "company.kr", "basiclike.kr" }, config.EmailDomains);
    }

    [Fact]
    public void 별표면_모든_도메인을_허용한다()
        => Assert.True(new ServerConfig { AllowedEmailDomains = ["*"] }.AnyEmailDomain);

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

    [Fact]
    public void 감사_로그_필터_목록에_중복이_없다()
        => Assert.Equal(AuditActions.All.Length, AuditActions.All.Distinct().Count());
}
