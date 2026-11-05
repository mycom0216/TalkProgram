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
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   프로필 카드 창. 컨트롤을 쌓는 대신 OnPaint에서 배경·사진·글자를 직접 그리고, 버튼 두 개(아래 막대)만 진짜 컨트롤이다. 이 창은 "무엇을 하고 싶은지"를 ActionRequested 이벤트로 알리기만 하고, 파일 고르기·서버 전송은 메인 창이 한다.
//
// ■ 구조
//   - 표시: 같은 폼을 "내 카드(_isMe)"와 "남의 카드"로 나눠 쓴다. 버튼 글자·동작이 UpdateButtons/클릭 핸들러에서 갈린다.
//       내 카드: [상태 메시지] [프로필 편집 ▾] + 사진 클릭 = 사진 바꾸기, 왼쪽 위 알약 = 배경 바꾸기
//       남의 카드: [1:1 채팅] [친구 추가/삭제]
//   - 그리기 순서(OnPaint): ① 배경(사진 or 그라데이션) ② 아래쪽 어둡게(글자 가독성) ③ 둥근 사각형 사진 + 접속 점/카메라 표시 ④ 이름·상태 메시지·부서 ⑤ 배경 바꾸기 알약
//   - 상호작용: 그릴 때 저장해 둔 사각형(_avatarRect, _backgroundPill)과 마우스 위치를 비교(히트 테스트)해서 클릭/커서 모양을 정한다.
//   - 갱신: 메인 창이 UpdateUser로 새 정보를 넘기거나, AvatarCache.Changed(사진이 도착함) 이벤트가 오면 Invalidate()로 다시 그린다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 이벤트로 "의도"만 알리고 처리는 바깥에서(느슨한 결합) ... "event callback decoupling UI from logic"
//   - 직접 그리기(OnPaint, DoubleBuffered) .................... "WinForms OnPaint custom drawing DoubleBuffered"
//   - GraphicsPath로 둥근 사각형 + 클리핑(SetClip) ............. "GraphicsPath rounded rectangle SetClip DrawImage"
//   - 알파 그라데이션으로 글자 뒤 어둡게 ....................... "LinearGradientBrush alpha overlay text readability"
//   - GDI+ 객체 수명(Font/Brush/Pen의 Dispose) ................ "GDI+ Dispose using Font Brush Pen leak"
//   - 사진 캐시와 변경 알림(AvatarCache.Changed) ............... "image cache change notification invalidate"
//   - 히트 테스트 ............................................ "hit test rectangle mouse click custom control"
//
// ■ 면접 예상 질문과 답
//   Q1. 프로필 사진을 바꾸는 코드가 왜 이 창에 없나요?
//       A. 이 창은 화면 담당이라 "사진 바꾸기를 눌렀다"는 사실만 ActionRequested로 알립니다. 파일 선택·크기 검사·서버 전송은 메인 창이 하므로, 같은 동작을 다른 곳(친구 목록의 내 프로필 등)에서도 재사용할 수 있고 이 창은 서버를 몰라도 됩니다.
//   Q2. 사진이 나중에 도착하면 화면은 어떻게 바뀌나요?
//       A. AvatarCache가 사진을 받으면 Changed 이벤트를 올리고, 이 창은 구독해 두었다가 Invalidate()로 다시 그립니다. 창이 닫히면 구독을 해제(-=)해서 닫힌 창이 이벤트를 받지 않게 합니다.
//   Q3. 직접 그리는 코드에서 메모리 누수를 어떻게 막았나요?
//       A. 한 번만 쓰는 Brush/Pen/Path는 using으로 그때그때 해제하고, 계속 쓰는 글꼴 4개는 필드로 한 번 만들어 FormClosed에서 해제합니다. (그리기마다 new Font를 만들면 GDI 핸들이 쌓입니다.)
//   Q4. 흰 글자가 밝은 사진 위에서도 읽히게 한 방법은요?
//       A. 아래쪽 3/5 영역에 투명→반투명 검정 그라데이션을 덧그려서 사진과 상관없이 글자 뒤가 어둡게 합니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - 아래 버튼 두 개에 넣는 글꼴(new Font(Font, Bold))은 폼이 닫힐 때 해제 목록에 없다. 두 개뿐이고 GC가 정리하므로 큰 문제는 아니지만, 위 글꼴들처럼 맞추면 일관된다.
//   - 고정 크기(340×520, FixedSingle)라 글자가 매우 길면 말줄임(EndEllipsis)으로 잘린다 — 의도된 동작.
//   - 인터넷에서 받은 사진은 AvatarCache가 크기/형식 검사를 하고 이 창은 그리기만 한다(검사 책임이 이 파일에는 없음).
// ───────────────────────────────────────────────────────────────────────────

