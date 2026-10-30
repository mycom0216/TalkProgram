using System.Collections.Concurrent;

namespace ChatApp.Server.Logging;

//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   서버 전체가 쓰는 로그 기록기. 여러 스레드가 Info/Error로 "큐에 넣기만" 하고, 이름이 "Logger"인 스레드 1개가 큐에서 꺼내 콘솔과 파일에 쓴다(생산자-소비자).
//
// ■ 흐름
//   어느 스레드든 log.Info("...") → 한 줄로 형식 만들기 → _queue.Add(빠름) → 바로 돌아감
//   Logger 스레드: GetConsumingEnumerable로 기다리다가 한 줄씩 → Console.WriteLine + 파일에 WriteLine(AutoFlush로 바로 디스크에 반영)
//   줄 모양: [2026-10-30 14:03:05 +09:00] [INFO ] [T 7] 메시지   (시각 · 등급 · 스레드 ID · 내용)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 생산자-소비자 패턴, BlockingCollection ........... "C# BlockingCollection producer consumer"
//   - GetConsumingEnumerable과 CompleteAdding ......... "GetConsumingEnumerable CompleteAdding graceful shutdown"
//   - FileShare.ReadWrite(실행 중 로그 열어보기) ....... "FileStream FileShare.ReadWrite log file"
//   - AutoFlush(비정상 종료 시 로그 유실 방지) ......... "StreamWriter AutoFlush"
//   - 배경 스레드(IsBackground) ....................... "IsBackground thread process exit"
//   - DateTimeOffset의 시간대 표기(zzz) ............... "DateTimeOffset format zzz"
//
// ■ 면접 예상 질문과 답
//   Q1. 로그를 왜 큐에 넣고 따로 쓰나요? 그냥 파일에 바로 쓰면 안 되나요?
//       A. 파일·콘솔 출력은 느릴 수 있고, 바로 쓰면 접속자를 처리하는 스레드가 그만큼 멈춥니다. 큐에 넣는 건 매우 빨라서 처리 스레드는 거의 안 막히고, 느린 쓰기는 전용 스레드 1개가 맡습니다. 또 파일에 쓰는 스레드가 하나뿐이라 서로 다른 스레드의 로그가 한 줄 안에서 섞이지 않습니다.
//   Q2. 큐에 제한이 없는데 괜찮나요?
//       A. 이 로거의 큐는 크기 제한이 없습니다(AuditLog의 큐는 5만 건 제한). 디스크가 아주 느려지거나 로그가 폭주하면 메모리가 계속 늘 수 있다는 점은 약점입니다. 로그는 줄 단위 문자열이라 평소에는 문제가 되지 않지만, 필요하면 한도를 두고 넘치면 버리는 방식으로 바꿀 수 있습니다.
//   Q3. 서버를 끌 때 마지막 로그가 사라지지 않나요?
//       A. Dispose가 CompleteAdding으로 "더 안 받는다"를 알리면 Logger 스레드는 남은 줄을 다 쓴 뒤 루프를 끝내고, Dispose는 최대 2초까지 그 스레드를 기다립니다(Join). 2초 안에 못 끝날 만큼 쌓였다면 일부가 못 쓰일 수 있습니다.
//   Q4. Dispose 뒤에 로그를 남기려고 하면요?
//       A. CompleteAdding 이후의 Add는 InvalidOperationException을 던지는데, Write에서 잡아 조용히 버립니다. 종료 중에 다른 스레드가 로그를 찍어도 서버가 예외로 흔들리지 않게 하려는 처리입니다.
//   Q5. 로그에 스레드 ID를 넣은 이유는요?
//       A. 같은 시각의 로그가 여러 스레드에서 섞여 나올 때, 같은 접속자(같은 수신 스레드)의 흐름만 골라 읽을 수 있습니다. 멀티스레드 문제를 추적할 때 유용합니다.
//   Q6. 시각을 UTC가 아니라 지역 시각으로 쓴 이유는요?
//       A. 운영자가 서버 PC 시계와 바로 비교하기 쉽고, 뒤에 시간대(+09:00)가 같이 붙어 필요하면 UTC로 환산할 수 있습니다. DB에는 UTC로 저장하는 것과 목적이 다릅니다(사람이 읽는 로그 vs 데이터).
//
// ■ 디버깅할 때 먼저 볼 곳
//   - 로그가 파일에 안 보인다 ........ 경로(Program.cs의 logPath), 폴더 쓰기 권한, 다른 프로그램이 파일을 독점으로 열고 있는지
//   - 종료 직전 로그가 없다 .......... Dispose의 2초 Join 제한, 프로세스가 강제 종료되었는지(배경 스레드는 프로세스가 끝나면 같이 끝남)
//
// ■ 개선 아이디어 (코드는 바꾸지 않음)
//   - 날짜별 파일 분리(로그 롤링)와 오래된 파일 정리: 지금은 한 파일에 계속 덧붙인다(FileMode.Append)
//   - 큐 크기 한도, 파일 쓰기 실패 시 처리: Run 루프에는 try/catch가 없어 파일 쓰기 예외가 나면 로거 스레드가 멈출 수 있다(확인 필요: 실제로 발생하는지는 환경에 따라 다름)
// ───────────────────────────────────────────────────────────────────────────

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
