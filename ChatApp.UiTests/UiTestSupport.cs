using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Security;
using ChatApp.Common.Utils;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace ChatApp.UiTests;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] UiTestSupport.cs - 화면 자동 테스트의 공통 도구
// - 서버 확인: 서버가 꺼져 있으면 테스트를 "건너뜀"으로 표시한다(실패로 보이지 않게).
// - 테스트 계정: 서버에 소켓으로 직접 붙어 새 계정을 가입시킨다(화면 조작 없이 빠르게).
// - 관찰자(Observer): 화면 밖의 두 번째 사용자. 화면에서 보낸 메시지가 진짜 서버를 거쳐 도착하는지 확인한다.
// - 클라이언트 실행: 임시 설정 폴더(CHATAPP_SETTINGS_DIR)로 띄워 내 PC의 진짜 설정을 건드리지 않는다.
// 환경 변수: CHATAPP_UITEST_HOST(기본 127.0.0.1), CHATAPP_UITEST_PORT(기본 9000), CHATAPP_CLIENT_PATH(클라이언트 exe)
// ─────────────────────────────────────────────────────────────────────────────

internal static class UiTestEnv
{
    public static string Host => Environment.GetEnvironmentVariable("CHATAPP_UITEST_HOST") ?? "127.0.0.1";
    public static int Port => int.TryParse(Environment.GetEnvironmentVariable("CHATAPP_UITEST_PORT"), out int p) ? p : 9000;
    public const string Password = "UiTest123!";

    /// <summary>클라이언트 exe 위치: 환경 변수 → 이 테스트와 같은 빌드 구성(Debug/Release)의 ChatApp.Client 출력 폴더.</summary>
    public static string ClientPath
    {
        get
        {
            if (Environment.GetEnvironmentVariable("CHATAPP_CLIENT_PATH") is { Length: > 0 } env) return env;
            // 테스트 dll: ...\ChatApp.UiTests\bin\Debug\net10.0-windows\ → 같은 구성의 클라이언트 출력
            string config = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "ChatApp.Client", "bin", config, "net10.0-windows", "ChatApp.Client.exe"));
        }
    }

    /// <summary>서버에 TCP로 붙어 보고, 안 되면 건너뛸 이유를 돌려준다.</summary>
    public static string? ServerUnavailableReason()
    {
        try
        {
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(2)))
                return $"서버({Host}:{Port})가 응답하지 않습니다. 서버를 먼저 켜고 다시 실행하세요.";
            return null;
        }
        catch (Exception ex)
        {
            return $"서버({Host}:{Port})에 연결할 수 없습니다: {ex.GetBaseException().Message}. 서버를 먼저 켜고 다시 실행하세요.";
        }
    }

    /// <summary>테스트마다 겹치지 않는 사원번호 (영문·숫자 4~20자 규칙).</summary>
    public static string NewEmployeeNo(string prefix) => $"{prefix}{DateTime.Now:MMddHHmmss}{Random.Shared.Next(10, 99)}";

    /// <summary>폴링: 조건이 참이 될 때까지(또는 값이 생길 때까지) 기다린다. 화면 갱신은 비동기라 바로 확인하면 안 된다.</summary>
    public static T? WaitFor<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (probe() is T value) return value;
            }
            catch (Exception)
            {
                // 창이 막 닫히거나 다시 그려지는 순간에는 요소가 사라질 수 있다 → 다시 시도
            }
            Thread.Sleep(200);
        }
        return null;
    }
}

/// <summary>
/// 화면 밖에서 서버에 직접 붙는 사용자 (TLS, 시험용이라 인증서 확인 생략).
/// 받은 패킷을 큐에 쌓아 두고 WaitFor로 원하는 패킷을 기다린다.
/// </summary>
internal sealed class SocketUser : IDisposable
{
    private readonly TcpClient _tcp = new();
    private Stream _stream = null!;
    private readonly BlockingCollection<Packet> _inbox = [];
    private readonly object _sendLock = new();

    public static async Task<SocketUser> ConnectAsync()
    {
        var user = new SocketUser();
        await user._tcp.ConnectAsync(UiTestEnv.Host, UiTestEnv.Port);
        user._stream = await TlsHelper.AuthenticateAsClientAsync(user._tcp.GetStream(), UiTestEnv.Host, null, acceptAny: true);
        new Thread(() =>
        {
            try { while (NetworkHelper.ReadPacket(user._stream) is Packet p) user._inbox.Add(p); }
            catch (Exception) { /* 연결 종료 */ }
        }) { IsBackground = true, Name = "UiTest-Socket" }.Start();
        return user;
    }

    public void Send(Packet packet)
    {
        lock (_sendLock) NetworkHelper.WritePacket(_stream, packet);
    }

