namespace ChatApp.Common.Models;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 역할] 관리자 기능(회원 관리·감사 로그)과 비밀번호 변경에 쓰는 데이터 모양(DTO).
// [연결 관계] 클라이언트 AdminForm/ChangePasswordForm이 요청을 만들고 → 서버 PacketHandler가 권한 확인 →
//   AdminService(회원 관리) / AuditLog(감사 로그) / AuthService(비밀번호 변경)가 처리한다.
// [왜] 회사에 파는 메신저에 꼭 필요한 기능:
//   - 퇴사자 계정을 바로 막을 수 있어야 한다(퇴사 후에도 회사 대화를 볼 수 있으면 정보 유출).
//   - "누가 언제 무엇을 했는지" 기록(감사 로그)이 있어야 보안 점검·사고 조사에 답할 수 있다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>관리자 화면의 회원 한 줄. 비밀번호 해시 같은 민감한 값은 넣지 않는다.</summary>
public sealed class AdminUserInfo
{
    /// <summary>사원번호(로그인 아이디).</summary>
    public string EmployeeNo { get; set; } = "";
    /// <summary>이메일 주소.</summary>
    public string Email { get; set; } = "";
    /// <summary>이름.</summary>
    public string Name { get; set; } = "";
    /// <summary>부서.</summary>
    public string Department { get; set; } = "";
    /// <summary>직급.</summary>
    public string Position { get; set; } = "";
    /// <summary>권한. "USER" 또는 "ADMIN".</summary>
    public string Role { get; set; } = "USER";
    /// <summary>계정 상태. PENDING(승인 대기) / ACTIVE / SUSPENDED / RESIGNED.</summary>
    public string Status { get; set; } = "ACTIVE";
    /// <summary>지금 접속 중인지 (서버 메모리의 접속자 목록 기준).</summary>
    public bool Online { get; set; }
    /// <summary>로그인 실패로 잠겨 있으면 남은 분, 아니면 0.</summary>
    public int LockMinutesLeft { get; set; }
    /// <summary>계정을 만든 시각(UTC).</summary>
    public DateTime CreatedAt { get; set; }
    /// <summary>마지막으로 로그인한 시각(UTC). 한 번도 로그인하지 않았으면 null.</summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>상태를 사람이 읽는 말로.</summary>
    public static string StatusText(string status) => status switch
    {
        "PENDING" => "승인 대기",
        "ACTIVE" => "사용 중",
        "SUSPENDED" => "정지",
        "RESIGNED" => "퇴사",
        _ => status,
    };
}

/// <summary>관리자가 할 수 있는 동작.</summary>
public enum AdminActionType
{
    /// <summary>가입 승인 (PENDING → ACTIVE)</summary>
    Approve,
    /// <summary>가입 거절 (PENDING 계정 삭제)</summary>
    Reject,
    /// <summary>정지 (접속 중이면 바로 끊음)</summary>
    Suspend,
    /// <summary>퇴사 처리 (접속 중이면 바로 끊음, 다시 로그인 불가)</summary>
    Resign,
    /// <summary>정지·퇴사 해제 (→ ACTIVE)</summary>
    Activate,
    /// <summary>로그인 실패 잠금 풀기</summary>
    Unlock,
    /// <summary>임시 비밀번호 발급 (다음 로그인 때 반드시 바꾸게 함)</summary>
    ResetPassword,
    /// <summary>강제 로그아웃 (계정은 그대로)</summary>
    Kick,
    /// <summary>관리자 지정</summary>
    GrantAdmin,
    /// <summary>관리자 해제</summary>
    RevokeAdmin,
}

/// <summary>관리 동작 요청 (AdminActionReq의 Data).</summary>
public sealed class AdminAction
{
    /// <summary>수행할 관리 동작 종류.</summary>
    public AdminActionType Type { get; set; }
    /// <summary>동작 대상 사원번호.</summary>
    public string EmployeeNo { get; set; } = "";

