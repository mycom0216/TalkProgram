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
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   관리자가 하는 일(승인·거절·정지·퇴사·복구·잠금 해제·비밀번호 초기화·강제 로그아웃·관리자 지정)의 "규칙"을 한 곳(Execute)에 모았다.
//   화면(관리자 창)과 서버 콘솔(/approve 등)이 같은 Execute를 쓰므로 둘이 다르게 동작할 수 없다. 이 파일은 "누가" 권한이 있는지는 보지 않는다 — 권한 확인은 PacketHandler가 먼저 한다.
//
// ■ Execute의 처리 순서 (여기만 이해하면 된다)
//   ① 입력 정리(공백 제거, 사유 100자로 자르기, 동작 종류가 정의된 값인지) → ② lock(_gate) 안에서 대상 사용자 조회
//   ③ "하면 안 되는 동작" 막기: 자기 자신을 정지·퇴사·관리자 해제·강제 종료·거절 / 마지막 관리자를 정지·퇴사·해제
//   ④ Apply: 동작별 실제 처리(DB 변경 + 필요하면 접속 중인 사람 끊기) → ⑤ 성공한 것만 감사 로그에 기록(임시 비밀번호는 기록하지 않음)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 확인 후 행동(check-then-act) 경쟁 상태와 lock ........ "check-then-act race condition lock"
//   - 최소 권한과 감사 로그(누가·언제·무엇을) .............. "audit log who did what when"
//   - 강제 로그아웃(세션 무효화) ............................ "force logout session invalidation"
//   - 상태 전이(PENDING → ACTIVE → SUSPENDED ...) .......... "state machine account status"
//   - 상태 조건부 UPDATE (fromStatus) ....................... "conditional update optimistic concurrency"
//   - 원시 문자열 리터럴 """...""" ........................... "C# raw string literals"
//
// ■ 면접 예상 질문과 답
//   Q1. 관리자가 한 명만 남았을 때 그 사람이 스스로 정지되는 것을 어떻게 막았나요?
//       A. 정지·퇴사·관리자 해제 때 "대상이 활성 관리자이고 활성 관리자 수가 1 이하"면 거절합니다. 또 자기 자신을 정지·퇴사·해제·강제 종료하는 것도 막습니다. 마지막 관리자를 막으면 아무도 시스템을 관리할 수 없게 되는 사고를 방지합니다.
//   Q2. 그 확인이 동시에 두 관리자에게서 오면 뚫리지 않나요?
//       A. 맞습니다. "관리자 수 확인 → 해제"는 확인 후 행동이라, 두 관리자가 동시에 서로를 해제하면 둘 다 "아직 2명"을 보고 통과해 0명이 될 수 있습니다. 그래서 Execute 전체를 lock(_gate) 하나로 감싸 한 번에 하나씩만 처리합니다. 관리 동작은 아주 드물게 일어나므로 성능 걱정이 없는 가장 단순한 방법입니다.
//   Q3. 계정을 정지했는데 이미 접속 중인 사람은요?
//       A. DB만 바꾸면 지금 접속한 사람은 계속 채팅할 수 있습니다. 그래서 정지·퇴사·비밀번호 초기화 때는 접속 중인 세션에 ForceLogout 패킷을 보내고 연결을 끊습니다(SendAndClose). 클라이언트는 이 패킷을 받으면 자동 재접속을 하지 않습니다.
//   Q4. 비밀번호 초기화 때 왜 연결을 끊나요? 임시 비밀번호는 어떻게 전달하나요?
//       A. 초기화를 하는 이유가 계정 도용인 경우가 많아서, 지금 접속해 있는 연결(도용한 사람일 수 있음)을 끊어야 의미가 있습니다. 임시 비밀번호는 DB에는 해시로만 저장하고, 원문은 결과(TempPassword)로 관리자에게 한 번만 돌려주며 감사 로그에는 남기지 않습니다. 다음 로그인 때 바꾸도록 강제합니다.
//   Q5. "승인"을 상태 확인 없이 UPDATE하면 안 되나요? (fromStatus의 의미)
//       A. 이미 정지된 계정을 "승인"으로 되살려 버리는 실수를 막기 위해서입니다. SetStatus(no, "ACTIVE", fromStatus: "PENDING")는 현재 상태가 PENDING일 때만 바뀌는 조건부 UPDATE라, 상태가 달라졌다면 false로 돌아와 "승인 대기 중인 계정이 아닙니다"라고 알려 줍니다.
//   Q6. 감사 로그는 왜 성공한 동작만 남기나요? 무엇을 남기고 무엇은 안 남기나요?
//       A. 실제로 일어난 변경의 기록이 목적이므로 성공한 것만 남깁니다(거절된 시도는 PacketHandler의 권한 확인 단계에서 Denied로 따로 남김). 행위자, 대상, 사유, IP를 남기고 임시 비밀번호 같은 비밀값은 절대 남기지 않습니다.
//   Q7. 서버 콘솔 명령과 관리자 화면이 같은 코드를 쓰는 이점은요?
//       A. 규칙이 한 곳에만 있어 화면과 콘솔이 서로 다르게 동작하는 일이 없고, 수정할 때도 한 곳만 고치면 됩니다. 콘솔에서 한 동작은 행위자가 "CONSOLE"로 기록되어 구분됩니다.
//   Q8. 한계는요? (솔직하게 답하기)
//       A. (1) lock이 서버 한 대 안에서만 의미가 있어 서버를 여러 대로 늘리면 DB 수준의 조건(트랜잭션)으로 바꿔야 한다. (2) 퇴사 처리는 로그인만 막고 대화 기록·파일·방 멤버 정보는 그대로 남는다(법적 보관 요건과 개인정보 삭제 요구가 충돌할 수 있어 별도 정책이 필요하다). (3) 관리자 권한 변경은 접속 중이면 서버 쪽 권한은 즉시 바뀌지만 클라이언트 메뉴는 다음 로그인 때 바뀐다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - "마지막 관리자입니다" ..................... 정상 방어. 다른 사람을 먼저 관리자로 지정(GrantAdmin) 후 해제
//   - 승인했는데 "승인 대기 중인 계정이 아닙니다" .. 이미 다른 관리자가 처리했거나 상태가 PENDING이 아님(조건부 UPDATE 결과)
//   - 정지했는데 계속 채팅한다 .................. Disconnect → sessions.FindByEmployeeNo 로 찾았는지, ForceLogout 전달 로그
//   - 통계 화면이 비어 있다 ..................... GetStats: db 인자가 null이면 예외(Program.cs에서 Db를 넘겼는지), 최근 7일 인덱스 쿼리
//   - 감사 로그에 기록이 없다 ................... Execute 성공 경로의 audit.Record, AuditWriter 스레드(큐가 비워졌는지)
// ───────────────────────────────────────────────────────────────────────────

