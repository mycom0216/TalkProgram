using ChatApp.Common.Models;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] StatusMessageForm.cs - 상태 메시지 입력 대화상자 (예: "오후 반차", "내선 1234")
// - 역할: 글만 입력받고, 서버 전송은 MainForm이 한다(CreateRoomForm과 같은 구조).
// - 스레드: UI 스레드 전용.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class StatusMessageForm : Form
{
    private readonly TextBox _message = new() { MaxLength = StatusUpdate.MaxMessageLength };

    public string Message => _message.Text.Trim();

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
        clear.Click += (_, _) => { _message.Clear(); DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => Close();
        Shown += (_, _) => { _message.Focus(); _message.SelectAll(); };
    }
}
