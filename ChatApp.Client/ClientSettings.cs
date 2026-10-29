using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace ChatApp.Client;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ClientSettings.cs
// - 역할: 클라이언트의 "작은 설정"(마지막 서버 주소·포트, 마지막 사원번호, 사원번호 기억 여부)을
//         JSON 파일로 저장했다가 다음 실행 때 다시 읽어 로그인 화면에 자동으로 채워 준다.
// - 스레드: UI 스레드에서만 사용한다(프로그램 시작 시 Load, 로그인 성공 시 Save).
// - 연결: Program.Main에서 Load() → LoginForm에 넘겨 입력칸을 채우고, 로그인 성공 시 LoginForm이 Save()를 부른다.
// [개념] JSON 설정 파일
//   - System.Text.Json의 JsonSerializer는 객체의 public 속성(get/set)을 {"Host":"...","Port":9000,...} 형태의
//     글자로 바꾸고(Serialize), 반대로 글자를 객체로 되돌린다(Deserialize). 사람이 메모장으로 열어 봐도 읽을 수 있다.
// [주의] 비밀번호는 절대 저장하지 않는다. 이 파일은 암호화되지 않은 평문이라 누구나 열어 볼 수 있기 때문이다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 마지막으로 접속한 서버 주소와 사원번호를 기억한다(다음 실행 때 자동 입력).
/// 저장 위치: %LocalAppData%\ChatApp\client.json  (비밀번호는 저장하지 않음)
/// </summary>
public sealed class ClientSettings
{
    // [개념] 속성 초기값(= "127.0.0.1" 등): 파일이 없거나 해당 항목이 빠져 있을 때 쓰이는 기본값.
    //        JSON을 읽을 때 파일에 없는 항목은 이 기본값이 그대로 남는다(예전 버전 설정 파일도 문제없이 읽힘).
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9000;
    public string LastEmployeeNo { get; set; } = "";
    /// <summary>설치별 자동 로그인 토큰을 서버에서 분리 관리하는 무작위 기기 ID.</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>로그인 화면의 "사원번호 기억" 체크 상태. false면 LastEmployeeNo를 비워 저장한다.</summary>
    public bool RememberEmployeeNo { get; set; } = true;

    /// <summary>true면 TLS(암호화)로 접속한다. 서버가 Tls.Enabled=false(평문)인 테스트 서버에 붙을 때만 끈다.</summary>
    public bool UseTls { get; set; } = true;

