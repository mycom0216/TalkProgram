using System.Data.Common;
using MySqlConnector;

namespace ChatApp.Server.Data;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] MariaDB 연결을 열어 주는 작은 도우미 + SQL 파라미터 추가 확장 메서드.
// [스레드]   아무 스레드에서나 호출된다. 연결 객체는 스레드끼리 공유하지 않는다(각자 열고 각자 닫음).
// [연결]     Program.cs가 1개 만들어 모든 Repository(User/Room/File/Message)에 넘겨준다.
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   DB 연결을 "필요할 때 열고 바로 닫는" 도우미. 모든 저장소(Repository)가 이 클래스로 연결을 얻고, 연결을 열 때마다 시간대를 UTC로 맞춘다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 연결 풀링(connection pooling) .................... "ADO.NET connection pooling MySqlConnector"
//   - using / Dispose(자원 반납) ....................... "C# using statement IDisposable"
//   - SQL 인젝션과 매개변수 쿼리 ....................... "SQL injection parameterized query"
//   - 확장 메서드 ...................................... "C# extension methods"
//   - DateTimeKind(Utc/Local/Unspecified) ............. "DateTime Kind UTC SpecifyKind"
//   - 스키마 버전 관리(마이그레이션) ................... "database schema versioning migration table"
//
// ■ 면접 예상 질문과 답
//   Q1. DB 연결을 하나 만들어 계속 쓰지 않고 매번 새로 여는 이유는요?
//       A. 연결 객체는 여러 스레드가 동시에 쓸 수 없습니다. 이 서버는 스레드가 많으므로 연결 하나를 공유하면 오류가 납니다. 매번 열어도 드라이버가 연결 풀링으로 닫힌 연결을 재사용하므로 실제 접속 비용은 거의 없고, 쓰는 쪽은 using으로 열고 닫기만 하면 됩니다.
//   Q2. using을 빼먹으면 어떻게 되나요?
//       A. 연결이 풀로 돌아가지 않아 "연결 누수"가 생깁니다. 풀의 최대 연결 수에 닿으면 새 요청이 기다리다 실패해 서버 전체가 멈춘 것처럼 보입니다. 그래서 Open() 주석에 "받은 쪽에서 반드시 using으로 닫을 것"을 적어 두었습니다.
//   Q3. SET time_zone = '+00:00'을 연결을 열 때마다 실행하는 이유는요?
//       A. 시각을 모두 UTC로 저장·비교하기 위해서입니다. DB 서버의 기본 시간대가 다르면 NOW() 같은 함수와 저장된 시각이 어긋납니다. 이 설정은 "연결 단위"이므로 풀에서 꺼낸 연결에도 일관되게 적용하려고 열 때마다 실행합니다. (비용: 열 때마다 DB 왕복 한 번이 추가됨)
//   Q4. DateTime을 읽어 온 뒤 AsUtc로 감싸는 이유는요?
//       A. DB에서 읽은 DateTime의 Kind는 "알 수 없음(Unspecified)"이라, 그대로 JSON으로 보내면 클라이언트가 지역 시간으로 잘못 해석할 수 있습니다. SpecifyKind로 "이것은 UTC"라고 표시만 붙입니다(값은 바꾸지 않음).
//   Q5. 값을 항상 AddParam(매개변수)으로 넘기는 이유는요?
//       A. 사용자 입력을 SQL 문자열에 이어 붙이면 입력에 SQL 조각을 넣어 쿼리 의미를 바꾸는 SQL 인젝션이 가능합니다. 매개변수로 넘기면 값은 "데이터"로만 처리됩니다. 값이 null이면 DB의 NULL(DBNull.Value)로 변환해 줍니다.
//   Q6. 스키마 버전은 왜 코드에 상수(RequiredSchemaVersion)로 두나요?
//       A. 코드가 기대하는 표·칸의 버전과 DB의 실제 버전이 다르면 저장이 조용히 실패하기 때문입니다. 서버가 시작할 때 이 값과 schema_version 표의 최대값을 비교해 오래된 DB면 어떤 SQL을 적용해야 하는지 알려 주고 멈춥니다. 새 마이그레이션을 추가하면 이 숫자도 함께 올려야 합니다.
//   Q7. 개선점은요? (솔직하게 답하기)
//       A. (1) Open()에서 SET time_zone이 실패하면 이미 열린 연결을 닫지 않고 예외가 나가 연결이 새어 나갈 수 있다 — try/catch로 Dispose해 주면 안전하다. (2) 연결 문자열에서 Pooling·MaxPoolSize·ConnectionTimeout 같은 값을 정하지 않아 기본값에 의존한다. (3) 매번 시간대를 설정하는 대신 연결 문자열 옵션이나 DB 서버 설정으로 처리하면 왕복을 줄일 수 있다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 서버가 "MariaDB에 연결할 수 없습니다" .... ConnectionString(서버·포트·계정·비밀번호), MariaDB 서비스 실행 여부, 방화벽
//   - 한참 쓰다 갑자기 모든 요청이 멈춘다 ...... using 누락으로 풀이 고갈됐는지(Too many connections, 풀 타임아웃 오류)
//   - 시각이 9시간 어긋난다 .................... SET time_zone 적용 여부, AsUtc 누락, 한국 시간(KST = UTC+9)을 그대로 저장한 옛 데이터(009 마이그레이션)
//   - "DB 스키마가 오래되었습니다" ............. schema_version 표의 최대 version과 RequiredSchemaVersion 비교
// ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// MariaDB 연결을 만든다.
/// 멀티스레드 규칙: 연결 객체는 스레드끼리 공유하지 않고, 필요할 때마다 새로 열고 using으로 닫는다.
/// MySqlConnector가 내부에서 연결 풀링을 하므로 매번 여는 비용은 작다.
/// </summary>
// [개념] 연결 풀링(connection pooling): DB 연결을 실제로 새로 맺는 일(TCP 접속 + 로그인)은 느리다.
//        드라이버가 닫힌 연결을 버리지 않고 "풀"에 보관했다가 다음 Open() 때 재사용한다.
//        그래서 코드에서는 "열고 → 쓰고 → 바로 닫기"를 자유롭게 반복해도 된다.
// [주의] DbConnection 하나를 여러 스레드가 동시에 쓰면 안 된다(스레드 안전하지 않음).
//        static 연결 하나를 공유하는 방식은 동시 접속이 많아지면 오류가 나므로, 이 구조를 쓴다.
// [개념] 왜 DbConnection(추상 타입)을 돌려주나? 나머지 코드가 MySqlConnector에 직접 의존하지 않게 해서,
//        나중에 다른 DB 드라이버로 바꾸더라도 이 파일만 고치면 되게 한다.
// [읽는 법] 연결 문자열 하나만 받는 작은 클래스. 상태(열린 연결)를 갖지 않으므로 서버 전체에서 하나만 만들어 모든 저장소가 공유해도 스레드 문제가 없다(공유하는 것은 "연결을 여는 방법"이지 연결 자체가 아니다).
public sealed class Db(string connectionString)
{
    /// <summary>연결을 열어서 돌려준다. 받은 쪽에서 반드시 using으로 닫아야 한다.</summary>
    // [개념] using DbConnection conn = db.Open(); → 변수의 범위(메서드)가 끝날 때 자동으로 Dispose(닫기)된다.
    //        예외가 나도 닫히므로 연결이 새는(leak) 일이 없다.
    public DbConnection Open()
    {
        // [읽는 법] new MySqlConnection은 아직 연결하지 않은 객체를 만들 뿐이고, 바로 아래 Open()에서 풀에서 연결을 빌리거나 새로 맺는다. 반환 형식을 DbConnection(추상 형식)으로 해서, 호출하는 쪽은 MySQL 전용 형식을 모른 채 쓴다.
        var connection = new MySqlConnection(connectionString);
        connection.Open();
        using (DbCommand command = connection.CreateCommand())
        {
            // [읽는 법] 이 연결의 시간대를 UTC로 맞춘다. 이 연결로 실행하는 NOW(), UTC_TIMESTAMP() 비교와 시각 열(TIMESTAMP) 변환이 모두 같은 기준을 쓰게 된다. 이 줄을 감싼 using은 명령 객체만 정리하고, 연결은 호출한 쪽이 닫는다.
            command.CommandText = "SET time_zone = '+00:00'";
            command.ExecuteNonQuery();
        }
        return connection;
    }