/// <summary>프로필 카드에서 누른 동작.</summary>
// [읽는 법] 카드에서 가능한 "하고 싶은 일" 목록. 메인 창이 switch로 받아 실제 처리를 연결한다(이 창은 처리하지 않는다).
internal enum ProfileAction
{
    /// <summary>이 사람과 1:1 대화창을 연다.</summary>
    Chat,
    /// <summary>이 사람을 친구로 추가한다.</summary>
    AddFriend,
    /// <summary>이 사람을 친구 목록에서 뺀다 (확인 후).</summary>
    RemoveFriend,
    /// <summary>내 프로필 사진을 바꾼다.</summary>
    ChangePhoto,
    /// <summary>내 프로필 사진을 지운다.</summary>
    RemovePhoto,
    /// <summary>내 프로필 카드의 배경 사진을 바꾼다.</summary>
    ChangeBackground,
    /// <summary>내 프로필 카드의 배경 사진을 지운다.</summary>
    RemoveBackground,
    /// <summary>내 상태 메시지를 바꾼다.</summary>
    ChangeStatusMessage,
}

/// <summary>사람 한 명의 프로필 카드 창. 사진·이름·상태를 그리고, 누른 동작은 ActionRequested로 메인 창에 알린다.</summary>
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
    // [개념] 이벤트 + Action<…> 대리자: "누가 구독하든 상관없이 알리기만" 한다. ?는 구독자가 없을 수 있다는 뜻이라 호출할 때 ?.Invoke를 쓴다.
    public event Action<ProfileAction, UserInfo>? ActionRequested;

    /// <summary>지금 보여 주는 사람의 사원번호.</summary>
    public string EmployeeNo => _user.EmployeeNo;

    /// <summary>프로필 카드를 만든다. isMe는 내 카드인지, isFriend는 친구인지, online은 접속 중인지.</summary>
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
        // [왜] OnPaint에서 배경·사진·글자를 겹쳐 그리므로, 이중 버퍼링으로 메모리에 완성본을 만든 뒤 한 번에 화면에 올려 깜빡임을 없앤다.
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
        // [읽는 법] "이 사람 배경 사진이 필요하다"고 캐시에 알린다. 이미 있으면 그대로, 없으면 받아 온 뒤 Changed가 올라와 다시 그린다. 배경 사진은 용량이 커서 카드를 열 때만 받는다.
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
    // [왜] 카드를 열어 둔 사이 상대가 상태 메시지를 바꾸거나 친구 관계/접속 상태가 달라질 수 있다. 메인 창이 알려 주면 값만 바꾸고 Invalidate()로 다시 그린다.
    public void UpdateUser(UserInfo user, bool isFriend, bool online)
    {
        _user = user;
        _isFriend = isFriend;
        _online = online;
        if (!_isMe) Text = $"{user.DisplayName} 프로필"; // 처음엔 사원번호만 알다가 서버 응답으로 이름이 채워질 수 있다
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

    /// <summary>카드 전체(배경, 사진, 이름, 상태 글)를 직접 그린다.</summary>
    // [읽는 법] 아래 ①~⑤ 순서가 곧 "아래층 → 위층" 그리는 순서다(나중에 그린 것이 위에 보임). 사진/알약의 사각형은 그리는 김에 필드에 저장해 마우스 처리에서 재사용한다.
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
            // [왜] 배경 사진이 없어도 허전하지 않게, 사람마다 정해진 색(AvatarColor)에서 하늘색으로 내려가는 그라데이션을 쓴다. 같은 사람은 항상 같은 색이라 알아보기 쉽다.
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
            // [개념] 클리핑: 둥근 사각형 안쪽에만 그려지게 제한한 뒤 사진을 그리면 모서리가 둥글게 잘린다. Save/Restore로 다른 그리기에 클리핑이 번지지 않게 되돌린다.
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

    // [읽는 법] 네 모서리에 호(Arc)를 하나씩 이어 붙여 둥근 사각형 경로를 만든다. 각도(180·270·0·90)는 왼위 → 오른위 → 오른아래 → 왼아래 순서. 반환한 경로는 호출한 쪽이 using으로 해제한다.
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

    /// <summary>내 카드에서 사진이나 배경 알약 위에 마우스가 있으면 손 모양 커서로 바꾼다.</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _isMe && (_avatarRect.Contains(e.Location) || _backgroundPill.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
    }

    /// <summary>내 카드에서 사진을 누르면 사진 바꾸기, 배경 알약을 누르면 배경 바꾸기를 요청한다.</summary>
    // [읽는 법] 남의 카드는 사진을 눌러도 아무 일도 없다(_isMe가 아니면 return). 내 카드에서만 왼쪽 클릭의 위치로 "사진 바꾸기"/"배경 바꾸기"를 가른다.
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!_isMe || e.Button != MouseButtons.Left) return;
        if (_avatarRect.Contains(e.Location)) Request(ProfileAction.ChangePhoto);
        else if (_backgroundPill.Contains(e.Location)) Request(ProfileAction.ChangeBackground);
    }
}
