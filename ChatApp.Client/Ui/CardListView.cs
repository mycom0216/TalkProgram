// ─────────────────────────────────────────────────────────────────────────────
// CardListView.cs
// 역할  : 메인 창의 "동료 목록"과 "채팅방 목록"을 보여 주는 카드형 목록 컨트롤.
//         한 줄 = [아바타(+접속 점)] [이름 / 설명] [배지(안 읽은 수 또는 인원)].
//         부서 머리줄(IsHeader)을 클릭하면 그 부서를 접고 펼 수 있다.
// 스레드: UI 스레드 전용. MainForm이 수신 스레드에서 받은 패킷을 BeginInvoke로 UI 스레드에 넘긴 뒤 SetItems를 부른다.
// 연결  : MainForm(RefreshUserList / RefreshRoomList)이 CardItem 목록을 만들어 SetItems로 넘기고,
//         ItemActivated 이벤트(더블클릭·Enter)를 받아 1:1 창을 열거나 방에 들어간다.
//         색·글꼴·아바타 그리기는 Theme을 사용한다.
// ─────────────────────────────────────────────────────────────────────────────
using System.ComponentModel;

namespace ChatApp.Client.Ui;

/// <summary>목록 한 줄. IsHeader면 "개발팀 (3)" 같은 그룹 머리줄(클릭하면 접기/펼치기).</summary>
// [개념] init 접근자: 객체를 만들 때(new CardItem { Title = ... })만 값을 넣을 수 있고 이후엔 바꿀 수 없다.
//        목록을 다시 그릴 때마다 새 CardItem을 만들어 통째로 교체하므로, 중간에 값이 바뀌지 않는 편이 안전하다.
internal sealed class CardItem
{
    public bool IsHeader { get; init; }
    // [왜] Key: 목록을 새로 만들어도 "같은 줄"을 알아보기 위한 고유 이름("user:A0001", "room:3", "dept:개발팀").
    //      선택 상태(_selectedKey)와 접힌 부서(_collapsed)를 객체가 아닌 Key로 기억하므로 목록을 교체해도 유지된다.
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public string? AvatarText { get; init; }
    public Color AvatarColor { get; init; } = Theme.Primary;
    public bool ShowStatus { get; init; }
    public bool Online { get; init; } = true;
    public Color? StatusColor { get; init; } // 값이 있으면 Online 대신 이 색으로 상태 점을 그린다 (자리 비움·회의 중 등)
    public string? Badge { get; init; }
    public bool BadgeAlert { get; init; } // true면 빨간 동그라미(안 읽은 수)
    public bool Highlight { get; init; }  // 현재 참여 중인 방 등
    // [개념] Tag: 화면과 상관없는 "원래 데이터"(UserInfo, RoomInfo)를 붙여 두는 자리.
    //        더블클릭했을 때 어떤 사용자/방인지 바로 꺼내 쓸 수 있다.
    public object? Tag { get; init; }
}

/// <summary>
/// 아바타 + 이름 + 설명 + 배지로 된 카드 목록 (친구 목록, 채팅방 목록에 사용).
/// 표준 ListView 대신 직접 그려서 메신저 느낌을 낸다. 보이는 줄만 그리므로 사람이 많아도 가볍다.
/// </summary>
// [왜] UserControl을 상속해 직접 그린다(OwnerDraw). 표준 ListView/ListBox는 줄 높이·둥근 배지·아바타 같은
//      모양을 자유롭게 바꾸기 어렵고, 줄마다 컨트롤(Label 등)을 만들면 사람이 수백 명일 때 매우 무거워진다.
//      여기서는 컨트롤 1개가 "보이는 줄만" 그림으로 그리므로 줄이 아무리 많아도 가볍다.
internal sealed class CardListView : UserControl
{
    // [개념] TextFormatFlags: TextRenderer.DrawText에 주는 글자 배치 옵션.
    //  - SingleLine    : 한 줄로만 쓴다 (줄바꿈 안 함)
    //  - EndEllipsis   : 칸보다 길면 끝을 "..."로 자른다
    //  - NoPrefix      : '&'를 단축키 밑줄 표시로 해석하지 않는다 (이름에 &가 있어도 그대로 보이게)
    //  - VerticalCenter: 칸의 세로 가운데에 쓴다
    private const TextFormatFlags LineFlags =
        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;

