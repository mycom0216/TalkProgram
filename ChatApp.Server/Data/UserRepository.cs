using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using ChatApp.Common.Models;

namespace ChatApp.Server.Data;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] users 테이블(회원 정보) 읽기·쓰기: 사원번호로 찾기, 가입, 로그인 실패/성공 기록.
// [스레드]   로그인·가입하는 사람들의 수신 스레드에서 동시에 호출된다. 공유 상태(필드)가 없고
//            메서드마다 연결을 새로 열기 때문에 lock이 필요 없다. 동시성은 DB 제약·원자적 UPDATE에 맡긴다.
// [연결]     AuthService만 이 클래스를 사용한다(비밀번호 검사 등 규칙은 AuthService, SQL은 여기).
// ═══════════════════════════════════════════════════════════════════════════
/// <summary>users 테이블 한 행.</summary>
// [개념] record: 값을 담는 용도의 간단한 클래스. 괄호 안에 쓴 값들이 읽기 전용 속성이 되고,
//        생성자·Equals·ToString이 자동으로 만들어진다. DB 한 행처럼 "데이터 묶음"에 잘 맞는다.
public sealed record UserRecord(
    string EmployeeNo,
    string Email,
    string Name,
    string Department,
    string Position,
    string Role,
    string Status,       // PENDING / ACTIVE / SUSPENDED / RESIGNED
    string AuthSource,   // LOCAL / LDAP
    string? PasswordHash,
    string? Salt,
    int LockMinutesLeft); // 0이면 잠기지 않음