    /// <summary>사유 (선택). 감사 로그에 남고, 정지·강제 로그아웃이면 대상자 화면에도 보인다.</summary>
    public string? Reason { get; set; }

    /// <summary>사유(Reason)의 최대 글자 수(100). 서버는 이보다 길면 잘라 낸다.</summary>
    public const int MaxReasonLength = 100;

    /// <summary>관리 동작을 화면에 보일 한국어 이름으로 바꿔 돌려준다(예: Approve → "가입 승인"). 모르는 값이면 enum 이름 그대로.</summary>
    public static string Text(AdminActionType type) => type switch
    {
        AdminActionType.Approve => "가입 승인",
        AdminActionType.Reject => "가입 거절",
        AdminActionType.Suspend => "정지",
        AdminActionType.Resign => "퇴사 처리",
        AdminActionType.Activate => "사용 재개",
        AdminActionType.Unlock => "잠금 해제",
        AdminActionType.ResetPassword => "비밀번호 초기화",
        AdminActionType.Kick => "강제 로그아웃",
        AdminActionType.GrantAdmin => "관리자 지정",
        AdminActionType.RevokeAdmin => "관리자 해제",
        _ => type.ToString(),
    };
}

/// <summary>관리 동작 결과 (AdminActionRes의 Data).</summary>
public sealed class AdminActionResult
{
    /// <summary>처리한 관리 동작 종류.</summary>
    public AdminActionType Type { get; set; }
    /// <summary>동작 대상 사원번호.</summary>
    public string EmployeeNo { get; set; } = "";

    /// <summary>비밀번호 초기화일 때만: 새로 만든 임시 비밀번호 (관리자가 본인에게 직접 전달).</summary>
    public string? TempPassword { get; set; }
}

/// <summary>감사 로그 조회 조건 (AdminAuditReq의 Data).</summary>
public sealed class AuditQuery
{
    /// <summary>이 사원번호가 "한 사람" 또는 "당한 사람"인 기록만. 비우면 전체.</summary>
    public string? EmployeeNo { get; set; }

    /// <summary>동작 이름(LOGIN_FAIL 등). 비우면 전체.</summary>
    public string? Action { get; set; }

    /// <summary>가져올 최대 기록 수(기본 200). 서버가 1~MaxLimit 범위로 맞춘다.</summary>
    public int Limit { get; set; } = 200;
    /// <summary>Limit의 상한(1000건).</summary>
    public const int MaxLimit = 1000;
}

/// <summary>감사 로그 한 줄.</summary>
public sealed class AuditEntry
{
    /// <summary>동작이 일어난 시각(UTC).</summary>
    public DateTime Time { get; set; }
    /// <summary>동작을 한 사람의 사원번호. 서버 콘솔이면 "CONSOLE", 시스템이면 null.</summary>
    public string? ActorNo { get; set; }
    /// <summary>동작 이름 (AuditActions 상수 또는 APPROVE 같은 관리 동작 이름).</summary>
    public string Action { get; set; } = "";
    /// <summary>동작을 당한 대상의 사원번호. 없으면 null.</summary>
    public string? TargetNo { get; set; }
    /// <summary>동작의 부가 설명(예: 사유, 방 이름). 없으면 null.</summary>
    public string? Detail { get; set; }
    /// <summary>요청한 클라이언트의 IP 주소. 없으면 null.</summary>
    public string? ClientIp { get; set; }
}

