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
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약 (※ 위 [연결] 설명은 예전 것: 지금은 AuthService뿐 아니라 FriendService·ProfileService·AdminService·RoomService·FileService도 이 클래스를 쓴다)
//   users 표와 그 곁가지(login_tokens, friends)를 다루는 SQL 모음: 로그인 조회·가입·잠금 기록, 자동 로그인 토큰, 상태·역할·비밀번호 변경, 회원 목록, 사진 버전, 친구·휴대폰 번호, LDAP 프로필 갱신.
//
// ■ 가장 중요한 규칙: "확인 후 행동"을 두 번에 나누지 않고 한 문장 SQL로 (동시성은 DB가 보장)
//   - 가입: 먼저 SELECT하지 않고 바로 INSERT → 기본 키/UNIQUE 위반(SQLSTATE 23000)이면 "이미 있음"
//   - 로그인 실패 횟수: 읽고 +1 하고 저장(분실 갱신 위험)이 아니라 UPDATE ... SET x = x + 1 한 문장(행 잠금으로 원자적)
//   - 상태 변경: WHERE에 "지금 이 상태일 때만" 조건을 넣어 두 관리자가 동시에 눌러도 한 명만 성공
//
// ■ 읽는 순서
//   ① UserRecord(DB 한 행 = 내부용, 해시 포함) → ② 로그인 토큰 두 함수 → ③ FindByEmployeeNo/TryInsert/RecordLogin* → ④ TryLoad*/TrySave*(칸이 없을 수 있는 기능)
//   → ⑤ UpdatePassword → ⑥ 관리자용(ListUsers, SetStatus, DeletePending, SetRole, Unlock, Count*) → ⑦ 사람 목록·사진·친구·휴대폰 → ⑧ LDAP 프로필
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - UNIQUE/PRIMARY KEY 제약으로 중복 방지(SQLSTATE 23000) ... "unique constraint duplicate key SQLSTATE 23000"
//   - 원자적 UPDATE(lost update 방지) ......................... "atomic UPDATE x = x + 1 lost update"
//   - 조건부 UPDATE(낙관적 상태 전이) .......................... "conditional update WHERE status optimistic"
//   - 토큰은 해시로 저장(SHA-256), 기기별 토큰 ................. "store hashed session token device binding"
//   - 칸 없음 오류(42S22) 허용, 단계적 마이그레이션 ............ "backward compatible schema migration column missing"
//   - 열거형 → 열 이름 매핑(문자열 이어 붙임이 안전한 경우) .... "dynamic column name whitelist SQL injection"
//
// ■ 면접 예상 질문과 답
//   Q1. 가입할 때 "먼저 있는지 SELECT"하지 않고 바로 INSERT하는 이유는요?
//       A. SELECT로 없음을 확인한 뒤 INSERT하는 사이에 다른 사람이 같은 사원번호로 가입하면 둘 다 통과하는 경쟁 상태가 생깁니다. 바로 INSERT하고 PRIMARY KEY/UNIQUE 제약에 걸리면 중복으로 판단하면 DB가 원자적으로 한 명만 성공시킵니다. 이메일·휴대폰 번호의 중복도 같은 방식입니다.
//   Q2. 로그인 실패 횟수를 C#에서 올리지 않고 SQL 한 문장으로 처리하는 이유는요?
//       A. C#에서 읽고 → +1 → 저장하면 동시에 틀린 두 요청이 같은 값을 읽어 1만 증가합니다(분실 갱신). UPDATE 한 문장은 DB가 행 잠금으로 처리해 정확합니다. 한도에 닿으면 같은 문장 안에서 잠금 시각을 정하고 횟수를 0으로 되돌립니다(MariaDB는 SET을 왼쪽부터 처리하므로 locked_until을 먼저 계산).
//   Q3. 잠금 남은 시간을 C#이 아니라 SQL에서 계산하는 이유는요?
//       A. "지금 시각"을 DB 시계(UTC_TIMESTAMP) 하나로 통일하기 위해서입니다. 서버와 DB의 시계가 다르거나 시간대가 달라도 잠금 판단이 일관됩니다. 남은 초를 분으로 올림(CEIL)해 "N분 후 다시 시도"로 보여 줍니다.
//   Q4. 자동 로그인 토큰은 어떻게 안전하게 저장하나요?
//       A. 토큰 원문은 저장하지 않고 SHA-256 해시만 저장합니다(DB가 유출돼도 토큰을 바로 쓸 수 없음). 기기마다(device_id) 하나씩 발급하고, 같은 기기에서 다시 발급하면 덮어씁니다. 검증은 해시 일치 + 만료 전 + 계정이 ACTIVE + LOCAL 계정일 때만 통과시킵니다. 비밀번호 변경이나 상태 변경(정지·퇴사 등) 때는 그 사람의 토큰을 모두 지워 기존 기기의 자동 로그인을 끊습니다.
//   Q5. 비밀번호에는 느린 해시(PBKDF2)를 쓰면서 토큰에는 빠른 SHA-256을 쓰는 이유는요?
//       A. 비밀번호는 사람이 정하는 예측 가능한 값이라 무차별 대입을 늦추려고 느린 해시를 씁니다. 토큰은 서버가 만든 충분히 긴 무작위 값이라 추측이 사실상 불가능하므로 빠른 해시로 충분합니다. (대신 토큰은 반드시 암호학적 난수로 만들어야 합니다.)
//   Q6. 칸이 없는 DB에서도 로그인이 되게 한 이유와 방법은요?
//       A. 새 기능(상태 메시지, 임시 비밀번호 표시)은 나중에 추가한 칸이라 마이그레이션 전 DB에는 없을 수 있습니다. 로그인 쿼리에 넣으면 로그인 자체가 실패하므로, 그 칸들은 TryLoad~ 함수로 따로 읽고 SQLSTATE 42S22(칸 없음)이면 기본값(null/false)을 돌려줍니다. (지금은 시작할 때 스키마 버전을 확인해 막으므로 이중 안전장치입니다.)
//   Q7. 사진 열 이름을 문자열로 이어 붙여도 SQL 인젝션이 아닌 이유는요?
//       A. 열 이름은 사용자 입력이 아니라 enum(PhotoKind)을 코드의 switch로 두 가지 문자열("photo_version"/"background_version") 중 하나로 바꾼 값이기 때문입니다. 열 이름은 매개변수로 넘길 수 없어서 이렇게 "허용 목록" 방식으로 만들고, 값(@v, @no)은 항상 매개변수를 씁니다.
//   Q8. LDAP 프로필 갱신에서 무엇을 조심했나요?
//       A. LDAP이 인증한 "기존 ACTIVE 계정"의 이름·부서·직급만 바꾸고, 상태·역할·이메일은 건드리지 않습니다. 값이 비어 있으면 기존 값을 유지(COALESCE + NULLIF)하고, 제어 문자를 제거하고 30자로 제한합니다. LDAP 서버가 이상한 값을 줘도 권한 상승이나 계정 생성은 일어나지 않습니다.
//   Q9. 한계는요? (솔직하게 답하기)
//       A. (1) 열 번호로 읽는 방식이라 SELECT 순서를 바꾸면 조용히 어긋난다. (2) 같은 UserInfo 만들기 코드가 세 군데에 복사되어 있다(공통 함수로 뽑을 수 있음). (3) 회원 목록·사람 목록은 상한(2000/10000)이 있어 그 이상의 조직에서는 페이지 처리가 필요하다. (4) 연결을 함수마다 열고 닫아 로그인 한 번에 DB 호출이 여러 번 일어난다(FindByEmployeeNo, TryLoad*, TryGet*, GetPhotoVersion×2 등) — 한 번의 JOIN 쿼리로 합치면 줄일 수 있다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 가입이 "이미 가입된 ..." ................ users의 PRIMARY KEY(사원번호), UNIQUE(이메일, 휴대폰) 중 어느 것이 충돌했는지
//   - 잠금이 안 풀린다/너무 빨리 풀린다 ........ locked_until 값과 DB 시간대(UTC), RecordLoginFailure의 maxAttempts/lockMinutes, Unlock
//   - 자동 로그인이 갑자기 풀린다 .............. 비밀번호·상태 변경 때 login_tokens 삭제(의도됨), 만료(expires_at), LDAP 계정은 토큰 불가(의도됨)
//   - 승인했는데 "대기 중이 아님" ............... SetStatus의 fromStatus 조건, 이미 처리됐는지
//   - 관리자 목록이 일부만 보인다 ............... ListUsers의 LIMIT 2000, 정렬(승인 대기 먼저, 최근 가입순)
//   - "Unknown column" 오류 ................... 마이그레이션(003 상태 메시지, 004 임시 비밀번호, 011 친구, 012·013 사진) 적용 여부
// ───────────────────────────────────────────────────────────────────────────
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
    /// <summary>자동 로그인 토큰을 (사원번호, 기기)별로 저장한다. 원문이 아닌 SHA-256 해시만 저장하며, 같은 기기면 덮어쓴다.</summary>
    // [읽는 법] 자동 로그인용 토큰 발급 저장. 토큰 원문(token)은 클라이언트에게만 있고 DB에는 SHA-256 해시(16진 문자열)만 저장한다. (사원번호, 기기)가 키라서 같은 기기에서 다시 발급하면 ON DUPLICATE KEY UPDATE로 덮어쓰고 만료 시각을 갱신한다.
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

    /// <summary>자동 로그인 토큰이 유효하면 true (해시 일치, 만료 전, ACTIVE 상태의 LOCAL 계정일 때만).</summary>
    // [읽는 법] 받은 토큰을 같은 방식으로 해시해 DB의 해시와 비교한다(원문은 어디에도 저장되지 않으므로 해시끼리 비교). 만료 전이어야 하고, 계정이 ACTIVE이며 LOCAL 계정일 때만 true. 위 주석처럼 LDAP 계정은 매번 디렉터리 인증을 다시 해서, 사내 계정이 비활성화되면 30일 토큰으로 계속 들어오지 못하게 한다.
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
        // [읽는 법] 조건에 맞는 행이 없으면 ExecuteScalar가 null → ?? 0으로 "실패(0)" 처리. 비교식(참/거짓)은 SQL에서 1/0 숫자로 나오므로 == 1로 bool로 바꾼다. 어떤 이유로 실패했는지는 일부러 구분하지 않는다(공격자에게 정보를 주지 않음).
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) == 1;
    }
    /// <summary>사원번호로 회원 한 명 찾기. 없으면 null.</summary>
    // [왜] 잠금 남은 시간(분)을 SQL에서 계산한다(NOW()는 DB 시계). 서버와 DB 시계가 달라도 잠금 판단이 일관된다.
    // [읽는 법] 로그인과 관리자 동작에서 가장 많이 부르는 조회. 돌려주는 UserRecord에는 비밀번호 해시·salt가 있는 "내부용" 정보이므로, 클라이언트로 보낼 때는 반드시 UserInfo로 변환한다(AuthService.Login). 잠금 남은 시간은 SQL에서 계산해 컬럼으로 받는다.
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
    // [읽는 법] 반환값이 곧 결과다: true = 가입됨, false = 사원번호/이메일/휴대폰 번호 중복(23000). 이 외의 DB 오류(연결 끊김 등)는 잡지 않고 위로 올려 "DB 오류"로 처리된다. 항상 auth_source = LOCAL로 넣는다(LDAP 계정은 이 함수로 만들지 않음).
    public bool TryInsert(string employeeNo, string email, string name, string department, string position,
                          string passwordHash, string salt, string status, string? phone = null)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (employee_no, email, phone, name, department, position, password_hash, salt, status, auth_source)
            VALUES (@no, @email, @phone, @name, @dept, @pos, @hash, @salt, @status, 'LOCAL')
            """;
        cmd.AddParam("@no", employeeNo);
        cmd.AddParam("@phone", phone);
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
    // [쿼리 읽는 법] 한 문장에서 두 칸을 함께 바꾼다: ① 이번 실패가 한도에 닿으면(failed_login_count + 1 >= max) 잠금 시각 = 지금 + N분, 아니면 그대로 ② 한도에 닿으면 횟수를 0으로, 아니면 +1.
    //                  ①을 ②보다 먼저 쓰는 이유: MariaDB는 SET을 왼쪽부터 처리해, 횟수를 먼저 0으로 바꾸면 ①의 조건 판단이 틀어진다.
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
    // [읽는 법] Try~로 시작하는 함수들(TryLoad, TrySave, TryGet)은 "칸이 아직 없는 옛 DB"에서도 예외 없이 기본값을 돌려주는 안전한 버전이다. SQLSTATE 42S22(칸 없음)만 잡고 다른 오류는 그대로 올린다. as string은 NULL이면 null이 되는 안전한 형 변환이다.
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
    // [읽는 법] 비밀번호를 바꾸면서 잠금과 실패 횟수도 풀고(잠겨서 연락한 사람에게 임시 비밀번호를 주는 경우가 많아서), 임시 비밀번호 여부(must_change_password)를 기록한다. LOCAL 계정만 대상이다(LDAP 계정은 영향받지 않아 행이 0개 → false).
    //           성공하면 바로 아래에서 그 사람의 자동 로그인 토큰을 모두 삭제한다. 비밀번호를 바꿨는데 예전 기기가 토큰으로 계속 들어오는 보안 구멍을 막는다.
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
        // [읽는 법] 바뀐 행이 정확히 1개일 때만 성공으로 본다(사원번호가 기본 키라 0 또는 1). 성공했을 때만 토큰을 지워 불필요한 DB 작업을 줄인다. 같은 연결(conn)을 재사용해 두 번째 명령을 실행한다.
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

    /// <summary>
    /// 로그인에 성공한 사용자의 옛 해시를 새 해시로 바꾼다(반복 횟수 업그레이드).
    /// UpdatePassword와 달리 잠금·임시 비밀번호 표시·자동 로그인 토큰은 건드리지 않는다(비밀번호는 그대로이므로).
    /// </summary>
    // [읽는 법] WHERE에 "옛 해시 그대로"를 넣어서, 그 사이 비밀번호가 바뀌었다면 덮어쓰지 않는다(0행 → false).
    public bool UpgradePasswordHash(string employeeNo, string oldHash, string newHash, string newSalt)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE users SET password_hash = @new, salt = @salt
            WHERE employee_no = @no AND auth_source = 'LOCAL' AND password_hash = @old
            """;
        cmd.AddParam("@new", newHash);
        cmd.AddParam("@salt", newSalt);
        cmd.AddParam("@no", employeeNo);
        cmd.AddParam("@old", oldHash);
        return cmd.ExecuteNonQuery() == 1;
    }

    // ───────── 관리자 기능 ─────────

    /// <summary>회원 목록 (상태로 거르기, null이면 전체). 최근 가입 순.</summary>
    // [쿼리 읽는 법] "@status IS NULL OR status = @status"로 필터가 있든 없든 한 쿼리. 정렬은 (승인 대기 = 1이 먼저, 그다음 최근 가입순) — 관리자가 처리할 신청이 맨 위에 오도록. 안전을 위해 최대 2000명까지만 읽는다.
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
    // [쿼리 읽는 법] 사원번호가 맞고, (fromStatus를 줬다면 지금 그 상태이고), 이미 같은 상태가 아닐 때만 바꾼다. 결과가 정확히 1행일 때만 true. 그래서 이미 정지된 계정을 또 "정지"하면 false로 "이미 정지된 계정입니다" 안내가 나간다.
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
        // [읽는 법] 상태가 실제로 바뀐 경우에만 그 사람의 자동 로그인 토큰을 모두 삭제한다 — 정지·퇴사된 사람이 저장된 토큰으로 다시 들어오는 것을 막는다(복구·승인 때도 새로 로그인하게 한다).
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
    // [읽는 법] 가입 거절 = 승인 대기(PENDING) 계정의 행 삭제. 아직 대화·파일 기록이 없는 계정이라 안전하다. 조건에 status = PENDING이 있어 이미 승인된 계정은 절대 지워지지 않는다.
    public bool DeletePending(string employeeNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM users WHERE employee_no = @no AND status = 'PENDING'";
        cmd.AddParam("@no", employeeNo);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>사용자의 역할(ADMIN/USER)을 바꾼다. 실제로 바뀌면 true, 이미 같은 역할이거나 대상이 없으면 false.</summary>
    // [읽는 법] 역할(ADMIN/USER) 변경. "role <> @role" 조건으로 이미 같은 역할이면 0행 → false("이미 관리자입니다" 안내). 마지막 관리자 보호는 이 함수가 아니라 AdminService가 lock 안에서 확인한다.
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
    // [읽는 법] 초대·조직도 화면용 사람 목록. 비밀번호 같은 민감한 칸은 SELECT하지 않고, 사진 버전은 함께 읽어 클라이언트 캐시 판단에 쓴다. 부서 → 이름순, 최대 10,000명. 자주 부르면 무거우므로 호출하는 쪽(MessageService.HandleDirectory)이 30초 캐시를 둔다.
    public List<UserInfo> ListActiveUsers()
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT employee_no, name, department, position, role, photo_version, background_version FROM users
            WHERE status = 'ACTIVE' ORDER BY department, name LIMIT 10000
            """;
        var list = new List<UserInfo>();
        using DbDataReader r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new UserInfo { EmployeeNo = r.GetString(0), Name = r.GetString(1), Department = r.GetString(2), Position = r.GetString(3), Role = r.GetString(4), PhotoVersion = Convert.ToInt64(r.GetValue(5)), BackgroundVersion = Convert.ToInt64(r.GetValue(6)) });
        return list;
    }

    /// <summary>주어진 사원번호 중 사용 중(ACTIVE)인 사람의 "이름 직급". 없거나 정지된 사람은 빠진다.</summary>
    // [읽는 법] 사원번호 여러 개를 한 번의 쿼리(IN)로 "이름 직급"으로 바꾼다. 사용 중(ACTIVE)이 아닌 사람은 결과에서 빠지므로, 초대 같은 기능이 "없거나 정지된 계정 걸러내기"를 겸한다. 반환 Dictionary에 키가 없으면 그 사람은 유효하지 않다는 뜻이다.
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
            // [읽는 법] MessageRepository.LoadReactions와 같은 방식: IN 목록의 값마다 @n0, @n1 ... 매개변수를 만든다. 사용자가 보낸 사원번호가 SQL에 직접 이어 붙지 않는다.
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
        cmd.CommandText = "SELECT employee_no, name, department, position, role, photo_version, background_version FROM users WHERE employee_no = @no AND status = 'ACTIVE'";
        cmd.AddParam("@no", employeeNo);
        using DbDataReader r = cmd.ExecuteReader();
        return r.Read()
            ? new UserInfo { EmployeeNo = r.GetString(0), Name = r.GetString(1), Department = r.GetString(2), Position = r.GetString(3), Role = r.GetString(4), PhotoVersion = Convert.ToInt64(r.GetValue(5)), BackgroundVersion = Convert.ToInt64(r.GetValue(6)) }
            : null;
    }

    // ───────── 프로필 사진 (ProfileService) ─────────

    // [개념] 열 이름은 SQL 매개변수로 넘길 수 없다. 그래서 enum을 코드가 정한 두 문자열 중 하나로 바꾸는 "허용 목록" 방식으로 열 이름을 고른다. 사용자 입력이 열 이름이 되는 길이 없으므로 아래 $"..." 문자열 이어 붙임은 SQL 인젝션 위험이 없다.
    private static string PhotoColumn(PhotoKind kind) => kind == PhotoKind.Background ? "background_version" : "photo_version";

    /// <summary>ACTIVE 사용자의 프로필(또는 배경) 사진 버전을 돌려준다. 사진이 없거나 계정이 없으면 0.</summary>
    public long GetPhotoVersion(string employeeNo, PhotoKind kind = PhotoKind.Profile)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {PhotoColumn(kind)} FROM users WHERE employee_no = @no AND status = 'ACTIVE'";
        cmd.AddParam("@no", employeeNo);
        // [읽는 법] 결과가 null(행 없음)도 아니고 DBNull(NULL 값)도 아닐 때만 숫자로 바꾸고, 아니면 0("사진 없음"). 패턴 매칭으로 두 경우를 한 줄에 처리한다. 계정이 ACTIVE일 때만 읽어, 정지된 사람의 사진 버전은 0처럼 보인다.
        return cmd.ExecuteScalar() is { } v and not DBNull ? Convert.ToInt64(v) : 0;
    }

    /// <summary>사용자의 프로필(또는 배경) 사진 버전을 저장한다.</summary>
    public void SetPhotoVersion(string employeeNo, PhotoKind kind, long version)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE users SET {PhotoColumn(kind)} = @v WHERE employee_no = @no";
        cmd.AddParam("@v", version);
        cmd.AddParam("@no", employeeNo);
        cmd.ExecuteNonQuery();
    }

    // ───────── 친구·휴대폰 번호 (FriendService) ─────────

    /// <summary>휴대폰 번호(숫자만)로 사용 중인 계정 찾기.</summary>
    public UserInfo? FindActiveByPhone(string phone)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT employee_no, name, department, position, role, photo_version, background_version FROM users WHERE phone = @phone AND status = 'ACTIVE'";
        cmd.AddParam("@phone", phone);
        using DbDataReader r = cmd.ExecuteReader();
        return r.Read()
            ? new UserInfo { EmployeeNo = r.GetString(0), Name = r.GetString(1), Department = r.GetString(2), Position = r.GetString(3), Role = r.GetString(4), PhotoVersion = Convert.ToInt64(r.GetValue(5)), BackgroundVersion = Convert.ToInt64(r.GetValue(6)) }
            : null;
    }

    /// <summary>내 휴대폰 번호 (숫자만, 없으면 null).</summary>
    public string? GetPhone(string employeeNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT phone FROM users WHERE employee_no = @no";
        cmd.AddParam("@no", employeeNo);
        return cmd.ExecuteScalar() is string phone ? phone : null;
    }

    /// <summary>휴대폰 번호 저장(null = 지우기). 다른 계정이 이미 쓰는 번호면 false (UNIQUE 제약).</summary>
    // [읽는 법] 휴대폰 번호 저장(null이면 NULL로 지움). 번호 열에 UNIQUE 제약이 있어 다른 계정이 쓰는 번호면 23000 오류 → false("이미 등록된 번호"). NULL은 UNIQUE 검사에서 서로 같다고 보지 않아 "번호 없음"이 여러 명이어도 괜찮다.
    public bool SetPhone(string employeeNo, string? phone)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET phone = @phone WHERE employee_no = @no";
        cmd.AddParam("@phone", phone);
        cmd.AddParam("@no", employeeNo);
        try
        {
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (DbException ex) when (ex.SqlState == "23000") // 같은 번호가 이미 있음
        {
            return false;
        }
    }

    /// <summary>내 친구 목록 (사용 중인 계정만, 이름순).</summary>
    // [읽는 법] friends(나, 친구) 표와 users를 JOIN해 친구의 표시 정보를 가져온다. 사용 중(ACTIVE)인 계정만, 이름순, 최대 2000명. 상태 메시지는 NULL이면 빈 문자열로 바꿔(COALESCE) 받는다.
    public List<UserInfo> ListFriends(string ownerNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT u.employee_no, u.name, u.department, u.position, u.role, COALESCE(u.status_message, ''), u.photo_version, u.background_version
            FROM friends f JOIN users u ON u.employee_no = f.friend_no
            WHERE f.owner_no = @owner AND u.status = 'ACTIVE'
            ORDER BY u.name LIMIT 2000
            """;
        cmd.AddParam("@owner", ownerNo);
        var list = new List<UserInfo>();
        using DbDataReader r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new UserInfo
            {
                EmployeeNo = r.GetString(0), Name = r.GetString(1), Department = r.GetString(2), Position = r.GetString(3),
                Role = r.GetString(4), StatusMessage = r.GetString(5), PhotoVersion = Convert.ToInt64(r.GetValue(6)), BackgroundVersion = Convert.ToInt64(r.GetValue(7)),
            });
        return list;
    }

    /// <summary>ownerNo가 friendNo를 친구로 등록했으면 true (방향이 있는 관계).</summary>
    public bool IsFriend(string ownerNo, string friendNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM friends WHERE owner_no = @owner AND friend_no = @friend";
        cmd.AddParam("@owner", ownerNo);
        cmd.AddParam("@friend", friendNo);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>ownerNo가 등록한 친구 수.</summary>
    public int CountFriends(string ownerNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM friends WHERE owner_no = @owner";
        cmd.AddParam("@owner", ownerNo);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>친구 추가. 이미 친구면 false. (INSERT IGNORE: 두 번 눌러도 한 줄만)</summary>
    // [읽는 법] INSERT IGNORE: 이미 친구면 오류 없이 0행 → false. 두 번 눌러도 한 줄만 생긴다(멱등). 시각은 UTC_TIMESTAMP(3)로 밀리초까지 저장한다.
    public bool AddFriend(string ownerNo, string friendNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT IGNORE INTO friends (owner_no, friend_no, created_at) VALUES (@owner, @friend, UTC_TIMESTAMP(3))";
        cmd.AddParam("@owner", ownerNo);
        cmd.AddParam("@friend", friendNo);
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>친구 등록을 지운다. 지운 줄이 있으면 true, 원래 친구가 아니었으면 false.</summary>
    public bool RemoveFriend(string ownerNo, string friendNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM friends WHERE owner_no = @owner AND friend_no = @friend";
        cmd.AddParam("@owner", ownerNo);
        cmd.AddParam("@friend", friendNo);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>지금 사용 중(ACTIVE) 계정인지. 로그인 처리 도중 정지된 경우를 잡으려고 로그인 마지막에 한 번 더 확인한다.</summary>
    // [읽는 법] 로그인 처리 도중 관리자가 계정을 정지한 경우를 잡으려고 로그인 마지막에 한 번 더 확인한다(확인 후 행동 사이의 짧은 틈). as string == "ACTIVE"는 결과가 null이어도 안전하게 false가 된다.
    public bool IsActive(string employeeNo)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM users WHERE employee_no = @no";
        cmd.AddParam("@no", employeeNo);
        return cmd.ExecuteScalar() as string == "ACTIVE";
    }

    /// <summary>LDAP가 인증한 기존 계정의 표시 프로필만 갱신한다. 계정 상태·역할·이메일은 여기서 변경하지 않는다.</summary>
    // [쿼리 읽는 법] COALESCE(NULLIF(@x, ''), x) = "새 값이 비어 있지 않으면 새 값, 비어 있으면 기존 값 유지". 조건에 LDAP + ACTIVE를 넣어, 로컬 계정이나 정지된 계정은 이 경로로 절대 바뀌지 않는다.
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

    // [읽는 법] 외부(LDAP)에서 온 값을 DB에 넣기 전에 정리한다: 제어 문자 제거 → 앞뒤 공백 제거 → 비면 null(=기존 값 유지) → 최대 길이로 자름. 칸 길이 초과로 저장이 실패하는 일과 이상한 문자가 화면에 퍼지는 일을 막는다.
    private static string? BoundProfile(string? value, int maxLength)
    {
        string cleaned = new((value ?? "").Where(c => !char.IsControl(c)).ToArray());
        cleaned = cleaned.Trim();
        return cleaned.Length == 0 ? null : cleaned[..Math.Min(cleaned.Length, maxLength)];
    }

    /// <summary>없는 칸(column)을 읽거나 쓰려 할 때 MariaDB가 주는 SQLSTATE.</summary>
    private const string ColumnMissingState = "42S22";
}
