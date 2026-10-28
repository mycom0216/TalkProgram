using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace ChatApp.UiTests;

public sealed class LoginScreenTests
{
    [Fact]
    public void 로그인_창의_필수_컨트롤이_UIA에_노출된다()
    {
        string client = Environment.GetEnvironmentVariable("CHATAPP_CLIENT_PATH") ?? Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ChatApp.Client", "bin", "Debug", "net10.0-windows", "ChatApp.Client.exe"));
        Assert.True(File.Exists(client), $"클라이언트 실행 파일을 찾을 수 없습니다: {client}");

        using var app = Application.Launch(client);
        using var automation = new UIA3Automation();
        try
        {
            Window? foundWindow = app.GetMainWindow(automation, TimeSpan.FromSeconds(10));
            Assert.NotNull(foundWindow);
            Window window = foundWindow!;
            Assert.Contains("로그인", window.Title);

            ConditionFactory cf = automation.ConditionFactory;
            var employeeNo = window.FindFirstDescendant(cf.ByAutomationId("EmployeeNo"));
            Assert.NotNull(employeeNo);
            Assert.NotNull(window.FindFirstDescendant(cf.ByAutomationId("Password")));
            Assert.NotNull(window.FindFirstDescendant(cf.ByAutomationId("AutoLogin")));
            var serverSettings = window.FindFirstDescendant(cf.ByText("서버 설정 ▾"));
            Assert.NotNull(serverSettings);
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
            employeeNo!.AsTextBox().Text = "A0001";
            login!.AsButton().Invoke();
            Assert.Contains("비밀번호를 입력하거나 자동 로그인을 선택", window.FindFirstDescendant(cf.ByAutomationId("LoginStatus"))?.AsLabel().Text);
        }
        finally
        {
            app.Close();
        }
    }
}
