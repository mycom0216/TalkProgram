namespace ChatApp.Client.Forms;

//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   방 상단에 고정할 공지를 입력/수정/삭제하는 작은 입력 창. 서버로 보내지 않고 입력값(NoticeText)만 돌려주며, 전송(RoomNotice)은 부르는 쪽이 한다. [공지 지우기]는 입력을 비우고 OK로 닫는다 → 빈 글 = 공지 삭제로 약속되어 있다.
//
// ■ 알아 둘 점
//   - 글자 수 제한(200자)은 이 창의 MaxLength와 서버의 RoomNoticeRequest.MaxLength에 따로 적혀 있다. 서버가 최종 검사하고 초과 시 거절한다(코드로 확인함). 한 값(상수)을 같이 쓰면 어긋날 일이 없다.
//   - 이 창은 UiHelper를 쓰지 않고 컨트롤을 직접 만들어 크기도 원시 픽셀이다(다른 창과 모양이 조금 다를 수 있음).
//   - 공지 권한(방장/관리자만)은 서버가 판단한다. 이 창을 열 수 있는 사람의 제한은 메뉴 쪽 편의다.
//
// ■ 면접 예상 질문과 답
//   Q1. "공지 삭제"를 별도 요청으로 만들지 않은 이유는요?
//       A. 빈 문자열을 "삭제"로 약속해서 요청 종류를 늘리지 않았습니다. 단순하지만, 클라이언트와 서버가 같은 약속을 알아야 하고 실수로 빈 값을 보내면 공지가 지워지는 점은 주의해야 합니다.
//
// ───────────────────────────────────────────────────────────────────────────

/// <summary>방 상단에 고정할 공지를 입력하거나 지우는 창.</summary>
internal sealed class RoomNoticeForm : Form
{
    private readonly TextBox _text = new() { Multiline = true, AcceptsReturn = true, MaxLength = 200, ScrollBars = ScrollBars.Vertical };
    // [읽는 법] 출력값. DialogResult.OK로 닫혔을 때 부르는 쪽이 읽는다. 빈 문자열이면 공지 삭제로 처리된다.
    public string NoticeText => _text.Text.Trim();

    public RoomNoticeForm(string roomName, string current)
    {
        Text = "방 공지 고정";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, 230);

        var hint = new Label { Text = $"'{roomName}' 방 상단에 표시할 공지 (최대 200자)", Dock = DockStyle.Top, Height = 38, TextAlign = ContentAlignment.MiddleLeft };
        _text.Text = current;
        _text.Dock = DockStyle.Fill;
        var save = new Button { Text = "고정하기", DialogResult = DialogResult.OK, Width = 90, Height = 30 };
        var clear = new Button { Text = "공지 지우기", Width = 90, Height = 30 };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Width = 75, Height = 30 };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        actions.Controls.Add(clear);
        // [읽는 법] [공지 지우기]는 DialogResult를 버튼 속성이 아니라 코드에서 OK로 정한다(입력을 비운 뒤 OK로 닫아야 부르는 쪽이 빈 NoticeText를 읽는다).
        clear.Click += (_, _) => { _text.Clear(); DialogResult = DialogResult.OK; Close(); };
        Controls.Add(_text);
        Controls.Add(actions);
        Controls.Add(hint);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
