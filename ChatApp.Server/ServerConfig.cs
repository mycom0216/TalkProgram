using System.Text.Json;

namespace ChatApp.Server;

/// <summary>
/// appsettings.json 설정. DB 비밀번호가 들어가므로 appsettings.json은 Git에 올리지 않고,
/// 형식만 보여 주는 appsettings.example.json을 올린다.
/// </summary>
public sealed class ServerConfig
{
    public int Port { get; set; } = 9000;

    /// <summary>MariaDB 연결 문자열.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// 회원가입을 허용할 사내 이메일 도메인 목록(@ 뒤). 예: ["company.kr", "basiclike.kr"]
    /// "*"를 넣으면 도메인 제한 없음(테스트용).
    /// </summary>
    public List<string>? AllowedEmailDomains { get; set; }

    /// <summary>예전 설정(도메인 하나) 호환용. AllowedEmailDomains가 없을 때만 쓴다.</summary>
    public string? AllowedEmailDomain { get; set; }

    /// <summary>"*" 포함 여부 = 모든 도메인 허용.</summary>
    public bool AnyEmailDomain => GetRawDomains().Any(d => d == "*");

    /// <summary>정리된 허용 도메인 목록: 앞의 @ 제거, 소문자, 중복 제거, "*" 제외.</summary>
    public List<string> EmailDomains => GetRawDomains()
        .Where(d => d != "*")
        .Distinct()
        .ToList();

    private IEnumerable<string> GetRawDomains()
    {
        IEnumerable<string> raw = AllowedEmailDomains is { Count: > 0 }
            ? AllowedEmailDomains
            : string.IsNullOrWhiteSpace(AllowedEmailDomain) ? ["company.kr"] : [AllowedEmailDomain];
        var cleaned = raw.Select(d => d.Trim().TrimStart('@').ToLowerInvariant()).Where(d => d.Length > 0).ToList();
        return cleaned.Count > 0 ? cleaned : ["company.kr"]; // 전부 빈칸이면 기본값
    }

    /// <summary>
    /// true면 가입 즉시 사용 가능(ACTIVE), false면 관리자 승인 대기(PENDING).
    /// 관리자 기능(6단계)을 만들기 전까지는 true로 둔다.
    /// </summary>
    public bool AutoApprove { get; set; } = true;

    /// <summary>비밀번호 연속 실패 허용 횟수 (SC-02).</summary>
    public int MaxLoginAttempts { get; set; } = 5;

    /// <summary>연속 실패 시 잠금 시간(분).</summary>
    public int LockMinutes { get; set; } = 10;

    /// <summary>이 시간(초) 동안 아무 패킷도 보내지 않은 연결을 끊는다(하트비트). 클라이언트는 15초마다 Ping을 보낸다.</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = ChatApp.Common.Protocol.Heartbeat.TimeoutSeconds;

    /// <summary>
    /// 받은 파일을 저장할 폴더. 상대 경로면 서버 exe 폴더 기준.
    /// 실제 파일은 "uploads/202610/파일ID" 처럼 원래 이름 대신 ID로 저장한다(이름으로 인한 경로 조작 방지).
    /// </summary>
    public string UploadFolder { get; set; } = "uploads";

    /// <summary>파일 1개 최대 크기(MB).</summary>
    public int MaxFileSizeMB { get; set; } = 3072; // 3GB

    public long MaxFileSizeBytes => Math.Max(1, MaxFileSizeMB) * 1024L * 1024L;

    public string UploadRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, UploadFolder));

    /// <summary>TLS(암호화 통신) 설정. appsettings.json에 "Tls" 항목이 없으면 기본값(켜짐)을 쓴다.</summary>
    // [왜] 기본값을 "켜짐"으로: 보안 기능은 깜빡하고 안 켜는 일이 많다. 끄려면 일부러 false를 적어야 하게 한다(secure by default).
    public TlsConfig Tls { get; set; } = new();

    public static ServerConfig Load(string path)
    {
        string json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        return JsonSerializer.Deserialize<ServerConfig>(json, options)
               ?? throw new InvalidDataException("appsettings.json 내용이 비어 있습니다.");
    }
}

/// <summary>appsettings.json의 "Tls" 항목.</summary>
public sealed class TlsConfig
{
    /// <summary>true면 모든 연결을 TLS로 암호화한다. false면 예전처럼 평문(테스트·디버깅용).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 서버 인증서(.pfx) 경로. 상대 경로면 서버 exe 폴더 기준. 파일이 없으면 서버가 자체 서명 인증서를 새로 만든다.
    /// 회사에 공인/사내 CA 인증서가 있으면 그 .pfx 경로를 넣으면 된다(그러면 클라이언트에 지문 확인 창이 뜨지 않음).
    /// </summary>
    public string CertificatePath { get; set; } = "certs/server.pfx";

    /// <summary>.pfx 파일 비밀번호 (자동 생성할 때도 이 비밀번호로 저장한다).</summary>
    // [주의] .pfx 안에는 서버의 "개인 키"가 들어 있다. 이 파일이 새면 누구나 우리 서버인 척할 수 있으므로
    //        Git에 올리지 말고(.gitignore의 certs/), 서버 PC에서도 관리자만 읽을 수 있게 둔다.
    public string CertificatePassword { get; set; } = "";

    public string CertificateFullPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, CertificatePath));
}
