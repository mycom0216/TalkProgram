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
    public string EmployeeNo { get; set; } = "";
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string Department { get; set; } = "";
    public string Position { get; set; } = "";
    public string Role { get; set; } = "USER";        // USER / ADMIN
    public string Status { get; set; } = "ACTIVE";    // PENDING / ACTIVE / SUSPENDED / RESIGNED
    public bool Online { get; set; }                  // 지금 접속 중인지 (서버 메모리의 접속자 목록 기준)
    public int LockMinutesLeft { get; set; }          // 로그인 실패로 잠겨 있으면 남은 분, 아니면 0
    public DateTime CreatedAt { get; set; }
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
    Approve,        // 가입 승인 (PENDING → ACTIVE)
    Reject,         // 가입 거절 (PENDING 계정 삭제)
    Suspend,        // 정지 (접속 중이면 바로 끊음)
    Resign,         // 퇴사 처리 (접속 중이면 바로 끊음, 다시 로그인 불가)
    Activate,       // 정지·퇴사 해제 (→ ACTIVE)
    Unlock,         // 로그인 실패 잠금 풀기
    ResetPassword,  // 임시 비밀번호 발급 (다음 로그인 때 반드시 바꾸게 함)
    Kick,           // 강제 로그아웃 (계정은 그대로)
    GrantAdmin,     // 관리자 지정
    RevokeAdmin,    // 관리자 해제
}

/// <summary>관리 동작 요청 (AdminActionReq의 Data).</summary>
public sealed class AdminAction
{
    public AdminActionType Type { get; set; }
    public string EmployeeNo { get; set; } = "";   // 대상 사원번호

    /// <summary>사유 (선택). 감사 로그에 남고, 정지·강제 로그아웃이면 대상자 화면에도 보인다.</summary>
    public string? Reason { get; set; }

    public const int MaxReasonLength = 100;

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
    public AdminActionType Type { get; set; }
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

    public int Limit { get; set; } = 200;
    public const int MaxLimit = 1000;
}

/// <summary>감사 로그 한 줄.</summary>
public sealed class AuditEntry
{
    public DateTime Time { get; set; }
    public string? ActorNo { get; set; }   // 한 사람 (서버 콘솔이면 "CONSOLE", 시스템이면 null)
    public string Action { get; set; } = "";
    public string? TargetNo { get; set; }  // 대상
    public string? Detail { get; set; }
    public string? ClientIp { get; set; }
}

/// <summary>감사 로그에 남기는 동작 이름 (DB audit_logs.action, 최대 30자).</summary>
// [왜] 문자열을 곳곳에 직접 쓰면 오타("LOGIN_FAIL" / "LOGIN_FAILED")가 생겨 검색이 안 된다. 한곳에 상수로 모은다.
public static class AuditActions
{
    public const string LoginOk = "LOGIN_OK";
    public const string LoginFail = "LOGIN_FAIL";
    public const string Logout = "LOGOUT";
    public const string Register = "REGISTER";
    public const string PasswordChange = "PASSWORD_CHANGE";
    public const string FileUpload = "FILE_UPLOAD";
    public const string FileDownload = "FILE_DOWNLOAD";
    public const string RoomCreate = "ROOM_CREATE";
    public const string Notice = "NOTICE";
    public const string Denied = "ADMIN_DENIED";      // 관리자가 아닌데 관리 기능을 요청함
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
        LoginOk, LoginFail, Logout, Register, PasswordChange, FileUpload, FileDownload, RoomCreate, Notice, Denied, ServerStart,
        "APPROVE", "REJECT", "SUSPEND", "RESIGN", "ACTIVATE", "UNLOCK", "RESET_PASSWORD", "KICK", "GRANT_ADMIN", "REVOKE_ADMIN",
    ];
}

/// <summary>비밀번호 변경 요청 (ChangePasswordReq의 Data).</summary>
public sealed class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
}