/// <summary>관리자 대시보드에 표시할 현재 서비스 지표.</summary>
public sealed class AdminStats
{
    /// <summary>상태가 ACTIVE(사용 중)인 회원 수.</summary>
    public int ActiveUsers { get; set; }
    /// <summary>승인 대기(PENDING) 회원 수.</summary>
    public int PendingUsers { get; set; }
    /// <summary>정지(SUSPENDED) 회원 수.</summary>
    public int SuspendedUsers { get; set; }
    /// <summary>지금 접속 중인 사용자 수.</summary>
    public int OnlineUsers { get; set; }
    /// <summary>닫히지 않은 채팅방 수.</summary>
    public int OpenRooms { get; set; }
    /// <summary>DB에 저장된 전체 메시지 수.</summary>
    public long Messages { get; set; }
    /// <summary>업로드를 마친 보관 파일 수.</summary>
    public long Files { get; set; }
    /// <summary>보관 파일 크기의 합(바이트).</summary>
    public long FileBytes { get; set; }
    /// <summary>이 지표를 집계한 시각(UTC).</summary>
    public DateTime CapturedAtUtc { get; set; }

    // ── 그래프용 (최근 7일). 시각은 모두 UTC "정시" — 화면에서 내 PC 시간대로 바꿔 날짜·시간대별로 묶는다.
    // [왜] 서버가 "날짜별"로 묶어 보내면 서버의 날짜 기준(UTC)으로 잘려서, 한국에서는 오전 9시에 날이 바뀐 것처럼 보인다.
    //      시간 단위로 받아서 클라이언트가 자기 시간대로 묶으면 어느 지역에서 봐도 맞다.
    /// <summary>시간별 메시지 수 (방 + 1:1, 삭제 포함).</summary>
    public List<HourCount> MessageHours { get; set; } = [];
    /// <summary>시간별 로그인한 사람 수 (같은 사람이 여러 번 로그인해도 1명).</summary>
    public List<HourCount> LoginHours { get; set; } = [];
    /// <summary>최근 7일 메시지가 많은 방 (최대 5개).</summary>
    public List<RoomActivity> TopRooms { get; set; } = [];

    // ── 저장 공간 (파일 보관 폴더가 있는 디스크)
    /// <summary>파일 보관 폴더가 있는 디스크의 남은 공간(바이트). 읽지 못했으면 0.</summary>
    public long DiskFreeBytes { get; set; }
    /// <summary>파일 보관 폴더가 있는 디스크의 전체 크기(바이트). 읽지 못했으면 0.</summary>
    public long DiskTotalBytes { get; set; }
}

/// <summary>한 시간 동안의 개수. HourUtc = 그 시간의 시작(UTC).</summary>
public sealed class HourCount
{
    /// <summary>집계 구간의 시작 시각(UTC 정시).</summary>
    public DateTime HourUtc { get; set; }
    /// <summary>그 한 시간 동안의 개수.</summary>
    public int Count { get; set; }
}

/// <summary>활발한 방 (관리 통계).</summary>
public sealed class RoomActivity
{
    /// <summary>방 번호.</summary>
    public int RoomId { get; set; }
    /// <summary>방 이름.</summary>
    public string Name { get; set; } = "";
    /// <summary>방 멤버 수.</summary>
    public int Members { get; set; }
    /// <summary>최근 7일 동안의 메시지 수(시스템 알림 제외).</summary>
    public long Messages { get; set; }
}

