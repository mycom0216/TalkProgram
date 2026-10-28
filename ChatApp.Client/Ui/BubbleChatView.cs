using System.Drawing.Drawing2D;
using ChatApp.Client.Chat;
using ChatApp.Common.Models;

namespace ChatApp.Client.Ui;

internal enum ChatItemKind { Mine, Other, System, Notice, Error }

/// <summary>파일 말풍선에서 고른 동작.</summary>
internal enum FileCommand { Open, SaveAs, ShowInFolder, Cancel }

/// <summary>대화 한 줄. 화면 배치(좌표)는 BubbleChatView가 계산해 넣는다.</summary>
internal sealed class ChatItem
{
    public ChatItemKind Kind { get; init; }
    public string Sender { get; init; } = "";
    public string SenderKey { get; init; } = ""; // 사원번호 (아바타 색, 연속 메시지 묶기)
    public string Text { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;

    /// <summary>파일·이미지·동영상 말풍선이면 값이 있다.</summary>
    public ChatFile? File { get; init; }

    /// <summary>방에서 나를 "@이름"으로 부른 메시지 → 노란 배경으로 강조.</summary>
    public bool Mentioned { get; init; }

    // ── 배치 결과 (BubbleChatView 전용) ──
    internal int Y;
    internal int Height;
    internal bool ShowHeader;
    internal Rectangle Bubble, TextRect, AvatarRect, NameRect, TimeRect;

    public bool IsSystemLike => Kind is ChatItemKind.System or ChatItemKind.Notice or ChatItemKind.Error;
}

/// <summary>
/// 말풍선 대화 화면. 내 말은 오른쪽 파란 말풍선, 상대 말은 왼쪽 흰 말풍선 + 아바타, 알림은 가운데.
/// 표준 컨트롤 대신 직접 그린다(보이는 말풍선만 그려서 대화가 길어도 가볍다).
/// UI 스레드에서만 호출할 것.
/// </summary>
internal sealed class BubbleChatView : UserControl
{
    private const TextFormatFlags WrapFlags =
        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;

    private readonly List<ChatItem> _items = [];
    private readonly ContextMenuStrip _copyMenu = new();
    private readonly ContextMenuStrip _fileMenu = new();
    private ToolStripItem? _showInFolderItem, _cancelItem;
    private ChatItem? _menuItem; // 파일 메뉴를 연 말풍선

    /// <summary>파일 말풍선 클릭(열기) 또는 오른쪽 클릭 메뉴.</summary>
    public event Action<ChatItem, FileCommand>? FileCommandRequested;
    private int _contentHeight;
    private int _layoutWidth = -1;

    private int S(int v) => LogicalToDeviceUnits(v);

    public BubbleChatView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        AutoScroll = true;
        BackColor = Theme.ChatBackground;

        _copyMenu.Items.Add("대화 내용 복사", null, (_, _) => CopyAll());
        _fileMenu.Items.Add("열기", null, (_, _) => RaiseFileCommand(FileCommand.Open));
        _fileMenu.Items.Add("다른 이름으로 저장...", null, (_, _) => RaiseFileCommand(FileCommand.SaveAs));
        _showInFolderItem = _fileMenu.Items.Add("폴더에서 보기", null, (_, _) => RaiseFileCommand(FileCommand.ShowInFolder));
        _cancelItem = _fileMenu.Items.Add("전송 취소", null, (_, _) => RaiseFileCommand(FileCommand.Cancel));
        _fileMenu.Items.Add(new ToolStripSeparator());
        _fileMenu.Items.Add("대화 내용 복사", null, (_, _) => CopyAll());
    }

    /// <summary>파일 진행률 등 이미 그린 말풍선의 내용이 바뀌었을 때 (크기는 그대로라 다시 그리기만).</summary>
    public void RefreshItem(ChatItem item)
    {
        if (_items.Contains(item)) Invalidate();
    }

    public void SetItems(IEnumerable<ChatItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
        Relayout();
        ScrollToBottom();
        Invalidate();
    }