    private List<CardItem> _items = [];               // 전체 목록(접힌 부서의 사람도 포함)
    private readonly List<CardItem> _visible = [];    // 지금 화면에 보여야 하는 줄만 (접힌 부서의 사람은 빠짐)
    private readonly HashSet<string> _collapsed = []; // 접혀 있는 부서 머리줄의 Key 목록
    private int _hover = -1;                          // 마우스가 올라가 있는 줄 번호 (_visible 기준, 없으면 -1)
    private string? _selectedKey;                     // 선택된 줄의 Key (목록이 새로 와도 선택이 유지되도록 Key로 기억)

    // [개념] DesignerSerializationVisibility.Hidden: 비주얼 스튜디오 디자이너가 이 속성 값을 폼 코드(.Designer.cs)에
    //        저장하지 않게 한다. 디자이너 경고(WFO1000)를 막는 용도이며 실행 동작에는 영향이 없다.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "";

    /// <summary>더블클릭 또는 Enter.</summary>
    // [왜] 목록은 "무엇이 눌렸는지"만 알리고, 그다음 할 일(1:1 창 열기, 방 입장)은 MainForm이 정한다.
    //      화면 부품과 동작을 분리하면 같은 목록 컨트롤을 동료 목록·방 목록에 함께 쓸 수 있다.
    public event Action<CardItem>? ItemActivated;
    public event Action? SelectionChanged;

    /// <summary>줄을 오른쪽 클릭함 (그 줄, 목록 안의 마우스 위치). 메뉴는 MainForm이 띄운다.</summary>
    public event Action<CardItem, Point>? ItemContextRequested;

    public CardItem? SelectedItem => _visible.FirstOrDefault(i => !i.IsHeader && i.Key == _selectedKey);

    // [개념] DPI(화면 배율) 대응: LogicalToDeviceUnits(30)은 "100% 배율 기준 30픽셀"을 지금 모니터 배율에 맞게 바꾼다.
    //        예) 150% 배율이면 45픽셀. 이렇게 하지 않으면 고해상도 노트북에서 줄이 너무 작아 보인다.
    private int HeaderHeight => LogicalToDeviceUnits(30);
    private int RowHeight => LogicalToDeviceUnits(58);
    // [왜] S(...)는 LogicalToDeviceUnits를 짧게 쓰기 위한 도우미. 그리기 코드에 숫자가 많아 읽기 쉽게 줄였다.
    private int S(int v) => LogicalToDeviceUnits(v);

    public CardListView()
    {
        // [개념] 더블 버퍼링 등 그리기 방식 설정 (GradientPanel.cs 주석 참고).
        //        Selectable: 이 컨트롤이 키보드 포커스를 받을 수 있게 해서 Enter 키로 항목을 열 수 있게 한다.
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        // [개념] AutoScroll: 내용 크기(AutoScrollMinSize)가 컨트롤보다 크면 스크롤바를 자동으로 보여 준다.
        AutoScroll = true;
        BackColor = Theme.Surface;
    }

    /// <summary>목록 전체를 바꾼다 (접속자 목록·방 목록이 서버에서 올 때마다 호출).</summary>
    public void SetItems(IEnumerable<CardItem> items)
    {
        _items = items.ToList();
        RebuildVisible();
    }