    public Packet? WaitFor(PacketType type, Func<Packet, bool>? match = null, int timeoutMs = 10_000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            TimeSpan left = end - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            if (_inbox.TryTake(out Packet? p, left) && p.Type == type && (match?.Invoke(p) ?? true)) return p;
        }
    }

    /// <summary>새 계정 가입 → (성공, 서버 안내). 승인 대기면 Success여도 Body에 "승인"이 들어 있다.</summary>
    public (bool Success, string Message) Register(string employeeNo, string name)
    {
        Send(Packet.Create(PacketType.RegisterReq).WithData(new RegisterRequest
        {
            EmployeeNo = employeeNo,
            Email = $"{employeeNo.ToLowerInvariant()}@uitest.example",
            Name = name,
            Department = "화면테스트",
            Position = "사원",
            Password = UiTestEnv.Password,
            ProtocolVersion = ProtocolInfo.Version,
        }));
        Packet? res = WaitFor(PacketType.RegisterRes);
        return (res?.Success == true, res?.Body ?? "서버가 가입 요청에 답하지 않았습니다.");
    }

    public bool Login(string employeeNo)
    {
        Send(Packet.Create(PacketType.LoginReq).WithData(new LoginRequest
        {
            EmployeeNo = employeeNo,
            Password = UiTestEnv.Password,
            ProtocolVersion = ProtocolInfo.Version,
        }));
        return WaitFor(PacketType.LoginRes)?.Success == true;
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch (Exception) { }
        _tcp.Dispose();
    }
}

/// <summary>임시 설정 폴더로 사내톡 클라이언트를 띄우고, 끝나면 프로세스와 폴더를 정리한다.</summary>
internal sealed class TestClient : IDisposable
{
    public Application App { get; }
    public UIA3Automation Automation { get; } = new();
    private readonly string _settingsDir;

    public TestClient()
    {
        Assert.True(File.Exists(UiTestEnv.ClientPath), $"클라이언트 실행 파일이 없습니다: {UiTestEnv.ClientPath} (솔루션을 먼저 빌드하세요)");
        _settingsDir = Path.Combine(Path.GetTempPath(), "ChatApp.UiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_settingsDir);
        // 테스트용 서버 주소. 신뢰한 지문은 비워 둔다 → 첫 접속 때 지문 확인 창이 뜨는 것까지 테스트한다.
        File.WriteAllText(Path.Combine(_settingsDir, "client.json"), JsonSerializer.Serialize(new
        {
            Host = UiTestEnv.Host,
            Port = UiTestEnv.Port,
            UseTls = true,
            RememberEmployeeNo = false,
        }));

        var start = new ProcessStartInfo(UiTestEnv.ClientPath) { UseShellExecute = false };
        start.Environment["CHATAPP_SETTINGS_DIR"] = _settingsDir;
        App = Application.Launch(start);
    }

    /// <summary>이 클라이언트 프로세스의 최상위 창들 (메시지 상자 포함).</summary>
    public Window[] Windows() => App.GetAllTopLevelWindows(Automation);

    public Window? FindWindow(Func<Window, bool> match, int timeoutMs = 15_000)
        => UiTestEnv.WaitFor(() => Windows().FirstOrDefault(match), TimeSpan.FromMilliseconds(timeoutMs));

    public AutomationElement? FindById(Window window, string automationId, int timeoutMs = 10_000)
        => UiTestEnv.WaitFor(() => window.FindFirstDescendant(Automation.ConditionFactory.ByAutomationId(automationId)),
                             TimeSpan.FromMilliseconds(timeoutMs));

    /// <summary>꼭 있어야 하는 컨트롤. 없으면 "무엇을 못 찾았는지" 알려 주며 실패한다 (null 오류 대신).</summary>
    public AutomationElement Require(Window window, string automationId, string what, int timeoutMs = 10_000)
        => FindById(window, automationId, timeoutMs)
           ?? throw new Xunit.Sdk.XunitException($"{what}({automationId})을(를) '{window.Title}' 창에서 찾지 못했습니다. (숨겨져 있거나 이름이 바뀌었을 수 있음)");

    /// <summary>
    /// 메시지 상자의 [예] 버튼을 찾아 누른다 (처음 접속할 때의 인증서 지문 확인 창 등). 창이 없으면 false.
    /// [개념] 윈도우 표준 메시지 상자의 버튼 AutomationId: 확인 = "1", 예 = "6".
    /// </summary>
    public bool ClickYesIfAsked(Window owner, int timeoutMs)
    {
        AutomationElement? yes = UiTestEnv.WaitFor(() =>
            owner.ModalWindows.Concat(Windows())
                 .Select(w => w.FindFirstDescendant(Automation.ConditionFactory.ByAutomationId("6")))
                 .FirstOrDefault(b => b is not null),
            TimeSpan.FromMilliseconds(timeoutMs));
        if (yes is null) return false;
        yes.AsButton().Invoke();
        return true;
    }

    public void Dispose()
    {
        // X는 트레이로 숨기기라 창을 닫아도 프로세스가 남는다 → 테스트가 끝나면 강제로 끝낸다
        try { App.Kill(); } catch (Exception) { }
        App.Dispose();
        Automation.Dispose();
        try { Directory.Delete(_settingsDir, recursive: true); } catch (Exception) { }
    }
}