    public void AddItem(ChatItem item)
    {
        bool wasAtBottom = IsAtBottom();
        _items.Add(item);

        if (_layoutWidth != ClientSize.Width) Relayout();
        else
        {
            LayoutItem(_items.Count - 1);
            UpdateScrollSize();
        }

        // 아래를 보고 있었거나 내가 보낸 메시지면 맨 아래로 (위 대화를 읽는 중이면 그대로 둔다)
        if (wasAtBottom || item.Kind == ChatItemKind.Mine) ScrollToBottom();
        Invalidate();
    }

    // ───────── 배치 계산 ─────────

    private void Relayout()
    {
        _layoutWidth = ClientSize.Width;
        _contentHeight = 0;
        for (int i = 0; i < _items.Count; i++) LayoutItem(i);
        UpdateScrollSize();
    }

    private void UpdateScrollSize() => AutoScrollMinSize = new Size(0, _contentHeight + S(14));

    private Size Measure(string text, Font font, int maxWidth)
        => TextRenderer.MeasureText(text, font, new Size(Math.Max(1, maxWidth), int.MaxValue), WrapFlags);

    private void LayoutItem(int index)
    {
        ChatItem item = _items[index];
        ChatItem? prev = index > 0 ? _items[index - 1] : null;
        int width = Math.Max(S(200), _layoutWidth);
        int side = S(12);

        bool sameSenderAsPrev = prev is not null && prev.Kind == item.Kind && prev.SenderKey == item.SenderKey
                                && !item.IsSystemLike && (item.Time - prev.Time) < TimeSpan.FromMinutes(3);
        int y = _contentHeight + (sameSenderAsPrev ? S(4) : S(12));
        item.ShowHeader = item.Kind == ChatItemKind.Other && !sameSenderAsPrev;

        int maxBubbleText = Math.Max(S(80), (int)(width * 0.62));
        string time = item.Time.ToString("HH:mm");
        Size timeSize = TextRenderer.MeasureText(time, Theme.Small);

        switch (item.Kind)
        {
            case ChatItemKind.Mine:
            {
                Size bs = MeasureBubble(item, maxBubbleText, out Size ts);
                int bw = bs.Width, bh = bs.Height;
                item.Bubble = new Rectangle(width - side - bw, y, bw, bh);
                item.TextRect = new Rectangle(item.Bubble.X + S(12), y + S(7), ts.Width, ts.Height);
                item.TimeRect = new Rectangle(item.Bubble.X - S(5) - timeSize.Width, item.Bubble.Bottom - timeSize.Height, timeSize.Width, timeSize.Height);
                item.Height = bh;
                break;
            }
            case ChatItemKind.Other:
            {
                int avatar = S(38);
                int left = side + avatar + S(8);
                int top = y;
                item.AvatarRect = item.ShowHeader ? new Rectangle(side, y, avatar, avatar) : Rectangle.Empty;
                if (item.ShowHeader)
                {
                    Size ns = TextRenderer.MeasureText(item.Sender, Theme.SmallBold);
                    item.NameRect = new Rectangle(left, y, ns.Width, ns.Height);
                    top = y + ns.Height + S(3);
                }

                int maxText = Math.Min(maxBubbleText, width - left - timeSize.Width - S(40));
                Size bs = MeasureBubble(item, maxText, out Size ts);
                int bw = bs.Width, bh = bs.Height;
                item.Bubble = new Rectangle(left, top, bw, bh);
                item.TextRect = new Rectangle(left + S(12), top + S(7), ts.Width, ts.Height);
                item.TimeRect = new Rectangle(item.Bubble.Right + S(5), item.Bubble.Bottom - timeSize.Height, timeSize.Width, timeSize.Height);
                item.Height = Math.Max(item.Bubble.Bottom, item.ShowHeader ? y + avatar : 0) - y;
                break;
            }
            default: // 입장/퇴장/공지/오류: 가운데 작은 알림
            {
                Size ts = Measure(item.Text, Theme.Small, width - S(80));
                int bw = ts.Width + S(24), bh = ts.Height + S(8);
                item.Bubble = new Rectangle((width - bw) / 2, y, bw, bh);
                item.TextRect = new Rectangle(item.Bubble.X + S(12), y + S(4), ts.Width, ts.Height);
                item.Height = bh;
                break;
            }
        }

        item.Y = y;
        _contentHeight = y + item.Height;
    }