    /// <summary>
    /// 접힌 부서를 빼고 "화면에 보일 줄" 목록을 다시 만들고, 전체 높이(스크롤 범위)를 다시 계산한다.
    /// </summary>
    private void RebuildVisible()
    {
        _visible.Clear();
        bool skipping = false; // 지금 접힌 부서 안의 줄을 지나는 중인지
        foreach (CardItem item in _items)
        {
            if (item.IsHeader)
            {
                // 머리줄은 항상 보이고, 이 부서가 접혀 있으면 다음 머리줄이 나올 때까지 사람 줄을 건너뛴다
                _visible.Add(item);
                skipping = _collapsed.Contains(item.Key);
            }
            else if (!skipping)
            {
                _visible.Add(item);
            }
        }

        // [개념] AutoScrollMinSize: "내용 전체의 크기". 컨트롤보다 크면 그 차이만큼 스크롤할 수 있게 된다.
        //        가로는 0(가로 스크롤 없음), 세로는 모든 줄 높이의 합.
        int total = _visible.Sum(i => i.IsHeader ? HeaderHeight : RowHeight);
        AutoScrollMinSize = new Size(0, total);
        _hover = -1;
        // [개념] Invalidate(): "이 컨트롤을 다시 그려야 한다"고 표시만 한다. 실제 그리기(OnPaint)는
        //        윈도우가 적당한 때(메시지 처리 시점)에 한 번 호출해 준다. 여러 번 불러도 한 번만 그려진다.
        Invalidate();
    }

    /// <summary>
    /// [개념] 히트 테스트(hit test): 마우스 좌표 y가 몇 번째 줄 위에 있는지 계산한다. 없으면 -1.
    /// 줄마다 컨트롤이 있는 게 아니라 그림만 있으므로, 클릭 위치를 직접 줄 번호로 바꿔야 한다.
    /// </summary>
    private int IndexAt(int y)
    {
        // [개념] AutoScrollPosition은 스크롤한 만큼 "음수"로 나온다(아래로 100px 내리면 Y = -100).
        //        그래서 화면 좌표 y에서 빼면(= 100을 더하면) 내용 전체 기준의 좌표가 된다.
        int contentY = y - AutoScrollPosition.Y; // AutoScrollPosition.Y는 0 또는 음수
        int top = 0;
        for (int i = 0; i < _visible.Count; i++)
        {
            int h = _visible[i].IsHeader ? HeaderHeight : RowHeight;
            if (contentY >= top && contentY < top + h) return i;
            top += h;
        }
        return -1;
    }

    // ───────── 마우스 / 키보드 ─────────
    // [개념] OnXxx 메서드 재정의: 컨트롤이 받는 마우스·키보드 이벤트를 직접 처리한다.
    //        base.OnXxx(e)를 먼저 불러야 원래 동작(외부에서 연결한 이벤트 핸들러 호출 등)도 유지된다.

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexAt(e.Y);
        // [왜] 같은 줄 안에서 움직일 때는 다시 그리지 않는다 (마우스가 움직일 때마다 그리면 낭비)
        if (index == _hover) return;
        _hover = index;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        // 마우스가 목록 밖으로 나가면 강조(hover) 표시를 지운다
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // [왜] 클릭하면 포커스를 가져와야 이후 Enter 키(OnKeyDown)를 이 목록이 받을 수 있다
        Focus();
        int index = IndexAt(e.Y);
        if (index < 0) return;

        CardItem item = _visible[index];
        if (item.IsHeader)
        {
            // [개념] HashSet.Add는 이미 있으면 false를 돌려준다 → "없으면 추가(접기), 있으면 제거(펼치기)"를 한 줄로 처리
            if (!_collapsed.Add(item.Key)) _collapsed.Remove(item.Key); // 접기 ↔ 펼치기
            RebuildVisible();
            return;
        }

