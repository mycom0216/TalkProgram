using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Server.Core;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;

namespace ChatApp.Server.Services;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 관리자 기능의 "규칙". 회원 승인·정지·퇴사·잠금 해제·비밀번호 초기화·강제 로그아웃·관리자 지정.
// [연결]     두 곳에서 같은 코드를 쓴다.
//            ① 클라이언트 관리자 창 → AdminActionReq → PacketHandler(권한 확인) → Execute(actor = 관리자 사원번호)
//            ② 서버 콘솔 명령(/approve 등) → Program.cs → Execute(actor = "CONSOLE")
//            → 규칙이 한 곳에만 있어서, 화면과 콘솔이 서로 다르게 동작하는 일이 없다.
// [스레드]   관리자들의 수신 스레드와 서버 메인 스레드(콘솔)에서 동시에 불릴 수 있다.
//            관리 동작은 드물게 일어나므로 lock 하나로 "한 번에 하나씩" 처리한다(아래 _gate 설명).
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>관리 동작 결과.</summary>
public sealed record AdminOutcome(bool Success, string Message, string? TempPassword = null);

public sealed class AdminService(UserRepository users, AuditRepository auditRepository, AuthService auth,
                                 SessionManager sessions, AuditLog audit, ServerLogger log)
{
    /// <summary>서버 콘솔에서 한 동작의 "행위자" 이름.</summary>
    public const string ConsoleActor = "CONSOLE";

    // [왜] lock이 필요한 이유: "관리자가 1명만 남았는지 확인 → 해제" 사이에 다른 관리자가 동시에 같은 일을 하면
    //      둘 다 "아직 2명"을 보고 통과해서 관리자가 0명이 될 수 있다(확인 후 행동 사이의 경쟁 상태, check-then-act).
    //      관리 동작은 1초에 몇 번 일어나지도 않으므로, 전체를 lock 하나로 묶는 가장 단순한 방법을 쓴다.
    private readonly object _gate = new();

    /// <summary>회원 목록 + 지금 접속 중인지.</summary>
    public List<AdminUserInfo> ListUsers(string? status)
    {
        string? filter = status is "PENDING" or "ACTIVE" or "SUSPENDED" or "RESIGNED" ? status : null;
        List<AdminUserInfo> list = users.ListUsers(filter);
        foreach (AdminUserInfo u in list) u.Online = sessions.FindByEmployeeNo(u.EmployeeNo) is not null;
        return list;
    }

    /// <summary>감사 로그 검색 (최근 것부터).</summary>
    public List<AuditEntry> QueryAudit(AuditQuery query) => auditRepository.Query(query);

    /// <summary>관리 동작 실행. actor = 관리자 사원번호 또는 ConsoleActor.</summary>
    public AdminOutcome Execute(AdminAction action, string actor, string? actorIp)
    {
        string no = action.EmployeeNo.Trim();
        string? reason = action.Reason?.Trim();
        if (reason is { Length: 0 }) reason = null;
        if (reason is { Length: > AdminAction.MaxReasonLength }) reason = reason[..AdminAction.MaxReasonLength];
        string what = AdminAction.Text(action.Type);

        if (no.Length == 0) return new(false, "대상 사원번호가 없습니다.");
        if (!Enum.IsDefined(action.Type)) return new(false, "알 수 없는 동작입니다.");

        lock (_gate)
        {
            UserRecord? target = users.FindByEmployeeNo(no);
            if (target is null) return new(false, $"사원번호 '{no}'인 계정이 없습니다.");

            // ① 자기 자신에게 하면 안 되는 동작 (실수로 내 계정을 막아 아무도 관리 못 하게 되는 일 방지)
            if (no == actor && action.Type is AdminActionType.Suspend or AdminActionType.Resign
                                          or AdminActionType.RevokeAdmin or AdminActionType.Kick or AdminActionType.Reject)
                return new(false, $"자기 자신에게는 '{what}'을(를) 할 수 없습니다.");

            // ② 마지막 관리자는 정지·퇴사·해제할 수 없다
            if (target is { Role: "ADMIN", Status: "ACTIVE" }
                && action.Type is AdminActionType.Suspend or AdminActionType.Resign or AdminActionType.RevokeAdmin
                && users.CountActiveAdmins() <= 1)
                return new(false, "마지막 관리자입니다. 다른 사람을 먼저 관리자로 지정하세요.");

            AdminOutcome outcome = Apply(action.Type, target, reason);
            if (!outcome.Success) return outcome;

            // ③ 성공한 동작만 감사 로그에 남긴다. (임시 비밀번호 자체는 절대 기록하지 않는다)
            audit.Record(AuditActions.Of(action.Type), actor, no, reason, actorIp);
            log.Info($"관리: {what} {target.Name}({no}) by {actor}{(reason is null ? "" : $" - {reason}")}");
            return outcome;
        }
    }

    /// <summary>동작별 실제 처리 (_gate 안에서만 부른다).</summary>
    private AdminOutcome Apply(AdminActionType type, UserRecord target, string? reason)
    {
        string no = target.EmployeeNo;
        string name = $"{target.Name}({no})";
        switch (type)
        {
            case AdminActionType.Approve:
                return users.SetStatus(no, "ACTIVE", fromStatus: "PENDING")
                    ? new(true, $"{name} 가입을 승인했습니다.")
                    : new(false, $"{name}은(는) 승인 대기 중인 계정이 아닙니다.");

            case AdminActionType.Reject:
                return users.DeletePending(no)
                    ? new(true, $"{name} 가입 신청을 거절(삭제)했습니다.")
                    : new(false, $"{name}은(는) 승인 대기 중인 계정이 아닙니다.");

            case AdminActionType.Suspend:
                if (!users.SetStatus(no, "SUSPENDED")) return new(false, $"{name}은(는) 이미 정지된 계정입니다.");
                // [왜] DB만 바꾸면 지금 접속해 있는 사람은 계속 채팅할 수 있다. 접속 중이면 바로 끊는다.
                Disconnect(no, $"관리자가 계정을 정지했습니다.{(reason is null ? "" : $" (사유: {reason})")}");
                return new(true, $"{name} 계정을 정지했습니다.");

            case AdminActionType.Resign:
                if (!users.SetStatus(no, "RESIGNED")) return new(false, $"{name}은(는) 이미 퇴사 처리된 계정입니다.");
                Disconnect(no, "퇴사 처리되어 더 이상 사용할 수 없는 계정입니다.");
                return new(true, $"{name} 퇴사 처리했습니다. (대화 기록은 남고, 로그인만 막힙니다)");

            case AdminActionType.Activate:
                if (target.Status is not ("SUSPENDED" or "RESIGNED"))
                    return new(false, $"{name}은(는) 정지·퇴사 상태가 아닙니다. (지금: {AdminUserInfo.StatusText(target.Status)})");
                users.SetStatus(no, "ACTIVE");
                return new(true, $"{name} 계정을 다시 쓸 수 있게 했습니다.");

            case AdminActionType.Unlock:
                users.Unlock(no);
                return new(true, $"{name} 로그인 잠금을 풀었습니다.");

            case AdminActionType.ResetPassword:
                if (target.AuthSource != "LOCAL") return new(false, "사내 계정(LDAP) 사용자는 여기서 비밀번호를 바꿀 수 없습니다.");
                string? temp = auth.ResetPassword(no);
                return temp is null
                    ? new(false, "비밀번호를 초기화하지 못했습니다.")
                    : new(true, $"{name} 임시 비밀번호를 발급했습니다. 본인에게 직접 전달하세요. (다음 로그인 때 새 비밀번호로 바꿔야 합니다)", temp);

            case AdminActionType.Kick:
                return Disconnect(no, $"관리자가 연결을 끊었습니다.{(reason is null ? "" : $" (사유: {reason})")}")
                    ? new(true, $"{name}을(를) 로그아웃시켰습니다.")
                    : new(false, $"{name}은(는) 접속 중이 아닙니다.");

            case AdminActionType.GrantAdmin:
            case AdminActionType.RevokeAdmin:
                string role = type == AdminActionType.GrantAdmin ? "ADMIN" : "USER";
                if (!users.SetRole(no, role))
                    return new(false, role == "ADMIN" ? $"{name}은(는) 이미 관리자입니다." : $"{name}은(는) 관리자가 아닙니다.");
                // 접속 중이면 서버 쪽 권한을 바로 바꾼다 (해제는 즉시 효과, 클라이언트 메뉴는 다음 로그인 때 바뀜)
                if (sessions.FindByEmployeeNo(no) is { User: not null } s)
                {
                    s.User = s.User.WithRole(role);
                    s.Send(Packet.Create(PacketType.SystemMsg, role == "ADMIN"
                        ? "관리자로 지정되었습니다. 다시 로그인하면 관리자 메뉴가 보입니다."
                        : "관리자 권한이 해제되었습니다."));
                }
                return new(true, role == "ADMIN" ? $"{name}을(를) 관리자로 지정했습니다." : $"{name}의 관리자 권한을 해제했습니다.");
        }
        return new(false, "알 수 없는 동작입니다.");
    }

    /// <summary>접속 중이면 이유를 보내고 끊는다. 끊었으면 true.</summary>
    // [왜] ForceLogout으로 끊는다: 클라이언트는 이 패킷을 받으면 자동 재접속을 하지 않는다(정지된 사람이 계속 재접속 시도하지 않게).
    private bool Disconnect(string employeeNo, string reason)
    {
        if (sessions.FindByEmployeeNo(employeeNo) is not ClientSession session) return false;
        session.SendAndClose(Packet.Create(PacketType.ForceLogout, reason));
        return true;
    }

    /// <summary>새 가입 신청을 접속 중인 관리자들에게 알린다 (자동 승인이 꺼져 있을 때).</summary>
    public void NotifyAdmins(string message)
    {
        Packet notify = Packet.Create(PacketType.AdminNotify, message);
        foreach (ClientSession s in sessions.GetAllSessions())
            if (s.User is { IsAdmin: true }) s.Send(notify);
    }
}