/// <summary>관리 동작 결과.</summary>
// [읽는 법] record = 값 묶음용 불변 클래스. 동작 결과를 (성공 여부, 안내 문구, 임시 비밀번호)로 돌려준다. TempPassword는 비밀번호 초기화 때만 값이 있고 나머지는 기본값 null이다. 실패는 예외가 아니라 Success=false로 알려 준다(예상 가능한 거절).
public sealed record AdminOutcome(bool Success, string Message, string? TempPassword = null);

/// <summary>관리자 기능(회원 승인·정지·권한 변경, 접속자 조회, 감사 로그 검색, 통계)을 모은 서비스. 클라이언트 관리자 창과 서버 콘솔이 같이 쓴다.</summary>
// [읽는 법] 기본 생성자로 받는 부품: users(DB), auditRepository(감사 로그 조회), auth(비밀번호 초기화 규칙), sessions(접속 중인 사람 찾기·끊기), audit(감사 기록), log, db와 uploadRoot(통계용, 없어도 만들 수 있게 기본값 null).
public sealed class AdminService(UserRepository users, AuditRepository auditRepository, AuthService auth,
                                 SessionManager sessions, AuditLog audit, ServerLogger log, Db? db = null,
                                 string? uploadRoot = null)
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
        // [읽는 법] 화면에서 온 상태 값이 4가지 중 하나일 때만 필터로 쓰고, 아니면 null(전체)로 둔다. 값을 SQL 조건에 쓰기 전에 허용 목록으로 걸러 이상한 입력이 DB로 가지 않게 한다.
        string? filter = status is "PENDING" or "ACTIVE" or "SUSPENDED" or "RESIGNED" ? status : null;
        List<AdminUserInfo> list = users.ListUsers(filter);
        foreach (AdminUserInfo u in list) u.Online = sessions.FindByEmployeeNo(u.EmployeeNo) is not null;
        return list;
    }

    /// <summary>감사 로그 검색 (최근 것부터).</summary>
    public List<AuditEntry> QueryAudit(AuditQuery query) => auditRepository.Query(query);

    /// <summary>회원·방·메시지·파일 수와 최근 7일 시간대별 통계를 DB에서 모아 돌려준다. DB가 연결되지 않았으면 InvalidOperationException.</summary>
    public AdminStats GetStats()
    {
        if (db is null) throw new InvalidOperationException("관리 통계용 DB가 연결되지 않았습니다.");
        // [읽는 법] 통계는 한 번의 DB 연결로 여러 조회를 한다. 첫 조회는 하위 쿼리 7개를 한 문장으로 묶어 DB 왕복을 줄인다(COUNT/SUM). using var는 메서드가 끝나면(예외가 나도) 연결을 자동으로 반납한다.
        //           [참고] $""" 보간 문자열에 들어간 AuditActions.LoginOk는 사용자 입력이 아니라 코드의 상수라서 SQL 인젝션 위험이 없다. 사용자 입력은 AddParam으로 넣어야 한다.
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM users WHERE status = 'ACTIVE'),
                   (SELECT COUNT(*) FROM users WHERE status = 'PENDING'),
                   (SELECT COUNT(*) FROM users WHERE status = 'SUSPENDED'),
                   (SELECT COUNT(*) FROM rooms WHERE closed_at IS NULL),
                   (SELECT COUNT(*) FROM messages),
                   (SELECT COUNT(*) FROM files WHERE status = 'DONE'),
                   (SELECT COALESCE(SUM(file_size), 0) FROM files WHERE status = 'DONE')
            """;
        AdminStats stats;
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("관리 통계를 읽지 못했습니다.");
            stats = new AdminStats
            {
                ActiveUsers = Convert.ToInt32(reader.GetValue(0)), PendingUsers = Convert.ToInt32(reader.GetValue(1)), SuspendedUsers = Convert.ToInt32(reader.GetValue(2)),
                OnlineUsers = sessions.GetUsers().Count, OpenRooms = Convert.ToInt32(reader.GetValue(3)), Messages = Convert.ToInt64(reader.GetValue(4)),
                Files = Convert.ToInt64(reader.GetValue(5)), FileBytes = Convert.ToInt64(reader.GetValue(6)), CapturedAtUtc = DateTime.UtcNow,
            };
        }

        // [성능] 모두 sent_at / created_at 인덱스(idx_sent_at, idx_action)로 최근 7일만 훑는다. 접속 세션은 UTC(+00:00).
        // [읽는 법] 최근 7일 동안 시간대별 메시지 수. DATE_FORMAT으로 "정시(분·초를 00으로)"로 묶어 GROUP BY한다. 시스템 알림(msg_type SYSTEM)은 제외한다. 인덱스를 타는 sent_at 조건이라 전체 테이블을 훑지 않는다.
        stats.MessageHours = ReadHours(conn, """
            SELECT DATE_FORMAT(sent_at, '%Y-%m-%d %H:00:00') AS h, COUNT(*)
            FROM messages WHERE sent_at >= UTC_TIMESTAMP() - INTERVAL 7 DAY AND msg_type <> 'SYSTEM'
            GROUP BY h
            """);
        stats.LoginHours = ReadHours(conn, $"""
            SELECT DATE_FORMAT(created_at, '%Y-%m-%d %H:00:00') AS h, COUNT(DISTINCT actor_no)
            FROM audit_logs WHERE action = '{AuditActions.LoginOk}' AND created_at >= UTC_TIMESTAMP() - INTERVAL 7 DAY
            GROUP BY h
            """);

        using (var top = conn.CreateCommand())
        {
            top.CommandText = """
                SELECT r.room_id, r.room_name,
                       (SELECT COUNT(*) FROM room_members rm WHERE rm.room_id = r.room_id) AS members,
                       t.cnt
                FROM (SELECT room_id, COUNT(*) AS cnt FROM messages
                      WHERE room_id IS NOT NULL AND sent_at >= UTC_TIMESTAMP() - INTERVAL 7 DAY AND msg_type <> 'SYSTEM'
                      GROUP BY room_id ORDER BY cnt DESC LIMIT 5) t
                JOIN rooms r ON r.room_id = t.room_id
                ORDER BY t.cnt DESC
                """;
            using var r = top.ExecuteReader();
            while (r.Read())
                stats.TopRooms.Add(new RoomActivity
                {
                    RoomId = Convert.ToInt32(r.GetValue(0)), Name = r.GetString(1),
                    Members = Convert.ToInt32(r.GetValue(2)), Messages = Convert.ToInt64(r.GetValue(3)),
                });
        }

        // 파일 보관 폴더가 있는 디스크의 남은 공간
        if (uploadRoot is not null)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(uploadRoot));
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    stats.DiskFreeBytes = drive.AvailableFreeSpace;
                    stats.DiskTotalBytes = drive.TotalSize;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                log.Info($"[경고] 디스크 공간을 읽지 못했습니다: {ex.Message}");
            }
        }
        return stats;
    }

    private static List<HourCount> ReadHours(System.Data.Common.DbConnection conn, string sql)
    {
        var list = new List<HourCount>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            DateTime hour = DateTime.ParseExact(r.GetString(0), "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            list.Add(new HourCount { HourUtc = DateTime.SpecifyKind(hour, DateTimeKind.Utc), Count = Convert.ToInt32(r.GetValue(1)) });
        }
        return list;
    }

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

        // [읽는 법] 여기부터 return까지가 한 덩어리로, 다른 관리 동작은 이 덩어리가 끝나기를 기다린다. 이 안에서 DB 읽기·쓰기가 일어나지만 관리 동작은 드물어 문제가 되지 않는다. Apply는 반드시 이 lock 안에서만 부른다.
        lock (_gate)
        {
            UserRecord? target = users.FindByEmployeeNo(no);
            if (target is null) return new(false, $"사원번호 '{no}'인 계정이 없습니다.");

            // ① 자기 자신에게 하면 안 되는 동작 (실수로 내 계정을 막아 아무도 관리 못 하게 되는 일 방지)
            // [읽는 법] "내가 나에게 + 위험한 동작 5가지 중 하나"면 거절. 실수로 내 계정을 막아 관리자가 없어지는 일을 방지한다.
            // [개념] "A && x is B or C or D"에서 or는 && 같은 논리 연산자가 아니라 is 패턴 안의 결합이라, (x is B or C or D)가 하나로 묶인 뒤 && 와 결합된다. 즉 의도대로 "no == actor 이고, 동작이 이 중 하나"로 읽힌다.
            if (no == actor && action.Type is AdminActionType.Suspend or AdminActionType.Resign
                                          or AdminActionType.RevokeAdmin or AdminActionType.Kick or AdminActionType.Reject)
                return new(false, $"자기 자신에게는 '{what}'을(를) 할 수 없습니다.");

            // ② 마지막 관리자는 정지·퇴사·해제할 수 없다
            // [읽는 법] 속성 패턴으로 "대상이 활성 상태의 관리자"인지 한 번에 확인한 뒤, 정지·퇴사·해제 같은 위험 동작이고 활성 관리자가 1명 이하(CountActiveAdmins)면 거절한다. 이 확인과 아래 Apply가 같은 lock 안이라 동시 요청에도 어긋나지 않는다.
            if (target is { Role: "ADMIN", Status: "ACTIVE" }
                && action.Type is AdminActionType.Suspend or AdminActionType.Resign or AdminActionType.RevokeAdmin
                && users.CountActiveAdmins() <= 1)
                return new(false, "마지막 관리자입니다. 다른 사람을 먼저 관리자로 지정하세요.");

            AdminOutcome outcome = Apply(action.Type, target, reason);
            if (!outcome.Success) return outcome;

            // ③ 성공한 동작만 감사 로그에 남긴다. (임시 비밀번호 자체는 절대 기록하지 않는다)
            // [읽는 법] 동작 종류를 감사 로그 종류로 바꿔 "누가(actor) 누구에게(no) 왜(reason) 어디서(IP)"를 남긴다. Apply가 성공을 돌려준 경우에만 여기에 도달한다. 기록은 AuditLog의 큐를 거쳐 별도 스레드가 DB에 쓴다.
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
                // [읽는 법] 조건부 UPDATE: 현재 상태가 PENDING일 때만 ACTIVE로 바꾼다(UPDATE ... WHERE status = 'PENDING'). 바뀐 행이 있으면 true. 그래서 이미 승인·정지된 계정을 실수로 되살리지 않는다.
                return users.SetStatus(no, "ACTIVE", fromStatus: "PENDING")
                    ? new(true, $"{name} 가입을 승인했습니다.")
                    : new(false, $"{name}은(는) 승인 대기 중인 계정이 아닙니다.");

            case AdminActionType.Reject:
                // [읽는 법] 거절은 상태 변경이 아니라 "승인 대기 중인 신청 행 삭제"다. 대기 중이 아니면 삭제되지 않는다(이미 가입된 계정을 거절로 지워 버리지 않게 하는 안전장치).
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
                // [읽는 법] 퇴사는 계정을 지우지 않고 상태만 RESIGNED로 바꾼다 — 그래야 지난 대화·파일·감사 기록의 "보낸 사람"이 계속 이름으로 표시된다. 로그인은 AuthService가 상태를 보고 막는다.
                Disconnect(no, "퇴사 처리되어 더 이상 사용할 수 없는 계정입니다.");
                return new(true, $"{name} 퇴사 처리했습니다. (대화 기록은 남고, 로그인만 막힙니다)");

            case AdminActionType.Activate:
                // [읽는 법] 복구(Activate)는 정지·퇴사 상태에서만 가능하다. 승인 대기(PENDING)를 여기로 승인해 버리는 것을 막는다(승인은 Approve 전용).
                if (target.Status is not ("SUSPENDED" or "RESIGNED"))
                    return new(false, $"{name}은(는) 정지·퇴사 상태가 아닙니다. (지금: {AdminUserInfo.StatusText(target.Status)})");
                users.SetStatus(no, "ACTIVE");
                return new(true, $"{name} 계정을 다시 쓸 수 있게 했습니다.");

            case AdminActionType.Unlock:
                users.Unlock(no);
                return new(true, $"{name} 로그인 잠금을 풀었습니다.");

            case AdminActionType.ResetPassword:
                if (target.AuthSource != "LOCAL") return new(false, "사내 계정(LDAP) 사용자는 여기서 비밀번호를 바꿀 수 없습니다.");
                // [읽는 법] 비밀번호 초기화는 LOCAL 계정만 가능하다(LDAP 계정은 사내 계정 시스템에서 관리). AuthService가 임시 비밀번호를 만들어 해시로 저장하고 원문을 돌려주면, 이 결과(TempPassword)를 관리자 화면에 한 번만 보여 준다.
                string? temp = auth.ResetPassword(no);
                if (temp is null) return new(false, "비밀번호를 초기화하지 못했습니다.");
                // [왜] 계정을 도용당해 초기화하는 경우가 많다. 지금 접속해 있는 연결(도용한 사람일 수 있음)을 끊어야 의미가 있다.
                Disconnect(no, "관리자가 비밀번호를 초기화했습니다. 새로 받은 임시 비밀번호로 다시 로그인하세요.");
                return new(true, $"{name} 임시 비밀번호를 발급했습니다. 본인에게 직접 전달하세요. (다음 로그인 때 새 비밀번호로 바꿔야 합니다)", temp);

            case AdminActionType.Kick:
                return Disconnect(no, $"관리자가 연결을 끊었습니다.{(reason is null ? "" : $" (사유: {reason})")}")
                    ? new(true, $"{name}을(를) 로그아웃시켰습니다.")
                    : new(false, $"{name}은(는) 접속 중이 아닙니다.");

            case AdminActionType.GrantAdmin:
            case AdminActionType.RevokeAdmin:
                // [읽는 법] 지정과 해제가 한 case를 공유한다(case가 연달아 있으면 같은 코드로 이어짐). 동작 종류에 따라 새 역할(ADMIN/USER)만 달라진다.
                string role = type == AdminActionType.GrantAdmin ? "ADMIN" : "USER";
                if (!users.SetRole(no, role))
                    return new(false, role == "ADMIN" ? $"{name}은(는) 이미 관리자입니다." : $"{name}은(는) 관리자가 아닙니다.");
                // 접속 중이면 서버 쪽 권한을 바로 바꾼다 (해제는 즉시 효과, 클라이언트 메뉴는 다음 로그인 때 바뀜)
                if (sessions.FindByEmployeeNo(no) is { User: not null } s)
                {
                    // [스레드] 본인이 동시에 상태를 바꿔도 권한 변경이 사라지지 않게 UpdateUser로 바꾼다
                    // [읽는 법] 접속 중이면 메모리의 사용자 정보를 새 역할로 바꿔 서버 쪽 권한을 즉시 반영한다(권한 확인은 session.User의 역할을 보므로 해제는 바로 효과). UpdateUser는 lock 안에서 교체해, 사용자가 동시에 상태 메시지를 바꿔도 이 변경이 사라지지 않는다.
                    s.UpdateUser(u => u.WithRole(role));
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
        // [읽는 법] "이유를 알리는 마지막 패킷을 보내고 연결을 닫는다." 닫기 전에 이유가 상대에게 도착하도록 보낸 뒤 닫는 순서가 SendAndClose 안에서 지켜진다. 이유(reason)가 사용자 화면에 그대로 보인다.
        session.SendAndClose(Packet.Create(PacketType.ForceLogout, reason));
        return true;
    }

    /// <summary>새 가입 신청을 접속 중인 관리자들에게 알린다 (자동 승인이 꺼져 있을 때).</summary>
    public void NotifyAdmins(string message)
    {
        Packet notify = Packet.Create(PacketType.AdminNotify, message);
        // [읽는 법] 접속 중인 모든 세션을 훑어 관리자(IsAdmin)에게만 같은 알림 패킷을 보낸다. 같은 Packet 객체를 여러 번 보내는 것은 직렬화 결과를 공유해도 되는 읽기 전용 사용이라 안전하다.
        foreach (ClientSession s in sessions.GetAllSessions())
            if (s.User is { IsAdmin: true }) s.Send(notify);
    }
}