        _selectedKey = item.Key;
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Right) return;
        // OnMouseDown에서 이미 그 줄을 선택해 두었다 → 오른쪽 클릭한 줄에 대한 메뉴를 띄우게 알린다
        int index = IndexAt(e.Y);
        if (index >= 0 && !_visible[index].IsHeader) ItemContextRequested?.Invoke(_visible[index], e.Location);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int index = IndexAt(e.Y);
        // 머리줄이 아닌 사람/방 줄을 더블클릭하면 "열기" 이벤트를 알린다
        if (index >= 0 && !_visible[index].IsHeader) ItemActivated?.Invoke(_visible[index]);
    }

    // [개념] IsInputKey: 기본적으로 Enter·화살표 같은 키는 폼이 먼저 가로채(기본 버튼 누르기 등) 컨트롤까지 오지 않는다.
    //        true를 돌려주면 "이 키는 내가 직접 처리하겠다"는 뜻이 되어 OnKeyDown에서 Enter를 받을 수 있다.
    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter && SelectedItem is CardItem item) ItemActivated?.Invoke(item);
    }

    // [왜] 스크롤하면 보이는 줄이 바뀌므로 다시 그린다 (보이는 줄만 그리는 방식이라 꼭 필요).
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Invalidate();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    // ───────── 그리기 ─────────

    /// <summary>
    /// [개념] OnPaint: 윈도우가 "이 영역을 다시 그려라"고 할 때 호출된다(처음 표시, 가려졌다 나타남, Invalidate 등).
    /// e.Graphics는 그림을 그리는 도화지(GDI+ Graphics 객체)이다.
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        int width = ClientSize.Width;

        // 목록이 비어 있으면 가운데에 안내 문구("아직 접속한 동료가 없습니다.")
        if (_visible.Count == 0)
        {
            TextRenderer.DrawText(g, EmptyText, Theme.Body, ClientRectangle, Theme.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        // [개념] 첫 줄의 화면상 y는 AutoScrollPosition.Y(0 또는 음수)에서 시작한다.
        //        아래로 스크롤했다면 위쪽 줄들은 화면 위(음수 좌표)에 놓이게 된다.
        int y = AutoScrollPosition.Y;
        for (int i = 0; i < _visible.Count; i++)
        {
            CardItem item = _visible[i];
            int h = item.IsHeader ? HeaderHeight : RowHeight;
            // [왜] 화면 안에 걸쳐 있는 줄만 그린다. 1000명이 있어도 실제로 그리는 건 화면에 보이는 10여 줄뿐이다.
            if (y + h >= 0 && y <= ClientSize.Height)
            {
                var row = new Rectangle(0, y, width, h);
                if (item.IsHeader) DrawHeader(g, row, item);
                else DrawRow(g, row, item, hover: i == _hover, selected: item.Key == _selectedKey);
            }
            y += h;
        }
    }

    /// <summary>부서 머리줄: 연한 배경 + "▾ 개발팀 (3)" (접혀 있으면 ▸).</summary>
    private void DrawHeader(Graphics g, Rectangle row, CardItem item)
    {
        // [개념] SolidBrush 같은 GDI+ 객체는 운영체제 자원을 쓰므로 다 쓰면 Dispose로 돌려줘야 한다.
        //        using을 쓰면 블록이 끝날 때 자동으로 Dispose된다(그리기는 자주 일어나므로 새는 자원이 쌓이면 문제가 된다).
        using (var bg = new SolidBrush(Theme.Background)) g.FillRectangle(bg, row);
        string arrow = _collapsed.Contains(item.Key) ? "▸" : "▾";
        var textRect = new Rectangle(row.X + S(12), row.Y, row.Width - S(24), row.Height);
        // [개념] TextRenderer(GDI)는 Graphics.DrawString(GDI+)보다 윈도우 기본 글자 렌더링과 똑같이 선명하게 그려
        //        한글이 흐려지지 않는다. 그래서 글자는 모두 TextRenderer로 그린다.
        TextRenderer.DrawText(g, $"{arrow}  {item.Title}", Theme.SmallBold, textRect, Theme.SubText, LineFlags);
    }

    /// <summary>사람/방 한 줄: 배경 → 아바타·상태 점 → 오른쪽 배지 → 이름·설명 → 구분선 순서로 그린다.</summary>
    private void DrawRow(Graphics g, Rectangle row, CardItem item, bool hover, bool selected)
    {
        // 선택 > 마우스 올림 > 기본 순서로 배경색 결정
        Color back = selected ? Theme.Selected : hover ? Theme.Hover : Theme.Surface;
        using (var bg = new SolidBrush(back)) g.FillRectangle(bg, row);

        // 아바타 + 접속 상태 점
        int size = S(40);
        // 줄의 세로 가운데에 놓기: (줄 높이 - 아바타 크기) / 2
        var avatar = new Rectangle(row.X + S(14), row.Y + (row.Height - size) / 2, size, size);
        Theme.DrawAvatar(g, avatar, item.AvatarText ?? item.Title, item.AvatarColor);
        if (item.ShowStatus)
        {
            int dot = S(13);
            // 아바타 오른쪽 아래 모서리에 살짝 걸치게 (+S(2))
            var dotRect = new Rectangle(avatar.Right - dot + S(2), avatar.Bottom - dot + S(2), dot, dot);
            if (item.StatusColor is Color statusColor) Theme.DrawStatusDot(g, dotRect, statusColor);
            else Theme.DrawStatusDot(g, dotRect, item.Online);
        }

        // 오른쪽 배지
        // [왜] 배지를 이름보다 먼저 그린다. 배지 폭을 알아야 이름이 차지할 수 있는 폭(right)을 정할 수 있기 때문.
        int right = row.Right - S(14);
        if (!string.IsNullOrEmpty(item.Badge))
        {
            // [개념] MeasureText: 그리기 전에 글자가 차지할 크기를 미리 잰다(배지 폭을 글자 길이에 맞추려고).
            Size ts = TextRenderer.MeasureText(item.Badge, Theme.SmallBold);
            if (item.BadgeAlert)
            {
                // 안 읽은 수: 빨간 알약 모양(높이의 절반을 반지름으로 한 둥근 사각형). 최소한 동그라미 크기는 되게 Math.Max
                int bh = S(20);
                int bw = Math.Max(bh, ts.Width + S(8));
                var pill = new Rectangle(right - bw, row.Y + (row.Height - bh) / 2, bw, bh);
                Theme.FillRounded(g, Theme.Badge, pill, bh / 2f);
                TextRenderer.DrawText(g, item.Badge, Theme.SmallBold, pill, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                right = pill.Left - S(8);
            }
            else
            {
                // 일반 배지(방 인원 "3/10" 등): 회색 글자만 오른쪽 정렬
                var badgeRect = new Rectangle(right - ts.Width, row.Y, ts.Width, row.Height);
                TextRenderer.DrawText(g, item.Badge, Theme.Small, badgeRect, Theme.SubText, LineFlags | TextFormatFlags.Right);
                right = badgeRect.Left - S(8);
            }
        }

        // 이름 / 설명
        int x = avatar.Right + S(12);
        // [주의] 창이 아주 좁으면 폭이 음수가 될 수 있어 Math.Max(0, ...)로 막는다
        int textWidth = Math.Max(0, right - x);
        var titleRect = new Rectangle(x, row.Y + S(9), textWidth, S(22));
        // 참여 중인 방(Highlight)은 굵은 파란 글씨로 강조
        TextRenderer.DrawText(g, item.Title, item.Highlight ? Theme.BodyBold : Theme.Body, titleRect,
            item.Highlight ? Theme.Primary : Theme.Text, LineFlags);
        if (!string.IsNullOrEmpty(item.Subtitle))
        {
            var subRect = new Rectangle(x, row.Y + S(31), textWidth, S(18));
            TextRenderer.DrawText(g, item.Subtitle, Theme.Small, subRect, Theme.SubText, LineFlags);
        }

        // 아래 구분선 (아바타 오른쪽부터 그어서 카카오톡처럼 들여쓴 느낌)
        using var pen = new Pen(Color.FromArgb(241, 245, 251));
        g.DrawLine(pen, x, row.Bottom - 1, row.Right, row.Bottom - 1);
    }
}
