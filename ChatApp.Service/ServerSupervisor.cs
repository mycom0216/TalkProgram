using System.Diagnostics;

namespace ChatApp.Service;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ServerSupervisor.cs - Windows 서비스 안에서 ChatApp.Server.exe를 실행하고 지켜보는 "감독"
// - 시작: 서비스 폴더\ChatApp.Server\ChatApp.Server.exe 를 창 없이 실행한다. 표준 입력은 열어 둔다(종료 명령용).
// - 감시: 서버가 스스로 죽으면(예외, DB 끊김으로 종료 등) 다시 켠다. 연달아 죽으면 5 → 10 → 20 → 40 → 60초로 간격을 늘린다.
//         10분 넘게 잘 돌았으면 간격을 다시 5초로 되돌린다.
// - 중지: 서비스 중지 시 서버에 "/quit"을 보내 정상 종료(접속자 정리, 감사 로그 저장)를 기다린다. 20초 안에 안 끝나면 강제 종료.
// - 로그: 서버 화면 출력은 서버 자체 로그(logs 폴더)에도 남으므로, 여기서는 오류 출력만 Windows 이벤트 로그(경고)로 보낸다.
// [왜] 서버 코드를 서비스용으로 다시 쓰지 않고 "감독 프로세스"를 따로 둔 이유:
//      서버는 콘솔로도, 서비스로도 똑같이 돈다 → 개발할 때(F5)와 운영할 때 동작이 같다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>감독 동작 설정. 운영에서는 기본값을 쓰고, 테스트에서만 가짜 서버 경로와 짧은 시간을 넣는다.</summary>
public sealed record ServerSupervisorOptions
{
    /// <summary>실행할 서버 파일. 기본: 서비스 폴더\ChatApp.Server\ChatApp.Server.exe</summary>
    public string ServerExePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "ChatApp.Server", "ChatApp.Server.exe");
    /// <summary>중지 때 "/quit"을 보낸 뒤 정상 종료를 기다리는 시간(넘으면 강제 종료).</summary>
    public TimeSpan QuitTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>이 시간보다 오래 돌았으면 "잘 돌았다"고 보고 재시작 간격을 처음으로 되돌린다.</summary>
    public TimeSpan HealthyRun { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>연달아 죽을 때 재시작 전에 기다리는 시간(실패 횟수 순서, 마지막 값을 계속 쓴다).</summary>
    public TimeSpan[] RestartDelays { get; init; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)];
}

/// <summary>서버 프로세스를 감시하고 예기치 않게 종료되면 다시 시작하는 Windows 서비스 호스트.</summary>
public sealed class ServerSupervisor(ILogger<ServerSupervisor> logger, ServerSupervisorOptions? options = null) : BackgroundService
{
    // [읽는 법] options를 안 넘기면(운영: 호스트가 등록 없이 만든다) 위 기본값을 쓴다.
    private readonly ServerSupervisorOptions _options = options ?? new();

    private Process? _server;
    private readonly object _gate = new();
    // [왜] 서비스 중지 중에 서버가 /quit으로 끝나면, 감시 루프가 이를 "비정상 종료"로 보고 다시 켜 버린다. 이 표시로 구분한다.
    private volatile bool _stopping;

    /// <summary>서버 exe를 창 없이 실행하고 종료를 기다리다가, 예기치 않게 죽으면 간격을 늘려 가며 다시 시작하는 감시 루프. exe가 없으면 FileNotFoundException.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string serverExe = _options.ServerExePath;
        if (!File.Exists(serverExe)) throw new FileNotFoundException("서비스 배포 폴더에 ChatApp.Server\\ChatApp.Server.exe가 없습니다.", serverExe);

        int failures = 0;
        while (!stoppingToken.IsCancellationRequested && !_stopping)
        {
            DateTime startedAt = DateTime.UtcNow;
            try
            {
                KillOrphans(serverExe);
                var start = new ProcessStartInfo(serverExe)
                {
                    WorkingDirectory = Path.GetDirectoryName(serverExe)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                Process server = Process.Start(start) ?? throw new InvalidOperationException("서버 프로세스를 시작하지 못했습니다.");
                lock (_gate) _server = server;
                logger.LogInformation("ChatApp.Server started (PID {ProcessId})", server.Id);
                // 화면 출력은 서버 로그 파일에도 있으므로 버리기만 한다(안 읽으면 버퍼가 차서 서버가 멈춘다).
                server.OutputDataReceived += (_, _) => { };
                server.ErrorDataReceived += (_, e) => { if (e.Data is not null) logger.LogWarning("{ServerError}", e.Data); };
                server.BeginOutputReadLine();
                server.BeginErrorReadLine();
                await server.WaitForExitAsync(stoppingToken);
                if (_stopping) break;
                logger.LogError("ChatApp.Server exited with code {ExitCode}", server.ExitCode);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "ChatApp.Server supervision failed"); }
            finally
            {
                lock (_gate)
                {
                    // 중지 중이면 StopAsync가 아직 이 프로세스를 쓰고 있을 수 있으니 거기서 정리한다
                    if (!_stopping) { _server?.Dispose(); _server = null; }
                }
            }
            if (_stopping || stoppingToken.IsCancellationRequested) break;

            failures = DateTime.UtcNow - startedAt > _options.HealthyRun ? 1 : failures + 1;
            TimeSpan delay = _options.RestartDelays[Math.Min(failures, _options.RestartDelays.Length) - 1];
            logger.LogWarning("Restarting ChatApp.Server in {Delay} seconds (failure #{Count})", delay.TotalSeconds, failures);
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>서비스 중지: 서버에 "/quit"을 보내 QuitTimeout 안에 정상 종료되기를 기다리고, 안 끝나면 프로세스 트리째 강제 종료한다.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        Process? server;
        lock (_gate) server = _server;
        if (server is { HasExited: false })
        {
            try
            {
                await server.StandardInput.WriteLineAsync("/quit");
                await server.StandardInput.FlushAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.QuitTimeout);
                await server.WaitForExitAsync(timeout.Token);
                logger.LogInformation("ChatApp.Server stopped normally");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
            {
                logger.LogWarning("ChatApp.Server did not stop in time; killing it");
                try { if (!server.HasExited) server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        }
        await base.StopAsync(cancellationToken);
        lock (_gate) { _server?.Dispose(); _server = null; }
    }

    /// <summary>
    /// 감독(이 서비스)이 강제로 죽었을 때 남은 서버 프로세스를 정리한다.
    /// [왜] 남은 서버가 포트(9000)를 쥐고 있으면 새로 켠 서버가 "포트 사용 중"으로 계속 실패한다.
    /// 같은 경로의 exe만 정리하므로, 개발용으로 따로 띄운 다른 폴더의 서버는 건드리지 않는다.
    /// </summary>
    private void KillOrphans(string serverExe)
    {
        foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(serverExe)))
        {
            using (p)
            {
                try
                {
                    if (!string.Equals(p.MainModule?.FileName, serverExe, StringComparison.OrdinalIgnoreCase)) continue;
                    logger.LogWarning("Killing orphaned ChatApp.Server (PID {ProcessId})", p.Id);
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }
}
