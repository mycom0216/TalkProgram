using ChatApp.Client.Forms;
using ChatApp.Client.Network;

namespace ChatApp.Client;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] Program.cs - WinForms 클라이언트 "사내톡"의 시작점(Main)
// - 역할: 설정 읽기 → 서버 연결 객체(ChatClient) 생성 → 로그인 창 → 로그인 성공 시 메인 창(친구 목록) 실행.
// - 스레드: Main은 "UI 스레드"다. 모든 창(Form)은 이 스레드에서 만들어지고, 이 스레드만 화면을 바꿀 수 있다.
//           ChatClient가 서버에 연결되면 내부에서 "수신 스레드"를 따로 만든다(서버 메시지 수신 전용).
// - 연결: ClientSettings(설정) → LoginForm(로그인/회원가입) → MainForm(친구 목록, 대화창 관리)
// ─────────────────────────────────────────────────────────────────────────────

internal static class Program
{
    /// <summary>
    /// 실행 흐름: 로그인 창(모달) → 성공하면 채팅 창.
    /// 스레드: UI 스레드(이 Main) + 수신 스레드(ChatClient 내부) 2개.
    /// </summary>
    // [개념] [STAThread] (Single-Threaded Apartment)
    //   - 윈도우의 COM 기술 규칙. 클립보드, 파일 열기 대화상자(OpenFileDialog), 끌어다 놓기(Drag&Drop) 같은 기능은
    //     내부적으로 COM을 쓰는데, 이 기능들은 "STA로 표시된 스레드"에서만 제대로 동작한다.
    //   - 그래서 WinForms 프로그램의 Main에는 항상 [STAThread]를 붙인다. 빠지면 클립보드/대화상자에서 예외가 난다.
    [STAThread]
    private static void Main()
    {
        // [개념] ApplicationConfiguration.Initialize(): .NET 6 이상 WinForms 템플릿의 초기화 코드.
        //   고해상도(DPI) 설정, 기본 글꼴, 비주얼 스타일(윈도우 테마 모양 버튼) 등을 한 번에 켠다. (csproj 설정으로 자동 생성됨)
        ApplicationConfiguration.Initialize();

        var settings = ClientSettings.Load();

        // [주의] UI 스레드에서 처리하지 못한 예외가 나도 프로그램이 조용히 꺼지지 않게, 알려 주고 계속 실행한다.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show($"예상하지 못한 오류가 발생했습니다.\n{e.Exception.Message}", "사내톡", MessageBoxButtons.OK, MessageBoxIcon.Error);

        // 로그아웃하면 로그인 창으로 돌아온다 (프로그램을 다시 켜지 않아도 다른 계정으로 바꿀 수 있다)
        while (true)
        {
            // [개념] using var: 이 변수가 범위를 벗어날 때 자동으로 Dispose()를 불러 준다.
            //   ChatClient.Dispose()는 서버에 로그아웃을 알리고 연결을 닫으므로, 연결이 깔끔하게 정리된다.
            using var client = new ChatClient();

            // [개념] ShowDialog(모달 창): 이 창이 닫힐 때까지 다음 줄로 넘어가지 않고 기다린다(그동안 다른 창 조작 불가).
            //   창이 닫히면 DialogResult(OK/Cancel 등)를 돌려줘서 "사용자가 로그인에 성공했는지" 알 수 있다.
            using (var login = new LoginForm(client, settings))
            {
                if (login.ShowDialog() != DialogResult.OK || client.Me is null)
                    return; // 로그인하지 않고 창을 닫음
            }

            // [개념] Application.Run(form): "메시지 루프"를 시작한다.
            //   윈도우는 클릭·키 입력·다시 그리기 요청을 "메시지"로 UI 스레드의 큐에 넣고, 메시지 루프가 하나씩 꺼내 처리한다.
            //   이 메인 창이 닫히면 루프가 끝나고 Run이 돌아온다.
            // [스레드] 다른 스레드가 BeginInvoke로 넘긴 작업도 이 메시지 큐에 들어가 UI 스레드에서 실행된다.
            using var main = new MainForm(client, settings);
            Application.Run(main);
            if (!main.LogoutRequested) return; // 트레이 "종료" → 프로그램 끝
        }
    }
}
