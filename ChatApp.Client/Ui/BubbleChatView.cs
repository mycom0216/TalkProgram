using System.Drawing.Drawing2D;
using ChatApp.Client.Chat;
using ChatApp.Common.Models;

namespace ChatApp.Client.Ui;

//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   말풍선 대화 화면을 "표준 컨트롤 없이 직접 그리는" UserControl. 말풍선마다 좌표를 미리 계산해 두고(배치), 화면에 보이는 것만 그린다(OnPaint). ChatItem(데이터 + 배치 결과)과 BubbleChatView(배치 + 그리기 + 마우스)가 한 파일에 있다.
//
// ■ 세 단계로 읽기: 배치 → 그리기 → 상호작용
//   ① 배치(LayoutItem): 글자 크기를 재서 말풍선 사각형(Bubble), 글(TextRect), 시각(TimeRect), 인용(QuoteRect), 공감 칩 위치를 ChatItem 안에 저장. 시작 y는 "바로 앞 말풍선의 아래쪽"에서 계산
//   ② 그리기(OnPaint → DrawItem): 스크롤 위치(AutoScrollPosition.Y)만큼 밀어서(Shift) 보이는 구간의 말풍선만 그린다
//   ③ 상호작용: 마우스 위치 → HitBubble/HitFile/HitReaction... 으로 "무엇을 눌렀나" 판정(저장된 사각형과 비교) → 이벤트 발생(창이 받아 메인 창에 전달)
//
// ■ 왜 이렇게 직접 그렸나 (설계 이유)
//   - 대화가 수천 개여도 ListBox/Panel 컨트롤을 수천 개 만들면 느리고 핸들을 많이 쓴다. 직접 그리면 "보이는 것만" 그려서 가볍다(가상화).
//   - 말풍선 모양(둥근 모서리, 인용, 공감 칩, 진행 막대)을 마음대로 그릴 수 있다.
//   - 대신 직접 구현해야 하는 것이 많다: 스크롤, 클릭 판정, 접근성(화면 낭독기·UI 자동 테스트용 AccessibleName으로 보완).
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 오너 드로우(UserPaint) 컨트롤과 더블 버퍼 ......... "custom painted control OptimizedDoubleBuffer UserPaint"
//   - 화면 가상화(보이는 것만 그리기) ................... "UI virtualization draw only visible items"
//   - 좌표 변환(콘텐츠 좌표 ↔ 화면 좌표, 스크롤) ......... "AutoScrollPosition content coordinates hit testing"
//   - 증분 배치(바뀐 항목부터 아래만 다시 계산) ......... "incremental layout invalidation chat list"
//   - TextRenderer(GDI)로 측정과 그리기를 일치시키기 ..... "TextRenderer MeasureText DrawText consistency"
//   - 히트 테스트(Hit testing) ......................... "hit testing rectangle mouse click custom control"
//   - 스크롤 위치 보존(이전 대화 불러올 때) ............. "preserve scroll position prepend items chat"
//
// ■ 면접 예상 질문과 답
//   Q1. 표준 컨트롤 대신 직접 그린 이유와 단점은요?
//       A. 이유: 말풍선 모양을 자유롭게 그리고, 대화가 길어도 보이는 말풍선만 그려 가볍기 때문입니다(말풍선마다 컨트롤을 만들면 핸들·메모리가 크게 늘어남). 단점: 스크롤·클릭 판정·글자 선택·접근성을 직접 구현해야 하고(글자 드래그 선택은 없음), 버그 범위가 넓습니다. 화면 낭독기용으로 마지막 메시지를 AccessibleName에 넣는 보완을 했습니다.
//   Q2. 새 메시지가 올 때마다 전체를 다시 계산하나요?
//       A. 아닙니다. AddItem은 새 말풍선 하나만 배치하고 스크롤 범위만 늘립니다. 크기가 바뀌는 변경(삭제·공감)은 그 말풍선부터 아래만 다시 배치(RefreshItem)하고, 진행률처럼 크기가 같으면 다시 그리기만 합니다. 창 너비가 바뀌면 줄바꿈이 달라지므로 전체를 다시 배치합니다(Relayout). 각 말풍선이 "바로 앞 말풍선의 아래"에서 시작하는 구조라서 부분 재배치가 가능합니다.
//   Q3. 새 메시지가 오면 항상 맨 아래로 스크롤하나요?
//       A. 아닙니다. 새 메시지가 오기 직전에 맨 아래(여유 40px)를 보고 있었거나 내가 보낸 메시지일 때만 맨 아래로 갑니다. 위쪽 대화를 읽는 중이면 위치를 그대로 둡니다(읽던 곳이 갑자기 밀리면 불편하므로).
//   Q4. 위로 스크롤해 이전 대화를 불러오면 화면이 튀지 않게 하는 방법은요?
//       A. SetItems(preserveViewport: true)가 지금 화면 맨 위에 보이는 말풍선(anchor)과 그 말풍선이 화면 위에서 떨어진 거리를 기억해 두고, 목록을 다시 배치한 뒤 같은 말풍선이 같은 거리에 오도록 스크롤을 복원합니다.
//   Q5. 클릭한 곳이 무엇인지 어떻게 알아내나요?
//       A. 배치 때 저장해 둔 사각형(Bubble, AvatarRect, 공감 칩, 댓글 칩, 인용)과 마우스 위치를 비교합니다. 마우스 좌표는 스크롤만큼 보정(ToContent)해서 "콘텐츠 좌표"로 바꾼 뒤 비교합니다. 칩·아바타·인용·파일을 먼저 검사하고 나머지를 말풍선 클릭으로 처리하는 우선순위가 있습니다.
//   Q6. 그리기가 느려지지 않게 한 점은요?
//       A. (1) 더블 버퍼(깜빡임 방지) (2) OnPaint에서 화면 구간 밖의 말풍선은 건너뛰고(위쪽은 continue, 아래쪽은 break) 보이는 것만 그림 (3) 측정은 배치 때 한 번만 하고 그릴 때는 저장된 사각형을 사용. 글자 측정과 그리기에 같은 TextRenderer를 써서 측정한 크기와 실제 그린 모양이 어긋나지 않게 했습니다.
//   Q7. 한계는요? (솔직하게 답하기)
//       A. (1) OnPaint가 첫 말풍선부터 선형으로 건너뛰며 찾는다(O(N)) — 이진 탐색으로 시작 위치를 찾으면 수만 개에서도 빠르다. (2) Relayout이 모든 말풍선을 다시 재므로 창 크기를 끌 때 매우 긴 대화에서는 느릴 수 있다. (3) 글자를 드래그해서 선택·복사하는 기능이 없고 "복사"는 말풍선 단위다. (4) ChatItem이 데이터와 배치 좌표를 함께 가져서, 같은 대화를 두 화면에 보일 때는 복사본(CloneForView)을 써야 한다. (5) 파일 이미지(Thumbnail)를 Dispose하지 않는다.
//
// ■ 문제가 났을 때 어디를 볼까
//   - 말풍선이 겹치거나 간격이 이상하다 ....... LayoutItem의 시작 y(prev.Y + prev.Height), 아래 말풍선 재배치 누락(RefreshItem의 크기 변화 감지)
//   - 창 크기를 바꾸면 줄바꿈이 어긋난다 ...... OnResize의 Relayout 조건(_layoutWidth), 최소화 중 크기 0
//   - 새 메시지에 스크롤이 안 내려간다 ......... AddItem의 wasAtBottom(IsAtBottom의 40px 여유), 내가 보낸 메시지 여부
//   - 클릭이 엉뚱한 말풍선에 간다 ............. ToContent(스크롤 보정), 저장된 사각형이 최신인지(배치 후 변경 반영)
//   - 이전 대화 불러오면 화면이 튄다 ........... SetItems의 preserveViewport 경로(ChatWindow.OnReloaded가 LoadingOlderHistory 전달)
//   - 삭제·공감 후 화면이 안 바뀐다 ............ Conversation.NotifyChanged → ChatWindow.OnItemChanged → RefreshItem
// ───────────────────────────────────────────────────────────────────────────

