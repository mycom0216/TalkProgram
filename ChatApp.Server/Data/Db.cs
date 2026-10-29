using System.Data.Common;
using MySqlConnector;

namespace ChatApp.Server.Data;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] MariaDB 연결을 열어 주는 작은 도우미 + SQL 파라미터 추가 확장 메서드.
// [스레드]   아무 스레드에서나 호출된다. 연결 객체는 스레드끼리 공유하지 않는다(각자 열고 각자 닫음).
// [연결]     Program.cs가 1개 만들어 모든 Repository(User/Room/File/Message)에 넘겨준다.
// ═══════════════════════════════════════════════════════════════════════════

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
public sealed class Db(string connectionString)
{
    /// <summary>연결을 열어서 돌려준다. 받은 쪽에서 반드시 using으로 닫아야 한다.</summary>
    // [개념] using DbConnection conn = db.Open(); → 변수의 범위(메서드)가 끝날 때 자동으로 Dispose(닫기)된다.
    //        예외가 나도 닫히므로 연결이 새는(leak) 일이 없다.
    public DbConnection Open()
    {
        var connection = new MySqlConnection(connectionString);
        connection.Open();
        using (DbCommand command = connection.CreateCommand())
        {
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
    public const int RequiredSchemaVersion = 13;

    public static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    public static DateTime? AsUtc(DateTime? value) => value is DateTime dt ? AsUtc(dt) : null;

    /// <summary>DB에 적용된 마이그레이션 번호 (schema_version 표). 표가 없으면(migrate_005 전) 0.</summary>
    public int SchemaVersion()
    {
        try
        {
            using DbConnection conn = Open();
            using DbCommand cmd = conn.CreateCommand();
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
    public static void AddParam(this DbCommand command, string name, object? value)
    {
        DbParameter p = command.CreateParameter();
        p.ParameterName = name;
        // [개념] DB의 NULL은 C#의 null이 아니라 DBNull.Value로 넘겨야 한다.
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }
}