/// <summary>감사 로그에 남기는 동작 이름 (DB audit_logs.action, 최대 30자).</summary>
// [왜] 문자열을 곳곳에 직접 쓰면 오타("LOGIN_FAIL" / "LOGIN_FAILED")가 생겨 검색이 안 된다. 한곳에 상수로 모은다.
public static class AuditActions
{
    /// <summary>로그인 성공.</summary>
    public const string LoginOk = "LOGIN_OK";
    /// <summary>로그인 실패.</summary>
    public const string LoginFail = "LOGIN_FAIL";
    /// <summary>로그아웃.</summary>
    public const string Logout = "LOGOUT";
    /// <summary>회원가입.</summary>
    public const string Register = "REGISTER";
    /// <summary>비밀번호 변경.</summary>
    public const string PasswordChange = "PASSWORD_CHANGE";
    /// <summary>파일 올리기(보냄).</summary>
    public const string FileUpload = "FILE_UPLOAD";
    /// <summary>파일 내려받기.</summary>
    public const string FileDownload = "FILE_DOWNLOAD";
    /// <summary>방 만들기.</summary>
    public const string RoomCreate = "ROOM_CREATE";
    /// <summary>메시지 삭제. 관리자가 남의 메시지를 지움.</summary>
    public const string MessageDelete = "MESSAGE_DELETE";
    /// <summary>메시지 수정.</summary>
    public const string MessageEdit = "MESSAGE_EDIT";
    /// <summary>방 삭제. 방장·관리자가 방을 삭제함.</summary>
    public const string RoomDelete = "ROOM_DELETE";
    /// <summary>방장 변경. 위임하거나 방장이 나가서 자동으로 바뀜.</summary>
    public const string RoomOwner = "ROOM_OWNER";
    /// <summary>방 정원 변경.</summary>
    public const string RoomCapacity = "ROOM_CAPACITY";
    /// <summary>공지 (방 공지 설정·삭제 또는 서버 콘솔 공지).</summary>
    public const string Notice = "NOTICE";
    /// <summary>권한 없는 관리 시도. 관리자가 아닌데 관리 기능을 요청함.</summary>
    public const string Denied = "ADMIN_DENIED";
    /// <summary>서버 시작.</summary>
    public const string ServerStart = "SERVER_START";

    /// <summary>관리 동작 → 감사 로그 이름 (APPROVE, SUSPEND ...).</summary>
    public static string Of(AdminActionType type) => type switch
    {
        AdminActionType.ResetPassword => "RESET_PASSWORD",
        AdminActionType.GrantAdmin => "GRANT_ADMIN",
        AdminActionType.RevokeAdmin => "REVOKE_ADMIN",
        _ => type.ToString().ToUpperInvariant(),
    };

    /// <summary>화면에 보일 한국어 이름.</summary>
    public static string Text(string action) => action switch
    {
        LoginOk => "로그인",
        LoginFail => "로그인 실패",
        Logout => "로그아웃",
        Register => "회원가입",
        PasswordChange => "비밀번호 변경",
        FileUpload => "파일 보냄",
        FileDownload => "파일 받음",
        RoomCreate => "방 만듦",
        MessageDelete => "메시지 삭제(관리자)",
        MessageEdit => "메시지 수정",
        RoomDelete => "방 삭제",
        RoomOwner => "방장 변경",
        RoomCapacity => "방 정원 변경",
        Notice => "공지",
        Denied => "권한 없는 관리 시도",
        ServerStart => "서버 시작",
        "APPROVE" => "가입 승인",
        "REJECT" => "가입 거절",
        "SUSPEND" => "정지",
        "RESIGN" => "퇴사 처리",
        "ACTIVATE" => "사용 재개",
        "UNLOCK" => "잠금 해제",
        "RESET_PASSWORD" => "비밀번호 초기화",
        "KICK" => "강제 로그아웃",
        "GRANT_ADMIN" => "관리자 지정",
        "REVOKE_ADMIN" => "관리자 해제",
        _ => action,
    };

    /// <summary>화면 필터용 목록.</summary>
    public static readonly string[] All =
    [
        LoginOk, LoginFail, Logout, Register, PasswordChange, FileUpload, FileDownload, RoomCreate, RoomDelete, RoomOwner, RoomCapacity, MessageDelete, MessageEdit, Notice, Denied, ServerStart,
        "APPROVE", "REJECT", "SUSPEND", "RESIGN", "ACTIVATE", "UNLOCK", "RESET_PASSWORD", "KICK", "GRANT_ADMIN", "REVOKE_ADMIN",
    ];
}

/// <summary>비밀번호 변경 요청 (ChangePasswordReq의 Data).</summary>
public sealed class ChangePasswordRequest
{
    /// <summary>현재 비밀번호(본인 확인용 원문).</summary>
    public string CurrentPassword { get; set; } = "";
    /// <summary>새 비밀번호(원문. 서버가 해시로 바꿔 저장한다).</summary>
    public string NewPassword { get; set; } = "";
}
