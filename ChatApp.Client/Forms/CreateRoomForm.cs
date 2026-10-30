using ChatApp.Common.Models;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] CreateRoomForm.cs - "새 채팅방 만들기" 대화상자
// - 역할: 방 이름과 최대 인원을 입력받는다. 서버에 직접 보내지는 않고, 입력값만 MainForm에 돌려준다.
// - 스레드: UI 스레드에서만 실행된다(네트워크와 무관한 순수 화면).
// - 연결: MainForm.CreateRoom()이 ShowDialog로 띄움 → OK면 RoomName/MaxUsers를 읽어 RoomCreate 패킷을 보낸다.
// [왜] 화면(입력)과 통신(전송)을 분리하면 이 창은 서버를 몰라도 되어 단순해지고, 재사용·테스트가 쉽다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   "새 채팅방" 입력 창. 방 이름·최대 인원·비공개 여부만 받고, 서버 전송은 하지 않는다. 확인(OK)으로 닫히면 부르는 쪽(MainForm.Rooms)이 RoomName / MaxUsers / IsPrivate를 읽어 RoomCreate 패킷을 보낸다. 이 파일은 "화면과 통신을 분리"한 가장 단순한 예다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 입력 대화상자 패턴(ShowDialog + DialogResult + 속성으로 값 반환) .. "WinForms modal dialog DialogResult properties return values"
//   - 상수를 Common에 두고 화면·서버가 같이 쓰기 ..................... "shared constants client server validation single source of truth"
//   - NumericUpDown의 Minimum/Maximum ............................ "NumericUpDown Minimum Maximum decimal"
//   - AcceptButton / CancelButton (Enter / Esc) .................... "WinForms AcceptButton CancelButton"
//   - AutoSize + GrowAndShrink (DPI·글꼴 변화 대응) ................ "WinForms AutoSize GrowAndShrink high DPI"
//   - UI 자동 테스트용 이름(Name = AutomationId) ................... "UI Automation AutomationId WinForms Name"
//
// ■ 면접 예상 질문과 답
//   Q1. 이 창이 서버에 직접 요청을 보내지 않고 값만 돌려주는 이유는요?
//       A. 화면과 통신을 나누면 이 창은 서버를 몰라도 되어 단순해지고, 같은 창을 다른 곳에서 재사용하거나 따로 테스트하기 쉽습니다. 응답(방 생성 성공/실패) 처리는 MainForm이 한곳에서 합니다.
//   Q2. 이름 길이·인원 범위의 검사는 어디서 하나요?
//       A. 화면은 CreateRoomRequest의 상수(MaxNameLength 30, MinUsers 2, MaxUsersLimit 1000)로 입력을 제한하고, 서버도 같은 상수로 다시 검사합니다. 상수를 Common 한 곳에 두어 화면과 서버가 어긋나지 않게 했습니다. 빈 이름만 화면이 직접 막고 나머지는 컨트롤의 제한이 처리합니다.
//   Q3. 비공개 방은 어떻게 동작하나요?
//       A. 이 창은 체크 값(IsPrivate)만 넘기고, 목록에 숨기고 초대한 사람만 들어오게 하는 규칙은 서버가 적용합니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - 최대 인원의 기본값(DefaultMaxUsers)이 상한(1000)과 같다. 즉 아무것도 바꾸지 않으면 사실상 인원 제한이 없는 방이 만들어진다. 팀 방에는 작은 기본값이 어울릴 수 있다(정책 선택이므로 코드는 그대로 둠).
//   - 이름 앞뒤 공백은 RoomName 속성에서 Trim하지만, 이름 중복 검사는 서버 몫이다(이 창은 모름).
// ───────────────────────────────────────────────────────────────────────────

/// <summary>방 만들기 창. 확인을 누르면 DialogResult.OK, RoomName·MaxUsers·IsPrivate에 입력값.</summary>
public sealed class CreateRoomForm : Form
{
    // [왜] 최대 글자 수·인원 범위를 Common의 CreateRoomRequest 상수에서 가져온다.
    //   서버도 같은 상수로 검사하므로, 화면 제한과 서버 제한이 어긋날 일이 없다(값을 한 곳에서만 관리).
    private readonly TextBox _name = new() { MaxLength = CreateRoomRequest.MaxNameLength };