    /// <summary>서버 시작 시 DB 접속 확인용.</summary>
    // [왜] 서버를 켜자마자 DB 설정 오류(비밀번호 틀림, MariaDB 꺼짐)를 알려 주기 위해.
    //      확인하지 않으면 첫 사용자가 로그인할 때서야 오류가 나서 원인 찾기가 어렵다.
    public void TestConnection()
    {
        using DbConnection connection = Open();
    }

    /// <summary>이 프로그램이 필요로 하는 DB 스키마 버전 (db/migrate_00N 스크립트 번호).</summary>
    // [읽는 법] 마이그레이션 SQL(db/migrate_00N)을 새로 만들 때마다 1씩 올리는 값. Program.cs가 시작할 때 DB의 실제 버전과 비교한다.
    public const int RequiredSchemaVersion = 13;

    /// <summary>시각 값은 그대로 두고 Kind만 UTC로 표시해 돌려준다 (DB에 UTC로 저장된 값을 읽을 때 쓴다).</summary>
    // [읽는 법] SpecifyKind는 시각 값은 그대로 두고 "이 시각은 UTC다"라는 꼬리표(Kind)만 바꾼다. 시간대를 바꿔 계산하는 ToUniversalTime과 다르다 — DB에 이미 UTC로 저장돼 있으므로 꼬리표만 붙이는 것이 맞다. 아래 줄은 null 가능한 값(DateTime?)용 같은 이름의 짝(오버로드)이다.
    public static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary><see cref="AsUtc(DateTime)"/>의 null 허용 버전. null이면 null을 돌려준다.</summary>
    public static DateTime? AsUtc(DateTime? value) => value is DateTime dt ? AsUtc(dt) : null;