/// <summary>대화 한 줄의 종류: 내 말, 상대 말, 시스템 알림, 공지, 오류.</summary>
internal enum ChatItemKind { Mine, Other, System, Notice, Error }

/// <summary>파일 말풍선에서 고른 동작.</summary>
internal enum FileCommand { Open, SaveAs, ShowInFolder, Cancel }

/// <summary>대화 한 줄. 화면 배치(좌표)는 BubbleChatView가 계산해 넣는다.</summary>
// [읽는 법] 이 클래스는 두 가지를 함께 가진다: ① 대화 내용(보낸 사람, 글, 시각, 메시지 번호, 공감 …) ② 아래쪽 "배치 결과" 필드(Y, Height, Bubble 사각형 등 internal). ②는 BubbleChatView만 쓰고 계산해 넣는다. 그래서 같은 객체를 두 화면이 쓰면 좌표가 꼬여 CloneForView로 복사해 쓴다.
internal sealed class ChatItem
{
    public ChatItemKind Kind { get; init; }
    public string Sender { get; init; } = "";
    public string SenderKey { get; init; } = ""; // 사원번호 (아바타 색, 연속 메시지 묶기)
    public string Text { get; set; } = "";
    public DateTime Time { get; init; } = DateTime.UtcNow;

    /// <summary>서버가 매긴 메시지 번호 (답장·공감·삭제·중복 걸러내기에 쓴다). 0 = 번호 없음(알림, 파일, 옛 기록).</summary>
    public long MessageId { get; init; }

    /// <summary>답장이면 원래 메시지 요약 (말풍선 위쪽에 인용으로 보인다).</summary>
    public ReplyInfo? ReplyTo { get; init; }

    /// <summary>삭제된 메시지 → "삭제된 메시지입니다."로 보인다. 나중에 바뀔 수 있어 set.</summary>
    public bool Deleted { get; set; }
    public bool Edited { get; set; }

    /// <summary>공감 목록 (이모지별로 누른 사람들). 서버의 ReactionUpdate로 통째로 바뀐다.</summary>
    public List<ReactionInfo> Reactions { get; set; } = [];

    /// <summary>이 메시지를 아직 안 읽은 사람 수 (카카오톡의 노란 숫자). 0이면 표시하지 않는다.</summary>
    public int UnreadCount { get; set; }

    /// <summary>이 메시지에 달린 댓글 수 (말풍선 아래 "↳ 댓글 N"). Conversation이 계산해 넣는다.</summary>
    public int ReplyCount { get; set; }

    /// <summary>
    /// 다른 화면(댓글 창)에 보여 줄 복사본. 말풍선마다 화면 배치 좌표를 갖고 있어서,
    /// 같은 객체를 두 화면이 같이 쓰면 위치가 꼬인다 → 내용만 복사한 새 객체를 만든다.
    /// </summary>
    public ChatItem CloneForView() => new()
    {
        Kind = Kind, Sender = Sender, SenderKey = SenderKey, Text = Text, Time = Time, MessageId = MessageId,
        ReplyTo = ReplyTo, Deleted = Deleted, Edited = Edited, Reactions = Reactions, UnreadCount = UnreadCount,
        ReplyCount = ReplyCount, File = File, Mentioned = Mentioned,
    };

    /// <summary>화면에 보일 글 (삭제됐으면 안내 문구).</summary>
    public string DisplayText => Deleted ? "삭제된 메시지입니다." : Edited ? Text + "  (수정됨)" : Text;

