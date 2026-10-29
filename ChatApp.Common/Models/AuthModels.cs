using System.Text.Json.Serialization;

namespace ChatApp.Common.Models;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 역할] 회원가입·로그인에 쓰는 데이터 모양(DTO)들.
// [개념] DTO(Data Transfer Object): 로직 없이 "주고받을 값"만 담는 클래스.
//   Packet.WithData(obj)로 JSON이 되어 Packet.Data 칸에 실려 가고, 받는 쪽은 GetData<T>()로 다시 객체로 만든다.
// [연결 관계] 클라이언트 LoginForm/RegisterForm이 요청을 만들고 → 서버 AuthService가 검사 →
//   성공하면 서버가 UserInfo를 돌려준다. UserInfo는 접속자 목록, 1:1 창 제목 등 곳곳에서 쓰인다.
// [개념] { get; set; } = ""; : 기본값을 빈 문자열로 둬서 null 걱정 없이 쓰게 한다.
//   JSON 역직렬화는 "매개변수 없는 생성자로 만든 뒤 속성에 값 넣기" 방식이라 public set이 필요하다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>회원가입 요청 (클라이언트 → 서버, Packet.Data).</summary>
// [주의] 여기 담긴 값은 사용자가 마음대로 보낼 수 있으므로 서버(AuthService.Register)가 형식을 다시 검사한다.
//   화면(클라이언트)의 검사는 "편의"일 뿐 보안 검사가 아니다.
public sealed class RegisterRequest
{
    public string EmployeeNo { get; set; } = "";  // 사원번호 = 로그인 아이디
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Department { get; set; } = "";  // 부서 (접속자 목록을 부서별로 묶는 데 사용)
    public string Position { get; set; } = "";    // 직급 (표시 이름 "홍길동 대리")
    // [주의] 비밀번호는 패킷 안에서는 원문이다. 네트워크 구간은 TLS가 암호화하고(TlsHelper),
    //   서버는 그대로 저장하지 않고 해시(PasswordHasher)로 바꿔 저장한다.
    public string Password { get; set; } = "";

    /// <summary>휴대폰 번호 (선택). 친구가 번호로 나를 찾을 때만 쓰고, 다른 사람에게 보여 주지 않는다.</summary>
    public string? Phone { get; set; }

    /// <summary>클라이언트의 프로토콜 버전 (ProtocolInfo.Version). 옛 클라이언트는 이 칸이 없어 0으로 온다.</summary>
    // [주의] 기본값을 ProtocolInfo.Version으로 두면 안 된다. JSON에 칸이 없으면 기본값이 그대로 남아,
    //   버전을 모르는 옛 클라이언트도 "최신"으로 보이게 된다. 그래서 0으로 두고 보내는 쪽이 직접 채운다.
    public int ProtocolVersion { get; set; }
}

/// <summary>로그인 요청 (클라이언트 → 서버, Packet.Data).</summary>
public sealed class LoginRequest
{
    public string EmployeeNo { get; set; } = "";
    public string Password { get; set; } = "";
    public string DeviceId { get; set; } = "";

    /// <summary>클라이언트의 프로토콜 버전 (RegisterRequest와 같은 이유로 기본값 0).</summary>
    public int ProtocolVersion { get; set; }
}

/// <summary>비밀번호 대신 서버 발급 토큰으로 로그인한다.</summary>
public sealed class TokenLoginRequest
{
    public string EmployeeNo { get; set; } = "";
    public string Token { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public int ProtocolVersion { get; set; }
}

/// <summary>
/// 로그인한 사용자 정보. 로그인 응답과 접속자 목록에 쓰인다.
/// 비밀번호 해시 같은 민감한 값은 절대 넣지 않는다.
/// </summary>
// [왜] 서버 내부의 UserRecord(DB 한 행, 해시·솔트 포함)와 따로 둔다. 밖으로 보내는 클래스에 민감한 칸이
//   아예 없으면 실수로 유출될 일이 없다.
public sealed class UserInfo
{
    public string EmployeeNo { get; set; } = "";
    public string Name { get; set; } = "";
    public string Department { get; set; } = "";
    public string Position { get; set; } = "";
    /// <summary>USER 또는 ADMIN. 클라이언트는 ADMIN일 때만 관리자 메뉴를 보여 준다(실제 권한 확인은 서버가 매번 한다).</summary>
    public string Role { get; set; } = "USER";

    /// <summary>
    /// true = 관리자가 비밀번호를 초기화해서 "임시 비밀번호"로 로그인한 상태. 비밀번호를 바꾸기 전에는 채팅을 쓸 수 없다.
    /// 로그인 응답(LoginRes)에서만 의미가 있다.
    /// </summary>
    public bool MustChangePassword { get; set; }

    [JsonIgnore]
    public bool IsAdmin => Role == "ADMIN";

    /// <summary>접속 상태 (온라인·자리 비움·회의 중·방해 금지). 로그인하면 온라인.</summary>
    public Presence Presence { get; set; } = Presence.Online;

    /// <summary>상태 메시지 (예: "오후 반차"). DB users.status_message에 저장돼 다음 로그인에도 남는다.</summary>
    public string StatusMessage { get; set; } = "";

    /// <summary>
    /// 상태만 바꾼 복사본. [스레드] 다른 스레드가 같은 UserInfo를 JSON으로 만드는 중에 필드를 고치면 반쯤 바뀐 값이 보일 수 있으므로,
    /// 기존 객체를 고치지 않고 새 객체를 만들어 통째로 바꿔 끼운다(참조 대입은 원자적).
    /// </summary>
    public UserInfo WithStatus(Presence presence, string statusMessage) => new()
    {
        EmployeeNo = EmployeeNo, Name = Name, Department = Department, Position = Position, Role = Role,
        Presence = presence, StatusMessage = statusMessage,
    };

    /// <summary>권한만 바꾼 복사본 (관리자 지정·해제가 접속 중인 사람에게 바로 적용되게). 이유는 WithStatus와 같다.</summary>
    public UserInfo WithRole(string role) => new()
    {
        EmployeeNo = EmployeeNo, Name = Name, Department = Department, Position = Position, Role = role,
        Presence = Presence, StatusMessage = StatusMessage,
    };

    /// <summary>채팅에 표시할 이름. 예: "홍길동 대리"</summary>
    // [개념] [JsonIgnore]: JSON으로 보낼 때 이 속성은 빼라는 표시. Name과 Position으로 언제든 계산할 수 있으니 보낼 필요가 없다.
    // [개념] => 만 있는 속성: 값을 저장하지 않고 읽을 때마다 계산하는 "계산 속성"(get 전용).
    [JsonIgnore]
    public string DisplayName => $"{Name} {Position}";

    /// <summary>로그 출력용: "홍길동 대리(A0001)"</summary>
    public override string ToString() => $"{DisplayName}({EmployeeNo})";
}
