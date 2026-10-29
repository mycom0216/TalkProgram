using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;

namespace ChatApp.UiTests;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ChatFlowTests.cs - 사내톡을 실제로 띄워 사람처럼 눌러 보는 화면 자동 테스트 (FlaUI + UI Automation)
// 흐름: 로그인 → (처음 접속) 인증서 지문 확인 → 메인 창 → 새 채팅방 만들기 → 메시지 보내기
//       → 화면 밖의 다른 사용자(소켓)가 그 메시지를 받는지 / 다른 사용자가 보낸 메시지가 화면에 뜨는지 확인
// 준비: 서버를 켜 둔다(MariaDB 포함). 가입 즉시 승인(AutoApprove)이 꺼져 있으면 테스트 계정을 쓸 수 없어 건너뛴다.
// 실행: Visual Studio → 테스트 탐색기 → ChatApp.UiTests, 또는 dotnet test ChatApp.UiTests
// [주의] 테스트가 도는 동안 마우스·키보드를 건드리지 말 것 (실제 입력을 보내므로 포커스가 바뀌면 실패할 수 있다).
// ─────────────────────────────────────────────────────────────────────────────

[Trait("Category", "UI")]
public sealed class ChatFlowTests
{
    [SkippableFact]
    public async Task 로그인하고_방을_만들어_메시지를_주고받는다()
    {
        string? reason = UiTestEnv.ServerUnavailableReason();
        Skip.If(reason is not null, reason);

        // ── 준비: 화면으로 쓸 계정(A)과 화면 밖 관찰자(B)를 서버에 직접 가입시킨다 ──
        string uiUser = UiTestEnv.NewEmployeeNo("UIA");
        string observerNo = UiTestEnv.NewEmployeeNo("UIB");
        using var setup = await SocketUser.ConnectAsync();
        var (okA, msgA) = setup.Register(uiUser, "화면테스터");
        Skip.IfNot(okA && !msgA.Contains("승인"), $"테스트 계정을 바로 쓸 수 없습니다 ({msgA}). 서버 appsettings.json의 AutoApprove를 true로 하고 다시 실행하세요.");
        Assert.True(setup.Register(observerNo, "관찰자").Success);

        using var observer = await SocketUser.ConnectAsync();
        Assert.True(observer.Login(observerNo), "관찰자 로그인 실패");

        using var client = new TestClient();

        // ── 1. 로그인 화면 ──
        Window login = client.FindWindow(w => w.Title.Contains("로그인"))
                       ?? throw new Xunit.Sdk.XunitException("로그인 창이 뜨지 않았습니다.");
        login.SetForeground(); // 키 입력이 다른 창으로 가지 않게 로그인 창을 맨 앞으로
        AutomationElement employeeNo = client.Require(login, "EmployeeNo", "사원번호 칸");
        employeeNo.AsTextBox().Text = uiUser;
        // [주의] 비밀번호 칸(UseSystemPasswordChar)은 UI Automation으로 값을 바로 넣을 수 없다 → 포커스 후 실제 키 입력
        client.Require(login, "Password", "비밀번호 칸").Focus();
        Keyboard.Type(UiTestEnv.Password);
        client.Require(login, "LoginButton", "로그인 버튼").AsButton().Invoke();

        // ── 2. 처음 접속: 서버 인증서 지문 확인 창 → [예] ──
        Assert.True(client.ClickYesIfAsked(login, 15_000), "처음 접속인데 인증서 지문 확인 창이 뜨지 않았습니다.");

        // ── 3. 메인 창 (제목: "사내톡 - 이름 직급") ──
        Window main = client.FindWindow(w => w.Title.StartsWith("사내톡 - ") && w.Title.Contains("화면테스터"))
                      ?? throw new Xunit.Sdk.XunitException("로그인 후 메인 창이 뜨지 않았습니다.");

        // ── 4. 새 채팅방 만들기 ──
        string roomName = "UI테스트" + DateTime.Now.ToString("HHmmss");
        // [채팅방] 탭을 먼저 연다 — 처음에는 [동료] 탭이 보여서 "새 채팅방 만들기" 버튼이 숨겨져 있다(숨긴 컨트롤은 UI Automation에 안 보임)
        client.Require(main, "RoomsTab", "채팅방 탭").AsButton().Invoke();
        // [주의] 모달 창(ShowDialog)을 여는 버튼은 Invoke 대신 실제 마우스 클릭으로 누른다.
        //        WinForms 버튼의 Invoke는 클릭 처리가 끝날 때까지 돌아오지 않는데, 모달 창은 닫힐 때까지 처리가 안 끝나서 테스트가 멈춘다.
        client.Require(main, "CreateRoomButton", "새 채팅방 만들기 버튼").Click();
        Window createForm = UiTestEnv.WaitFor(() => main.ModalWindows.FirstOrDefault()
                                                    ?? client.Windows().FirstOrDefault(w => w.Title == "새 채팅방"), TimeSpan.FromSeconds(10))
                            ?? throw new Xunit.Sdk.XunitException("방 만들기 창이 뜨지 않았습니다.");
        client.Require(createForm, "RoomName", "방 이름 칸").AsTextBox().Text = roomName;
        client.Require(createForm, "CreateRoomOk", "만들기 버튼").AsButton().Invoke();

        // 방을 만들면 그 방 대화창이 자동으로 열린다 (제목: "방이름 - 사내톡")
        Window room = client.FindWindow(w => w.Title.StartsWith(roomName))
                      ?? throw new Xunit.Sdk.XunitException("만든 방의 대화창이 열리지 않았습니다.");

        // ── 5. 관찰자가 그 방에 들어온다 (공개 방이라 목록에서 찾아 입장) ──
        observer.Send(Packet.Create(PacketType.RoomListReq));
        RoomInfo? listed = null;
        Assert.NotNull(observer.WaitFor(PacketType.RoomList, p => (listed = p.GetData<List<RoomInfo>>()?.FirstOrDefault(r => r.Name == roomName)) is not null));
        observer.Send(Packet.Create(PacketType.RoomJoin, target: listed!.RoomId.ToString()));
        Assert.True(observer.WaitFor(PacketType.RoomRes, p => p.GetData<RoomInfo>()?.RoomId == listed.RoomId)?.Success == true, "관찰자 입장 실패");

        // ── 6. 화면에서 메시지 보내기 → 관찰자가 받는다 ──
        string fromUi = "화면에서 보낸 메시지 " + Guid.NewGuid().ToString("N")[..6];
        client.Require(room, "MessageInput", "메시지 입력칸").AsTextBox().Text = fromUi;
        client.Require(room, "SendButton", "전송 버튼").AsButton().Invoke();
        Packet? received = observer.WaitFor(PacketType.ChatMsg, p => p.Body == fromUi);
        Assert.NotNull(received);
        Assert.True(received!.MessageId > 0, "서버가 메시지 번호를 매기지 않았습니다.");

        // 내 화면에도 보낸 말풍선이 생긴다 (말풍선 화면의 이름 = "대화 N개 · 마지막: 나: 내용")
        AutomationElement chatView = client.Require(room, "ChatView", "대화 화면");
        Assert.NotNull(UiTestEnv.WaitFor(() => chatView.Name.Contains(fromUi) ? chatView : null, TimeSpan.FromSeconds(10)));

        // ── 7. 관찰자가 보낸 메시지 → 화면에 뜬다 ──
        string fromObserver = "관찰자가 보낸 메시지 " + Guid.NewGuid().ToString("N")[..6];
        observer.Send(Packet.Create(PacketType.ChatMsg, fromObserver, target: listed.RoomId.ToString()));
        Assert.NotNull(UiTestEnv.WaitFor(() => chatView.Name.Contains(fromObserver) ? chatView : null, TimeSpan.FromSeconds(10)));

        // 입력칸은 보낸 뒤 비워진다
        Assert.Equal("", client.Require(room, "MessageInput", "메시지 입력칸").AsTextBox().Text);
    }

