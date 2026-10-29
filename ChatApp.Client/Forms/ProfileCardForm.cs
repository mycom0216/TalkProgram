using System.Drawing.Drawing2D;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ProfileCardForm.cs - 프로필 카드 (카카오톡의 "프로필 보기" 같은 창)
// - 배경 사진(없으면 그라데이션) 위에 둥근 사각형 프로필 사진, 이름·직급, 상태 메시지, 부서·사원번호를 보여 준다.
// - 내 카드: 프로필 사진을 누르면 바꾸기, 왼쪽 위 [배경 사진 바꾸기], 아래 [상태 메시지] [프로필 편집▾]
//   다른 사람 카드: 아래 [1:1 채팅] [친구 추가 / 친구 삭제]
// - 실제 동작(파일 고르기·서버 전송)은 메인 창이 한다. 이 창은 ActionRequested로 "무엇을 하고 싶은지"만 알린다.
// - 배경 사진은 이 창을 열 때만 받아 온다(AvatarCache.NoteBackground). 받으면 Changed로 다시 그린다.
// - 스레드: 모두 UI 스레드.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>프로필 카드에서 누른 동작.</summary>
internal enum ProfileAction
{
    Chat,
    AddFriend,
    RemoveFriend,
    ChangePhoto,
    RemovePhoto,
    ChangeBackground,
    RemoveBackground,
    ChangeStatusMessage,
}

internal sealed class ProfileCardForm : Form
{
    private readonly AvatarCache _avatars;
    private readonly bool _isMe;
    private UserInfo _user;
    private bool _isFriend, _online;

    private readonly Panel _bar = new() { Dock = DockStyle.Bottom, BackColor = Color.FromArgb(28, 28, 30) };
    private readonly Button _left = new(), _right = new();
    private readonly ContextMenuStrip _editMenu = new();
    private readonly Font _nameFont, _textFont, _smallFont, _pillFont;
    private Rectangle _avatarRect, _backgroundPill;

    /// <summary>카드에서 고른 동작 (메인 창이 처리한다).</summary>
    public event Action<ProfileAction, UserInfo>? ActionRequested;

    /// <summary>지금 보여 주는 사람의 사원번호.</summary>
    public string EmployeeNo => _user.EmployeeNo;

