using System.Diagnostics;

namespace ChatApp.Service;

/// <summary>기존 서버 프로세스를 감시하고 예기치 않은 종료 뒤 다시 시작하는 Windows Service 호스트.</summary>
public sealed class ServerSupervisor(ILogger<ServerSupervisor> logger) : BackgroundService
{
    private Process? _server;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string serverExe = Path.Combine(AppContext.BaseDirectory, "ChatApp.Server", "ChatApp.Server.exe");
        if (!File.Exists(serverExe)) throw new FileNotFoundException("서비스 배포 폴더에 ChatApp.Server\\ChatApp.Server.exe가 없습니다.", serverExe);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var start = new ProcessStartInfo(serverExe)
                {
                    WorkingDirectory = Path.GetDirectoryName(serverExe)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                _server = Process.Start(start) ?? throw new InvalidOperationException("서버 프로세스를 시작하지 못했습니다.");
                logger.LogInformation("ChatApp.Server started (PID {ProcessId})", _server.Id);
                _server.OutputDataReceived += (_, e) => { if (e.Data is not null) logger.LogInformation("{ServerLog}", e.Data); };
                _server.ErrorDataReceived += (_, e) => { if (e.Data is not null) logger.LogWarning("{ServerError}", e.Data); };
                _server.BeginOutputReadLine();
                _server.BeginErrorReadLine();
                await _server.WaitForExitAsync(stoppingToken);
                if (!stoppingToken.IsCancellationRequested)
                    logger.LogError("ChatApp.Server exited with code {ExitCode}; restarting in 5 seconds", _server.ExitCode);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "ChatApp.Server supervision failed"); }
            finally { _server?.Dispose(); _server = null; }
            if (!stoppingToken.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is { HasExited: false } server)
        {
            try
            {
                await server.StandardInput.WriteLineAsync("/quit");
                await server.StandardInput.FlushAsync(cancellationToken);
                await server.WaitForExitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
            {
                if (!server.HasExited) try { server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
        }
        await base.StopAsync(cancellationToken);
    }
}
