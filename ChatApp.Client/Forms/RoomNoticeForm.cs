namespace ChatApp.Client.Forms;

/// <summary>방 상단에 고정할 공지를 입력하거나 지우는 창.</summary>
internal sealed class RoomNoticeForm : Form
{
    private readonly TextBox _text = new() { Multiline = true, AcceptsReturn = true, MaxLength = 200, ScrollBars = ScrollBars.Vertical };
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
        clear.Click += (_, _) => { _text.Clear(); DialogResult = DialogResult.OK; Close(); };
        Controls.Add(_text);
        Controls.Add(actions);
        Controls.Add(hint);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