    [SkippableFact]
    public void 틀린_비밀번호면_로그인_창에_안내가_뜬다()
    {
        string? reason = UiTestEnv.ServerUnavailableReason();
        Skip.If(reason is not null, reason);
        using var client = new TestClient();

        Window login = client.FindWindow(w => w.Title.Contains("로그인"))
                       ?? throw new Xunit.Sdk.XunitException("로그인 창이 뜨지 않았습니다.");
        login.SetForeground();
        client.Require(login, "EmployeeNo", "사원번호 칸").AsTextBox().Text = UiTestEnv.NewEmployeeNo("NOPE");
        client.Require(login, "Password", "비밀번호 칸").Focus();
        Keyboard.Type("WrongPass123!");
        client.Require(login, "LoginButton", "로그인 버튼").AsButton().Invoke();
        client.ClickYesIfAsked(login, 10_000); // 처음 접속이면 지문 확인

        // 로그인 창이 그대로 있고, 상태 문구가 실패를 알린다 (메인 창으로 넘어가지 않는다)
        AutomationElement status = client.Require(login, "LoginStatus", "로그인 상태 문구");
        string? text = UiTestEnv.WaitFor(() => status.Name is { Length: > 0 } t && !t.Contains("로그인 중") ? t : null, TimeSpan.FromSeconds(10));
        Assert.NotNull(text);
        Assert.Contains("사원번호 또는 비밀번호", text);
        Assert.DoesNotContain(client.Windows(), w => w.Title.StartsWith("사내톡 - ") && !w.Title.Contains("로그인"));
    }
}