/// <summary>
/// users 테이블 접근. 여러 세션 스레드에서 동시에 호출되며, 메서드마다 자기 연결을 열어 쓴다.
/// 동시성 처리는 DB에 맡긴다(PRIMARY KEY/UNIQUE 제약, 한 문장짜리 UPDATE).
/// </summary>
public sealed class UserRepository(Db db)
{
    public void IssueLoginToken(string employeeNo, string deviceId, string token, DateTime expiresAt)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO login_tokens (employee_no, device_id, token_hash, expires_at) VALUES (@no, @device, @hash, @expires) ON DUPLICATE KEY UPDATE token_hash = VALUES(token_hash), expires_at = VALUES(expires_at), created_at = UTC_TIMESTAMP()";
        cmd.AddParam("@no", employeeNo);
        cmd.AddParam("@device", deviceId);
        cmd.AddParam("@hash", hash);
        cmd.AddParam("@expires", expiresAt);
        cmd.ExecuteNonQuery();
    }

    public bool ValidateLoginToken(string employeeNo, string deviceId, string token)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        // Tokens are only a convenience for local accounts. LDAP accounts must
        // re-authenticate against the directory so a disabled AD account cannot
        // keep using a 30-day bearer token.
        cmd.CommandText = """
            SELECT t.token_hash = @hash AND t.expires_at > UTC_TIMESTAMP()
            FROM login_tokens t
            JOIN users u ON u.employee_no = t.employee_no
            WHERE t.employee_no = @no AND t.device_id = @device AND u.status = 'ACTIVE' AND u.auth_source = 'LOCAL'
            """;
        cmd.AddParam("@no", employeeNo);
        cmd.AddParam("@device", deviceId);
        cmd.AddParam("@hash", hash);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) == 1;
    }
    /// <summary>사원번호로 회원 한 명 찾기. 없으면 null.</summary>
    // [왜] 잠금 남은 시간(분)을 SQL에서 계산한다(NOW()는 DB 시계). 서버와 DB 시계가 달라도 잠금 판단이 일관된다.
    public UserRecord? FindByEmployeeNo(string employeeNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT employee_no, email, name, department, position, role, status, auth_source,
                   password_hash, salt,
                   IF(locked_until > UTC_TIMESTAMP(), CEIL(TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(), locked_until) / 60), 0) AS lock_minutes_left
            FROM users
            WHERE employee_no = @no
            """;
        cmd.AddParam("@no", employeeNo);

        // [개념] ExecuteReader: SELECT 결과를 한 행씩 읽는 커서. Read()가 false면 결과가 없다.
        //        GetString(0)의 0은 SELECT에 적은 열의 순서(0부터).
        using DbDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        return new UserRecord(
            EmployeeNo: r.GetString(0),
            Email: r.GetString(1),
            Name: r.GetString(2),
            Department: r.GetString(3),
            Position: r.GetString(4),
            Role: r.GetString(5),
            Status: r.GetString(6),
            AuthSource: r.GetString(7),
            // [주의] NULL인 열을 GetString으로 읽으면 예외가 난다. IsDBNull로 먼저 확인한다(LDAP 계정은 비밀번호가 NULL).
            PasswordHash: r.IsDBNull(8) ? null : r.GetString(8),
            Salt: r.IsDBNull(9) ? null : r.GetString(9),
            LockMinutesLeft: Convert.ToInt32(r.GetValue(10)));
    }

    /// <summary>
    /// 새 계정 저장. 사원번호(PRIMARY KEY)나 이메일(UNIQUE)이 이미 있으면 false.
    /// 두 사람이 동시에 같은 사원번호로 가입해도 DB 제약 때문에 한 명만 성공한다.
    /// </summary>
    public bool TryInsert(string employeeNo, string email, string name, string department, string position,
                          string passwordHash, string salt, string status)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (employee_no, email, name, department, position, password_hash, salt, status, auth_source)
            VALUES (@no, @email, @name, @dept, @pos, @hash, @salt, @status, 'LOCAL')
            """;
        cmd.AddParam("@no", employeeNo);
        cmd.AddParam("@email", email);
        cmd.AddParam("@name", name);
        cmd.AddParam("@dept", department);
        cmd.AddParam("@pos", position);
        cmd.AddParam("@hash", passwordHash);
        cmd.AddParam("@salt", salt);
        cmd.AddParam("@status", status);

        try
        {
            cmd.ExecuteNonQuery();
            return true;
        }
        // [개념] SQLSTATE: DB 오류를 분류하는 표준 5자리 코드. 23000 = 무결성 제약 위반(중복 키 등).
        // [왜] "있는지 먼저 SELECT → 없으면 INSERT"로 하면 두 사람이 동시에 같은 사원번호로 가입할 때 둘 다 통과할 수 있다.
        //      그냥 INSERT하고, PRIMARY KEY/UNIQUE 제약에 걸리면 중복으로 판단하는 편이 정확하다(DB가 원자성을 보장).
        catch (DbException ex) when (ex.SqlState == "23000") // 무결성 제약 위반(중복 키, 오류 1062)
        {
            return false;
        }
    }

    /// <summary>
    /// 로그인 실패 1회 기록. maxAttempts번째 실패면 lockMinutes분 잠그고 횟수를 0으로 되돌린다.
    /// 한 문장짜리 UPDATE라 여러 스레드가 동시에 실패를 기록해도 횟수가 꼬이지 않는다.
    /// (MariaDB는 SET을 왼쪽부터 처리하므로 locked_until을 먼저 계산한다.)
    /// </summary>
    // [주의] C#에서 "현재 횟수 읽기 → +1 → 저장"으로 나누면, 동시에 틀린 두 요청이 같은 값을 읽어 1만 증가하는
    //        경쟁 상태(lost update)가 생긴다. UPDATE ... SET x = x + 1 한 문장은 DB가 행 잠금으로 원자적으로 처리한다.
    public void RecordLoginFailure(string employeeNo, int maxAttempts, int lockMinutes)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users
            SET locked_until       = IF(failed_login_count + 1 >= @max, UTC_TIMESTAMP() + INTERVAL @min MINUTE, locked_until),
                failed_login_count = IF(failed_login_count + 1 >= @max, 0, failed_login_count + 1)
            WHERE employee_no = @no
            """;
        cmd.AddParam("@max", maxAttempts);
        cmd.AddParam("@min", lockMinutes);
        cmd.AddParam("@no", employeeNo);
        cmd.ExecuteNonQuery();
    }

    /// <summary>로그인 성공: 실패 횟수·잠금 초기화, 마지막 로그인 시각 기록.</summary>
    public void RecordLoginSuccess(string employeeNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users
            SET failed_login_count = 0, locked_until = NULL, last_login_at = UTC_TIMESTAMP()
            WHERE employee_no = @no
            """;
        cmd.AddParam("@no", employeeNo);
        cmd.ExecuteNonQuery();
    }

    // ───────── 상태 메시지 (users.status_message, migrate_003에서 추가) ─────────

    /// <summary>
    /// 저장된 상태 메시지. 칸이 없으면(migrate_003을 아직 실행 안 함) null.
    /// [왜] 로그인 쿼리(FindByEmployeeNo)에 이 칸을 넣으면, 마이그레이션을 안 한 DB에서는 로그인 자체가 실패한다.
    ///      그래서 따로 읽고, 칸이 없어도 로그인은 되게 한다.
    /// </summary>
    public string? TryLoadStatusMessage(string employeeNo)
    {
        try
        {
            using DbConnection conn = db.Open();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT status_message FROM users WHERE employee_no = @no";
            cmd.AddParam("@no", employeeNo);
            return cmd.ExecuteScalar() as string;
        }
        catch (DbException ex) when (ex.SqlState == ColumnMissingState)
        {
            return null;
        }
    }

    /// <summary>상태 메시지 저장. 칸이 없으면 false (메모리에서만 바뀌고 다음 로그인 때 사라짐).</summary>
    public bool TrySaveStatusMessage(string employeeNo, string message)
    {
        try
        {
            using DbConnection conn = db.Open();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE users SET status_message = @msg WHERE employee_no = @no";
            cmd.AddParam("@msg", message.Length == 0 ? null : message);
            cmd.AddParam("@no", employeeNo);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (DbException ex) when (ex.SqlState == ColumnMissingState)
        {
            return false;
        }
    }

    // ───────── 비밀번호 변경·초기화 (must_change_password는 migrate_004에서 추가) ─────────

    /// <summary>임시 비밀번호로 로그인했는지 (비밀번호를 바꿔야 하는지). 칸이 없으면 false.</summary>
    public bool TryGetMustChangePassword(string employeeNo)
    {
        try
        {
            using DbConnection conn = db.Open();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT must_change_password FROM users WHERE employee_no = @no";
            cmd.AddParam("@no", employeeNo);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) == 1;
        }
        catch (DbException ex) when (ex.SqlState == ColumnMissingState)
        {
            return false;
        }
    }

    /// <summary>
    /// 비밀번호 저장. mustChange = true면 "임시 비밀번호"(관리자 초기화), false면 본인이 바꾼 것.
    /// 초기화할 때는 잠금도 같이 풀어 준다(잠겨서 연락한 사람에게 임시 비밀번호를 주는 경우가 대부분이라).
    /// </summary>
    public bool UpdatePassword(string employeeNo, string hash, string salt, bool mustChange)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users
            SET password_hash = @hash, salt = @salt, failed_login_count = 0, locked_until = NULL,
                must_change_password = @must
            WHERE employee_no = @no AND auth_source = 'LOCAL'
            """;
        cmd.AddParam("@hash", hash);
        cmd.AddParam("@salt", salt);
        cmd.AddParam("@must", mustChange ? 1 : 0);
        cmd.AddParam("@no", employeeNo);
        bool updated = cmd.ExecuteNonQuery() == 1;
        if (updated)
        {
            using DbCommand revoke = conn.CreateCommand();
            revoke.CommandText = "DELETE FROM login_tokens WHERE employee_no = @no";
            revoke.AddParam("@no", employeeNo);
            revoke.ExecuteNonQuery();
        }
        return updated;
    }

    // ───────── 관리자 기능 ─────────

    /// <summary>회원 목록 (상태로 거르기, null이면 전체). 최근 가입 순.</summary>
    public List<AdminUserInfo> ListUsers(string? status)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        // [왜] 상태 필터를 문자열로 이어 붙이지 않고 "@status IS NULL OR status = @status"로 쓴다.
        //      SQL Injection 걱정이 없고, 필터가 있든 없든 같은 쿼리 하나로 처리된다.
        cmd.CommandText = """
            SELECT employee_no, email, name, department, position, role, status,
                   IF(locked_until > UTC_TIMESTAMP(), CEIL(TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(), locked_until) / 60), 0),
                   created_at, last_login_at
            FROM users
            WHERE @status IS NULL OR status = @status
            ORDER BY (status = 'PENDING') DESC, created_at DESC
            LIMIT 2000
            """;
        cmd.AddParam("@status", status);
        var list = new List<AdminUserInfo>();
        using DbDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AdminUserInfo
            {
                EmployeeNo = r.GetString(0),
                Email = r.GetString(1),
                Name = r.GetString(2),
                Department = r.GetString(3),
                Position = r.GetString(4),
                Role = r.GetString(5),
                Status = r.GetString(6),
                LockMinutesLeft = Convert.ToInt32(r.GetValue(7)),
                CreatedAt = Db.AsUtc(r.GetDateTime(8)),
                LastLoginAt = r.IsDBNull(9) ? null : Db.AsUtc(r.GetDateTime(9)),
            });
        }
        return list;
    }

    /// <summary>
    /// 상태 바꾸기. fromStatus를 주면 "지금 그 상태일 때만" 바꾼다(예: 승인은 PENDING일 때만).
    /// 바뀐 행이 없으면 false (없는 사원번호이거나 상태가 달라서).
    /// </summary>
    // [왜] "SELECT로 상태 확인 → UPDATE" 두 번에 나누지 않고 WHERE에 조건을 넣는다.
    //      두 관리자가 동시에 승인/거절을 눌러도 DB가 한 문장으로 처리하므로 한 명만 성공한다.
    public bool SetStatus(string employeeNo, string status, string? fromStatus = null)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET status = @status
            WHERE employee_no = @no AND (@from IS NULL OR status = @from) AND status <> @status
            """;
        cmd.AddParam("@status", status);
        cmd.AddParam("@no", employeeNo);
        cmd.AddParam("@from", fromStatus);
        bool changed = cmd.ExecuteNonQuery() == 1;
        if (changed)
        {
            using DbCommand revoke = conn.CreateCommand();
            revoke.CommandText = "DELETE FROM login_tokens WHERE employee_no = @no";
            revoke.AddParam("@no", employeeNo);
            revoke.ExecuteNonQuery();
        }
        return changed;
    }

    /// <summary>승인 대기 계정 삭제 (가입 거절). 대화·파일 기록이 없는 PENDING 계정만 지울 수 있다.</summary>
    public bool DeletePending(string employeeNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM users WHERE employee_no = @no AND status = 'PENDING'";
        cmd.AddParam("@no", employeeNo);
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool SetRole(string employeeNo, string role)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET role = @role WHERE employee_no = @no AND role <> @role";
        cmd.AddParam("@role", role);
        cmd.AddParam("@no", employeeNo);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>로그인 실패 잠금 풀기.</summary>
    public bool Unlock(string employeeNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET failed_login_count = 0, locked_until = NULL WHERE employee_no = @no";
        cmd.AddParam("@no", employeeNo);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>사용 중인(ACTIVE) 관리자 수. 마지막 관리자를 정지·해제하지 못하게 막을 때 쓴다.</summary>
    public int CountActiveAdmins()
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE role = 'ADMIN' AND status = 'ACTIVE'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>승인 대기 중인 계정 수 (관리자가 로그인할 때 알려 주기).</summary>
    public int CountPending()
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE status = 'PENDING'";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ───────── 회사 사람 목록 (초대·조직도) ─────────

    /// <summary>사용 중(ACTIVE)인 모든 사람. 부서 → 이름 순. 비밀번호 같은 민감한 칸은 읽지 않는다.</summary>
    public List<UserInfo> ListActiveUsers()
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT employee_no, name, department, position, role FROM users
            WHERE status = 'ACTIVE' ORDER BY department, name LIMIT 10000
            """;
        var list = new List<UserInfo>();
        using DbDataReader r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new UserInfo { EmployeeNo = r.GetString(0), Name = r.GetString(1), Department = r.GetString(2), Position = r.GetString(3), Role = r.GetString(4) });
        return list;
    }

    /// <summary>주어진 사원번호 중 사용 중(ACTIVE)인 사람의 "이름 직급". 없거나 정지된 사람은 빠진다.</summary>
    public Dictionary<string, string> GetActiveDisplayNames(IReadOnlyCollection<string> employeeNos)
    {
        var result = new Dictionary<string, string>();
        if (employeeNos.Count == 0) return result;
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        var names = new List<string>();
        int i = 0;
        foreach (string no in employeeNos)
        {
            string name = $"@n{i++}";
            names.Add(name);
            cmd.AddParam(name, no);
        }
        cmd.CommandText = $"SELECT employee_no, CONCAT(name, ' ', position) FROM users WHERE status = 'ACTIVE' AND employee_no IN ({string.Join(",", names)})";
        using DbDataReader r = cmd.ExecuteReader();
        while (r.Read()) result[r.GetString(0)] = r.GetString(1);
        return result;
    }

    /// <summary>사용 중(ACTIVE)인 사람 한 명 (접속하지 않은 사람에게 1:1 메시지를 보낼 때 이름 확인용). 없으면 null.</summary>
    public UserInfo? FindActiveUser(string employeeNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT employee_no, name, department, position, role FROM users WHERE employee_no = @no AND status = 'ACTIVE'";
        cmd.AddParam("@no", employeeNo);
        using DbDataReader r = cmd.ExecuteReader();
        return r.Read()
            ? new UserInfo { EmployeeNo = r.GetString(0), Name = r.GetString(1), Department = r.GetString(2), Position = r.GetString(3), Role = r.GetString(4) }
            : null;
    }

    /// <summary>지금 사용 중(ACTIVE) 계정인지. 로그인 처리 도중 정지된 경우를 잡으려고 로그인 마지막에 한 번 더 확인한다.</summary>
    public bool IsActive(string employeeNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM users WHERE employee_no = @no";
        cmd.AddParam("@no", employeeNo);
        return cmd.ExecuteScalar() as string == "ACTIVE";
    }

    /// <summary>LDAP가 인증한 기존 계정의 표시 프로필만 갱신한다. 계정 상태·역할·이메일은 여기서 변경하지 않는다.</summary>
    public bool UpdateLdapProfile(string employeeNo, string? name, string? department, string? position)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET
              name = COALESCE(NULLIF(@name, ''), name),
              department = COALESCE(NULLIF(@department, ''), department),
              position = COALESCE(NULLIF(@position, ''), position)
            WHERE employee_no = @no AND auth_source = 'LDAP' AND status = 'ACTIVE'
            """;
        cmd.AddParam("@no", employeeNo);
        cmd.AddParam("@name", BoundProfile(name, 30));
        cmd.AddParam("@department", BoundProfile(department, 30));
        cmd.AddParam("@position", BoundProfile(position, 30));
        return cmd.ExecuteNonQuery() == 1;
    }

    private static string? BoundProfile(string? value, int maxLength)
    {
        string cleaned = new((value ?? "").Where(c => !char.IsControl(c)).ToArray());
        cleaned = cleaned.Trim();
        return cleaned.Length == 0 ? null : cleaned[..Math.Min(cleaned.Length, maxLength)];
    }

    /// <summary>없는 칸(column)을 읽거나 쓰려 할 때 MariaDB가 주는 SQLSTATE.</summary>
    private const string ColumnMissingState = "42S22";
}