    /// <summary>DB에 적용된 마이그레이션 번호 (schema_version 표). 표가 없으면(migrate_005 전) 0.</summary>
    public int SchemaVersion()
    {
        try
        {
            using DbConnection conn = Open();
            using DbCommand cmd = conn.CreateCommand();
            // [읽는 법] 적용된 마이그레이션 중 가장 큰 번호. 기록이 하나도 없으면 MAX가 NULL이므로 COALESCE로 0을 돌려준다. 표 자체가 없으면(옛 DB) 아래 catch(SqlState 42S02 = 표 없음)에서 0으로 처리한다.
            cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (DbException ex) when (ex.SqlState == "42S02") // 표 없음
        {
            return 0;
        }
    }
}

/// <summary>DbCommand에 기능을 덧붙이는 확장 메서드 모음.</summary>
// [개념] 확장 메서드(extension method): static 클래스의 static 메서드 첫 매개변수에 this를 붙이면,
//        cmd.AddParam(...)처럼 원래 그 클래스의 메서드인 것처럼 부를 수 있다.
internal static class DbCommandExtensions
{
    /// <summary>SQL 파라미터 추가. 값은 항상 파라미터로 넘겨 SQL Injection을 막는다.</summary>
    // [개념] SQL Injection: 사용자 입력을 SQL 문자열에 직접 이어 붙이면("... WHERE id = '" + 입력 + "'"),
    //        입력에 ' OR '1'='1 같은 SQL 조각을 넣어 쿼리 의미를 바꿀 수 있는 공격.
    //        파라미터(@no 등)로 넘기면 값은 "데이터"로만 취급되고 SQL 문법으로 해석되지 않는다.
    // [읽는 법] 첫 매개변수의 this 덕분에 cmd.AddParam("@id", 값)처럼 쓸 수 있다. ADO.NET의 "파라미터 만들기 → 이름·값 지정 → 추가" 세 줄을 한 줄로 줄여, 모든 저장소가 같은 방식(항상 매개변수)을 쓰게 만든다.
    public static void AddParam(this DbCommand command, string name, object? value)
    {
        DbParameter p = command.CreateParameter();
        p.ParameterName = name;
        // [개념] DB의 NULL은 C#의 null이 아니라 DBNull.Value로 넘겨야 한다.
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }
}
