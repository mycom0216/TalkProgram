using System.Text.Json;

namespace ChatApp.Server;

//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   appsettings.json의 내용을 담는 설정 상자. 포트, DB 연결, 가입 규칙, 보호 한도, 보관 기간, TLS, LDAP이 모여 있고 거의 모든 값에 "안전한 기본값"이 있다.
//
// ■ 설정 값 지도 (기본값)
//   접속: Port 9000 / MaxConnections 2000 / MaxConnectionsPerIp 50 / LoginTimeoutSeconds 300 / HeartbeatTimeoutSeconds(공통 상수)
//   보호: MaxSendQueueMB 64 / SendTimeoutSeconds 120 / MaxLoginAttempts 5 / LockMinutes 10 / RateLimitLoopback false
//   파일: UploadFolder "uploads" / MaxFileSizeMB 3072(3GB)
//   보관: 대화 365일 / 파일 180일 / 감사 730일 / 닫힌 방 파일 0일(-1 = 안 지움, 나머지 항목은 0 = 안 지움)
//   보안: AutoApprove false(가입 후 관리자 승인) / Tls.Enabled true(기본 켜짐) / Ldap.Enabled false
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 설정 파일 역직렬화(JSON → 객체) .............. "System.Text.Json Deserialize options"
//   - secure by default(기본값을 안전하게) ......... "secure by default configuration"
//   - 계산 속성(=> 식)과 호환 유지 ................. "C# expression-bodied property backward compatibility config"
//   - 비밀 값(DB 비밀번호, 인증서 개인 키) 관리 ..... "secrets management do not commit appsettings"
//   - 서버 보호(DoS 방어) 한도 ..................... "connection limit rate limit DoS protection"
//
// ■ 면접 예상 질문과 답
//   Q1. 설정을 코드에 상수로 두지 않고 JSON 파일로 둔 이유는요?
//       A. 운영 환경마다 값이 다르기 때문입니다(DB 주소, 포트, 허용 도메인, 보관 기간). 코드를 다시 빌드하지 않고 파일만 고쳐 바꿀 수 있고, 비밀번호 같은 비밀 값은 Git에 올리지 않는 파일에만 둘 수 있습니다.
//   Q2. DB 비밀번호와 인증서 개인 키는 어떻게 관리했나요?
//       A. appsettings.json은 .gitignore로 Git에서 제외하고, 형식만 보여 주는 appsettings.example.json을 올립니다. 인증서(.pfx)에는 서버 개인 키가 들어 있어 certs/ 폴더도 Git에서 제외하고 서버 PC에서 관리자만 읽게 둡니다. (한계: 비밀번호가 평문 파일이라, 더 안전하게 하려면 환경 변수나 Windows 자격 증명 저장소·시크릿 매니저를 씁니다.)
//   Q3. "secure by default"를 어떻게 적용했나요?
//       A. 보안 기능은 깜빡하고 안 켜는 일이 많아서 기본값을 안전한 쪽으로 정했습니다. TLS는 기본 켜짐(끄려면 일부러 false), 가입은 기본 관리자 승인 필요(AutoApprove false), 로그인 실패 5회 잠금이 기본이며, 같은 PC의 속도 제한 면제는 기본 꺼짐입니다.
//   Q4. 설정 항목이 빠져 있거나 옛날 형식이면 어떻게 되나요?
//       A. 모든 속성에 기본값이 있어서 항목이 없으면 기본값을 씁니다. 옛 설정(AllowedEmailDomain 하나)도 호환해서 새 설정(AllowedEmailDomains 목록)이 없을 때만 읽고, 둘 다 없거나 비어 있으면 기본 도메인을 씁니다. 대소문자 무시·주석·끝 쉼표 허용 옵션으로 사람이 편집하다 생기는 실수도 받아 줍니다.
//   Q5. MaxConnections, MaxConnectionsPerIp 같은 한도를 두는 이유는요?
//       A. 접속마다 스레드 2개(수신, 송신)가 생기므로 한도가 없으면 연결만 수천 개 열어도 스레드·메모리가 바닥납니다. 전체 2,000개와 IP당 50개로 막고, 로그인하지 않은 연결은 300초 뒤 끊고, 받지 않는 클라이언트는 송신 큐 64MB 또는 쓰기 120초 제한으로 끊습니다.
//   Q6. 한계는요? (솔직하게 답하기)
//       A. (1) 값 검증이 부족하다 — 예를 들어 Port에 음수를 쓰거나 한도에 0을 쓰는 실수를 시작할 때 걸러 주지 않고, 일부 값만 Math.Max로 보정한다. 시작할 때 설정 값을 검사하는 Validate가 있으면 좋다. (2) 설정 변경은 서버를 다시 시작해야 반영된다(핫 리로드 없음).
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 이메일 도메인 때문에 가입이 막힌다 ....... AllowedEmailDomains(앞의 @ 제거·소문자 정리됨), "*"면 제한 없음
//   - 가입 후 로그인이 안 된다 ................. AutoApprove false면 관리자 승인 대기(PENDING)가 정상
//   - 큰 파일이 거절된다 ....................... MaxFileSizeMB(기본 3072), 클라이언트에 보내는 ServerInfo.MaxFileSize
//   - 설정을 바꿨는데 그대로다 ................. 서버 재시작 필요, 실행 폴더(bin\...)의 appsettings.json을 고쳤는지(프로젝트 폴더 것이 아님)
//   - 시작이 "내용이 비어 있습니다"로 실패 ...... appsettings.json이 빈 파일이거나 JSON 형식 오류(쉼표·따옴표)
// ───────────────────────────────────────────────────────────────────────────

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
    // [읽는 법] "=>"로 쓴 읽기 전용 계산 속성. 값을 저장하지 않고 부를 때마다 계산한다. 목록에 "*"가 하나라도 있으면 true — 도메인 제한 없음(테스트용).
    public bool AnyEmailDomain => GetRawDomains().Any(d => d == "*");

    /// <summary>정리된 허용 도메인 목록: 앞의 @ 제거, 소문자, 중복 제거, "*" 제외.</summary>
    public List<string> EmailDomains => GetRawDomains()
        .Where(d => d != "*")
        .Distinct()
        .ToList();

    // [읽는 법] 정리 순서: ① 새 설정(목록)이 있으면 그것을, 없으면 옛 설정(하나)을, 그것도 없으면 기본 "company.kr" ② 각 항목의 공백·앞의 @ 제거, 소문자로 통일, 빈 항목 제거 ③ 전부 비었으면 다시 기본값. 사람이 JSON을 손으로 고쳐도 모양이 달라 가입이 막히는 일을 줄인다.
    //           삼항 연산자가 두 겹이라 어렵게 보이지만 "목록 있음? 목록 : (옛 설정 비었음? 기본값 : 옛 설정)"으로 읽으면 된다.
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
    /// [왜] 기본값 false: 사내 메신저는 아무나 가입해서 바로 회사 대화를 보면 안 된다(관리자 창에서 승인).
    /// </summary>
    // [읽는 법] 값을 안 쓰면 bool의 기본값 false. 즉 설정 파일에 적지 않으면 "관리자 승인 필요"가 되어 안전한 쪽으로 동작한다.
    public bool AutoApprove { get; set; }

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

    // [읽는 법] MB → 바이트 변환. Math.Max(1, ...)로 0이나 음수를 쓴 실수를 최소 1MB로 보정한다. 1024L의 L은 long(64비트)이라는 표시 — int로 곱하면 3GB(약 32억)가 int 최대값(약 21억)을 넘어 계산이 틀어진다.
    public long MaxFileSizeBytes => Math.Max(1, MaxFileSizeMB) * 1024L * 1024L;

    // [읽는 법] 상대 경로("uploads")를 "서버 exe가 있는 폴더 기준 절대 경로"로 바꾼다. 실행 위치(현재 폴더)가 달라도 항상 같은 곳에 저장된다. FileService.FullPath의 경로 조작 방어가 이 값을 기준(_root)으로 삼는다.
    public string UploadRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, UploadFolder));

    // ───────── 서버 보호 (DoS 방어) ─────────

    /// <summary>동시에 연결할 수 있는 최대 수 (로그인 전 포함). 넘으면 새 연결을 바로 끊는다.</summary>
    // [왜] 접속마다 스레드 2개가 생기므로, 제한이 없으면 연결만 수천 개 열어도 서버 스레드·메모리가 바닥난다.
    // [읽는 법] 서버 보호 한도 묶음의 시작. 이 값들은 ChatServer가 읽어 쓴다(접속 수·IP별 수 확인, 로그인 제한 시간, 그리고 ClientSession을 만들 때 송신 큐 크기·쓰기 제한 시간을 넘겨 준다).
    public int MaxConnections { get; set; } = 2000;

    /// <summary>IP 하나에서 동시에 열 수 있는 최대 연결 수. 127.0.0.1(같은 PC, 부하 테스트)은 제외.</summary>
    public int MaxConnectionsPerIp { get; set; } = 50;

    /// <summary>접속 후 이 시간(초) 안에 로그인하지 않으면 끊는다. 회원가입 화면을 채우는 시간을 고려해 넉넉히 둔다.</summary>
    public int LoginTimeoutSeconds { get; set; } = 300;

    /// <summary>한 사람에게 보내려고 쌓아 둘 수 있는 최대 데이터(MB). 넘으면 "받지 않는 클라이언트"로 보고 끊는다.</summary>
    public int MaxSendQueueMB { get; set; } = 64;

    /// <summary>소켓 쓰기 제한 시간(초). 상대가 전혀 받지 않으면 이 시간 뒤 끊는다(송신 스레드가 영원히 멈추지 않게).</summary>
    public int SendTimeoutSeconds { get; set; } = 120;

    /// <summary>false면 같은 PC(127.0.0.1)에서 오는 로그인·가입은 속도 제한을 하지 않는다(부하 테스트·개발용). 운영 서버도 보통 false로 둔다.</summary>
    // [읽는 법] false(기본)면 같은 PC(127.0.0.1)에서 오는 로그인·가입은 속도 제한 대상에서 빠진다(부하 테스트·개발 편의). 실제 사용자가 다른 PC에서 오는 운영에서는 IP별 제한이 그대로 적용된다.
    public bool RateLimitLoopback { get; set; }

    // ───────── 보관 기간 (0 = 지우지 않음) ─────────

    /// <summary>대화(메시지) 보관 일수. 지난 것은 하루 한 번 지운다. 회사 규정에 맞춰 정한다.</summary>
    public int MessageRetentionDays { get; set; } = 365;

    /// <summary>주고받은 파일 보관 일수. 지난 파일은 서버 디스크에서도 지운다.</summary>
    public int FileRetentionDays { get; set; } = 180;

    /// <summary>
    /// 삭제된 방(또는 마지막 멤버가 나가 닫힌 방)의 파일을 디스크에서 지우기까지의 일수. 0 = 다음 정리 때 바로, -1 = 지우지 않음.
    /// [왜] 닫힌 방의 파일은 아무도 받을 수 없는데 디스크만 차지한다. 대화 기록(감사용)은 남기고 파일 원본만 지운다.
    /// </summary>
    public int ClosedRoomFileDays { get; set; } = 0;

    /// <summary>감사 로그 보관 일수. 보안 점검 대비로 대화보다 길게 둔다.</summary>
    public int AuditRetentionDays { get; set; } = 730;

    /// <summary>배포한 최신 클라이언트 버전(예: "1.3.0"). 이보다 낮은 클라이언트는 로그인 후 "새 버전" 안내를 본다.</summary>
    public string ClientLatestVersion { get; set; } = "";

    /// <summary>설치 파일 위치(예: \\fileserver\share\사내톡-설치.exe 또는 https://...). 안내를 누르면 연다.</summary>
    public string ClientUpdateUrl { get; set; } = "";

    /// <summary>TLS(암호화 통신) 설정. appsettings.json에 "Tls" 항목이 없으면 기본값(켜짐)을 쓴다.</summary>
    // [왜] 기본값을 "켜짐"으로: 보안 기능은 깜빡하고 안 켜는 일이 많다. 끄려면 일부러 false를 적어야 하게 한다(secure by default).
    // [읽는 법] JSON에 "Tls" 항목이 없어도 null이 아니라 기본 객체(켜짐)가 들어 있다. null 확인 없이 config.Tls.Enabled를 바로 쓸 수 있고, 빠뜨렸을 때 암호화가 꺼지지 않는다.
    public TlsConfig Tls { get; set; } = new();

    /// <summary>선택적 Active Directory/LDAP 인증 설정. LDAPS(SSL)는 항상 사용한다.</summary>
    public LdapConfig Ldap { get; set; } = new();

    public static ServerConfig Load(string path)
    {
        string json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            // [읽는 법] JSON 역직렬화 옵션 세 가지: 이름의 대소문자 무시(port와 Port 같음), 주석(// ...) 허용 — 설정 파일에 설명을 적을 수 있다, 마지막 쉼표 허용 — 줄을 추가·삭제하다 남는 쉼표 때문에 서버가 안 켜지는 일을 막는다.
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        ServerConfig config = JsonSerializer.Deserialize<ServerConfig>(json, options)
               // [읽는 법] Deserialize는 파일 내용이 JSON의 null일 때 null을 돌려준다. ?? throw는 "null이면 예외를 던진다"는 한 줄 표현이다. (JSON 문법 오류는 JsonException으로 따로 발생한다.)
               ?? throw new InvalidDataException("appsettings.json 내용이 비어 있습니다.");
        ApplyEnvironmentOverrides(config, Environment.GetEnvironmentVariable);
        return config;
    }

    /// <summary>환경 변수에 비밀 값이 있으면 appsettings.json의 값보다 우선한다. 이름: CHATAPP_CONNECTION_STRING, CHATAPP_TLS_CERT_PASSWORD.</summary>
    // [왜] DB 비밀번호와 인증서 비밀번호를 파일에 평문으로 두지 않고 서비스 계정의 환경 변수(서비스 레지스트리 Environment 값)로 줄 수 있게 한다.
    //      환경 변수가 없으면 아무것도 바꾸지 않으므로 기존 appsettings.json 방식도 그대로 동작한다.
    // [읽는 법] 값을 읽는 함수를 매개변수로 받아서, 테스트가 진짜 환경 변수를 건드리지 않고 가짜 값으로 확인할 수 있다.
    public static void ApplyEnvironmentOverrides(ServerConfig config, Func<string, string?> getVariable)
    {
        if (getVariable("CHATAPP_CONNECTION_STRING") is { Length: > 0 } connection) config.ConnectionString = connection;
        if (getVariable("CHATAPP_TLS_CERT_PASSWORD") is { Length: > 0 } certPassword) config.Tls.CertificatePassword = certPassword;
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
    // [읽는 법] 비밀번호의 기본값이 빈 문자열이라 설정을 안 해도 만들어지지만, 운영에서는 반드시 값을 넣고 이 파일(appsettings.json)을 Git에 올리지 않아야 한다.
    public string CertificatePassword { get; set; } = "";

    public string CertificateFullPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, CertificatePath));
}

public sealed class LdapConfig
{
    public bool Enabled { get; set; }
    public string Server { get; set; } = "";
    public int Port { get; set; } = 636;
    public string UserPrincipalNameSuffix { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 5;
    /// <summary>선택. 설정하면 로그인 성공 후 허용된 프로필 필드만 동기화한다. 계정 자동 생성/권한 부여는 하지 않는다.</summary>
    public string ProfileBaseDn { get; set; } = "";
    public string EmployeeNumberAttribute { get; set; } = "sAMAccountName";
    public string DisplayNameAttribute { get; set; } = "displayName";
    public string DepartmentAttribute { get; set; } = "department";
    public string PositionAttribute { get; set; } = "title";
}