    /// <summary>답장·공감·삭제를 할 수 있는 메시지인지 (서버 번호가 있는 보통 메시지).</summary>
    public bool CanInteract => MessageId > 0 && !IsSystemLike && !Deleted;

    /// <summary>파일·이미지·동영상 말풍선이면 값이 있다.</summary>
    public ChatFile? File { get; init; }

    /// <summary>방에서 나를 "@이름"으로 부른 메시지 → 노란 배경으로 강조.</summary>
    public bool Mentioned { get; init; }

    // ── 배치 결과 (BubbleChatView 전용) ──
    /// <summary>이 줄의 위쪽 y 좌표 (스크롤 내용 기준, 앞 줄과의 간격 뒤).</summary>
    internal int Y;

    /// <summary>이 줄이 차지하는 높이(px). 말풍선과 그 아래 공감·댓글 칩 줄을 합친 값이다.</summary>
    internal int Height;

    /// <summary>상대 말풍선 위에 아바타와 이름을 그릴지 (같은 사람의 연속 메시지는 첫 줄만 true).</summary>
    internal bool ShowHeader;

    /// <summary>말풍선, 글, 아바타, 이름, 시각, 답장 인용 영역의 사각형 (스크롤 내용 기준 좌표, 없으면 Rectangle.Empty).</summary>
    internal Rectangle Bubble, TextRect, AvatarRect, NameRect, TimeRect, QuoteRect;

    /// <summary>말풍선 아래 공감 칩들의 (사각형, 이모지) 목록. 클릭 판정과 그리기에 쓴다.</summary>
    internal readonly List<(Rectangle Rect, string Emoji)> ReactionChips = [];

    /// <summary>"↳ 댓글 N" 칩의 사각형 (없으면 Empty).</summary>
    internal Rectangle ThreadChip;

    /// <summary>입장·퇴장 알림, 공지, 오류처럼 사람이 쓴 글이 아닌 가운데 알림 줄인지 (System, Notice, Error).</summary>
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
    private readonly ContextMenuStrip _messageMenu = new();
    private ToolStripItem? _showInFolderItem, _cancelItem;
    private ToolStripItem? _replyItem, _reactItem, _deleteItem, _copyTextItem, _editItem, _threadItem;
    private ChatItem? _menuItem; // 메뉴를 연 말풍선

    /// <summary>파일 말풍선 클릭(열기) 또는 오른쪽 클릭 메뉴.</summary>
    public event Action<ChatItem, FileCommand>? FileCommandRequested;

    /// <summary>메시지 메뉴 → 답장.</summary>
    public event Action<ChatItem>? ReplyRequested;

    /// <summary>"↳ 댓글 N" 칩 또는 메뉴 → 그 메시지의 댓글 창 열기.</summary>
    public event Action<ChatItem>? ThreadRequested;

    /// <summary>말풍선 옆 프로필 사진을 누름 (사원번호) → 프로필 카드.</summary>
    public event Action<string>? AvatarClicked;

    /// <summary>false면 댓글 칩을 그리지 않는다 (댓글 창 안에서는 이미 그 댓글들을 보고 있으므로).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowThreadChips { get; set; } = true;

    /// <summary>공감 누르기/취소 (메뉴에서 고르거나, 말풍선 아래 공감 칩을 클릭).</summary>
    public event Action<ChatItem, string>? ReactionRequested;

    /// <summary>메시지 메뉴 → 삭제.</summary>
    public event Action<ChatItem>? DeleteRequested;

    /// <summary>메시지 메뉴 → 수정.</summary>
    public event Action<ChatItem>? EditRequested;

    /// <summary>내 사원번호. 내가 누른 공감 칩을 진하게 그리는 데 쓴다.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string MyKey { get; set; } = "";

    /// <summary>이 메시지를 지울 수 있는지 (내 메시지 24시간 이내, 관리자는 모두). 없으면 삭제 메뉴를 숨긴다.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<ChatItem, bool>? CanDelete { get; set; }

    /// <summary>이 메시지를 고칠 수 있는지 (내 메시지, 보낸 지 5분 이내). 없으면 수정 메뉴를 숨긴다.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<ChatItem, bool>? CanEdit { get; set; }
    private int _layoutWidth = -1;
    private bool _topNotified;

    /// <summary>스크롤이 맨 위에 닿았을 때 한 번 발생한다 (이전 대화를 불러오라는 신호).</summary>
    public event Action? ReachedTop;

    private int S(int v) => LogicalToDeviceUnits(v);

    /// <summary>말풍선 대화 화면을 만들고 직접 그리기 설정과 오른쪽 클릭 메뉴를 준비한다.</summary>
    public BubbleChatView()
    {
        // [읽는 법] 직접 그리는 컨트롤의 기본 설정 네 가지: 더블 버퍼(화면 밖 그림판에 그린 뒤 한 번에 보여 깜빡임 방지) / AllPaintingInWmPaint(배경 지우기를 따로 하지 않음) / UserPaint(내가 그린다) / ResizeRedraw(크기가 바뀌면 다시 그림).
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

        // 글 말풍선 오른쪽 클릭: 답장 / 공감 ▸ / 복사 / 삭제
        _replyItem = _messageMenu.Items.Add("댓글 달기", null, (_, _) => { if (_menuItem is not null) ReplyRequested?.Invoke(_menuItem); });
        _threadItem = _messageMenu.Items.Add("댓글 보기", null, (_, _) => { if (_menuItem is not null) ThreadRequested?.Invoke(_menuItem); });
        var react = new ToolStripMenuItem("공감");
        foreach (string emoji in ReactionRequest.Allowed)
            react.DropDownItems.Add(emoji, null, (_, _) => { if (_menuItem is not null) ReactionRequested?.Invoke(_menuItem, emoji); });
        _reactItem = react;
        _messageMenu.Items.Add(react);
        _copyTextItem = _messageMenu.Items.Add("복사", null, (_, _) => CopyText(_menuItem));
        _deleteItem = _messageMenu.Items.Add("삭제", null, (_, _) => { if (_menuItem is not null) DeleteRequested?.Invoke(_menuItem); });
        _editItem = new ToolStripMenuItem("수정", null, (_, _) => { if (_menuItem is not null) EditRequested?.Invoke(_menuItem); });
        _messageMenu.Items.Insert(0, _editItem);
        _messageMenu.Items.Insert(1, new ToolStripSeparator());
        _messageMenu.Items.Add(new ToolStripSeparator());
        _messageMenu.Items.Add("대화 내용 복사", null, (_, _) => CopyAll());
    }