    /// <summary>말풍선 크기. 글이면 글 크기 + 여백, 사진이면 미리보기 크기, 그 밖의 파일이면 카드 크기.</summary>
    private Size MeasureBubble(ChatItem item, int maxText, out Size textSize)
    {
        if (item.File is ChatFile file)
        {
            textSize = Size.Empty;
            if (file.Thumbnail is Image thumb)
            {
                // 긴 변 기준으로 줄이되 너무 작거나 크지 않게
                double scale = Math.Min((double)S(220) / thumb.Width, (double)S(260) / thumb.Height);
                int w = Math.Max(S(60), (int)(thumb.Width * scale)), h = Math.Max(S(60), (int)(thumb.Height * scale));
                return new Size(w, h);
            }
            return new Size(Math.Min(S(250), Math.Max(S(160), maxText + S(24))), S(66));
        }
        textSize = Measure(item.Text, Theme.Body, maxText);
        return new Size(textSize.Width + S(24), textSize.Height + S(14));
    }

    // ───────── 파일 말풍선: 마우스 ─────────

    private ChatItem? HitFile(Point location)
    {
        var p = new Point(location.X, location.Y - AutoScrollPosition.Y);
        foreach (ChatItem item in _items)
        {
            if (item.File is not null && item.Bubble.Contains(p)) return item;
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = HitFile(e.Location) is null ? Cursors.Default : Cursors.Hand;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && HitFile(e.Location) is ChatItem item)
            FileCommandRequested?.Invoke(item, FileCommand.Open);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Right) return;

