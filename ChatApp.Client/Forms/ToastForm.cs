using ChatApp.Client.Ui;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ToastForm.cs - 화면 오른쪽 아래에 잠깐 떴다 사라지는 새 메시지 알림 창
// - 역할: 창이 트레이로 숨어 있거나 다른 일을 하는 중일 때 "누가 무슨 말을 했는지" 알려 준다. 누르면 그 대화창을 연다.
// - 스레드: UI 스레드 전용.
// - 연결: MainForm.ShowToast()가 만들고 띄운다. 한 번에 하나만 보이게 이전 알림은 닫는다.
// [왜] 포커스를 빼앗지 않는다(ShowWithoutActivation): 다른 프로그램에 글을 쓰다가 알림 때문에 입력이 끊기면 안 되기 때문.
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class ToastForm : Form
{
    private const int ShowMilliseconds = 5000;

    private readonly string _title;
    private readonly string _text;
    private readonly string _avatarKey;
    private readonly System.Windows.Forms.Timer _closeTimer = new() { Interval = ShowMilliseconds };

    private int S(int v) => LogicalToDeviceUnits(v);

    public ToastForm(string title, string text, string avatarKey)
    {
        _title = title;
        _text = text;
        _avatarKey = avatarKey;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;                      // 다른 창 위에 보이게
        StartPosition = FormStartPosition.Manual;
        Size = new Size(S(320), S(84));
        BackColor = Theme.Surface;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;

        // 작업 표시줄 바로 위, 오른쪽 아래 구석
        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - S(12), area.Bottom - Height - S(12));

        _closeTimer.Tick += (_, _) => Close();
        Shown += (_, _) => _closeTimer.Start();
        // 마우스를 올려 두면 읽는 중이므로 닫지 않는다
        MouseEnter += (_, _) => _closeTimer.Stop();
        MouseLeave += (_, _) => _closeTimer.Start();
        FormClosed += (_, _) => _closeTimer.Dispose();
    }

    /// <summary>[개념] true면 Show() 해도 이 창이 활성(포커스)되지 않는다. 지금 쓰고 있는 프로그램의 입력이 끊기지 않는다.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e); // MainForm이 Click 이벤트로 대화창을 연다
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        Theme.FillGradient(g, new Rectangle(0, 0, S(5), Height)); // 왼쪽 하늘색 띠

        int size = S(42);
        var avatar = new Rectangle(S(18), (Height - size) / 2, size, size);
        Theme.DrawAvatar(g, avatar, _title, Theme.AvatarColor(_avatarKey), _avatarKey);

        int x = avatar.Right + S(12), w = Width - x - S(12);
        TextRenderer.DrawText(g, _title, Theme.BodyBold, new Rectangle(x, S(16), w, S(20)), Theme.Text,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _text, Theme.Body, new Rectangle(x, S(38), w, S(36)), Theme.SubText,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
    }
}
