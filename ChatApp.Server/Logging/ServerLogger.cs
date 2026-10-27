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
        _file = new StreamWriter(filePath, append: true) { AutoFlush = true };

        _thread = new Thread(Run) { IsBackground = true, Name = "Logger" };
        _thread.Start();
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        // 어느 스레드에서 찍힌 로그인지 보이게 스레드 ID를 함께 남긴다(멀티스레드 디버깅에 유용).
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level,-5}] [T{Environment.CurrentManagedThreadId,2}] {message}";
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