    // [개념] NumericUpDown: 숫자만 입력되는 상자(위/아래 화살표 포함). Minimum~Maximum 밖의 값은 자동으로 막아 준다.
    // [주의] NumericUpDown.Value는 decimal이다. 범위(Minimum~Maximum)는 값을 "직접 타이핑"한 경우에는 포커스를 잃을 때 맞춰지므로, 읽을 때 (int)로 바꾼 값이 범위 안인지는 컨트롤이 보장한다고 본다.
    private readonly NumericUpDown _maxUsers = new()
    {
        Minimum = CreateRoomRequest.MinUsers,
        Maximum = CreateRoomRequest.MaxUsersLimit,
        Value = CreateRoomRequest.DefaultMaxUsers,
    };
    // 비공개 방: 방 목록에 보이지 않고, 멤버가 초대한 사람만 들어올 수 있다 (팀 방, 프로젝트 방 등)
    private readonly CheckBox _private = new() { Text = "비공개 방 (초대한 사람만 참여, 목록에 안 보임)", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = UiHelper.Danger, Margin = new Padding(0, 8, 0, 0) };

    // [개념] => 로 만든 읽기 전용 속성: 읽을 때마다 현재 입력값을 계산해서 돌려준다.
    // [읽는 법] 아래 세 속성이 이 창의 "출력값"이다. 창이 닫힌 뒤에도(Dispose 전까지) 읽을 수 있어서, 부르는 쪽이 using var form 블록 안에서 OK일 때 읽는다.
    public string RoomName => _name.Text.Trim();
    // NumericUpDown.Value는 decimal 형식이라 int로 바꿔서 돌려준다.
    public int MaxUsers => (int)_maxUsers.Value;
    public bool IsPrivate => _private.Checked;

    public CreateRoomForm()
    {
        Text = "새 채팅방";
        Font = UiHelper.BaseFont;
        // [왜] FixedDialog + 최대화/최소화 버튼 없음 + 작업 표시줄에 안 보임 = 전형적인 "대화상자" 모양.
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent; // 부모 창(친구 목록) 가운데에 뜬다
        // [개념] AutoSize + GrowAndShrink: 창 크기를 안의 내용(표) 크기에 딱 맞춘다. 글꼴/DPI가 달라도 잘리지 않는다.
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        var create = UiHelper.CreateButton("만들기", primary: true);
        // 화면 자동 테스트(UI Automation)가 찾는 이름 (AutomationId)
        _name.Name = "RoomName";
        create.Name = "CreateRoomOk";
        var cancel = UiHelper.CreateButton("취소");

        // 화면 배치: [방 이름 | 입력칸] / [최대 인원 | 숫자] / [취소 만들기] / [오류 문구]
        var table = UiHelper.CreateFormTable();
        UiHelper.AddFieldRow(table, "방 이름", _name, 220);
        UiHelper.AddFieldRow(table, "최대 인원", _maxUsers, 80);
        UiHelper.AddSpanRow(table, _private);
        UiHelper.AddSpanRow(table, UiHelper.CreateButtonRow(cancel, create));
        UiHelper.AddSpanRow(table, _status);
        Controls.Add(table);
        UiHelper.AddDialogHeader(this, table, "새 채팅방 만들기");

        // [개념] AcceptButton = Enter 키를 누르면 클릭되는 버튼, CancelButton = Esc 키를 누르면 클릭되는 버튼.
        AcceptButton = create;
        CancelButton = cancel;

        // [개념] 람다식 이벤트 처리기 (_, _) => { ... }
        //   Click 이벤트는 (object sender, EventArgs e) 두 값을 넘겨주는데, 여기서는 쓰지 않으므로 버림 표시(_)로 받는다.
        // [읽는 법] OK 경로: 빈 이름만 막고 DialogResult.OK로 닫는다. 길이·범위는 컨트롤의 MaxLength/Minimum/Maximum이 이미 제한했다.
        create.Click += (_, _) =>
        {
            // 빈 이름은 서버까지 보내지 않고 화면에서 먼저 막는다(불필요한 통신 줄이기). 최종 검사는 서버가 다시 한다.
            if (RoomName.Length == 0)
            {
                _status.Text = "방 이름을 입력하세요.";
                _name.Focus();
                return;
            }
            // [개념] DialogResult를 OK로 정하고 닫으면, 이 창을 연 쪽의 ShowDialog()가 OK를 돌려받는다.
            DialogResult = DialogResult.OK;
            Close();
        };
        // 취소: DialogResult를 정하지 않고 닫으면 ShowDialog()는 Cancel을 돌려준다.
        cancel.Click += (_, _) => Close();
    }
}
