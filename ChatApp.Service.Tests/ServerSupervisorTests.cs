using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ChatApp.Service.Tests;

// ServerSupervisor(서버 프로세스 감독) 테스트.
// 진짜 서버 대신 임시 폴더의 가짜 서버(.cmd 파일)를 실행시켜 감독의 동작을 확인한다.
// 가짜 서버는 켜질 때마다 starts.log에 한 줄을 남기므로 "몇 번 켜졌는지" 셀 수 있다.
public sealed class ServerSupervisorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ChatAppServiceTests-" + Guid.NewGuid().ToString("N"));
    private readonly ListLogger _logger = new();

    public ServerSupervisorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static readonly TimeSpan[] FastDelays = [TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(120)];

    private string StartLog => Path.Combine(_dir, "starts.log");
    private string QuitLog => Path.Combine(_dir, "quit.log");

    // 가짜 서버 파일을 만든다. 첫 줄은 항상 "켜졌다" 기록, 그 뒤에 mode별 동작(body)이 이어진다.
    private string WriteFakeServer(string body)
    {
        string path = Path.Combine(_dir, "fake-server.cmd");
        File.WriteAllText(path, $"@echo off\r\necho started>>\"{StartLog}\"\r\n{body}\r\n");
        return path;
    }

    // 죽는 서버: 바로 종료(오류 코드 3)
    private string CrashingServer() => WriteFakeServer("exit /b 3");
    // 말을 잘 듣는 서버: 표준 입력으로 한 줄(/quit)을 받으면 기록하고 정상 종료
    private string ObedientServer() => WriteFakeServer($"set /p line=\r\necho quit:%line%>>\"{QuitLog}\"\r\nexit /b 0");
    // 안 끝나는 서버: 표준 입력을 무시하고 계속 돈다
    private string HangingServer() => WriteFakeServer("ping -n 120 127.0.0.1 >nul");

    private int StartCount => ReadLines(StartLog).Length;

    // 가짜 서버가 파일을 쓰는 중에도 읽을 수 있게 공유 모드로 연다
    private static string[] ReadLines(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (IOException) { return []; }
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"{timeoutMs}ms 안에 일어나지 않았다: {what}");
            await Task.Delay(50);
        }
    }

    private ServerSupervisor Create(string exePath, TimeSpan[]? delays = null, TimeSpan? healthyRun = null, TimeSpan? quitTimeout = null)
        => new(_logger, new ServerSupervisorOptions
        {
            ServerExePath = exePath,
            RestartDelays = delays ?? FastDelays,
            HealthyRun = healthyRun ?? TimeSpan.FromMinutes(10),
            QuitTimeout = quitTimeout ?? TimeSpan.FromSeconds(5),
        });

    // ===============
    // 이 테스트는 “운영 기본 설정값이 문서(SERVICE.md)와 같다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 운영_기본_설정값이_문서와_같다()
    {
        var options = new ServerSupervisorOptions();
        Assert.Equal([5, 10, 20, 40, 60], options.RestartDelays.Select(d => (int)d.TotalSeconds));
        Assert.Equal(TimeSpan.FromSeconds(20), options.QuitTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), options.HealthyRun);
        Assert.EndsWith(Path.Combine("ChatApp.Server", "ChatApp.Server.exe"), options.ServerExePath);
    }

    // ===============
    // 이 테스트는 “서버 파일이 없으면 서비스가 시작에 실패한다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public async Task 서버_파일이_없으면_시작에_실패한다()
    {
        using ServerSupervisor supervisor = Create(Path.Combine(_dir, "없는-서버.exe"));
        // StartAsync 자체는 끝까지 기다리지 않고 돌아온다. 실패는 ExecuteTask에 담기고, 실제 서비스에서는 호스트가 이를 보고 서비스를 멈춘다.
        await supervisor.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<FileNotFoundException>(() => supervisor.ExecuteTask!);
    }

    // ===============
    // 이 테스트는 “서버가 죽으면 다시 켜고 실패가 이어지면 간격을 늘린다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public async Task 서버가_죽으면_다시_켜고_실패가_이어지면_간격을_늘린다()
    {
        using ServerSupervisor supervisor = Create(CrashingServer());
        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntil(() => StartCount >= 3, "서버가 3번 켜짐");
        await supervisor.StopAsync(CancellationToken.None);

        Assert.True(_logger.Contains("ChatApp.Server exited with code 3"));
        Assert.True(_logger.Contains("(failure #1)"));
        Assert.True(_logger.Contains("(failure #2)"));
    }

    // ===============
    // 이 테스트는 “오래 잘 돌았으면 재시작 간격을 처음으로 되돌린다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public async Task 오래_잘_돌았으면_재시작_간격을_처음으로_되돌린다()
    {
        // HealthyRun = 0 이면 "아무리 짧게 돌아도 잘 돌았다"가 되어, 실패 횟수가 계속 1로 되돌아간다
        using ServerSupervisor supervisor = Create(CrashingServer(), healthyRun: TimeSpan.Zero);
        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntil(() => StartCount >= 3, "서버가 3번 켜짐");
        await supervisor.StopAsync(CancellationToken.None);

        Assert.True(_logger.Contains("(failure #1)"));
        Assert.False(_logger.Contains("(failure #2)"));
    }

    // ===============
    // 이 테스트는 “중지하면 /quit을 보내 정상 종료하고 다시 켜지 않는다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public async Task 중지하면_quit을_보내_정상_종료하고_다시_켜지_않는다()
    {
        using ServerSupervisor supervisor = Create(ObedientServer(), delays: [TimeSpan.FromMilliseconds(30)]);
        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntil(() => _logger.Contains("started (PID"), "서버 시작 로그");

        await supervisor.StopAsync(CancellationToken.None);

        Assert.Contains("quit:/quit", ReadLines(QuitLog));
        Assert.True(_logger.Contains("ChatApp.Server stopped normally"));
        Assert.False(_logger.Contains("Restarting ChatApp.Server"));
        Assert.Equal(1, StartCount);
    }

    // ===============
    // 이 테스트는 “중지 명령에 응하지 않으면 시간 초과 뒤 강제 종료한다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public async Task 중지_명령에_응하지_않으면_시간_초과_뒤_강제_종료한다()
    {
        using ServerSupervisor supervisor = Create(HangingServer(), quitTimeout: TimeSpan.FromMilliseconds(500));
        await supervisor.StartAsync(CancellationToken.None);
        await WaitUntil(() => _logger.Contains("started (PID") && StartCount >= 1, "서버 시작");
        int pid = _logger.StartedPid();

        await supervisor.StopAsync(CancellationToken.None);

        Assert.True(_logger.Contains("did not stop in time"));
        await WaitUntil(() => !IsRunning(pid), "서버 프로세스가 강제 종료됨", timeoutMs: 10_000);
        Assert.False(_logger.Contains("Restarting ChatApp.Server"));
        Assert.Equal(1, StartCount);
    }

    // ===============
    // 이 테스트는 “감독이 시작할 때 같은 경로의 남은 서버 프로세스를 먼저 정리한다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public async Task 시작할_때_같은_경로의_남은_서버_프로세스를_정리한다()
    {
        // ping.exe를 이 테스트만의 경로·이름으로 복사해 "남은 서버"로 쓴다.
        // (경로가 유일하므로 같은 이름의 다른 프로그램은 정리 대상이 되지 않는다)
        string orphanExe = Path.Combine(_dir, "orphan-server.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), orphanExe);
        using Process orphan = Process.Start(new ProcessStartInfo(orphanExe, "-n 120 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            Assert.False(orphan.HasExited);

            // 복사한 ping.exe는 인수 없이 켜면 바로 끝난다 → 재시작 간격은 길게 해서 반복 실행을 막는다
            using ServerSupervisor supervisor = Create(orphanExe, delays: [TimeSpan.FromMinutes(5)]);
            await supervisor.StartAsync(CancellationToken.None);
            await WaitUntil(() => orphan.HasExited, "남은 서버 프로세스가 정리됨", timeoutMs: 15_000);
            await supervisor.StopAsync(CancellationToken.None);

            Assert.True(_logger.Contains("Killing orphaned ChatApp.Server"));
        }
        finally
        {
            if (!orphan.HasExited) orphan.Kill(entireProcessTree: true);
        }
    }

    private static bool IsRunning(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; } // 그 번호의 프로세스가 없음 = 끝남
    }

    // 감독이 남기는 로그를 모아 두는 가짜 로거
    private sealed class ListLogger : ILogger<ServerSupervisor>
    {
        private readonly List<string> _lines = [];

        public bool Contains(string text) { lock (_lines) return _lines.Any(l => l.Contains(text, StringComparison.Ordinal)); }

        // "ChatApp.Server started (PID 1234)" 로그에서 번호를 꺼낸다
        public int StartedPid()
        {
            lock (_lines)
            {
                string line = _lines.First(l => l.Contains("started (PID", StringComparison.Ordinal));
                int from = line.IndexOf("PID ", StringComparison.Ordinal) + 4;
                return int.Parse(line[from..line.IndexOf(')', from)]);
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }
}
