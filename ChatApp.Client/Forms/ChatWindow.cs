// ═══════════════════════════════════════════════════════════════════════════════
// ChatWindow.cs — 따로 뜨는 대화창 (채팅방 1개 또는 1:1 상대 1명마다 창 1개)
// ═══════════════════════════════════════════════════════════════════════════════
//
// [역할]
//   - "화면"만 담당한다. 서버로 직접 보내지 않고, 사용자가 무언가를 하면 이벤트(SendRequested, FilesRequested,
//     LeaveRequested, FileCommandRequested)를 발생시켜 메인 창(MainForm)에 맡긴다.
//   - [왜] 창은 여러 개 뜨지만 서버 연결(ChatClient)은 하나다. 전송·상태 관리를 MainForm 한 곳에 모아 두면
//     창마다 연결/상태를 따로 들고 있을 때 생기는 불일치(예: 어떤 창은 끊긴 줄 모름)가 생기지 않는다.
//
// [화면 구성]
//   ┌ _header (GradientPanel, Dock=Top)  : 아바타 · 제목 · 부제목 · [나가기](채팅방만) ┐
//   │ _view   (BubbleChatView, Dock=Fill): 말풍선 대화                                │
//   └ inputArea (Panel, Dock=Bottom)     : [+][☺] [입력칸 _input] [전송]              ┘
//
// [데이터 흐름]
//   - 대화 내용은 이 창이 아니라 Conversation 객체가 갖고 있다(메인 창이 소유).
//     창을 닫았다 다시 열어도 이전 대화가 남아 있는 이유가 이것이다.
//   - Conversation.ItemAdded / ItemChanged / Reloaded 이벤트를 구독해서 _view(BubbleChatView)에 반영한다.
//
// [스레드]
//   - 이 클래스의 모든 코드는 UI 스레드에서만 실행된다.
//   - 서버에서 온 패킷은 수신 스레드 → (MainForm이 BeginInvoke로) UI 스레드 → Conversation.Add → 여기 OnItemAdded 순서로 들어온다.
//     그래서 이 파일에는 Invoke/BeginInvoke가 필요 없다.
//
// [다른 클래스와의 연결]
//   - MainForm        : 창을 만들고 이벤트를 연결한다(실제 전송, 파일 열기/저장 처리).
//   - Conversation    : 대화 내용 보관함. 창은 보여 주기만 한다.
//   - BubbleChatView  : 말풍선을 직접 그리는 컨트롤. 파일 말풍선 클릭을 FileCommandRequested로 알려 준다.
//   - ImageHelper     : 클립보드 이미지를 PNG 파일로 저장.
//   - FileRules       : 사진/동영상 확장자 목록(파일 선택 창 필터에 사용).
// ═══════════════════════════════════════════════════════════════════════════════

using System.ComponentModel;
using ChatApp.Client.Chat;
using System.Drawing.Drawing2D;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;

namespace ChatApp.Client.Forms;

/// <summary>대화창 종류: 채팅방(Room) / 1:1(Direct). 머리 모양과 [나가기] 표시 여부가 달라진다.</summary>
internal enum ChatWindowKind { Room, Direct }

/// <summary>
/// 따로 뜨는 대화창 (채팅방 또는 1:1).
/// ┌ 그라데이션 머리: 아바타 · 제목 · 부제목 ·[나가기] ┐
/// │ 말풍선 대화 (BubbleChatView)                    │
/// ├ [+][☺] [입력칸 (Enter 전송, Shift+Enter 줄바꿈)] [전송] ┤
/// 화면만 담당하고, 실제 전송은 SendRequested / FilesRequested 이벤트로 메인 창에 맡긴다.
/// 파일 보내기: [+] 메뉴(사진 / 동영상 / 파일 / 캡처 붙여넣기), 창에 끌어다 놓기, 입력칸에서 Ctrl+V
/// </summary>
// [개념] sealed: 이 클래스를 상속하지 못하게 막는다. 상속할 계획이 없는 클래스는 sealed로 두면 의도가 분명해지고
//        컴파일러가 가상 호출을 조금 더 빠르게 처리할 수 있다.
// [개념] internal: 같은 프로젝트(ChatApp.Client) 안에서만 쓸 수 있다. 외부에 공개할 필요가 없는 화면 클래스라서.
internal sealed class ChatWindow : Form
{
    // [왜] 2000년대 메신저 느낌의 텍스트 이모티콘. 그림 이모지는 윈도우 글꼴에 따라 깨질 수 있어 글자로만 구성했다.
    // [개념] ["a", "b"] 는 C# 12의 "컬렉션 식". new string[] { "a", "b" } 와 같은 뜻이다.
    // [개념] "\\(^o^)/" 처럼 \\ 는 문자열 안에서 역슬래시(\) 한 글자를 뜻한다(이스케이프).
    private static readonly string[] Emoticons =
        ["^^", "^_^", "ㅎㅎ", "ㅋㅋㅋ", "ㅠㅠ", "(^-^)b", "(>_<)", "(-_-;)", "\\(^o^)/", "♥", "★", "♪"];

    // ── 화면 부품 ──
    // [개념] readonly 필드: 생성자에서 한 번 만든 뒤 다른 객체로 바꿔 끼울 수 없다(속성 값은 바꿀 수 있음).
    //        "이 버튼은 창이 살아 있는 동안 계속 같은 버튼"이라는 의도를 코드로 보장한다.
    private readonly GradientPanel _header = new();      // 위쪽 하늘색 그라데이션 머리
    private readonly BubbleChatView _view = new();       // 말풍선 대화 화면(직접 그리는 컨트롤)
    private readonly TextBox _input = new();             // 입력칸
    private readonly Button _sendButton = new();         // [전송]
    private readonly Button _emoticonButton = new();     // [☺] 이모티콘 메뉴 열기
    private readonly Button _attachButton = new();       // [+] 사진/동영상/파일 메뉴 열기
    private readonly Button _leaveButton = new();        // [나가기] (채팅방에서만 보임)
    // [개념] ContextMenuStrip: 원래는 오른쪽 클릭 메뉴용 컨트롤이지만, Show()로 원하는 위치에 직접 띄울 수도 있다.
    //        여기서는 버튼을 눌렀을 때 버튼 위쪽으로 뜨는 팝업 메뉴로 쓴다.
    private readonly ContextMenuStrip _emoticonMenu = new();
    private readonly ContextMenuStrip _attachMenu = new();
    // [왜] "캡처 이미지 붙여넣기" 항목은 클립보드 상태에 따라 켜고/끄기 때문에 따로 기억해 둔다.
    // [개념] 타입 뒤 ? (nullable): 아직 만들어지지 않았을 수 있음(null 가능)을 컴파일러에게 알린다.
    private ToolStripMenuItem? _pasteMenuItem;