        _menuItem = HitFile(e.Location);
        if (_menuItem?.File is ChatFile file)
        {
            _showInFolderItem!.Enabled = file.HasLocalFile;
            _cancelItem!.Enabled = file.CanCancel; // 보내는 중·받는 중일 때만
            _fileMenu.Show(this, e.Location);
        }
        else
        {
            _copyMenu.Show(this, e.Location);
        }
    }

    private void RaiseFileCommand(FileCommand command)
    {
        if (_menuItem is not null) FileCommandRequested?.Invoke(_menuItem, command);
    }

    // ───────── 스크롤 ─────────

    private bool IsAtBottom() => -AutoScrollPosition.Y + ClientSize.Height >= AutoScrollMinSize.Height - S(40);

    // ───────── 대화 검색 (Ctrl+F) ─────────

    private string? _searchQuery;
    private ChatItem? _searchCurrent;

    /// <summary>이 말풍선이 검색어를 포함하는지 (대소문자 무시).</summary>
    public static bool Matches(ChatItem item, string query)
        => query.Length > 0 && item.Text.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>검색어와 지금 보고 있는 결과를 정한다. null이면 강조를 끈다.</summary>
    public void SetSearch(string? query, ChatItem? current)
    {
        _searchQuery = string.IsNullOrEmpty(query) ? null : query;
        _searchCurrent = current;
        if (current is not null) ScrollToItem(current);
        Invalidate();
    }

    /// <summary>그 말풍선이 화면 위쪽 1/3쯤에 오도록 스크롤한다.</summary>
    public void ScrollToItem(ChatItem item)
    {
        if (!_items.Contains(item)) return;
        // [개념] AutoScrollPosition에 넣을 때는 양수(내려간 거리), 읽을 때는 음수로 나온다 (WinForms의 특이한 규칙).
        AutoScrollPosition = new Point(0, Math.Max(0, item.Y - ClientSize.Height / 3));
    }

    internal void ScrollToBottom()
        => AutoScrollPosition = new Point(0, Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height));

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (ClientSize.Width > 0 && ClientSize.Width != _layoutWidth)
        {
            bool wasAtBottom = IsAtBottom();
            Relayout();
            if (wasAtBottom) ScrollToBottom();
        }
        Invalidate();
    }

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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int dy = AutoScrollPosition.Y;
        int viewTop = -dy, viewBottom = viewTop + ClientSize.Height;

        foreach (ChatItem item in _items)
        {
            if (item.Y + item.Height + S(30) < viewTop) continue;
            if (item.Y > viewBottom) break;
            if (_searchQuery is not null && Matches(item, _searchQuery))
            {
                // 검색 결과: 말풍선 뒤에 노란 테두리 영역 (지금 보고 있는 결과는 진하게)
                var hit = Rectangle.Inflate(Shift(item.Bubble, dy), S(4), S(4));
                Theme.FillRounded(g, item == _searchCurrent ? Theme.Away : Theme.SearchHit, hit, S(16));
            }
            DrawItem(g, item, dy);
        }
    }

    private static Rectangle Shift(Rectangle r, int dy) => new(r.X, r.Y + dy, r.Width, r.Height);

    private void DrawItem(Graphics g, ChatItem item, int dy)
    {
        Rectangle bubble = Shift(item.Bubble, dy);
        Rectangle text = Shift(item.TextRect, dy);

        switch (item.Kind)
        {
            case ChatItemKind.Mine when item.File is not null:
                DrawFile(g, item, bubble, mine: true);
                DrawTime(g, item, dy);
                break;

            case ChatItemKind.Mine:
                Theme.FillRounded(g, Theme.Primary, bubble, S(14));
                TextRenderer.DrawText(g, item.Text, Theme.Body, text, Color.White, WrapFlags);
                DrawTime(g, item, dy);
                break;

            case ChatItemKind.Other:
                if (item.ShowHeader)
                {
                    Theme.DrawAvatar(g, Shift(item.AvatarRect, dy), item.Sender, Theme.AvatarColor(item.SenderKey));
                    TextRenderer.DrawText(g, item.Sender, Theme.SmallBold, Shift(item.NameRect, dy), Theme.SubText, TextFormatFlags.NoPrefix);
                }
                if (item.File is not null)
                {
                    DrawFile(g, item, bubble, mine: false);
                    DrawTime(g, item, dy);
                    break;
                }
                Theme.FillRounded(g, item.Mentioned ? Theme.MentionBack : Theme.Surface, bubble, S(14));
                Theme.DrawRounded(g, item.Mentioned ? Theme.Away : Theme.Border, bubble, S(14));
                TextRenderer.DrawText(g, item.Text, Theme.Body, text, Theme.Text, WrapFlags);
                DrawTime(g, item, dy);
                break;

            default:
                (Color back, Color fore) = item.Kind switch
                {
                    ChatItemKind.Notice => (Color.FromArgb(254, 243, 199), Theme.Notice),
                    ChatItemKind.Error => (Color.FromArgb(254, 226, 226), Theme.Danger),
                    _ => (Color.FromArgb(210, 227, 248), Theme.SubText),
                };
                Theme.FillRounded(g, back, bubble, bubble.Height / 2f);
                TextRenderer.DrawText(g, item.Text, Theme.Small, text, fore, WrapFlags);
                break;
        }
    }

    /// <summary>파일 말풍선: 사진이면 미리보기, 아니면 [아이콘 | 이름 / 크기·상태 | 진행 막대] 카드.</summary>
    private void DrawFile(Graphics g, ChatItem item, Rectangle bubble, bool mine)
    {
        ChatFile file = item.File!;
        string status = StatusText(file);
        bool failed = file.State is FileState.Failed or FileState.Canceled;

        if (file.Thumbnail is Image thumb)
        {
            using (GraphicsPath clip = Theme.RoundedRect(bubble, S(12)))
            {
                GraphicsState saved = g.Save();
                g.SetClip(clip);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(thumb, bubble);
                if (file.IsBusy || failed)
                {
                    using var shade = new SolidBrush(Color.FromArgb(140, 15, 23, 42));
                    g.FillRectangle(shade, bubble);
                    TextRenderer.DrawText(g, status, Theme.SmallBold, Rectangle.Inflate(bubble, -S(6), -S(6)),
                        failed ? Color.FromArgb(254, 202, 202) : Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    if (file.IsBusy) DrawProgress(g, new Rectangle(bubble.X + S(10), bubble.Bottom - S(12), bubble.Width - S(20), S(4)), file.Percent);
                }
                g.Restore(saved);
            }
            Theme.DrawRounded(g, mine ? Color.FromArgb(150, 190, 245) : Theme.Border, bubble, S(12));
            return;
        }

        Theme.FillRounded(g, mine ? Color.FromArgb(232, 242, 255) : Theme.Surface, bubble, S(12));
        Theme.DrawRounded(g, mine ? Color.FromArgb(150, 190, 245) : Theme.Border, bubble, S(12));

        // 아이콘: 동영상 ▶, 그 밖은 확장자 (PDF, ZIP ...)
        var icon = new Rectangle(bubble.X + S(12), bubble.Y + (bubble.Height - S(40)) / 2, S(40), S(40));
        Color iconColor = file.Info.Kind switch
        {
            FileKind.Video => Color.FromArgb(139, 92, 246),
            FileKind.Image => Color.FromArgb(16, 185, 129),
            _ => Theme.Primary,
        };
        Theme.FillRounded(g, iconColor, icon, S(8));
        string ext = FileRules.Extension(file.Info.FileName).TrimStart('.').ToUpperInvariant();
        string iconText = file.Info.Kind == FileKind.Video ? "▶" : ext.Length is > 0 and <= 4 ? ext : "FILE";
        TextRenderer.DrawText(g, iconText, file.Info.Kind == FileKind.Video ? Theme.Title : Theme.SmallBold, icon, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        int x = icon.Right + S(10);
        int w = bubble.Right - S(12) - x;
        var single = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, file.Info.FileName, Theme.BodyBold, new Rectangle(x, bubble.Y + S(11), w, S(22)), Theme.Text, single);
        TextRenderer.DrawText(g, status, Theme.Small, new Rectangle(x, bubble.Y + S(34), w, S(18)), failed ? Theme.Danger : Theme.SubText, single);
        if (file.IsBusy) DrawProgress(g, new Rectangle(x, bubble.Bottom - S(9), w, S(4)), file.Percent);
    }

    private static void DrawProgress(Graphics g, Rectangle r, int percent)
    {
        Theme.FillRounded(g, Color.FromArgb(200, 214, 230, 250), r, r.Height / 2f);
        int filled = (int)(r.Width * Math.Clamp(percent, 0, 100) / 100.0);
        if (filled > r.Height) Theme.FillRounded(g, Theme.Primary, new Rectangle(r.X, r.Y, filled, r.Height), r.Height / 2f);
    }

    private static string StatusText(ChatFile file) => file.State switch
    {
        FileState.Uploading => $"보내는 중 {file.Percent}% · 클릭하면 취소",
        FileState.Processing => "서버에 저장하는 중...",
        FileState.Downloading => $"받는 중 {file.Percent}% · 클릭하면 취소",
        FileState.Failed => $"실패: {file.Error}",
        FileState.Canceled => "보내기를 취소했습니다",
        _ => file.HasLocalFile ? $"{file.Info.SizeText} · 클릭해서 열기" : $"{file.Info.SizeText} · 클릭해서 받기",
    };

    private static void DrawTime(Graphics g, ChatItem item, int dy)
        => TextRenderer.DrawText(g, item.Time.ToString("HH:mm"), Theme.Small, Shift(item.TimeRect, dy), Theme.SubText, TextFormatFlags.NoPrefix);

    private void CopyAll()
    {
        string all = string.Join(Environment.NewLine, _items.Select(i => i.IsSystemLike
            ? $"*** {i.Text}"
            : $"[{i.Time:HH:mm}] {(i.Kind == ChatItemKind.Mine ? "나" : i.Sender)}: {i.Text}"));
        if (all.Length > 0) Clipboard.SetText(all);
    }
}
