using System.Collections.Concurrent;

namespace ChatApp.Server.Logging;

/// <summary>
/// 생산자-소비자 패턴 로거.
/// 여러 세션 스레드가 Info/Error로 큐에 넣기만 하고(빠름),
/// 전용 로그 스레드 1개가 꺼내서 콘솔과 파일에 쓴다(파일 I/O가 세션 스레드를 막지 않음).
/// </summary>
public sealed class ServerLogger : IDisposable
{
    private readonly BlockingCollection<string> _queue = new();
    private readonly StreamWriter _file;
    private readonly Thread _thread;

    public ServerLogger(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        // [왜] FileShare.ReadWrite: 서버가 돌고 있는 동안에도 메모장으로 로그를 열어 볼 수 있고,
        //      실수로 서버를 하나 더 켜도 "파일을 다른 프로세스가 사용 중" 예외로 바로 죽지 않는다.
        //      (두 번째 서버는 포트가 이미 쓰이고 있다는 안내를 남기고 스스로 끝난다 → Program.cs)
        var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _file = new StreamWriter(stream) { AutoFlush = true };

        _thread = new Thread(Run) { IsBackground = true, Name = "Logger" };
        _thread.Start();
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        // 어느 스레드에서 찍힌 로그인지 보이게 스레드 ID를 함께 남긴다(멀티스레드 디버깅에 유용).
        // [시각] 서버 PC의 지역 시각 + 시간대(예: +09:00). 운영자가 시계와 바로 비교할 수 있고, 시간대가 함께 있어 UTC로도 환산된다.
        //        (DB에는 UTC로 저장한다. 로그는 사람이 읽는 것이라 지역 시각으로 쓴다.)
        string line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] [{level,-5}] [T{Environment.CurrentManagedThreadId,2}] {message}";
        try
        {
            _queue.Add(line);
        }
        catch (InvalidOperationException)
        {
            // Dispose 이후 들어온 로그는 버린다.
        }
    }

    private void Run()
    {
        foreach (string line in _queue.GetConsumingEnumerable())
        {
            Console.WriteLine(line);
            _file.WriteLine(line);
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();       // 남은 로그를 다 쓰고 Run 루프가 끝나게 한다
        _thread.Join(TimeSpan.FromSeconds(2));
        _file.Dispose();
        _queue.Dispose();
    }
}
