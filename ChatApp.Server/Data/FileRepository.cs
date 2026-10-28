using System.Data.Common;
using ChatApp.Common.Models;

namespace ChatApp.Server.Data;

/// <summary>files 테이블 한 행.</summary>
public sealed record FileRecord(
    string FileId,
    int? RoomId,
    string SenderNo,
    string? ReceiverNo,
    string FileName,
    long Size,
    FileKind Kind,
    string StoredPath,
    bool Done);

/// <summary>
/// files 테이블. 올리기를 시작할 때 UPLOADING으로 넣고, 다 받으면 DONE으로 바꾼다.
/// (시작할 때 넣어 두면 DB 문제를 큰 파일을 다 받기 "전에" 알 수 있다)
/// </summary>
public sealed class FileRepository(Db db)
{
    /// <summary>테이블이 없을 때 MariaDB가 주는 SQLSTATE.</summary>
    public const string TableMissingState = "42S02";

    /// <summary>서버 시작 시 files 테이블이 있는지 확인. 없으면 false.</summary>
    public bool TableExists()
    {
        try
        {
            using DbConnection conn = db.Open();
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM files LIMIT 0";
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (DbException ex) when (ex.SqlState == TableMissingState)
        {
            return false;
        }
    }

    public void InsertUploading(FileRecord f)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO files (file_id, room_id, sender_no, receiver_no, file_name, file_size, file_kind, stored_path, status)
            VALUES (@id, @room, @sender, @receiver, @name, @size, @kind, @path, 'UPLOADING')
            """;
        cmd.AddParam("@id", f.FileId);
        cmd.AddParam("@room", f.RoomId);
        cmd.AddParam("@sender", f.SenderNo);
        cmd.AddParam("@receiver", f.ReceiverNo);
        cmd.AddParam("@name", f.FileName);
        cmd.AddParam("@size", f.Size);
        cmd.AddParam("@kind", f.Kind.ToString().ToUpperInvariant());
        cmd.AddParam("@path", f.StoredPath);
        cmd.ExecuteNonQuery();
    }

    public void MarkDone(string fileId)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        // created_at을 "올리기가 끝난 시각"으로 바꾼다. 대화 기록에서 파일 메시지가 실제로 올라온 위치에 보이도록.
        // 시각은 DB 서버 시계(CURRENT_TIMESTAMP)가 아니라 채팅 메시지와 같은 C# 서버 시계를 쓴다.
        // (DB와 채팅 서버의 시간대가 다르면 기록을 시간순으로 합칠 때 순서가 뒤섞이기 때문)
        cmd.CommandText = "UPDATE files SET status = 'DONE', created_at = @now WHERE file_id = @id";
        DateTime now = DateTime.Now;
        cmd.AddParam("@now", new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond));
        cmd.AddParam("@id", fileId);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string fileId)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE file_id = @id";
        cmd.AddParam("@id", fileId);
        cmd.ExecuteNonQuery();
    }

    public FileRecord? Find(string fileId)
    {
        using DbConnection conn = db.Open();
        using DbCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT file_id, room_id, sender_no, receiver_no, file_name, file_size, file_kind, stored_path, status
            FROM files WHERE file_id = @id
            """;
        cmd.AddParam("@id", fileId);

        using DbDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new FileRecord(
            FileId: r.GetString(0),
            RoomId: r.IsDBNull(1) ? null : Convert.ToInt32(r.GetValue(1)),
            SenderNo: r.GetString(2),
            ReceiverNo: r.IsDBNull(3) ? null : r.GetString(3),
            FileName: r.GetString(4),
            Size: Convert.ToInt64(r.GetValue(5)),
            Kind: Enum.TryParse(r.GetString(6), ignoreCase: true, out FileKind kind) ? kind : FileKind.File,
            StoredPath: r.GetString(7),
            Done: r.GetString(8) == "DONE");
    }
}