    public ProfileCardForm(UserInfo user, bool isMe, bool isFriend, bool online, AvatarCache avatars)
    {
        _user = user;
        _isMe = isMe;
        _isFriend = isFriend;
        _online = online;
        _avatars = avatars;

        Text = isMe ? "내 프로필" : $"{user.DisplayName} 프로필";
        Font = UiHelper.BaseFont;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(28, 28, 30);
        DoubleBuffered = true;
        KeyPreview = true;
        ClientSize = LogicalToDeviceUnits(new Size(340, 520));
        Name = "ProfileCard";

        _nameFont = new Font(Font.FontFamily, Font.Size * 1.55f, FontStyle.Bold);
        _textFont = new Font(Font.FontFamily, Font.Size * 1.0f);
        _smallFont = new Font(Font.FontFamily, Font.Size * 0.88f);
        _pillFont = new Font(Font.FontFamily, Font.Size * 0.85f, FontStyle.Bold);

        // 아래 버튼 두 개
        _bar.Height = LogicalToDeviceUnits(64);
        _bar.Padding = new Padding(LogicalToDeviceUnits(16), LogicalToDeviceUnits(12), LogicalToDeviceUnits(16), LogicalToDeviceUnits(14));
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = _bar.BackColor };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (Button b in new[] { _left, _right })
        {
            b.Dock = DockStyle.Fill;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 84);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(64, 64, 68);
            b.BackColor = Color.FromArgb(46, 46, 50);
            b.ForeColor = Color.White;
            b.Font = new Font(Font, FontStyle.Bold);
            b.Margin = new Padding(LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(4), 0);
            b.Cursor = Cursors.Hand;
        }
        _left.Name = "ProfileLeftButton";
        _right.Name = "ProfileRightButton";
        row.Controls.Add(_left, 0, 0);
        row.Controls.Add(_right, 1, 0);
        _bar.Controls.Add(row);
        Controls.Add(_bar);

        _left.Click += (_, _) => Request(_isMe ? ProfileAction.ChangeStatusMessage : ProfileAction.Chat);
        _right.Click += (_, _) =>
        {
            if (_isMe) _editMenu.Show(_right, new Point(0, _right.Height));
            else Request(_isFriend ? ProfileAction.RemoveFriend : ProfileAction.AddFriend);
        };

        // 내 카드의 [프로필 편집] 메뉴
        _editMenu.Items.Add("프로필 사진 바꾸기...", null, (_, _) => Request(ProfileAction.ChangePhoto));
        _editMenu.Items.Add("프로필 사진 지우기", null, (_, _) => Request(ProfileAction.RemovePhoto));
        _editMenu.Items.Add(new ToolStripSeparator());
        _editMenu.Items.Add("배경 사진 바꾸기...", null, (_, _) => Request(ProfileAction.ChangeBackground));
        _editMenu.Items.Add("배경 사진 지우기", null, (_, _) => Request(ProfileAction.RemoveBackground));
        _editMenu.Items.Add(new ToolStripSeparator());
        _editMenu.Items.Add("상태 메시지 바꾸기...", null, (_, _) => Request(ProfileAction.ChangeStatusMessage));

        UpdateButtons();
        _avatars.NoteBackground(user); // 배경 사진은 카드를 열 때만 받아 온다
        _avatars.Changed += OnPhotosChanged;
        FormClosed += (_, _) =>
        {
            _avatars.Changed -= OnPhotosChanged;
            _editMenu.Dispose();
            _nameFont.Dispose();
            _textFont.Dispose();
            _smallFont.Dispose();
            _pillFont.Dispose();
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    /// <summary>열려 있는 동안 상대 정보(상태 메시지·사진 버전·친구 여부)가 바뀌면 메인 창이 불러 준다.</summary>
    public void UpdateUser(UserInfo user, bool isFriend, bool online)
    {
        _user = user;
        _isFriend = isFriend;
        _online = online;
        _avatars.NoteBackground(user);
        UpdateButtons();
        Invalidate();
    }

    private void OnPhotosChanged() => Invalidate();

    private void Request(ProfileAction action) => ActionRequested?.Invoke(action, _user);

    private void UpdateButtons()
    {
        _left.Text = _isMe ? "상태 메시지" : "1:1 채팅";
        _right.Text = _isMe ? "프로필 편집 ▾" : _isFriend ? "친구 삭제" : "친구 추가";
    }

    private int S(int value) => LogicalToDeviceUnits(value);

    // ───────── 그리기 ─────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var area = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height - _bar.Height);

        // ① 배경: 사진이 있으면 꽉 채워 그리고, 없으면 그 사람 색에서 하늘색으로 내려가는 그라데이션
        if (_avatars.GetBackground(_user.EmployeeNo) is { } background)
        {
            ImageTools.DrawCover(g, background, area);
        }
        else
        {
            Color top = ControlPaint.Dark(Theme.AvatarColor(_user.EmployeeNo), 0.1f);
            using var gradient = new LinearGradientBrush(area, top, Theme.SkyDark, LinearGradientMode.Vertical);
            g.FillRectangle(gradient, area);
        }
        // ② 아래쪽을 어둡게 (사진이 밝아도 흰 글자가 읽히게)
        var shade = new Rectangle(0, area.Height * 2 / 5, area.Width, area.Height - area.Height * 2 / 5 + 1);
        using (var dark = new LinearGradientBrush(shade, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(200, 0, 0, 0), LinearGradientMode.Vertical))
            g.FillRectangle(dark, shade);

        // ③ 프로필 사진 (둥근 사각형)
        int size = S(96);
        int textBlock = S(92);
        _avatarRect = new Rectangle(S(24), area.Bottom - S(18) - textBlock - size, size, size);
        DrawAvatar(g, _avatarRect);
        if (_online && !_isMe) // 내 카드는 그 자리에 연필 표시가 있다
        {
            int dot = S(16);
            Theme.DrawStatusDot(g, new Rectangle(_avatarRect.Right - dot + S(2), _avatarRect.Bottom - dot + S(2), dot, dot),
                Theme.PresenceColor(_user.Presence));
        }
        if (_isMe) DrawCameraBadge(g, _avatarRect);

        // ④ 이름 · 상태 메시지 · 부서
        int x = S(24), w = area.Width - S(48);
        var flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        int y = _avatarRect.Bottom + S(12);
        TextRenderer.DrawText(g, _user.DisplayName, _nameFont, new Rectangle(x, y, w, _nameFont.Height + S(4)), Color.White, flags);
        y += _nameFont.Height + S(6);
        string status = _user.StatusMessage.Length > 0 ? _user.StatusMessage : _isMe ? "상태 메시지를 적어 보세요" : "";
        if (status.Length > 0)
        {
            Color statusColor = _user.StatusMessage.Length > 0 ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(140, 255, 255, 255);
            TextRenderer.DrawText(g, status, _textFont, new Rectangle(x, y, w, _textFont.Height + S(2)), statusColor, flags);
        }
        y += _textFont.Height + S(6);
        string presence = _online ? Theme.PresenceText(_user.Presence) : "접속하지 않음";
        TextRenderer.DrawText(g, $"{_user.Department} · {_user.EmployeeNo} · {presence}", _smallFont,
            new Rectangle(x, y, w, _smallFont.Height + S(2)), Color.FromArgb(170, 255, 255, 255), flags);

        // ⑤ 내 카드: 왼쪽 위 [배경 사진 바꾸기]
        _backgroundPill = Rectangle.Empty;
        if (_isMe)
        {
            const string label = "배경 사진 바꾸기";
            Size text = TextRenderer.MeasureText(label, _pillFont);
            _backgroundPill = new Rectangle(S(14), S(14), text.Width + S(20), text.Height + S(10));
            using (var path = Rounded(_backgroundPill, _backgroundPill.Height / 2))
            using (var fill = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                g.FillPath(fill, path);
            TextRenderer.DrawText(g, label, _pillFont, _backgroundPill, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    private void DrawAvatar(Graphics g, Rectangle r)
    {
        int radius = S(30);
        using GraphicsPath path = Rounded(r, radius);
        if (_avatars.Get(_user.EmployeeNo) is { } photo)
        {
            GraphicsState state = g.Save();
            g.SetClip(path);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(photo, r);
            g.Restore(state);
        }
        else
        {
            using (var fill = new SolidBrush(Theme.AvatarColor(_user.EmployeeNo))) g.FillPath(fill, path);
            string initial = _user.Name.Length > 0 ? _user.Name[..1] : "?";
            using var font = new Font(Font.FontFamily, r.Height * 0.40f, FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(g, initial, font, r, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        using var border = new Pen(Color.FromArgb(70, 255, 255, 255));
        g.DrawPath(border, path);
    }

    /// <summary>내 사진 오른쪽 아래의 작은 동그라미 (누르면 바꿀 수 있다는 표시).</summary>
    private void DrawCameraBadge(Graphics g, Rectangle avatar)
    {
        int d = S(26);
        var badge = new Rectangle(avatar.Right - d + S(6), avatar.Bottom - d + S(6), d, d);
        using (var fill = new SolidBrush(Color.White)) g.FillEllipse(fill, badge);
        using (var pen = new Pen(Color.FromArgb(60, 0, 0, 0))) g.DrawEllipse(pen, badge);
        TextRenderer.DrawText(g, "✎", _pillFont, badge, Color.FromArgb(40, 40, 40),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ───────── 마우스 (내 카드: 사진 누르면 바꾸기, 왼쪽 위 알약 누르면 배경 바꾸기) ─────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _isMe && (_avatarRect.Contains(e.Location) || _backgroundPill.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!_isMe || e.Button != MouseButtons.Left) return;
        if (_avatarRect.Contains(e.Location)) Request(ProfileAction.ChangePhoto);
        else if (_backgroundPill.Contains(e.Location)) Request(ProfileAction.ChangeBackground);
    }
}
