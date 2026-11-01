using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace ChatApp.UiTests;

public sealed class LoginScreenTests
{
    // 이 테스트는 “로그인 창의 필수 컨트롤이 UIA에 노출된다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    // 로그인 화면이 제대로 열리고, 필요한 칸과 버튼이 보이는지 자동으로 확인합니다.
    public void 로그인_창의_필수_컨트롤이_UIA에_노출된다()
    {
        // 테스트할 앱 파일을 찾습니다. 직접 지정한 위치가 없으면 기본 빌드 폴더를 사용합니다.
        string client = Environment.GetEnvironmentVariable("CHATAPP_CLIENT_PATH") ?? Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ChatApp.Client", "bin", "Debug", "net10.0-windows", "ChatApp.Client.exe"));
        Assert.True(File.Exists(client), $"클라이언트 실행 파일을 찾을 수 없습니다: {client}");

        // 내 PC의 진짜 설정(자동 로그인 등)에 영향받지 않게, 빈 임시 설정 폴더로 띄운다
        string settingsDir = Path.Combine(Path.GetTempPath(), "ChatApp.UiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        var start = new System.Diagnostics.ProcessStartInfo(client) { UseShellExecute = false };
        start.Environment["CHATAPP_SETTINGS_DIR"] = settingsDir;
        using var app = Application.Launch(start);
        using var automation = new UIA3Automation();
        try
        {
            // 앱이 열릴 때까지 기다린 다음, 로그인 창이 실제로 나타났는지 확인합니다.
            Window? foundWindow = app.GetMainWindow(automation, TimeSpan.FromSeconds(10));
            Assert.NotNull(foundWindow);
            Window window = foundWindow!;
            Assert.Contains("로그인", window.Title);

            ConditionFactory cf = automation.ConditionFactory;
            // AutomationId는 화면 요소에 붙인 고유 이름입니다. 이름으로 입력칸과 버튼을 찾습니다.
            var employeeNo = window.FindFirstDescendant(cf.ByAutomationId("EmployeeNo"));
            Assert.NotNull(employeeNo);
            Assert.NotNull(window.FindFirstDescendant(cf.ByAutomationId("Password")));
            Assert.NotNull(window.FindFirstDescendant(cf.ByAutomationId("AutoLogin")));
            var serverSettings = window.FindFirstDescendant(cf.ByText("서버 설정 ▾"));
            Assert.NotNull(serverSettings);
            // 서버 설정을 열고, TLS 보안 연결 체크 상자를 켰다가 끌 수 있는지 확인합니다.
            var bounds = serverSettings!.BoundingRectangle;
            Mouse.Click(new System.Drawing.Point((int)bounds.Left + (int)bounds.Width / 2, (int)bounds.Top + (int)bounds.Height / 2), MouseButton.Left);
            var tls = window.FindFirstDescendant(cf.ByAutomationId("UseTls"));
            Assert.NotNull(tls);
            var tlsCheck = tls!.AsCheckBox();
            bool before = tlsCheck.IsChecked ?? false;
            tlsCheck.Toggle();
            Assert.Equal(!before, tlsCheck.IsChecked ?? false);
            tlsCheck.Toggle();
            Assert.Equal(before, tlsCheck.IsChecked ?? false);
            var login = window.FindFirstDescendant(cf.ByAutomationId("LoginButton"));
            Assert.NotNull(login);
            Assert.NotNull(window.FindFirstDescendant(cf.ByText("회원가입")));
            // 비밀번호 없이 로그인을 누르면, 비밀번호를 입력하라는 안내가 나와야 합니다.
            employeeNo!.AsTextBox().Text = "A0001";
            login!.AsButton().Invoke();
            Assert.Contains("비밀번호를 입력하거나 자동 로그인을 선택", window.FindFirstDescendant(cf.ByAutomationId("LoginStatus"))?.AsLabel().Text);
        }
        finally
        {
            // 검사가 성공하거나 실패해도 앱과 임시 설정 폴더를 정리합니다.
            try { app.Kill(); } catch (Exception) { }
            try { Directory.Delete(settingsDir, recursive: true); } catch (Exception) { }
        }
    }
}