    /// <summary>컨트롤 트리 밖에 있는 오른쪽 클릭 메뉴 3개를 직접 해제한다.</summary>
    // [주의] ContextMenuStrip은 컨트롤 트리에 들어 있지 않아 창이 닫혀도 자동으로 해제되지 않는다(메뉴 창 핸들이 남음).
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _copyMenu.Dispose();
            _fileMenu.Dispose();
            _messageMenu.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// 이미 그린 말풍선의 내용이 바뀌었을 때. 파일 진행률처럼 크기가 같으면 다시 그리기만,
    /// 삭제·공감처럼 크기가 바뀌면 그 말풍선부터 아래쪽만 다시 배치한다.
    /// </summary>
    public void RefreshItem(ChatItem item)
    {
        int index = _items.IndexOf(item);
        if (index < 0) return;
        if (_layoutWidth == ClientSize.Width)
        {
            bool wasAtBottom = IsAtBottom();
            int oldHeight = item.Height;
            Rectangle oldBubble = item.Bubble;
            LayoutItem(index);
            if (item.Height != oldHeight || item.Bubble.Size != oldBubble.Size)
            {
                for (int i = index + 1; i < _items.Count; i++) LayoutItem(i);
                UpdateScrollSize();
                if (wasAtBottom) ScrollToBottom();
            }
        }
        else
        {
            // 창이 최소화 등으로 너비가 다를 때 → 다음에 크기가 정해지면 전체를 다시 배치하게 표시해 둔다
            _layoutWidth = -1;
        }
        Invalidate();
    }

    /// <summary>읽음 숫자처럼 크기와 상관없는 표시만 바뀌었을 때 (전체 다시 그리기).</summary>
    public void RefreshAll() => Invalidate();

    /// <summary>화면의 말풍선 목록을 통째로 바꾸고 다시 배치한다. preserveViewport가 true면 보던 위치를 유지하고, 아니면 맨 아래로 간다.</summary>
    public void SetItems(IEnumerable<ChatItem> items, bool preserveViewport = false)
    {
        ChatItem? anchor = null;
        int anchorOffset = 0;
        if (preserveViewport && _items.Count > 0)
        {
            int top = -AutoScrollPosition.Y;
            anchor = _items.FirstOrDefault(item => item.Y + item.Height >= top);
            if (anchor is not null) anchorOffset = anchor.Y - top;
        }
        _items.Clear();
        _items.AddRange(items);
        Relayout();
        if (anchor is not null && _items.Contains(anchor))
            AutoScrollPosition = new Point(0, Math.Max(0, anchor.Y - anchorOffset));
        else
            ScrollToBottom();
        UpdateAccessibleName();
        Invalidate();
    }

    /// <summary>
    /// 화면 낭독기·화면 자동 테스트용 이름: "대화 N개 · 마지막: 보낸 사람: 내용".
    /// [왜] 말풍선은 직접 그려서(컨트롤이 아님) UI Automation이 내용을 볼 수 없다. 마지막 메시지를 이름으로 알려 준다.
    /// </summary>
    private void UpdateAccessibleName()
    {
        ChatItem? last = _items.LastOrDefault(i => !i.IsSystemLike);
        AccessibleName = last is null
            ? $"대화 {_items.Count}개"
            : $"대화 {_items.Count}개 · 마지막: {(last.Kind == ChatItemKind.Mine ? "나" : last.Sender)}: {last.DisplayText}";
    }

