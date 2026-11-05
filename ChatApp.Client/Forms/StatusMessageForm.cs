using ChatApp.Common.Models;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] StatusMessageForm.cs - 상태 메시지 입력 대화상자 (예: "오후 반차", "내선 1234")
// - 역할: 글만 입력받고, 서버 전송은 MainForm이 한다(CreateRoomForm과 같은 구조).
// - 스레드: UI 스레드 전용.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   상태 메시지("오후 반차", "내선 1234")를 입력받는 창. CreateRoomForm과 같은 구조(입력만 받고 값은 속성으로 돌려줌, 전송은 MainForm). [지우기]는 입력을 비우고 OK로 닫는다 → 빈 문자열 = 상태 메시지 삭제.
//
// ■ 면접 예상 질문과 답
//   Q1. 상태 메시지는 다른 사람에게 어떻게 보이나요?
//       A. 이 창은 글만 돌려주고, MainForm이 StatusUpdate로 서버에 보냅니다. 서버가 접속자 목록을 새로 만들어 모두에게 보내면 각 클라이언트의 동료 목록에서 이름 아래에 표시됩니다(StatusUpdate는 접속 상태(온라인/자리 비움 등)와 같은 패킷).
//   Q2. 길이 제한은 어디서 지키나요?
//       A. 화면은 StatusUpdate.MaxMessageLength로 입력을 막고, 서버도 같은 상수로 검사합니다(Common의 상수를 공유).
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - 저장과 지우기가 둘 다 OK로 닫히므로, 부르는 쪽은 Message가 빈 문자열인지로 삭제 여부를 구분한다.
// ───────────────────────────────────────────────────────────────────────────

/// <summary>내 상태 메시지를 입력하거나 지우는 창.</summary>
public sealed class StatusMessageForm : Form
{
    private readonly TextBox _message = new() { MaxLength = StatusUpdate.MaxMessageLength };

    /// <summary>입력한 상태 메시지 (앞뒤 공백 제거). 빈 문자열이면 상태 메시지를 지운다는 뜻이다.</summary>
    // [읽는 법] 출력값(앞뒤 공백 제거). 빈 문자열이면 상태 메시지를 지우는 것이다.
    public string Message => _message.Text.Trim();

    /// <summary>상태 메시지 창을 만든다. current는 입력칸에 처음 채울 현재 상태 메시지.</summary>
    public StatusMessageForm(string current)
    {
        Text = "상태 메시지";
        Font = UiHelper.BaseFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;
        _message.Text = current;

        var save = UiHelper.CreateButton("저장", primary: true);
        var clear = UiHelper.CreateButton("지우기");
        var cancel = UiHelper.CreateButton("취소");

        var table = UiHelper.CreateFormTable();
        UiHelper.AddFieldRow(table, "상태 메시지", _message, 240);
        UiHelper.AddSpanRow(table, new Label
        {
            Text = $"동료 목록에서 내 이름 아래에 보입니다. (최대 {StatusUpdate.MaxMessageLength}자)",
            ForeColor = UiHelper.Muted,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0),
        });
        UiHelper.AddSpanRow(table, UiHelper.CreateButtonRow(cancel, clear, save));
        Controls.Add(table);
        UiHelper.AddDialogHeader(this, table, "상태 메시지");

        AcceptButton = save;
        CancelButton = cancel;
        save.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        // [읽는 법] [지우기]: 입력을 비운 뒤 OK로 닫는다(그래야 부르는 쪽이 빈 Message를 읽어 서버에 "삭제"로 보낸다).
        clear.Click += (_, _) => { _message.Clear(); DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => Close();
        Shown += (_, _) => { _message.Focus(); _message.SelectAll(); };
    }
}