    /// <summary>알림을 끈 채팅방 번호. 설정은 이 PC의 client.json에 저장한다.</summary>
    public List<int> MutedRoomIds { get; set; } = [];
    public Dictionary<string, string> ProtectedLoginTokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string LoginTokenKey(string host, int port, string employeeNo) => $"{host.Trim()}:{port}:{employeeNo.Trim()}";
    public string? GetLoginToken(string host, int port, string employeeNo)
    {
        if (!ProtectedLoginTokens.TryGetValue(LoginTokenKey(host, port, employeeNo), out string? encrypted)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Encoding.UTF8.GetBytes("ChatApp.LoginToken.v1"), DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { ProtectedLoginTokens.Remove(LoginTokenKey(host, port, employeeNo)); return null; }
    }
    public void SetLoginToken(string host, int port, string employeeNo, string? token)
    {
        string key = LoginTokenKey(host, port, employeeNo);
        if (string.IsNullOrEmpty(token)) ProtectedLoginTokens.Remove(key);
        else ProtectedLoginTokens[key] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes("ChatApp.LoginToken.v1"), DataProtectionScope.CurrentUser));
    }

    /// <summary>
    /// 사용자가 "신뢰"한 서버 인증서 지문. 키 = "서버주소:포트", 값 = SHA-256 지문.
    /// SSH의 known_hosts 파일과 같은 역할: 다음 접속 때 지문이 다르면 경고하고 연결하지 않는다.
    /// </summary>
    // [왜] 지문은 공개된 값(인증서의 해시)이라 평문 파일에 저장해도 괜찮다. 비밀번호와는 다르다.
    //      다만 이 파일을 누가 몰래 고치면 가짜 서버를 믿게 될 수 있으므로, 사용자 전용 폴더(LocalAppData)에 둔다.
    public Dictionary<string, string> TrustedServers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string ServerKey(string host, int port) => $"{host.Trim()}:{port}";

    /// <summary>이 서버에 대해 저장한 지문 (없으면 null).</summary>
    public string? GetTrustedFingerprint(string host, int port) =>
        TrustedServers.TryGetValue(ServerKey(host, port), out string? fp) ? fp : null;

    public void SetTrustedFingerprint(string host, int port, string fingerprint) =>
        TrustedServers[ServerKey(host, port)] = fingerprint;

    // [개념] LocalApplicationData(%LocalAppData%, 보통 C:\Users\이름\AppData\Local)
    //   - 윈도우가 "프로그램이 사용자별 데이터를 저장하라"고 정해 둔 폴더. 쓰기 권한이 항상 있다.
    //   - exe가 있는 폴더(Program Files 등)는 관리자 권한이 없으면 쓸 수 없는 경우가 많아서 이 폴더를 쓴다.
    //   - "Local"은 회사 도메인 환경에서 다른 PC로 따라다니지(로밍) 않는 폴더라는 뜻이다.
    // [왜] 속성(=>)으로 만든 이유: 경로를 쓸 때마다 계산하지만 비용이 거의 없고, 필드 초기화 순서를 신경 쓸 필요가 없다.
    // [테스트] 환경 변수 CHATAPP_SETTINGS_DIR이 있으면 그 폴더의 client.json을 쓴다.
    //        화면 자동 테스트(ChatApp.UiTests)가 사용자의 진짜 설정(서버 주소, 신뢰한 지문, 자동 로그인)을 건드리지 않게 하려고.
    private static string FilePath =>
        Environment.GetEnvironmentVariable("CHATAPP_SETTINGS_DIR") is { Length: > 0 } dir
            ? Path.Combine(dir, "client.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatApp", "client.json");

    /// <summary>설정 파일을 읽는다. 없거나 깨져 있으면 기본값으로 시작한다(프로그램이 죽지 않게).</summary>
    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                // [개념] ?? new() : Deserialize가 null을 돌려주면(파일 내용이 "null" 등) 새 기본 객체를 쓴다.
                ClientSettings settings = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath)) ?? new();
                if (settings.DeviceId is not { Length: 32 } || !settings.DeviceId.All(Uri.IsHexDigit))
                    settings.DeviceId = Guid.NewGuid().ToString("N");
                settings.MutedRoomIds ??= [];
                settings.ProtectedLoginTokens = new Dictionary<string, string>(settings.ProtectedLoginTokens ?? [], StringComparer.OrdinalIgnoreCase);
                // [주의] JSON에서 읽은 Dictionary는 "대소문자 구분" 비교로 새로 만들어진다(위의 OrdinalIgnoreCase가 사라짐).
                //        그러면 "Server:9000"과 "server:9000"을 다른 서버로 보고 지문을 또 묻는다 → 비교 방식을 되살린다.
                settings.TrustedServers = new Dictionary<string, string>(settings.TrustedServers ?? [], StringComparer.OrdinalIgnoreCase);
                return settings;
            }
        }
        // [개념] 예외 필터(catch ... when): 조건에 맞는 예외만 잡는다.
        //   IOException(파일 사용 중 등), JsonException(내용이 깨짐), UnauthorizedAccessException(권한 없음)만 잡고,
        //   그 밖의 예상 못 한 예외(프로그램 버그)는 숨기지 않고 그대로 드러나게 한다.
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 설정 파일이 깨졌으면 기본값 사용
        }
        return new ClientSettings();
    }

    /// <summary>현재 값을 파일에 저장한다. 실패해도 프로그램 동작에는 지장이 없으므로 조용히 넘어간다.</summary>
    public void Save()
    {
        try
        {
            // [왜] 처음 실행하면 %LocalAppData%\ChatApp 폴더가 없으므로 먼저 만든다(이미 있으면 아무 일도 안 함).
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // [왜] 임시 파일에 다 쓴 뒤 바꿔치기한다(원자적 저장). 쓰는 도중 프로그램이 꺼지거나 PC 전원이 나가도
            //      반쯤 쓰인 깨진 설정 파일이 남지 않는다(깨지면 신뢰한 서버 지문까지 사라진다).
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 저장 실패는 무시(다음에 다시 입력하면 됨)
        }
    }
}
