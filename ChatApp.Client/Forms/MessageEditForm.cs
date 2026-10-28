namespace ChatApp.Client.Forms;

/// <summary>짧은 시간 안에 보낸 메시지 내용을 고치는 입력 창.</summary>
internal sealed class MessageEditForm : Form
{
    private readonly TextBox _text = new() { Multiline = true, AcceptsReturn = true, MaxLength = 500, ScrollBars = ScrollBars.Vertical };
    public string EditedText => _text.Text.Trim();

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