    /// <summary>말풍선 하나를 맨 끝에 추가한다. 맨 아래를 보고 있었거나 내가 보낸 메시지면 맨 아래로 스크롤한다.</summary>
    // [읽는 법] 순서가 중요하다: ① 추가하기 "전에" 맨 아래를 보고 있었는지 기록(추가하면 스크롤 범위가 늘어 판단이 달라지므로) ② 목록에 추가 ③ 너비가 같으면 새 말풍선 하나만 배치, 다르면 전체 재배치 ④ 아래를 보고 있었거나 내가 보낸 메시지면 맨 아래로 스크롤.
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
        UpdateAccessibleName();
        Invalidate();
    }

    // ───────── 배치 계산 ─────────

    private void Relayout()
    {
        _layoutWidth = ClientSize.Width;
        for (int i = 0; i < _items.Count; i++) LayoutItem(i);
        UpdateScrollSize();
    }

    /// <summary>스크롤 범위 = 마지막 말풍선의 아래쪽 + 여백.</summary>
    private void UpdateScrollSize()
    {
        int content = _items.Count == 0 ? 0 : _items[^1].Y + _items[^1].Height;
        AutoScrollMinSize = new Size(0, content + S(14));
    }

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
        // [왜] 바로 앞 말풍선의 아래쪽에서 시작한다(누적 값이 아니라). 그래야 중간 말풍선 하나만 다시 배치할 수 있다.
        int start = prev is null ? 0 : prev.Y + prev.Height;
        int y = start + (sameSenderAsPrev ? S(4) : S(12));
        item.ShowHeader = item.Kind == ChatItemKind.Other && !sameSenderAsPrev;

        int maxBubbleText = Math.Max(S(80), (int)(width * 0.62));
        string time = item.Time.ToLocalTime().ToString("HH:mm");
        Size timeSize = TextRenderer.MeasureText(time, Theme.Small);

        switch (item.Kind)
        {
            case ChatItemKind.Mine:
            {
                Size bs = MeasureBubble(item, maxBubbleText, out Size ts, out int quoteH);
                int bw = bs.Width, bh = bs.Height;
                item.Bubble = new Rectangle(width - side - bw, y, bw, bh);
                item.QuoteRect = quoteH > 0 ? new Rectangle(item.Bubble.X + S(10), y + S(6), bw - S(20), quoteH - S(6)) : Rectangle.Empty;
                item.TextRect = new Rectangle(item.Bubble.X + S(12), y + quoteH + S(7), ts.Width, ts.Height);
                item.TimeRect = new Rectangle(item.Bubble.X - S(5) - timeSize.Width, item.Bubble.Bottom - timeSize.Height, timeSize.Width, timeSize.Height);
                item.Height = bh + LayoutReactions(item, alignRight: true);
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
                Size bs = MeasureBubble(item, maxText, out Size ts, out int quoteH);
                int bw = bs.Width, bh = bs.Height;
                item.Bubble = new Rectangle(left, top, bw, bh);
                item.QuoteRect = quoteH > 0 ? new Rectangle(left + S(10), top + S(6), bw - S(20), quoteH - S(6)) : Rectangle.Empty;
                item.TextRect = new Rectangle(left + S(12), top + quoteH + S(7), ts.Width, ts.Height);
                item.TimeRect = new Rectangle(item.Bubble.Right + S(5), item.Bubble.Bottom - timeSize.Height, timeSize.Width, timeSize.Height);
                int reactions = LayoutReactions(item, alignRight: false);
                item.Height = Math.Max(item.Bubble.Bottom + reactions, item.ShowHeader ? y + avatar : 0) - y;
                break;
            }
            default: // 입장/퇴장/공지/오류: 가운데 작은 알림
            {
                Size ts = Measure(item.Text, Theme.Small, width - S(80));
                int bw = ts.Width + S(24), bh = ts.Height + S(8);
                item.Bubble = new Rectangle((width - bw) / 2, y, bw, bh);
                item.TextRect = new Rectangle(item.Bubble.X + S(12), y + S(4), ts.Width, ts.Height);
                item.QuoteRect = Rectangle.Empty;
                item.ReactionChips.Clear();
                item.Height = bh;
                break;
            }
        }

        item.Y = y;
    }

    /// <summary>
    /// 말풍선 크기. 글이면 글 크기 + 여백(+ 답장 인용), 사진이면 미리보기 크기, 그 밖의 파일이면 카드 크기.
    /// quoteHeight = 말풍선 위쪽 인용 영역 높이(답장이 아니면 0).
    /// </summary>
    private Size MeasureBubble(ChatItem item, int maxText, out Size textSize, out int quoteHeight)
    {
        quoteHeight = 0;
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
        textSize = Measure(item.DisplayText, Theme.Body, maxText);
        int width = textSize.Width;
        if (item.ReplyTo is ReplyInfo reply && !item.Deleted)
        {
            // 인용: "↳ 홍길동 대리" 한 줄 + 원문 앞부분 한 줄 (길면 … 로 자른다)
            Size name = TextRenderer.MeasureText(QuoteTitle(reply), Theme.SmallBold);
            Size preview = TextRenderer.MeasureText(reply.Text, Theme.Small);
            width = Math.Max(width, Math.Min(maxText, Math.Max(name.Width, preview.Width) + S(10)));
            quoteHeight = name.Height + preview.Height + S(12);
        }
        return new Size(width + S(24), quoteHeight + textSize.Height + S(14));
    }

    private static string QuoteTitle(ReplyInfo reply) => $"↳ {reply.SenderName}에게 댓글";

    private static string ThreadText(ChatItem item) => $"↳ 댓글 {item.ReplyCount}";

    /// <summary>말풍선 아래 공감 칩 배치. 칩 줄의 높이(없으면 0)를 돌려준다.</summary>
    // [읽는 법] 말풍선 아래 한 줄에 [댓글 칩][공감 칩 ...]을 가로로 늘어놓는다. 내 말풍선은 오른쪽 끝에 맞추고(alignRight) 상대는 왼쪽 끝에 맞춘다. 돌려주는 값은 이 줄이 차지하는 높이(없으면 0)이며, 말풍선의 전체 Height에 더해진다.
    private int LayoutReactions(ChatItem item, bool alignRight)
    {
        item.ReactionChips.Clear();
        item.ThreadChip = Rectangle.Empty;
        // 댓글 칩: 지워진 메시지에도 댓글은 남아 있을 수 있으므로 보여 준다 (카카오톡과 같음)
        bool thread = ShowThreadChips && item.ReplyCount > 0;
        List<(string Emoji, int Width)> sizes = item.Deleted ? [] : item.Reactions.Where(r => r.EmployeeNos.Count > 0)
            .Select(r => (r.Emoji, TextRenderer.MeasureText(ChipText(r), Theme.Small).Width + S(12))).ToList();
        if (sizes.Count == 0 && !thread) return 0;

        int h = S(22), gap = S(4);
        int threadWidth = thread ? TextRenderer.MeasureText(ThreadText(item), Theme.SmallBold).Width + S(14) : 0;
        int total = sizes.Sum(c => c.Width) + gap * Math.Max(0, sizes.Count - 1) + (thread ? threadWidth + (sizes.Count > 0 ? gap : 0) : 0);
        int x = alignRight ? item.Bubble.Right - total : item.Bubble.X;
        int y = item.Bubble.Bottom + S(3);
        if (thread)
        {
            item.ThreadChip = new Rectangle(x, y, threadWidth, h); // 댓글 칩이 맨 앞 (카카오톡처럼)
            x += threadWidth + gap;
        }
        foreach (var (emoji, w) in sizes)
        {
            item.ReactionChips.Add((new Rectangle(x, y, w, h), emoji));
            x += w + gap;
        }
        return h + S(3);
    }

    private static string ChipText(ReactionInfo r) => $"{r.Emoji} {r.EmployeeNos.Count}";

    // ───────── 마우스: 파일 열기, 공감 칩, 답장 인용, 오른쪽 클릭 메뉴 ─────────

    private Point ToContent(Point location) => new(location.X, location.Y - AutoScrollPosition.Y);

    /// <summary>그 위치의 말풍선 (알림 말풍선 제외).</summary>
    private ChatItem? HitBubble(Point location)
    {
        Point p = ToContent(location);
        foreach (ChatItem item in _items)
        {
            if (item.Y > p.Y) break; // 아래쪽 말풍선은 볼 필요 없다 (위에서부터 차례로 놓여 있음)
            if (!item.IsSystemLike && item.Bubble.Contains(p)) return item;
        }
        return null;
    }

    private ChatItem? HitFile(Point location) => HitBubble(location) is { File: not null } item ? item : null;

    /// <summary>그 위치의 공감 칩 (말풍선, 이모지).</summary>
    private (ChatItem Item, string Emoji)? HitReaction(Point location)
    {
        Point p = ToContent(location);
        foreach (ChatItem item in _items)
        {
            if (item.Y > p.Y) break;
            foreach (var (rect, emoji) in item.ReactionChips)
                if (rect.Contains(p)) return (item, emoji);
        }
        return null;
    }

    /// <summary>그 위치의 "↳ 댓글 N" 칩.</summary>
    private ChatItem? HitThread(Point location)
    {
        Point p = ToContent(location);
        foreach (ChatItem item in _items)
        {
            if (item.Y > p.Y) break;
            if (!item.ThreadChip.IsEmpty && item.ThreadChip.Contains(p)) return item;
        }
        return null;
    }

    /// <summary>그 위치의 프로필 사진 (다른 사람 말풍선 옆).</summary>
    private ChatItem? HitAvatar(Point location)
    {
        Point p = ToContent(location);
        foreach (ChatItem item in _items)
        {
            if (item.Y > p.Y) break;
            if (!item.AvatarRect.IsEmpty && item.AvatarRect.Contains(p) && item.SenderKey.Length > 0) return item;
        }
        return null;
    }

    private ChatItem? HitQuote(Point location)
    {
        Point p = ToContent(location);
        return HitBubble(location) is { ReplyTo: not null } item && item.QuoteRect.Contains(p) ? item : null;
    }

    /// <summary>파일, 공감 칩, 답장 인용, 댓글 칩, 아바타 위에서는 손 모양 커서로 바꾼다.</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool hand = HitFile(e.Location) is not null || HitReaction(e.Location) is not null || HitQuote(e.Location) is not null
                    || HitThread(e.Location) is not null || HitAvatar(e.Location) is not null;
        Cursor = hand ? Cursors.Hand : Cursors.Default;
    }

    /// <summary>왼쪽 클릭 처리: 아바타(프로필), 댓글 칩, 공감 칩, 답장 인용(원문으로 이동), 파일(열기) 순으로 눌린 곳에 맞는 동작을 알린다.</summary>
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (HitAvatar(e.Location) is ChatItem sender)
        {
            AvatarClicked?.Invoke(sender.SenderKey);
            return;
        }
        if (HitThread(e.Location) is ChatItem threadRoot)
        {
            ThreadRequested?.Invoke(threadRoot);
            return;
        }
        // [읽는 법] 튜플 패턴: HitReaction이 (말풍선, 이모지)를 돌려주면 reacted, emoji에 풀어 담는다(null이면 건너뜀). 같은 이모지 칩을 다시 누르면 서버가 "공감 취소"로 처리하므로 클라이언트는 매번 같은 요청을 보낸다.
        if (HitReaction(e.Location) is var (reacted, emoji))
        {
            ReactionRequested?.Invoke(reacted, emoji); // 같은 이모지를 다시 누르면 서버가 취소로 처리한다
            return;
        }
        if (HitQuote(e.Location) is ChatItem quoting)
        {
            // 답장 인용을 누르면 원래 메시지로 이동 (지금 불러온 대화 안에 있을 때만)
            ChatItem? original = _items.FirstOrDefault(i => i.MessageId == quoting.ReplyTo!.MessageId);
            if (original is not null) FocusItem(original);
            return;
        }
        if (HitFile(e.Location) is ChatItem item)
            FileCommandRequested?.Invoke(item, FileCommand.Open);
    }

    /// <summary>글 말풍선을 더블클릭하면 그 메시지에 댓글 달기(ReplyRequested)를 요청한다.</summary>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        // 글 말풍선 더블클릭 = 댓글 달기 (자주 쓰는 동작을 빠르게)
        if (e.Button == MouseButtons.Left && HitBubble(e.Location) is { File: null, CanInteract: true } item)
            ReplyRequested?.Invoke(item);
    }

    /// <summary>오른쪽 클릭 시 눌린 곳에 따라 파일 메뉴, 메시지 메뉴, 대화 복사 메뉴 중 하나를 띄운다.</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Right) return;

        _menuItem = HitBubble(e.Location);
        if (_menuItem?.File is ChatFile file)
        {
            _showInFolderItem!.Enabled = file.HasLocalFile;
            _cancelItem!.Enabled = file.CanCancel; // 보내는 중·받는 중일 때만
            _fileMenu.Show(this, e.Location);
        }
        else if (_menuItem is not null)
        {
            bool interact = _menuItem.CanInteract;
            _replyItem!.Enabled = interact;
            _threadItem!.Visible = ShowThreadChips && _menuItem.ReplyCount > 0;
            _threadItem.Text = $"댓글 보기 ({_menuItem.ReplyCount})";
            _reactItem!.Enabled = interact;
            _copyTextItem!.Enabled = !_menuItem.Deleted;
            _deleteItem!.Visible = interact && (CanDelete?.Invoke(_menuItem) ?? false);
            _editItem!.Visible = interact && (CanEdit?.Invoke(_menuItem) ?? false);
            _messageMenu.Show(this, e.Location);
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

    // [읽는 법] "지금 맨 아래를 보고 있나?": 화면 맨 아래 위치(-AutoScrollPosition.Y + 화면 높이)가 전체 높이에서 40px 이내이면 true. 정확히 끝이 아니라 약간 위여도 "아래를 보는 중"으로 쳐 준다.
    private bool IsAtBottom() => -AutoScrollPosition.Y + ClientSize.Height >= AutoScrollMinSize.Height - S(40);

    // ───────── 대화 검색 (Ctrl+F) ─────────

    private string? _searchQuery;
    private ChatItem? _searchCurrent;
    private ChatItem? _focusItem; // 답장 인용·전체 검색으로 이동한 말풍선 (검색어와 상관없이 강조)

    /// <summary>그 말풍선으로 스크롤하고 강조한다 (Ctrl+F 검색어는 그대로 둔다).</summary>
    public void FocusItem(ChatItem item)
    {
        _focusItem = item;
        ScrollToItem(item);
        Invalidate();
    }

    /// <summary>이 말풍선이 검색어를 포함하는지 (대소문자 무시).</summary>
    public static bool Matches(ChatItem item, string query)
        => query.Length > 0 && !item.Deleted && item.Text.Contains(query, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>맨 아래(가장 최근 말풍선)로 스크롤한다.</summary>
    internal void ScrollToBottom()
        => AutoScrollPosition = new Point(0, Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height));

    /// <summary>너비가 바뀌면 말풍선을 전체 다시 배치한다 (맨 아래를 보고 있었다면 맨 아래를 유지).</summary>
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

    /// <summary>마우스 휠로 스크롤하면 다시 그리고, 맨 위에 닿았는지 확인한다.</summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Invalidate();
        CheckReachedTop();
    }

    /// <summary>스크롤 막대로 스크롤하면 다시 그리고, 맨 위에 닿았는지 확인한다.</summary>
    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
        CheckReachedTop();
    }

    private void CheckReachedTop()
    {
        // [읽는 법] AutoScrollPosition.Y는 읽을 때 음수(위로 올라간 거리의 반대). 0에 가까우면 맨 위. _topNotified는 한 번 알린 뒤에는 위에서 내려갔다가 다시 올 때까지 또 알리지 않게 하는 "한 번만" 장치다(스크롤 이벤트가 연달아 와도 요청이 한 번만 나감).
        bool atTop = AutoScrollPosition.Y >= -S(24);
        if (atTop && !_topNotified)
        {
            _topNotified = true;
            ReachedTop?.Invoke();
        }
        else if (!atTop) _topNotified = false;
    }

    // ───────── 그리기 ─────────

    /// <summary>화면에 보이는 범위의 말풍선(과 검색 강조)만 직접 그린다.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int dy = AutoScrollPosition.Y;
        int viewTop = -dy, viewBottom = viewTop + ClientSize.Height;

        foreach (ChatItem item in _items)
        {
            // [읽는 법] 가상화의 핵심 두 줄: 화면 위쪽 밖이면 건너뛰고(continue), 화면 아래쪽 밖이 처음 나오면 이후는 모두 밖이므로 반복을 끝낸다(break). 말풍선이 위에서 아래로 정렬되어 있어서 가능한 최적화다. S(30)은 시각·공감 칩이 말풍선 밖에 걸쳐 그려지는 여유.
            if (item.Y + item.Height + S(30) < viewTop) continue;
            if (item.Y > viewBottom) break;
            if (item == _focusItem)
                Theme.FillRounded(g, Theme.SearchHit, Rectangle.Inflate(Shift(item.Bubble, dy), S(4), S(4)), S(16));
            else if (_searchQuery is not null && Matches(item, _searchQuery))
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

            case ChatItemKind.Mine when item.Deleted:
                Theme.FillRounded(g, Color.FromArgb(203, 213, 225), bubble, S(14));
                TextRenderer.DrawText(g, item.DisplayText, Theme.Body, text, Theme.SubText, WrapFlags);
                DrawTime(g, item, dy);
                DrawReactions(g, item, dy); // 지워진 메시지에 남은 댓글 칩
                break;

            case ChatItemKind.Mine:
                Theme.FillRounded(g, Theme.Primary, bubble, S(14));
                if (item.ReplyTo is not null) DrawQuote(g, item, dy, mine: true);
                TextRenderer.DrawText(g, item.DisplayText, Theme.Body, text, Color.White, WrapFlags);
                DrawTime(g, item, dy);
                DrawReactions(g, item, dy);
                break;

            case ChatItemKind.Other:
                if (item.ShowHeader)
                {
                    Theme.DrawAvatar(g, Shift(item.AvatarRect, dy), item.Sender, Theme.AvatarColor(item.SenderKey), item.SenderKey);
                    TextRenderer.DrawText(g, item.Sender, Theme.SmallBold, Shift(item.NameRect, dy), Theme.SubText, TextFormatFlags.NoPrefix);
                }
                if (item.File is not null)
                {
                    DrawFile(g, item, bubble, mine: false);
                    DrawTime(g, item, dy);
                    break;
                }
                if (item.Deleted)
                {
                    Theme.FillRounded(g, Color.FromArgb(241, 245, 249), bubble, S(14));
                    Theme.DrawRounded(g, Theme.Border, bubble, S(14));
                    TextRenderer.DrawText(g, item.DisplayText, Theme.Body, text, Theme.SubText, WrapFlags);
                    DrawTime(g, item, dy);
                    DrawReactions(g, item, dy);
                    break;
                }
                Theme.FillRounded(g, item.Mentioned ? Theme.MentionBack : Theme.Surface, bubble, S(14));
                Theme.DrawRounded(g, item.Mentioned ? Theme.Away : Theme.Border, bubble, S(14));
                if (item.ReplyTo is not null) DrawQuote(g, item, dy, mine: false);
                TextRenderer.DrawText(g, item.DisplayText, Theme.Body, text, Theme.Text, WrapFlags);
                DrawTime(g, item, dy);
                DrawReactions(g, item, dy);
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
        FileState.Queued => "대기 중 · 앞 파일을 받은 뒤 자동으로 받습니다 (클릭하면 취소)",
        FileState.Failed => $"실패: {file.Error}",
        FileState.Canceled => "보내기를 취소했습니다",
        _ => file.HasLocalFile ? $"{file.Info.SizeText} · 클릭해서 열기" : $"{file.Info.SizeText} · 클릭해서 받기",
    };

    /// <summary>시각 + (안 읽은 사람이 있으면) 그 위에 노란 숫자.</summary>
    private void DrawTime(Graphics g, ChatItem item, int dy)
    {
        Rectangle time = Shift(item.TimeRect, dy);
        TextRenderer.DrawText(g, item.Time.ToLocalTime().ToString("HH:mm"), Theme.Small, time, Theme.SubText, TextFormatFlags.NoPrefix);
        if (item.UnreadCount <= 0 || item.Deleted) return;

        string count = item.UnreadCount.ToString();
        Size size = TextRenderer.MeasureText(count, Theme.SmallBold);
        int x = item.Kind == ChatItemKind.Mine ? time.Right - size.Width : time.X; // 내 말은 오른쪽 맞춤(말풍선 쪽)
        TextRenderer.DrawText(g, count, Theme.SmallBold, new Rectangle(x, time.Y - size.Height + S(2), size.Width, size.Height),
            Theme.Notice, TextFormatFlags.NoPrefix);
    }

    /// <summary>답장 인용: 말풍선 안 위쪽에 "↳ 누구에게 답장" + 원문 한 줄, 그 아래 얇은 구분선.</summary>
    private void DrawQuote(Graphics g, ChatItem item, int dy, bool mine)
    {
        ReplyInfo reply = item.ReplyTo!;
        Rectangle q = Shift(item.QuoteRect, dy);
        Color title = mine ? Color.FromArgb(225, 239, 255) : Theme.Primary;
        Color body = mine ? Color.FromArgb(200, 225, 255) : Theme.SubText;
        var single = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        int lineH = TextRenderer.MeasureText("가", Theme.SmallBold).Height;
        TextRenderer.DrawText(g, QuoteTitle(reply), Theme.SmallBold, new Rectangle(q.X, q.Y, q.Width, lineH), title, single);
        TextRenderer.DrawText(g, reply.Text, Theme.Small, new Rectangle(q.X, q.Y + lineH, q.Width, lineH), body, single);
        using var pen = new Pen(mine ? Color.FromArgb(120, 255, 255, 255) : Theme.Border);
        g.DrawLine(pen, q.X, q.Bottom - S(2), q.Right, q.Bottom - S(2));
    }

    /// <summary>말풍선 아래 "↳ 댓글 N" 칩과 공감 칩 "👍 2". 내가 누른 공감은 파란 테두리로.</summary>
    private void DrawReactions(Graphics g, ChatItem item, int dy)
    {
        if (!item.ThreadChip.IsEmpty)
        {
            Rectangle t = Shift(item.ThreadChip, dy);
            Theme.FillRounded(g, Theme.Surface, t, t.Height / 2f);
            Theme.DrawRounded(g, Theme.Border, t, t.Height / 2f);
            TextRenderer.DrawText(g, ThreadText(item), Theme.SmallBold, t, Theme.Primary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        foreach (var (rect, emoji) in item.ReactionChips)
        {
            ReactionInfo? info = item.Reactions.FirstOrDefault(r => r.Emoji == emoji);
            if (info is null) continue;
            bool mine = info.EmployeeNos.Contains(MyKey);
            Rectangle r = Shift(rect, dy);
            Theme.FillRounded(g, mine ? Theme.Selected : Theme.Surface, r, r.Height / 2f);
            Theme.DrawRounded(g, mine ? Theme.Primary : Theme.Border, r, r.Height / 2f);
            TextRenderer.DrawText(g, ChipText(info), Theme.Small, r, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    private void CopyAll()
    {
        string all = string.Join(Environment.NewLine, _items.Select(i => i.IsSystemLike
            ? $"*** {i.Text}"
            : $"[{i.Time.ToLocalTime():HH:mm}] {(i.Kind == ChatItemKind.Mine ? "나" : i.Sender)}: {i.DisplayText}"));
        SetClipboard(all);
    }

    private static void CopyText(ChatItem? item)
    {
        if (item is { Deleted: false }) SetClipboard(item.Text);
    }

    /// <summary>클립보드는 다른 프로그램이 잠깐 잡고 있으면 실패한다 → 프로그램이 죽지 않게 무시.</summary>
    private static void SetClipboard(string text)
    {
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }
}
