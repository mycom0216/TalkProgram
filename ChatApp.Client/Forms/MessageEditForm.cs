namespace ChatApp.Client.Forms;

//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   보낸 메시지를 고치는 작은 입력 창. 원래 글을 채워 보여 주고, 저장하면 EditedText를 돌려준다. 실제 수정 요청(MessageEdit)과 5분 제한 확인은 서버가 한다.
//
// ■ 수정 규칙 (코드로 확인함)
//   - 서버 MessageService의 EditWindow = 5분. 보낸 지 5분이 지나면 서버가 거절한다. 이 창의 안내 문구("5분 안에만")는 그 값을 손으로 적은 것이라, 서버 값이 바뀌면 같이 고쳐야 한다.
//   - 글자 수는 클라이언트 입력 제한(500자)과 같다. 최종 검사는 서버가 한다.
//
// ■ 면접 예상 질문과 답
//   Q1. 수정 가능 시간 제한은 어디서 지키나요?
//       A. 서버가 메시지의 보낸 시각과 현재 시각을 비교해서(EditWindow 5분) 지킵니다. 화면은 안내 문구와 메뉴 표시만 하고, 클라이언트 시계를 믿지 않습니다.
//   Q2. 수정한 글이 다른 사람에게는 어떻게 보이나요?
//       A. 서버가 MessageEdited를 방 사람들에게 보내면 각 클라이언트가 그 말풍선의 글을 바꾸고 "(수정됨)"을 붙여 보여 줍니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - 빈 글로 저장하는 것을 이 창이 막지는 않는다(부르는 쪽 또는 서버가 거절한다고 보임 — 부르는 쪽 코드는 이 파일에서 보이지 않음).
//   - 창이 열려 있는 사이 5분이 지나면 저장 때 서버가 거절하고 오류가 보인다.
//
// ───────────────────────────────────────────────────────────────────────────

/// <summary>짧은 시간 안에 보낸 메시지 내용을 고치는 입력 창.</summary>
internal sealed class MessageEditForm : Form
{
    private readonly TextBox _text = new() { Multiline = true, AcceptsReturn = true, MaxLength = 500, ScrollBars = ScrollBars.Vertical };
    /// <summary>고친 메시지 내용 (앞뒤 공백 제거). 저장(OK)으로 닫혔을 때만 읽는다.</summary>
    public string EditedText => _text.Text.Trim();

    /// <summary>메시지 수정 창을 만든다. original은 입력칸에 처음 채울 원래 메시지 내용.</summary>
    public MessageEditForm(string original)
    {
        Text = "메시지 수정";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(420, 210);
        _text.Text = original;
        _text.Dock = DockStyle.Fill;
        var hint = new Label { Text = "메시지는 보낸 뒤 5분 안에만 수정할 수 있습니다.", Dock = DockStyle.Top, Height = 32, TextAlign = ContentAlignment.MiddleLeft };
        var save = new Button { Text = "저장", DialogResult = DialogResult.OK, Width = 80, Height = 30 };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Width = 80, Height = 30 };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 5, 0, 0) };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        Controls.Add(_text);
        Controls.Add(actions);
        Controls.Add(hint);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