    // ── 머리(헤더)에 그릴 내용과 상태 ──
    private string _headerTitle = "";
    private string _headerSubtitle = "";
    private bool _online = true;                // 1:1 상대 접속 여부 (초록/회색 점, 입력 가능 여부)
    private bool _showWithoutActivation;        // ShowInactive() 동안만 true → 포커스를 빼앗지 않고 창 띄우기

    /// <summary>채팅방 창인지 1:1 창인지.</summary>
    public ChatWindowKind Kind { get; }

    /// <summary>이 창이 보여 주는 대화(메시지 보관함). 메인 창이 소유하고, 창은 빌려서 보여 주기만 한다.</summary>
    public Conversation Conversation { get; }

    /// <summary>아바타 색을 정하는 키 (방은 "room:번호", 1:1은 상대 사원번호). 같은 키 → 항상 같은 색.</summary>
    public string AvatarKey { get; }

    /// <summary>프로그램이 닫은 경우(방 이동 등) true. 사용자가 X로 닫은 것과 구분한다.</summary>
    public bool ClosedByProgram { get; private set; }

    // ── 메인 창에 알리는 이벤트 ──
    // [개념] event Action<T1, T2>: "이런 일이 일어났다"고 외부에 알리는 통로. 구독(+=)한 쪽의 메서드가 호출된다.
    //        이 창은 누가 구독하는지 몰라도 되므로(느슨한 결합) 화면과 통신 로직을 분리할 수 있다.
    // [개념] ? (nullable 이벤트): 아무도 구독하지 않았으면 null. 그래서 호출할 때 SendRequested?.Invoke(...)처럼 ?. 를 쓴다.

    /// <summary>[전송] 또는 Enter: (이 창, 보낼 글).</summary>
    public event Action<ChatWindow, string>? SendRequested;

    /// <summary>[나가기] 클릭 (채팅방만).</summary>
    public event Action<ChatWindow>? LeaveRequested;

    /// <summary>창이 활성화됨 = 사용자가 지금 이 대화를 보고 있음 → 안 읽은 수를 0으로.</summary>
    public event Action<ChatWindow>? ReadAll;

    /// <summary>보낼 파일 경로들 (📎, 끌어다 놓기, Ctrl+V).</summary>
    public event Action<ChatWindow, List<string>>? FilesRequested;

    /// <summary>파일 말풍선 열기/저장/폴더 보기.</summary>
    public event Action<ChatWindow, ChatItem, FileCommand>? FileCommandRequested;

    // [개념] DPI 대응: 화면 배율(125%, 150% 등)이 커지면 같은 "100픽셀"이 작아 보인다.
    //        LogicalToDeviceUnits는 "100% 기준 픽셀"을 현재 배율의 실제 픽셀로 바꿔 준다. 이름이 길어서 S()로 줄였다.
    // [개념] => (식 본문 멤버): { return LogicalToDeviceUnits(v); } 를 한 줄로 쓴 것.
    private int S(int v) => LogicalToDeviceUnits(v);

    /// <summary>
    /// 대화창 만들기. 화면을 구성하고, 대화(Conversation)의 기존 메시지를 표시한 뒤 이후 변화를 구독한다.
    /// </summary>
    /// <param name="kind">채팅방 / 1:1</param>
    /// <param name="conversation">보여 줄 대화 (메인 창이 갖고 있는 것)</param>
    /// <param name="avatarKey">아바타 색 키</param>
    public ChatWindow(ChatWindowKind kind, Conversation conversation, string avatarKey)
    {
        Kind = kind;
        Conversation = conversation;
        AvatarKey = avatarKey;

        Font = Theme.Body;
        BackColor = Theme.Surface;
        Size = LogicalToDeviceUnits(new Size(430, 600));       // 100% 기준 430×600을 현재 DPI에 맞춰
        MinimumSize = LogicalToDeviceUnits(new Size(340, 420)); // 너무 작게 줄여 레이아웃이 깨지지 않게
        // [왜] 창을 여러 개 띄우면 윈도우가 알아서 조금씩 비켜서 놓아 준다(모두 같은 위치에 겹치지 않게).
        StartPosition = FormStartPosition.WindowsDefaultLocation;

        BuildLayout();

        // 이미 쌓여 있던 메시지(창을 닫았다 다시 연 경우 등)를 한꺼번에 표시
        _view.SetItems(conversation.Items);

        // [개념] 이벤트 구독(+=): 대화에 메시지가 추가/변경/재정렬되면 이 창의 메서드가 불린다.
        conversation.ItemAdded += OnItemAdded;
        conversation.ItemChanged += OnItemChanged;
        conversation.Reloaded += OnReloaded;

        // [개념] 람다식 (_, _) => { ... }: 이름 없는 메서드. FormClosed의 (sender, e) 인자를 안 쓰므로 _ 로 버린다.
        FormClosed += (_, _) =>
        {
            // 창이 닫혀도 대화(Conversation)는 메인 창에 남으므로, 이벤트를 꼭 끊어야 닫힌 창이 메모리에 남지 않는다
            // [주의] 이벤트 구독은 "Conversation → 이 창"으로 참조를 만든다. 구독을 해제하지 않으면
            //        Conversation이 살아 있는 한 닫힌 창도 가비지 컬렉터가 회수하지 못한다(메모리 누수).
            //        게다가 닫힌(Dispose된) 창의 _view에 그리려다 ObjectDisposedException이 날 수도 있다.
            conversation.ItemAdded -= OnItemAdded;
            conversation.ItemChanged -= OnItemChanged;
            conversation.Reloaded -= OnReloaded;
        };

        // [왜] BubbleChatView는 "어느 창"인지 모른다. 이 창이 자기 자신(this)을 붙여서 메인 창에 다시 알린다(이벤트 전달).
        // [개념] 람다 캡처(closure): 람다 안에서 쓴 this는 람다가 만들어질 때의 이 창을 계속 가리킨다.
        _view.FileCommandRequested += (item, command) => FileCommandRequested?.Invoke(this, item, command);

        // 끌어다 놓기: 창 어디에 놓아도 보낸다
        // [개념] 드래그 앤 드롭: AllowDrop = true인 컨트롤만 놓기를 받을 수 있다.
        //        DragEnter(위로 들어올 때: "받을 수 있다/없다" 표시) → DragDrop(실제로 놓았을 때) 순서로 이벤트가 온다.
        // [왜] 마우스 아래에 있는 "가장 안쪽 컨트롤"이 이벤트를 받으므로, 폼뿐 아니라 말풍선 화면과 입력칸에도 똑같이 연결한다.
        AllowDrop = true;
        foreach (Control target in new Control[] { this, _view, _input })
        {
            target.AllowDrop = true;
            target.DragEnter += OnDragEnter;
            target.DragDrop += OnDragDrop;
        }

        // [왜] 창이 앞으로 오면(클릭, Alt+Tab 등) 사용자가 이 대화를 읽은 것으로 보고 안 읽은 수를 지운다.
        Activated += (_, _) => ReadAll?.Invoke(this);
        ActiveControl = _input; // 창이 실제로 활성화될 때 입력칸에 포커스 (자동으로 뜬 창이 포커스를 빼앗지 않게)
        // [주의] 여기서 _input.Focus()를 직접 부르면, 메시지가 와서 자동으로 뜬 창이 다른 프로그램에서 타이핑 중인
        //        사용자의 포커스를 빼앗는다. ActiveControl은 "활성화될 때 어디에 포커스를 줄지"만 정해 둔다.
        Shown += (_, _) => _view.ScrollToBottom(); // 창이 만들어진 뒤에야 스크롤이 적용된다
        // [주의] 생성자 시점에는 창의 실제 크기(핸들)가 없어 스크롤 위치 계산이 틀린다. 처음 화면에 보인 직후(Shown)에 맨 아래로.
    }

    // ───────── 화면 구성 ─────────

    /// <summary>
    /// 컨트롤을 만들고 배치한다.
    /// [개념] Dock: 컨트롤을 부모의 한쪽 가장자리에 붙인다(Top/Bottom/Left/Right) 또는 남은 공간을 채운다(Fill).
    ///        창 크기를 바꿔도 자동으로 따라 늘어나므로 좌표를 직접 계산할 필요가 없다.
    /// [개념] Dock 순서와 z-order: WinForms는 z-order의 "뒤쪽(먼저 추가된 쪽)"부터 가장자리를 차지시키고,
    ///        Fill은 남은 공간을 채운다. 그래서 Fill 컨트롤을 BringToFront()로 맨 앞에 두어야
    ///        다른 Top/Bottom 컨트롤에 가려지지 않고 "나머지 공간"을 정확히 채운다.
    /// </summary>
    private void BuildLayout()
    {
        // 머리: 아바타 + 제목 + 부제목, 오른쪽에 [나가기](채팅방만)
        _header.Dock = DockStyle.Top;
        _header.Height = S(68);
        // [왜] 머리의 아바타·글자는 컨트롤(Label 등)로 만들지 않고 Paint 이벤트에서 직접 그린다.
        //      그라데이션 배경 위에 투명한 글자를 깔끔하게 올리기 쉽고, 컨트롤 수가 줄어 가볍다.
        _header.Paint += PaintHeader;

        // [나가기] 버튼: 하늘색 머리 위에 흰 테두리만 있는 납작한(Flat) 버튼
        _leaveButton.Text = "나가기";
        _leaveButton.FlatStyle = FlatStyle.Flat;
        _leaveButton.FlatAppearance.BorderColor = Color.White;
        _leaveButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(80, 150, 240); // 마우스를 올리면 조금 진한 파랑
        _leaveButton.BackColor = Color.Transparent;
        _leaveButton.ForeColor = Color.White;
        _leaveButton.Font = Theme.SmallBold;
        _leaveButton.AutoSize = true;            // 글자 길이에 맞춰 버튼 크기 자동
        // [개념] Anchor = Top | Right: 부모 크기가 바뀌어도 위·오른쪽 거리를 유지 → 창을 넓혀도 오른쪽 끝에 붙어 있다.
        // [개념] | (비트 OR): 플래그 enum 값 여러 개를 합친다.
        _leaveButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _leaveButton.Visible = false;            // 기본은 숨김. 채팅방(기본 방 제외)일 때 메인 창이 ShowLeaveButton = true로 켠다.
        _leaveButton.Cursor = Cursors.Hand;
        // [왜] 여기서 바로 서버에 나가기를 보내지 않는다. 확인 창을 띄우고 전송하는 일은 메인 창이 한다.
        _leaveButton.Click += (_, _) => LeaveRequested?.Invoke(this);
        _header.Controls.Add(_leaveButton);
        // [왜] AutoSize 버튼은 실제 너비가 나중에 정해지므로, 머리 크기가 바뀔 때마다 위치를 다시 계산한다.
        _header.Resize += (_, _) => PlaceLeaveButton();

        // 대화
        _view.Dock = DockStyle.Fill;   // 머리와 입력 영역을 뺀 나머지 전부

        // [+] 버튼 → 카카오톡처럼 사진 / 동영상 / 파일을 따로 고르는 메뉴
        // [왜] 처음에는 📎 이모지를 썼는데 PC 글꼴에 따라 네모로 깨져 보여서, 어느 PC에서나 보이는 "+" 글자로 바꿨다.
        _attachButton.Text = "+";
        _attachButton.Font = new Font(Theme.Body.FontFamily, 17F, FontStyle.Bold);
        _attachButton.FlatStyle = FlatStyle.Flat;
        _attachButton.FlatAppearance.BorderSize = 0;   // 테두리 없는 아이콘 버튼처럼
        _attachButton.ForeColor = Theme.SubText;
        _attachButton.Size = new Size(S(40), S(40));
        _attachButton.Dock = DockStyle.Left;
        _attachButton.Cursor = Cursors.Hand;
        _attachButton.Click += (_, _) => ShowAttachMenu();
        // [개념] ToolTip: 마우스를 올려 두면 뜨는 작은 설명. 컨트롤마다 SetToolTip으로 문구를 지정한다.
        new ToolTip().SetToolTip(_attachButton, "사진·동영상·파일 보내기 (최대 3GB, 끌어다 놓기·Ctrl+V도 가능)");
        BuildAttachMenu();

        // 입력 영역
        _emoticonButton.Text = "☺";
        _emoticonButton.Font = new Font(Theme.Body.FontFamily, 14F);
        _emoticonButton.FlatStyle = FlatStyle.Flat;
        _emoticonButton.FlatAppearance.BorderSize = 0;
        _emoticonButton.ForeColor = Theme.SubText;
        _emoticonButton.Size = new Size(S(40), S(40));
        _emoticonButton.Dock = DockStyle.Left;
        _emoticonButton.Cursor = Cursors.Hand;
        // [개념] 람다 캡처: foreach 변수 emoticon을 람다가 캡처한다. C# 5부터 foreach 변수는 반복마다 새로 만들어지므로
        //        각 메뉴 항목이 "자기" 이모티콘을 정확히 기억한다(옛날 C#에서는 모두 마지막 값이 되는 함정이 있었다).
        foreach (string emoticon in Emoticons)
            _emoticonMenu.Items.Add(emoticon, null, (_, _) => InsertText(emoticon));
        // [왜] AboveRight: 버튼이 창 맨 아래에 있으므로 메뉴를 버튼 "위쪽"으로 펼쳐야 화면 밖으로 잘리지 않는다.
        _emoticonButton.Click += (_, _) => _emoticonMenu.Show(_emoticonButton, new Point(0, 0), ToolStripDropDownDirection.AboveRight);

        _input.Multiline = true;
        _input.AcceptsReturn = true; // 줄바꿈은 Shift+Enter, 전송은 Enter (KeyDown에서 처리)
        _input.ScrollBars = ScrollBars.Vertical;
        _input.BorderStyle = BorderStyle.None;   // 흰 상자(inputBox)가 테두리 역할을 하므로 입력칸 자체 테두리는 없앤다
        _input.Font = Theme.Body;
        // [왜] 서버도 500자로 자른다(PacketHandler.MaxMessageLength, DB content VARCHAR(500)). 화면에서 먼저 막아 일관성 유지.
        _input.MaxLength = 500;
        _input.Dock = DockStyle.Fill;
        _input.KeyDown += OnInputKeyDown;

        _sendButton.Text = "전송";
        _sendButton.FlatStyle = FlatStyle.Flat;
        _sendButton.FlatAppearance.BorderSize = 0;
        _sendButton.BackColor = Theme.Primary;
        _sendButton.ForeColor = Color.White;
        _sendButton.Font = Theme.BodyBold;
        _sendButton.Width = S(70);
        _sendButton.Dock = DockStyle.Right;
        _sendButton.Cursor = Cursors.Hand;
        _sendButton.Click += (_, _) => Send();

        // [개념] 객체 초기화자 new Panel { 속성 = 값, ... }: 만들면서 속성을 한 번에 지정한다.
        // inputBox = 흰 둥근 상자 안에 [+][☺][입력칸][전송]
        var inputBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(6), S(6), S(6), S(6)), BackColor = Theme.Surface };
        inputBox.Controls.Add(_input);
        inputBox.Controls.Add(_emoticonButton);
        inputBox.Controls.Add(_attachButton); // 나중에 넣은 Left 도킹이 더 왼쪽 → [+][☺]
        inputBox.Controls.Add(_sendButton);
        _input.BringToFront(); // Fill은 z-order 맨 앞 → 좌우 버튼을 뺀 나머지를 채운다

        // inputArea = 창 아래쪽 연한 파랑 띠. 안쪽 여백(Padding)을 두고 inputBox를 담는다.
        var inputArea = new Panel { Dock = DockStyle.Bottom, Height = S(84), Padding = new Padding(S(10), S(10), S(10), S(10)), BackColor = Theme.Background };
        inputArea.Controls.Add(inputBox);
        // 대화 영역과 입력 영역 사이에 얇은 구분선 1줄
        inputArea.Paint += (_, e) =>
        {
            // [개념] using var: 이 블록이 끝날 때 pen.Dispose()가 자동 호출된다.
            //        Pen/Brush/Font 같은 GDI 객체는 윈도우 자원을 쓰므로 다 쓰면 반드시 해제해야 한다(안 하면 자원 누수).
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, inputArea.Width, 0);
        };

        // 창에 붙이는 순서: 대화(Fill) → 입력(Bottom) → 머리(Top)
        BuildSearchBar();
        Controls.Add(_view);
        Controls.Add(inputArea);
        Controls.Add(_searchBar); // 머리(_header)보다 먼저 Add → 머리 바로 아래에 붙는다 (Dock은 나중에 Add한 것이 먼저 가장자리를 차지)
        Controls.Add(_header);
        // [왜] Fill인 _view를 맨 앞으로 → 머리(Top)와 입력(Bottom)이 먼저 자리를 잡고 _view가 그 사이를 채운다.
        _view.BringToFront();
    }

    /// <summary>[나가기] 버튼을 머리 오른쪽 끝, 세로 가운데에 놓는다.</summary>
    private void PlaceLeaveButton()
        => _leaveButton.Location = new Point(_header.Width - _leaveButton.Width - S(14), (_header.Height - _leaveButton.Height) / 2);

    /// <summary>
    /// 머리 그리기 (Paint 이벤트). 그라데이션 배경은 GradientPanel이 먼저 그리고, 그 위에 아바타·제목·부제목을 그린다.
    /// [개념] Paint 이벤트: 윈도우가 "이 영역을 다시 그려라"라고 할 때마다(창이 가려졌다 보일 때, Invalidate() 호출 등) 실행된다.
    ///        그래서 그릴 내용은 필드(_headerTitle 등)에 저장해 두고, 바뀌면 Invalidate()로 다시 그리게 한다.
    /// </summary>
    private void PaintHeader(object? sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        int size = S(44);
        // 아바타: 왼쪽에서 14px, 세로 가운데
        var avatar = new Rectangle(S(14), (_header.Height - size) / 2, size, size);

        // 흰 테두리 + 아바타 (채팅방은 '#')
        // [개념] Color.FromArgb(200, 255, 255, 255): 첫 값 200이 알파(불투명도, 0=투명~255=불투명). 반투명 흰색 테두리.
        // [개념] Rectangle.Inflate(r, 2, 2): 사방으로 2px씩 키운 사각형 → 아바타보다 조금 큰 원 = 테두리처럼 보임.
        using (var ring = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
            g.FillEllipse(ring, Rectangle.Inflate(avatar, S(2), S(2)));
        // [개념] 삼항 연산자 조건 ? A : B
        string avatarText = Kind == ChatWindowKind.Room ? "#" : _headerTitle;
        Theme.DrawAvatar(g, avatar, avatarText, Theme.AvatarColor(AvatarKey));
        if (Kind == ChatWindowKind.Direct)
        {
            // 1:1이면 아바타 오른쪽 아래에 접속 상태 점(초록=접속, 회색=오프라인)
            int dot = S(14);
            Theme.DrawStatusDot(g, new Rectangle(avatar.Right - dot + S(2), avatar.Bottom - dot + S(2), dot, dot), _online);
        }

        // 글자 영역: 아바타 오른쪽부터 [나가기] 버튼 왼쪽까지 (버튼이 없으면 머리 끝까지)
        int x = avatar.Right + S(12);
        int right = _leaveButton.Visible ? _leaveButton.Left - S(8) : _header.Width - S(12);
        // [개념] TextFormatFlags: SingleLine(한 줄), EndEllipsis(넘치면 끝을 "..."로), NoPrefix(& 문자를 단축키 밑줄로 해석하지 않음)
        // [주의] NoPrefix가 없으면 방 이름에 "A&B"가 있을 때 &가 사라지고 B에 밑줄이 그어진다.
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        // [개념] TextRenderer.DrawText: GDI 방식 글자 그리기. 윈도우 기본 컨트롤과 같은 방식이라 한글이 선명하다.
        TextRenderer.DrawText(g, _headerTitle, Theme.Title, new Rectangle(x, avatar.Y - S(2), right - x, S(26)), Color.White, flags);
        TextRenderer.DrawText(g, _headerSubtitle, Theme.Small, new Rectangle(x, avatar.Y + S(24), right - x, S(20)), Theme.HeaderSubText, flags);
    }

    // ───────── 외부에서 바꾸는 상태 ─────────
    // (메인 창이 방 정보·접속 상태가 바뀔 때 호출한다. 모두 UI 스레드에서 불린다.)

    /// <summary>머리의 제목·부제목과 창 제목 표시줄(Text)을 바꾼다. 예: "개발팀 회의" / "3/10 참여 중 · 방장 홍길동 대리".</summary>
    public void SetHeader(string title, string subtitle)
    {
        _headerTitle = title;
        _headerSubtitle = subtitle;
        // [개념] $"..." 문자열 보간: {} 안의 식 값이 문자열에 들어간다.
        Text = Kind == ChatWindowKind.Room ? $"{title} - {Theme.AppName}" : $"{title} - 1:1 대화";
        _header.Invalidate(); // [개념] Invalidate: "다시 그려 달라" 요청 → 곧 PaintHeader가 새 값으로 실행된다.
    }

    /// <summary>[나가기] 버튼 표시 여부. 기본 방("전체 채팅")과 1:1 창에서는 숨긴다.</summary>
    // [개념] [DesignerSerializationVisibility(Hidden)]: Visual Studio 디자이너가 이 속성 값을 코드로 저장하지 않게 한다.
    //        public 속성을 가진 Form은 디자이너/분석기(WFO1000 경고)가 "직렬화할 속성인가?"를 묻기 때문에 명시해 둔다.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowLeaveButton
    {
        get => _leaveButton.Visible;
        set
        {
            _leaveButton.Visible = value;
            PlaceLeaveButton();
            _header.Invalidate(); // 제목이 쓸 수 있는 너비가 달라지므로 다시 그림
        }
    }

    /// <summary>1:1 상대의 접속 상태. 오프라인이면 입력을 막는다.</summary>
    public void SetOnline(bool online)
    {
        // [왜] 서버는 접속 중인 사람에게만 귓속말·파일을 전달할 수 있다(오프라인 메시지 미지원).
        //      보내 봐야 오류가 나므로 처음부터 입력을 막아 사용자가 헷갈리지 않게 한다.
        // [주의] 입력 상태는 값이 같아도 매번 다시 맞춘다. 재접속하는 동안 SetInputEnabled(false)로 막아 둔 창이
        //        다시 연결된 뒤 "상대 온라인(이전과 같은 값)"을 받았을 때도 입력이 풀려야 하기 때문이다.
        SetInputEnabled(online);
        if (_online == online) return; // 머리 부분(접속 점)은 값이 바뀔 때만 다시 그린다
        _online = online;
        _header.Invalidate();
    }

    /// <summary>입력칸과 버튼들을 한꺼번에 켜고/끈다 (연결 끊김, 상대 오프라인).</summary>
    public void SetInputEnabled(bool enabled)
    {
        _input.Enabled = enabled;
        _sendButton.Enabled = enabled;
        _emoticonButton.Enabled = enabled;
        _attachButton.Enabled = enabled;
        // [왜] Flat 버튼은 Enabled = false여도 배경색이 그대로라 눌리는 것처럼 보인다. 회색으로 바꿔 꺼진 것을 알린다.
        _sendButton.BackColor = enabled ? Theme.Primary : Theme.Offline;
    }

    /// <summary>새 메시지가 와서 자동으로 뜰 때 지금 작업 중인 창의 포커스를 빼앗지 않는다.</summary>
    public void ShowInactive()
    {
        // [개념] ShowWithoutActivation: 이 속성이 true를 돌려주는 동안 Show()하면 창이 "활성화되지 않은 채로" 나타난다.
        //        (보이기는 하지만 키보드 포커스는 지금 쓰던 프로그램에 그대로 남는다)
        // [왜] 다른 사람이 1:1 메시지를 보냈을 때 창이 자동으로 뜨는데, 사용자가 다른 곳에서 타이핑 중이면
        //      입력이 갑자기 이 창으로 빨려 들어가는 사고가 생긴다. 그래서 잠깐만 true로 바꿔서 띄운다.
        _showWithoutActivation = true;
        Show();
        _showWithoutActivation = false;
    }

    // [개념] override: 부모(Form)의 가상 속성을 다시 정의. 윈도우가 창을 띄울 때 이 값을 물어본다.
    protected override bool ShowWithoutActivation => _showWithoutActivation;

    /// <summary>
    /// 프로그램이 창을 닫을 때 (방에서 나가기 완료, 메인 창 종료 등).
    /// [왜] 사용자가 X를 눌러 닫은 것(=창만 닫고 방에는 남음)과 구분하려고 ClosedByProgram을 먼저 켠다.
    /// </summary>
    public void CloseByProgram()
    {
        ClosedByProgram = true;
        Close();
    }

    // ───────── 입력 / 전송 ─────────

    // [스레드] 아래 세 메서드는 Conversation 이벤트로 불린다. Conversation은 UI 스레드에서만 바뀌므로 여기도 UI 스레드.
    /// <summary>대화에 메시지 1개가 추가됨 → 말풍선 1개 추가 (전체를 다시 배치하지 않아 빠르다).</summary>
    private void OnItemAdded(ChatItem item) => _view.AddItem(item);

    /// <summary>파일 진행률·상태처럼 기존 말풍선 내용만 바뀜 → 다시 그리기만.</summary>
    private void OnItemChanged(ChatItem item) => _view.RefreshItem(item);

    /// <summary>지난 대화가 앞에 들어왔다 → 전체를 다시 배치해서 그린다.</summary>
    private void OnReloaded() => _view.SetItems(Conversation.Items);

    /// <summary>
    /// 입력칸 키 처리: Ctrl+V(파일·이미지 붙여넣기), Enter(전송), Shift+Enter(줄바꿈).
    /// </summary>
    // ───────── 대화 검색 (Ctrl+F) ─────────

    private readonly Panel _searchBar = new();
    private readonly TextBox _searchBox = new();
    private readonly Label _searchCount = new();
    private List<ChatItem> _matches = [];
    private int _matchIndex;

    private void BuildSearchBar()
    {
        _searchBar.Dock = DockStyle.Top;
        _searchBar.Height = S(40);
        _searchBar.Padding = new Padding(S(10), S(7), S(10), S(7));
        _searchBar.BackColor = Theme.Background;
        _searchBar.Visible = false;

        Button MakeButton(string text, string tip, Action click)
        {
            var b = new Button
            {
                Text = text, Dock = DockStyle.Right, Width = S(30), FlatStyle = FlatStyle.Flat,
                ForeColor = Theme.SubText, Cursor = Cursors.Hand, TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => click();
            new ToolTip().SetToolTip(b, tip);
            return b;
        }

        _searchBox.Dock = DockStyle.Fill;
        _searchBox.PlaceholderText = "대화 내용 검색 (Enter 이전 결과, Shift+Enter 다음 결과)";
        _searchBox.TextChanged += (_, _) => RunSearch();
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; MoveMatch(e.Shift ? +1 : -1); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CloseSearch(); }
        };
        _searchCount.Dock = DockStyle.Right;
        _searchCount.Width = S(70);
        _searchCount.TextAlign = ContentAlignment.MiddleCenter;
        _searchCount.ForeColor = Theme.SubText;
        _searchCount.Font = Theme.Small;

        // Dock Right는 나중에 Add한 것이 더 오른쪽 → 순서: [검색칸][3/12][▲][▼][×]
        _searchBar.Controls.Add(_searchBox);
        _searchBar.Controls.Add(_searchCount);
        _searchBar.Controls.Add(MakeButton("▲", "이전 결과 (Enter)", () => MoveMatch(-1)));
        _searchBar.Controls.Add(MakeButton("▼", "다음 결과 (Shift+Enter)", () => MoveMatch(+1)));
        _searchBar.Controls.Add(MakeButton("✕", "검색 닫기 (Esc)", CloseSearch));
        _searchBox.BringToFront(); // Fill은 맨 앞으로 → 오른쪽 버튼들을 뺀 나머지를 채운다
    }

    /// <summary>
    /// [개념] ProcessCmdKey: 창 안의 어느 컨트롤에 포커스가 있든 단축키를 먼저 받아 볼 수 있는 곳.
    ///   입력칸에 포커스가 있어도 Ctrl+F가 검색 막대를 연다.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F))
        {
            OpenSearch();
            return true; // 처리했음 → 컨트롤에 넘기지 않는다
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OpenSearch()
    {
        _searchBar.Visible = true;
        _searchBox.Focus();
        _searchBox.SelectAll();
        RunSearch();
    }

    private void CloseSearch()
    {
        _searchBar.Visible = false;
        _matches = [];
        _view.SetSearch(null, null);
        _input.Focus();
    }

    /// <summary>검색어가 바뀜 → 일치하는 말풍선을 다시 찾고, 가장 최근 결과부터 보여 준다(카카오톡처럼 아래에서 위로).</summary>
    private void RunSearch()
    {
        string query = _searchBox.Text.Trim();
        _matches = query.Length == 0 ? [] : Conversation.Items.Where(i => BubbleChatView.Matches(i, query)).ToList();
        _matchIndex = _matches.Count - 1;
        ShowMatch(query);
    }

    /// <summary>-1 = 이전(위, 더 오래된) 결과, +1 = 다음(아래, 더 최근) 결과. 끝에 닿으면 반대쪽으로 돈다.</summary>
    private void MoveMatch(int step)
    {
        if (_matches.Count == 0) return;
        _matchIndex = (_matchIndex + step + _matches.Count) % _matches.Count;
        ShowMatch(_searchBox.Text.Trim());
    }

    private void ShowMatch(string query)
    {
        ChatItem? current = _matches.Count == 0 ? null : _matches[_matchIndex];
        _searchCount.Text = query.Length == 0 ? "" : _matches.Count == 0 ? "결과 없음" : $"{_matchIndex + 1}/{_matches.Count}";
        _view.SetSearch(query, current);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        // [개념] KeyData = 누른 키 + 함께 누른 Ctrl/Shift/Alt 조합. (Keys.Control | Keys.V) = Ctrl+V.
        // [개념] && 단락 평가: 앞이 false면 뒤(TryPasteFiles)는 아예 실행되지 않는다 → Ctrl+V일 때만 클립보드를 확인.
        if (e.KeyData == (Keys.Control | Keys.V) && TryPasteFiles())
        {
            e.SuppressKeyPress = true; // 파일·이미지를 보냈으면 글자로 붙여 넣지 않는다
            // [개념] SuppressKeyPress = true: 이 키 입력을 텍스트 상자에 전달하지 않는다(기본 동작 취소).
            return;
        }
        if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            e.SuppressKeyPress = true; // Enter = 전송 (Shift+Enter는 그대로 줄바꿈)
            // [주의] 이걸 안 하면 전송 후 입력칸에 줄바꿈 문자가 하나 남거나 "띵" 소리가 난다.
            Send();
        }
    }

    /// <summary>입력칸의 글을 메인 창에 넘겨 보내게 하고, 입력칸을 비운다.</summary>
    private void Send()
    {
        string text = _input.Text.Trim(); // 앞뒤 공백·줄바꿈 제거 (빈 줄만 있는 메시지 방지)
        if (text.Length == 0 || !_input.Enabled) return;
        // [왜] 실제 전송(패킷 만들기, 방 번호/상대 지정)은 메인 창이 한다. 이 창은 "보내 달라"고만 알린다.
        SendRequested?.Invoke(this, text);
        _input.Clear();
        _input.Focus();
    }

    // ───────── 파일 보내기 ─────────

    /// <summary>[+] 메뉴 아이콘 종류 (클래스 안에서만 쓰는 enum이라 private).</summary>
    private enum AttachIcon { Photo, Video, File, Paste }

    /// <summary>
    /// [+] 버튼을 누르면 뜨는 메뉴: 사진 / 동영상 / 파일 / (구분선) / 캡처 이미지 붙여넣기.
    /// [왜] 카카오톡처럼 종류를 먼저 고르게 하면, 파일 선택 창이 그 종류의 파일만 보여 줘서 찾기 쉽다.
    /// </summary>
    private void BuildAttachMenu()
    {
        _attachMenu.Font = Theme.Body;
        _attachMenu.ImageScalingSize = new Size(S(24), S(24)); // 메뉴 아이콘 크기 (DPI에 맞게)
        // [개념] () => ChooseFiles(FileKind.Image): 매개변수 없는 람다. 메뉴를 클릭했을 때 실행할 일을 "값처럼" 넘긴다.
        _attachMenu.Items.Add(CreateAttachItem("사진", AttachIcon.Photo, Color.FromArgb(16, 185, 129), () => ChooseFiles(FileKind.Image)));
        _attachMenu.Items.Add(CreateAttachItem("동영상", AttachIcon.Video, Color.FromArgb(139, 92, 246), () => ChooseFiles(FileKind.Video)));
        _attachMenu.Items.Add(CreateAttachItem("파일", AttachIcon.File, Theme.Primary, () => ChooseFiles(FileKind.File)));
        _attachMenu.Items.Add(new ToolStripSeparator());
        // [개념] () => TryPasteFiles(): TryPasteFiles는 bool을 돌려주지만 Action(반환값 없음) 자리에 넣으면 반환값은 그냥 버려진다.
        _pasteMenuItem = CreateAttachItem("캡처 이미지 붙여넣기  (Ctrl+V)", AttachIcon.Paste, Color.FromArgb(245, 158, 11), () => TryPasteFiles());
        _attachMenu.Items.Add(_pasteMenuItem);
    }

    /// <summary>[+] 메뉴 띄우기. 붙여넣기 항목은 클립보드에 이미지/파일이 있을 때만 켠다.</summary>
    private void ShowAttachMenu()
    {
        if (_pasteMenuItem is not null)
        {
            // [주의] 클립보드는 윈도우 전체가 함께 쓰는 자원이라, 다른 프로그램이 잠깐 잡고 있으면 ExternalException이 난다.
            //        예외로 프로그램이 죽지 않도록 잡고, 그때는 항목만 꺼 둔다.
            try { _pasteMenuItem.Enabled = Clipboard.ContainsImage() || Clipboard.ContainsFileDropList(); }
            catch (System.Runtime.InteropServices.ExternalException) { _pasteMenuItem.Enabled = false; }
        }
        _attachMenu.Show(_attachButton, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
    }

    /// <summary>메뉴 항목 1개 만들기: 글자 + 직접 그린 아이콘 + 클릭 시 실행할 일.</summary>
    private ToolStripMenuItem CreateAttachItem(string text, AttachIcon icon, Color color, Action onClick)
    {
        var item = new ToolStripMenuItem(text, CreateAttachIcon(icon, color)) { Padding = new Padding(0, S(3), 0, S(3)) };
        // [개념] onClick(Action)을 람다가 캡처해서, 클릭할 때 그 동작을 실행한다.
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>메뉴 아이콘: 색 있는 둥근 사각형 + 흰 그림(사진: 산과 해, 동영상: ▶, 파일: 접힌 문서, 붙여넣기: 겹친 종이).</summary>
    /// <remarks>
    /// [왜] 이미지 파일(리소스)을 넣지 않고 코드로 그린다: 프로젝트에 그림 파일이 필요 없고, DPI에 맞춰 선명하게 그릴 수 있다.
    /// [개념] Bitmap + Graphics.FromImage: 메모리 속 그림판을 만들고 거기에 그린다. 그 결과를 메뉴 아이콘으로 쓴다.
    /// </remarks>
    private Bitmap CreateAttachIcon(AttachIcon icon, Color color)
    {
        int size = S(24);
        var bmp = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias; // [개념] 안티앨리어싱: 곡선·대각선 가장자리를 부드럽게(계단 현상 제거)
        Theme.FillRounded(g, color, new Rectangle(0, 0, size - 1, size - 1), size * 0.28f);

        float u = size / 24f; // 24px 기준 좌표를 DPI에 맞게
        // [왜] 아래 좌표들은 "24×24 칸 기준"으로 생각해서 적고 u를 곱한다. 화면 배율이 150%면 u = 1.5가 되어 그대로 확대된다.
        using var white = new SolidBrush(Color.White);
        using var pen = new Pen(Color.White, 1.6f * u);
        switch (icon)
        {
            case AttachIcon.Photo:
                // 오른쪽 위 해(원) + 아래쪽 산 두 개(다각형)
                g.FillEllipse(white, 14 * u, 5 * u, 4.5f * u, 4.5f * u);
                g.FillPolygon(white, new[] { new PointF(4 * u, 19 * u), new PointF(10 * u, 10 * u), new PointF(14 * u, 15 * u), new PointF(16 * u, 13 * u), new PointF(20 * u, 19 * u) });
                break;
            case AttachIcon.Video:
                // 재생 삼각형 ▶
                g.FillPolygon(white, new[] { new PointF(9 * u, 6.5f * u), new PointF(18 * u, 12 * u), new PointF(9 * u, 17.5f * u) });
                break;
            case AttachIcon.File:
                // 오른쪽 위 모서리가 잘린 문서 + 그 자리를 배경색 삼각형으로 칠해 "접힌 귀퉁이"처럼 보이게
                g.FillPolygon(white, new[] { new PointF(7 * u, 4.5f * u), new PointF(14 * u, 4.5f * u), new PointF(17.5f * u, 8 * u), new PointF(17.5f * u, 19.5f * u), new PointF(7 * u, 19.5f * u) });
                using (var fold = new SolidBrush(color))
                    g.FillPolygon(fold, new[] { new PointF(14 * u, 4.5f * u), new PointF(14 * u, 8 * u), new PointF(17.5f * u, 8 * u) });
                break;
            case AttachIcon.Paste:
                // 뒤쪽 종이(테두리만) + 앞쪽 종이(채움) = 겹친 종이(복사/붙여넣기)
                g.DrawRectangle(pen, 6 * u, 7 * u, 9 * u, 11 * u);
                g.FillRectangle(white, 10 * u, 4 * u, 9 * u, 11 * u);
                break;
        }
        // [주의] bmp는 using으로 해제하지 않는다. 메뉴 항목이 계속 이 그림을 쓰기 때문(메뉴가 해제될 때 함께 정리된다).
        return bmp;
    }

    /// <summary>[+] 메뉴에서 사진 / 동영상 / 파일을 골라 보낸다. 여러 개 선택 가능.</summary>
    private void ChooseFiles(FileKind kind)
    {
        // [개념] 로컬 함수(메서드 안의 메서드). static이라 바깥 변수를 캡처하지 않는다.
        // 확장자 목록 [".jpg", ".png"] → "*.jpg;*.png" (파일 선택 창 필터 형식)
        static string Patterns(IEnumerable<string> extensions) => string.Join(";", extensions.Select(e => "*" + e));

        // [개념] using var dialog: 대화 상자를 다 쓰면 Dispose (윈도우 자원 해제).
        using var dialog = new OpenFileDialog { Multiselect = true };
        switch (kind)
        {
            // [개념] OpenFileDialog.Filter 문법: "표시 이름|패턴;패턴|표시 이름2|패턴2"
            //        | 로 (이름, 패턴) 쌍을 구분하고, 한 쌍 안의 여러 패턴은 ; 로 구분한다.
            case FileKind.Image:
                dialog.Title = "보낼 사진 선택 (여러 장 선택 가능)";
                dialog.Filter = $"사진|{Patterns(FileRules.ImageExtensions)}";
                break;
            case FileKind.Video:
                dialog.Title = "보낼 동영상 선택";
                dialog.Filter = $"동영상|{Patterns(FileRules.VideoExtensions)}";
                break;
            default:
                dialog.Title = "보낼 파일 선택 (최대 3GB)";
                dialog.Filter = "모든 파일 (*.*)|*.*";
                break;
        }
        // [개념] ShowDialog(this): 이 창을 주인으로 하는 "모달" 창. 닫을 때까지 이 창은 클릭할 수 없다.
        if (dialog.ShowDialog(this) == DialogResult.OK) RequestFiles(dialog.FileNames);
    }

    /// <summary>
    /// 무언가를 끌고 창 위로 들어왔을 때: 파일이면 "복사" 커서, 아니면 "금지" 커서를 보여 준다.
    /// </summary>
    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        // [개념] DataFormats.FileDrop: 탐색기에서 파일을 끌어 올 때의 데이터 형식(경로 배열).
        // [개념] e.Data?.GetDataPresent(...) == true: e.Data가 null이면 결과가 null → "== true"로 null을 false처럼 처리.
        // [왜] 입력이 막혀 있으면(연결 끊김·상대 오프라인) 놓아도 보낼 수 없으므로 처음부터 받지 않는다고 표시.
        bool ok = _input.Enabled && e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>파일을 놓았을 때: 경로 목록을 받아 보내기 요청.</summary>
    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        // [개념] is string[] paths: 타입 패턴. 형변환이 되면 paths에 담고 true, 아니면 false.
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) RequestFiles(paths);
    }

    /// <summary>Ctrl+V: 복사한 파일이면 그 파일을, 캡처한 이미지면 PNG로 저장해서 보낸다. 보냈으면 true.</summary>
    /// <remarks>
    /// 돌려주는 값의 뜻: true = "파일/이미지로 처리했으니 글자 붙여넣기는 하지 마라", false = "평소처럼 글자 붙여넣기".
    /// </remarks>
    private bool TryPasteFiles()
    {
        try
        {
            // 1) 탐색기에서 파일을 복사(Ctrl+C)한 경우 → 그 파일들을 보낸다
            if (Clipboard.ContainsFileDropList())
            {
                // [개념] GetFileDropList()는 옛날 컬렉션(StringCollection)이라 Cast<string>()으로 IEnumerable<string>으로 바꾼다.
                RequestFiles(Clipboard.GetFileDropList().Cast<string>());
                return true;
            }
            // 2) 캡처 도구(Win+Shift+S 등)로 이미지를 복사한 경우 → 임시 PNG 파일로 저장해서 보낸다
            //    (전송 엔진은 "파일 경로"를 받아 조각내어 보내므로, 메모리 속 이미지를 먼저 파일로 만든다)
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is Image image)
            {
                using (image) // 이미지도 GDI 자원 → 다 쓰면 해제
                {
                    // [왜] 실수로 Ctrl+V한 이미지가 바로 방 전체에 올라가지 않도록 한 번 묻는다.
                    if (MessageBox.Show(this, "복사한 이미지를 보낼까요?", "이미지 보내기", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        return !Clipboard.ContainsText(); // 글자도 같이 복사됐으면 글자로 붙여 넣는다
                    // [개념] [값]: 원소 1개짜리 컬렉션 식 → IEnumerable<string>으로 넘어간다.
                    RequestFiles([ImageHelper.SaveClipboardImage(image)]);
                }
                return true;
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // 다른 프로그램이 클립보드를 잡고 있음 → 그냥 글자 붙여 넣기
        }
        return false;
    }

    /// <summary>
    /// 보낼 파일 목록 정리 후 메인 창에 전달 (📎 메뉴, 끌어다 놓기, Ctrl+V가 모두 여기로 모인다).
    /// - 폴더는 뺀다(폴더는 조각 전송을 할 수 없음).
    /// - 10개 넘게 한꺼번에 보내면 한 번 확인한다(실수로 폴더 내용 전체를 끌어다 놓는 경우 대비).
    /// </summary>
    private void RequestFiles(IEnumerable<string> paths)
    {
        if (!_input.Enabled) return;
        // 폴더는 보낼 수 없다
        // [주의] IEnumerable은 여러 번 돌면 매번 다시 계산될 수 있으므로 ToList()로 한 번만 목록을 만든다.
        List<string> all = paths.ToList();
        // [개념] File.Exists를 메서드 그룹으로 넘김: x => File.Exists(x) 와 같다. 폴더 경로는 false라서 걸러진다.
        List<string> files = all.Where(File.Exists).ToList();
        if (files.Count < all.Count)
            MessageBox.Show(this, "폴더는 보낼 수 없습니다. 압축(zip)해서 보내 주세요.", "파일 보내기", MessageBoxButtons.OK, MessageBoxIcon.Information);
        if (files.Count == 0) return;
        if (files.Count > 10 &&
            MessageBox.Show(this, $"파일 {files.Count}개를 보낼까요?", "파일 보내기", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        // [왜] 실제 업로드(미리보기 생성, 전송 스레드 시작, 진행률 말풍선)는 메인 창 + FileTransferService가 한다.
        FilesRequested?.Invoke(this, files);
    }

    /// <summary>이모티콘을 커서 위치에 끼워 넣는다 (선택된 글자가 있으면 그 글자를 바꾼다).</summary>
    private void InsertText(string text)
    {
        // [개념] SelectedText에 값을 넣으면 "현재 선택 영역(없으면 커서 위치)"이 그 글자로 바뀐다.
        _input.SelectedText = text;
        _input.Focus(); // 메뉴를 누르느라 빠진 포커스를 입력칸으로 되돌려 바로 이어서 타이핑할 수 있게
    }
}
