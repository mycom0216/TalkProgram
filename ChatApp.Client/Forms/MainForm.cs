using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Net.Sockets;
using System.Security.Authentication;
using ChatApp.Client.Chat;
using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Security;
using ChatApp.Common.Transfer;

namespace ChatApp.Client.Forms;

//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]  ※ MainForm은 partial class로 10개 파일에 나뉘어 있다. 이 파일은 "뼈대"(필드·생성자·화면 구성·닫기·공통 보내기)이고, 기능은 아래 파일에 있다.
//
// ■ 한 줄 요약
//   로그인 후 뜨는 세로형 메인 창이자 클라이언트의 "관제탑": 서버 패킷을 모두 받아 대화(Conversation)에 나눠 담고, 대화창(ChatWindow)·파일 전송·알림·재접속을 조율한다.
//
// ■ 파일 지도 (MainForm.*.cs: 어디에 무엇이 있나)
//   MainForm.cs ........ 필드, 생성자(이벤트 연결), BuildLayout, OnLoad/OnMainClosing, 창에 연결(WireWindow), 보내기 도우미
//   .Network.cs ........ 패킷 수신 입구(OnPacketReceived → HandlePacket 큰 switch), 끊김·자동 재접속, 재로그인, 채팅/귓속말/시스템 처리
//   .Messages.cs ....... 읽음 표시(MarkRead, RecomputeUnread), 공감·삭제·수정·입력 중 반영
//   .Rooms.cs .......... 방 응답/갱신/나감, 방 만들기·입장·초대·정원·공지, 방 대화창 열기
//   .Direct.cs ......... 1:1 창 열기, 지난 대화(HandleHistory) 모으기, 안 읽은 1:1
//   .Files.cs .......... 파일 올리기/받기/취소, 전송 이벤트 → 말풍선 갱신, 이미지 뷰어
//   .Lists.cs .......... 동료·채팅방 목록 그리기(RefreshUserList/RefreshRoomList)
//   .Profile.cs ........ 프로필 카드·사진·친구 추가·로그아웃·새 버전 확인
//   .Status.cs ......... 상태(온라인/자리비움) 메뉴, 자동 자리 비움, 트레이 아이콘, 토스트 알림, 관리자 창 열기
//   .Tools.cs .......... 검색 창, 사람 찾기
//
// ■ 읽는 순서
//   ① 필드(대화 사전, 창 사전, 타이머) → ② 생성자(누가 누구를 구독하나) → ③ OnLoad(구독 시작·ReleaseHeld) → ④ OnMainClosing(정리 순서)
//   → ⑤ .Network.cs의 HandlePacket(모든 패킷의 갈림길) → ⑥ 관심 기능 파일
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - partial class: 큰 클래스를 파일로 나눔 ....... "C# partial class split large form"
//   - UI 스레드와 크로스 스레드 호출 ................ "WinForms cross-thread BeginInvoke"
//   - 이벤트 구독/해제와 메모리 누수 ................ "event handler memory leak unsubscribe"
//   - 디바운스 타이머(여러 알림을 한 번의 다시 그리기로) .. "debounce timer UI refresh"
//   - 트레이 아이콘(NotifyIcon)과 X = 숨기기 ......... "NotifyIcon minimize to tray FormClosing Cancel"
//   - DPI 배율(LogicalToDeviceUnits) ................ "WinForms high DPI LogicalToDeviceUnits"
//   - 낙관적 UI 대신 "서버 방송으로 반영" ........... "server authoritative UI update"
//
// ■ 면접 예상 질문과 답
//   Q1. MainForm이 너무 커서 partial로 나눴는데, 그냥 파일만 나눈 것 아닌가요?
//       A. 맞습니다. partial은 컴파일러에겐 하나의 클래스라 필드를 공유하고, 파일은 기능별로만 나뉩니다. 책임을 진짜로 분리한 것은 아니라는 한계를 압니다. 다음 단계는 "대화 관리", "전송 관리", "재접속 관리"를 별도 클래스로 빼고 MainForm은 화면만 맡게 하는 것입니다. 우선은 위험이 적은 방법(파일 분할)으로 읽기 쉬움부터 확보했습니다.
//   Q2. 서버 패킷은 어느 스레드로 오고, 화면은 어떻게 바꾸나요?
//       A. 수신 스레드에서 PacketReceived가 발생합니다. OnPacketReceived가 UiHelper.RunOnUi(BeginInvoke)로 UI 스레드에 넘기고, 그 뒤에 HandlePacket이 대화와 화면을 바꿉니다. Invoke(동기)를 쓰면 ChatClient.ReleaseHeld의 lock과 UI 스레드가 서로 기다리는 데드락이 날 수 있어 BeginInvoke만 씁니다.
//   Q3. 내가 보낸 메시지가 화면에 바로 나타나나요, 서버 응답을 기다리나요?
//       A. 서버가 모든 참여자(나 포함)에게 방송하는 메시지를 받아서 그릴 때 나타납니다. 그래서 화면에 보이는 것은 항상 서버가 받아 준 메시지이고, 순서와 시각이 서버 기준으로 통일됩니다. 대신 네트워크가 느리면 내 메시지도 늦게 보입니다(파일은 올리는 중 말풍선을 먼저 보여 줍니다).
//   Q4. 삭제·수정 가능 여부는 누가 판단하나요?
//       A. 클라이언트의 CanDelete/CanEdit는 메뉴를 보여 줄지만 정합니다(내가 보낸 24시간 안, 관리자는 전체 / 수정은 5분 안). 최종 판단은 서버(MessageService의 DeleteWindow, EditWindow)가 합니다. 클라이언트 검사는 편의일 뿐이고, 수정된 클라이언트가 보내도 서버가 거부합니다.
//   Q5. X 버튼을 누르면 왜 종료가 아니라 숨기기인가요? 어떻게 구현했나요?
//       A. 메신저는 계속 떠 있어야 알림을 받기 때문입니다. FormClosing에서 CloseReason이 UserClosing이고 _exiting이 아니면 e.Cancel = true로 닫기를 막고 Hide()합니다. 트레이 메뉴의 "종료"가 _exiting을 켜고 진짜 닫습니다. 윈도우 종료 때는 CloseReason이 달라 그대로 닫혀 OS 종료를 막지 않습니다.
//   Q6. 사진이 한꺼번에 도착하면 화면을 몇 번 다시 그리나요?
//       A. 한 번입니다. AvatarCache.Changed가 올 때마다 150ms 타이머를 다시 시작(Stop → Start)하고, 사진이 더 안 오면 타이머가 한 번 Tick해서 목록을 다시 그립니다(디바운스). 사진 50장이 와도 다시 그리기는 한 번입니다.
//   Q7. 창을 닫은 뒤에도 남는 자원은 어떻게 정리하나요?
//       A. 타이머·ContextMenuStrip·메뉴 그림·아이콘은 컨트롤 트리에 없어 창과 함께 해제되지 않습니다. Dispose(bool)에서 직접 정리하고, NotifyIcon은 Visible=false 후 Dispose하지 않으면 종료 뒤에도 트레이에 유령 아이콘이 남습니다. 로그아웃 → 재로그인을 반복하면 핸들이 쌓이는 문제도 이렇게 막았습니다.
//   Q8. 한계는요? (솔직하게 답하기)
//       A. (1) 이 클래스가 대화·전송·재접속·알림·트레이를 모두 알아 책임이 많다(partial로 읽기만 쉬워짐). (2) 필드가 많아 상태 조합이 복잡하다(_connected, _reconnecting, _exiting, _autoAway ...). (3) 화면 자동 테스트가 컨트롤 이름(Name)에 의존한다. (4) 대화 내용이 메모리에만 있어 프로그램을 끄면 사라지고, 재접속 때 서버에서 다시 받는다.
//
// ■ 문제가 났을 때 어디를 볼까
//   - 로그인 직후 목록이 비어 있다 ........... OnLoad의 구독 → ReleaseHeld 순서 (ChatClient.Dispatch의 _holding)
//   - 알림은 왔는데 대화창이 안 열린다 ........ ShowToast 클릭 처리(.Status.cs), OpenConversation(.Network.cs)
//   - 닫아도 프로세스가 남는다 ................ _exiting 경로(트레이 종료), OnMainClosing에서 Disconnect 호출, 백그라운드 스레드
//   - 창이 흐리거나 크기가 어긋난다 ............ S()/LogicalToDeviceUnits를 쓰지 않은 고정 픽셀 값
//   - 재접속을 두 번 시도한다 ................. _reconnecting 플래그, _reconnectAttempt(.Network.cs)
// ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// 메인 창 (세로로 긴 친구 목록 창).
/// ┌ 그라데이션 머리: 내 아바타 · 이름 직급 · 부서 · 사원번호 · ● 상태(온라인 등) ┐
/// │ [ 동료 N ] [ 채팅방 N ]                                   │
/// │ 부서별 동료 목록 / 채팅방 목록 (더블클릭 → 대화창)         │
/// └ 상태 줄: 연결 상태, 공지 ───────────────────────────────┘
///
/// 서버에서 오는 모든 패킷은 여기서 받아(수신 스레드 → UI 스레드) 대화(Conversation)에 나눠 담고,
/// 열려 있는 대화창(ChatWindow)은 자기 대화만 보여 준다.
///
/// 파일 전송은 FileTransferService가 작업 스레드·수신 스레드에서 처리하고,
/// 여기서는 진행률·완료 이벤트를 UI 스레드로 받아 해당 말풍선(ChatFile)을 갱신한다.
/// </summary>
public sealed partial class MainForm : Form
{
    private readonly ChatClient _client;
    private readonly ClientSettings _settings;
    private UserInfo _me; // 다시 로그인하면 서버가 준 최신 정보(이름·직급·권한)로 바뀐다
    private readonly FileTransferService _transfers;

    // 화면
    private readonly GradientPanel _header = new();
    private readonly Button _usersTab = new();
    private readonly Button _roomsTab = new();
    private readonly CardListView _userList = new();
    private readonly CardListView _roomList = new();
    private readonly Panel _roomPanel = new();
    private readonly Button _createRoomButton = new();
    private readonly Label _statusLabel = new();
    private bool _showingRooms;

    // 상태
    private readonly Dictionary<string, Conversation> _conversations = [];
    private readonly Dictionary<string, ChatWindow> _directWindows = [];   // 사원번호 → 1:1 창
    private readonly Dictionary<int, ChatWindow> _roomWindows = [];      // 방 번호 → 방 대화창 (여러 방 동시 참여)
    private readonly Dictionary<int, RoomInfo> _joinedRooms = [];        // 내가 참여 중인 방
    private readonly Dictionary<int, RoomFilesForm> _roomFilesForms = [];
    private ChatWindow? _lastActiveWindow;
    private List<RoomInfo> _rooms = [];
    private List<UserInfo> _users = [];
    private List<UserInfo> _friends = [];              // 내 친구 (접속하지 않은 사람 포함, 서버 FriendList)
    private readonly Panel _userPanel = new();          // 동료 목록 + [친구 추가]
    private readonly Button _addFriendButton = new();
    private AddFriendForm? _addFriendForm;
    private ProfileCardForm? _profileCard;
    private readonly AvatarCache _avatars;               // 프로필 사진 (메모리 + 디스크 캐시)
    private readonly System.Windows.Forms.Timer _photoRefresh = new() { Interval = 150 }; // 사진이 한꺼번에 와도 한 번만 다시 그리기
    private bool _openRoomOnJoin;   // 내가 방을 고르거나 만든 경우에만 입장 후 창을 띄운다 (로그인 때 기본 방은 안 띄움)
    private bool _connected = true;

    // ── 상태·트레이·알림 ──
    private readonly ContextMenuStrip _statusMenu = new();
    private readonly NotifyIcon _tray = new();
    // [개념] WinForms Timer: UI 스레드에서 30초마다 Tick. 자리 비움 확인처럼 가벼운 주기 작업에 쓴다.
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 30_000 };
    private static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(10); // 이만큼 입력이 없으면 자동 자리 비움
    private Presence _presence = Presence.Online;
    private string _statusMessage = "";
    private bool _autoAway;       // 자동으로 자리 비움이 됐는지 (돌아오면 자동으로 온라인. 직접 고른 자리 비움은 그대로 둠)
    private bool _exiting;        // 트레이 메뉴 "종료"로 진짜 끄는 중 (X 버튼은 트레이로 숨기기)
    private bool _trayHintShown;
    private ToastForm? _toast;
    private AdminForm? _adminForm; // 관리자 창 (한 번에 하나만)

    // ── 자동 재접속 ──
    private bool _reconnecting;                     // 재접속 루프가 도는 중 (두 개가 동시에 돌지 않게)
    // [왜] 시도 횟수를 필드로 둔다: 연결은 됐는데 로그인 전에 또 끊기면 새 루프가 시작되는데,
    //      횟수가 1로 돌아가면 2초 간격으로 계속 두드리게 된다. 다시 로그인에 성공했을 때만 0으로 되돌린다.
    private int _reconnectAttempt;
    private readonly HashSet<int> _rejoinRooms = []; // 재접속 후 서버가 아직 다시 연결해 주지 않은 방
    // 다시 로그인하면 서버가 내 방들을 자동으로 다시 연결해 준다. 3초 뒤에도 소식이 없는 방만 직접 입장을 요청한다.
    private readonly System.Windows.Forms.Timer _rejoinTimer = new() { Interval = 3000 };

    // ── 그 밖의 창·정보 ──
    private readonly Dictionary<string, UserInfo> _knownPeers = []; // 사원번호 → 사람 (접속하지 않은 1:1 상대 표시용)
    private SearchForm? _searchForm;
    private ImageViewerForm? _imageViewer;
    private Conversation? _imageViewerConversation;
    private List<ChatItem> _imageViewerItems = [];
    private string? _updateUrl;        // 새 버전 설치 파일 위치 (서버가 알려 줌)
    private bool _updateNotified;
    private bool _suppressUnreadToast;
    private ContextMenuStrip? _trayMenu;

    /// <summary>사용자가 "로그아웃"을 골랐는지. Program이 보고 로그인 창으로 돌아간다.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool LogoutRequested { get; private set; }

    // 파일 ID → (말풍선, 대화). 진행률 갱신과 내가 보낸 파일의 확인(FileMsg)에 쓴다
    private readonly Dictionary<string, (ChatItem Item, Conversation Conv)> _fileItems = [];

    // [읽는 법] 화면 배율(DPI) 보정: 코드에 쓴 100은 "100%일 때의 픽셀"이고, 배율이 150%인 모니터에서는 150으로 바꿔 준다. 고정 픽셀을 그대로 쓰면 고해상도 화면에서 창이 작고 흐려진다.
    private int S(int v) => LogicalToDeviceUnits(v);

    public MainForm(ChatClient client, ClientSettings settings)
    {
        _client = client;
        _settings = settings;
        _me = client.Me ?? throw new InvalidOperationException("로그인 정보가 없습니다.");
        // [읽는 법] 전송 엔진에 "보내는 함수 두 개"(일반 Send, 대량 SendBulk)만 넘긴다. 엔진은 ChatClient나 화면을 모른 채 패킷을 만들어 이 함수로 보내기만 하므로 단독으로 테스트하기 쉽다(의존성 주입의 가장 단순한 형태).
        _transfers = new FileTransferService(client.Send, client.SendBulk);
        WireTransfers();

        Text = $"{Theme.AppName} - {_me.DisplayName}";
        Font = Theme.Body;
        BackColor = Theme.Surface;
        StartPosition = FormStartPosition.Manual;
        Size = LogicalToDeviceUnits(new Size(340, 660));
        MinimumSize = LogicalToDeviceUnits(new Size(300, 460));

        BuildLayout();

        _userList.ItemActivated += item => { if (item.Tag is UserInfo user) OpenDirect(user, activate: true); };
        _userList.ItemContextRequested += (item, location) => { if (item.Tag is UserInfo user) ShowUserMenu(user, location); };
        _addFriendButton.Click += (_, _) => OpenAddFriend();
        _avatars = new AvatarCache(_client, ClientSettings.DataFolder);
        Theme.PhotoLookup = _avatars.Get;
        // [읽는 법] 디바운스: 사진이 하나 올 때마다 150ms 타이머를 "다시 시작"한다. 연달아 오는 동안은 타이머가 계속 미뤄지다가, 잠잠해지면 한 번만 Tick해서 목록·머리·열린 창을 다시 그린다.
        _avatars.Changed += () => { _photoRefresh.Stop(); _photoRefresh.Start(); };
        _photoRefresh.Tick += (_, _) =>
        {
            _photoRefresh.Stop();
            RefreshUserList();
            _header.Invalidate();
            foreach (ChatWindow window in OpenWindows()) window.Invalidate(true);
        };
        FormClosed += (_, _) => _photoRefresh.Dispose();
        _roomList.ItemActivated += item => { if (item.Tag is RoomInfo room) EnterRoom(room); };
        _roomList.ItemContextRequested += (item, location) => { if (item.Tag is RoomInfo room) ShowRoomListMenu(room, location); };
        _usersTab.Name = "UsersTab"; // 화면 자동 테스트(UI Automation)가 찾는 이름
        _roomsTab.Name = "RoomsTab";
        _usersTab.Click += (_, _) => ShowTab(rooms: false);
        _roomsTab.Click += (_, _) => ShowTab(rooms: true);
        _createRoomButton.Click += (_, _) => CreateRoom();

        // 상태 표시: 머리 부분(내 이름·상태)을 누르면 상태 메뉴
        _statusMessage = _me.StatusMessage;
        BuildStatusMenu();
        _header.Cursor = Cursors.Hand;
        _header.MouseClick += (_, e) =>
        {
            if (HeaderAvatarRect().Contains(e.Location)) ShowProfile(_me); // 내 사진을 누르면 내 프로필 카드
            else if (_connected) _statusMenu.Show(_header, e.Location);
        };
        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();

        // 트레이 아이콘 (X로 닫으면 트레이로 숨고 계속 실행)
        Icon = CreateAppIcon();
        BuildTray();

        _rejoinTimer.Tick += (_, _) => RejoinMissingRooms();

        Load += OnLoad;
        FormClosing += OnMainClosing;
    }

    // ───────────────────────── 화면 구성 ─────────────────────────

    private void BuildLayout()
    {
        _header.Dock = DockStyle.Top;
        _header.Height = S(100);
        _header.Paint += PaintHeader;

        // 탭: [동료] [채팅방]
        foreach (Button tab in new[] { _usersTab, _roomsTab })
        {
            tab.FlatStyle = FlatStyle.Flat;
            tab.FlatAppearance.BorderSize = 0;
            tab.Dock = DockStyle.Fill;
            tab.Margin = new Padding(0);
            tab.Cursor = Cursors.Hand;
            tab.Font = Theme.BodyBold;
            tab.Paint += PaintTabUnderline;
        }
        var tabBar = new TableLayoutPanel { Dock = DockStyle.Top, Height = S(42), ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface };
        tabBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        tabBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        tabBar.Controls.Add(_usersTab, 0, 0);
        tabBar.Controls.Add(_roomsTab, 1, 0);

        // 동료 목록
        _userList.Dock = DockStyle.Fill;
        _userList.EmptyText = "친구를 추가하거나 동료가 접속하면 여기에 보입니다.";
        _addFriendButton.Text = "+  친구 추가";
        _addFriendButton.Name = "AddFriendButton"; // 화면 자동 테스트(UI Automation)가 찾는 이름
        _addFriendButton.Dock = DockStyle.Bottom;
        _addFriendButton.Height = S(42);
        _addFriendButton.FlatStyle = FlatStyle.Flat;
        _addFriendButton.FlatAppearance.BorderSize = 0;
        _addFriendButton.BackColor = Theme.Primary;
        _addFriendButton.ForeColor = Color.White;
        _addFriendButton.Font = Theme.BodyBold;
        _addFriendButton.Cursor = Cursors.Hand;
        _userPanel.Dock = DockStyle.Fill;
        _userPanel.Controls.Add(_userList);
        _userPanel.Controls.Add(_addFriendButton);
        _userList.BringToFront();

        // 채팅방 목록 + [새 채팅방]
        _roomList.Dock = DockStyle.Fill;
        _roomList.EmptyText = "채팅방이 없습니다.";
        _createRoomButton.Text = "+  새 채팅방 만들기";
        _createRoomButton.Name = "CreateRoomButton"; // 화면 자동 테스트(UI Automation)가 찾는 이름
        _createRoomButton.Dock = DockStyle.Bottom;
        _createRoomButton.Height = S(42);
        _createRoomButton.FlatStyle = FlatStyle.Flat;
        _createRoomButton.FlatAppearance.BorderSize = 0;
        _createRoomButton.BackColor = Theme.Primary;
        _createRoomButton.ForeColor = Color.White;
        _createRoomButton.Font = Theme.BodyBold;
        _createRoomButton.Cursor = Cursors.Hand;
        _roomPanel.Dock = DockStyle.Fill;
        _roomPanel.Controls.Add(_roomList);
        _roomPanel.Controls.Add(_createRoomButton);
        _roomList.BringToFront();
        _roomPanel.Visible = false;

        // 상태 줄
        _statusLabel.Dock = DockStyle.Bottom;
        _statusLabel.Height = S(28);
        _statusLabel.Padding = new Padding(S(10), 0, S(10), 0);
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Font = Theme.Small;
        _statusLabel.BackColor = Theme.Background;
        _statusLabel.AutoEllipsis = true;

        Controls.Add(_userPanel);
        Controls.Add(_roomPanel);
        Controls.Add(tabBar);
        Controls.Add(_header);
        Controls.Add(_statusLabel);
        _userPanel.BringToFront();
        _roomPanel.BringToFront();
    }

    /// <summary>머리 영역의 내 사진 위치 (그리기와 클릭에 같이 쓴다).</summary>
    private Rectangle HeaderAvatarRect()
    {
        int size = S(56);
        return new Rectangle(S(16), (_header.Height - size) / 2, size, size);
    }

    private void PaintHeader(object? sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        int size = S(56);
        var avatar = new Rectangle(S(16), (_header.Height - size) / 2, size, size);
        using (var ring = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
            g.FillEllipse(ring, Rectangle.Inflate(avatar, S(3), S(3)));
        Theme.DrawAvatar(g, avatar, _me.Name, Theme.AvatarColor(_me.EmployeeNo), _me.EmployeeNo);

        int x = avatar.Right + S(14);
        int w = _header.Width - x - S(10);
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, _me.DisplayName, Theme.Title, new Rectangle(x, avatar.Y - S(2), w, S(26)), Color.White, flags);
        TextRenderer.DrawText(g, $"{_me.Department} · {_me.EmployeeNo}", Theme.Small, new Rectangle(x, avatar.Y + S(23), w, S(18)), Theme.HeaderSubText, flags);

        int dot = S(10);
        var dotRect = new Rectangle(x, avatar.Y + S(43), dot, dot);
        Theme.DrawStatusDot(g, dotRect, _connected ? Theme.PresenceColor(_presence) : Theme.Offline);
        string status = !_connected ? "연결 끊김"
            : _statusMessage.Length > 0 ? $"{Theme.PresenceText(_presence)} · {_statusMessage} ▾" : $"{Theme.PresenceText(_presence)} ▾";
        TextRenderer.DrawText(g, status, Theme.SmallBold,
            new Rectangle(dotRect.Right + S(5), dotRect.Y - S(4), w - dot - S(5), S(18)), Color.White, flags);
    }

    private void PaintTabUnderline(object? sender, PaintEventArgs e)
    {
        if (sender is not Button tab) return;
        bool active = (tab == _roomsTab) == _showingRooms;
        using var pen = new Pen(active ? Theme.Primary : Theme.Border, active ? S(3) : 1);
        int y = tab.Height - (active ? S(2) : 1);
        e.Graphics.DrawLine(pen, 0, y, tab.Width, y);
    }

    private void ShowTab(bool rooms)
    {
        _showingRooms = rooms;
        _roomPanel.Visible = rooms;
        _userPanel.Visible = !rooms;
        _usersTab.ForeColor = rooms ? Theme.SubText : Theme.Primary;
        _roomsTab.ForeColor = rooms ? Theme.Primary : Theme.SubText;
        _usersTab.Invalidate();
        _roomsTab.Invalidate();
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        // 메신저처럼 화면 오른쪽에 세로로 띄운다
        Rectangle area = Screen.FromControl(this).WorkingArea;
        Location = new Point(area.Right - Width - S(40), area.Top + Math.Max(0, (area.Height - Height) / 2));

        ShowTab(rooms: false);
        SetStatus(ConnectedStatusText(), Theme.SubText);
        RefreshUserList();
        RefreshRoomList();

        _client.PacketReceived += OnPacketReceived;
        _client.Disconnected += OnDisconnected;
        _client.ReleaseHeld(); // 로그인 성공 직후부터 ChatClient가 보관해 둔 패킷(방 입장 결과·접속자·방 목록 등)을 이제 전달받는다
        _client.Send(Packet.Create(PacketType.ServerInfoReq)); // 파일 최대 크기·최신 버전 확인
        _client.Send(Packet.Create(PacketType.FriendListReq)); // 내 친구 목록
        _avatars.Note(_me); // 내 프로필 사진

        // [주의] 로그인 창이 닫히고 이 창이 이벤트를 구독하기 "전에" 연결이 끊겼다면 Disconnected 알림을 놓쳤다.
        //        그대로 두면 "연결됨"으로 보이는데 아무것도 오지 않는다 → 여기서 한 번 확인해 재접속을 시작한다.
        if (!_client.IsConnected) ShowDisconnected("서버와 연결이 끊어졌습니다.");
    }

    private void OnMainClosing(object? sender, FormClosingEventArgs e)
    {
        // X 버튼 = 트레이로 숨기기 (카카오톡처럼). 트레이 메뉴 "종료"나 윈도우 종료일 때만 진짜 끈다.
        // [개념] e.Cancel = true: 닫기를 취소한다. CloseReason으로 사용자가 닫은 것인지, 윈도우 종료인지 구분한다.
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(3000, Theme.AppName, "트레이에서 계속 실행됩니다. 완전히 끄려면 트레이 아이콘을 오른쪽 클릭 → 종료", ToolTipIcon.Info);
            }
            return;
        }

        _idleTimer.Stop();
        _rejoinTimer.Stop();
        _toast?.Close();
        _searchForm?.Close();
        _adminForm?.Close();
        // 보내던·받던 파일은 실패로 정리한다(받던 .part 임시 파일 삭제, 작업 스레드 깨우기)
        _transfers.CancelAll(LogoutRequested ? "로그아웃했습니다." : "프로그램을 종료했습니다.");
        // [주의] NotifyIcon은 Dispose하지 않으면 프로그램이 끝난 뒤에도 트레이에 아이콘이 남아 보인다(마우스를 올리면 사라지는 유령 아이콘).
        _tray.Visible = false;
        _tray.Dispose();

        _client.PacketReceived -= OnPacketReceived;
        _client.Disconnected -= OnDisconnected;

        foreach (ChatWindow window in OpenWindows().ToList()) window.CloseByProgram();

        _client.Disconnect(); // 로그아웃 패킷 전송 후 연결 종료
    }

    // ───────────────────────── 공통 ─────────────────────────

    private Conversation GetConversation(string key, string title)
    {
        if (!_conversations.TryGetValue(key, out Conversation? conv))
        {
            conv = new Conversation(key, title);
            _conversations[key] = conv;
        }
        return conv;
    }

    private void WireWindow(ChatWindow window)
    {
        window.SetViewOptions(_me.EmployeeNo, CanDelete, CanEdit);
        window.TypingActivity += SendTypingActivity;
        window.OlderHistoryRequested += RequestOlderHistory;
        window.FileCommandRequested += OnFileCommand;
        window.RoomFilesRequested += OpenRoomFiles;
        window.ProfileRequested += ShowProfileByNo;
        // [읽는 법] 대화창은 "공감을 누르고 싶다"는 요청만 올리고, 서버에 보내는 일은 메인 창이 한다(창은 통신을 모른다). 화면의 공감 숫자는 서버가 방송하는 ReactionUpdate를 받아야 바뀐다(HandleReactionUpdate, .Messages.cs).
        window.ReactionRequested += (_, item, emoji) =>
            _client.Send(Packet.Create(PacketType.Reaction).WithData(new ReactionRequest { MessageId = item.MessageId, Emoji = emoji }));
        window.DeleteRequested += (w, item) =>
        {
            string who = item.Kind == ChatItemKind.Mine ? "" : $"{item.Sender}님의 ";
            if (MessageBox.Show(w, $"{who}메시지를 삭제할까요?\n대화에 참여한 모든 사람의 화면에서 \"삭제된 메시지입니다.\"로 바뀝니다.", "메시지 삭제",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                _client.Send(Packet.Create(PacketType.MessageDelete).WithData(new MessageRef { MessageId = item.MessageId }));
        };
        window.EditRequested += EditMessage;
        window.ReadAll += w =>
        {
            _lastActiveWindow = w;
            MarkRead(w.Conversation);
            if (w.Conversation.Unread == 0) return;
            w.Conversation.Unread = 0;
            RefreshUserList();
            RefreshRoomList();
        };
    }

    private void SendTypingActivity(ChatWindow window, bool isTyping)
    {
        if (!_connected) return;
        var request = new TypingRequest { IsTyping = isTyping };
        if (window.Kind == ChatWindowKind.Room && TryGetRoomId(window.Conversation, out int roomId))
            request.RoomId = roomId;
        else if (window.Kind == ChatWindowKind.Direct && window.Conversation.Key.StartsWith("dm:"))
            request.PeerNo = window.Conversation.Key["dm:".Length..];
        else return;
        _client.Send(Packet.Create(PacketType.Typing).WithData(request));
    }

    private void RequestOlderHistory(ChatWindow window)
    {
        Conversation conv = window.Conversation;
        if (!_connected || conv.HistoryLoading || !conv.HistoryHasMore) return;
        ChatItem? oldest = conv.Items.FirstOrDefault(item => !item.IsSystemLike && (item.MessageId > 0 || item.File is { State: FileState.Ready }));
        if (oldest is null) { conv.HistoryHasMore = false; return; }
        // [읽는 법] 위로 스크롤해 더 오래된 대화를 받을 때의 "기준점"을 만든다: (가장 오래된 말풍선의 시각 + 키). 키는 파일이면 "F:파일번호", 메시지면 "M:" + 번호를 20자리로 0 채움(ToString("D20"), 예: 123 → 00000000000000000123) — 문자열로 비교해도 번호 크기 순서가 맞도록. 서버는 이 기준점보다 앞선 것을 한 페이지(50개) 돌려준다(커서 기반 페이지).
        string beforeKey = oldest.File is ChatFile oldFile
            ? "F:" + oldFile.Info.FileId
            : "M:" + oldest.MessageId.ToString("D20", System.Globalization.CultureInfo.InvariantCulture);
        var request = new HistoryRequest { Before = oldest.Time, BeforeKey = beforeKey };
        if (window.Kind == ChatWindowKind.Room && int.TryParse(conv.Key["room:".Length..], out int roomId))
            request.RoomId = roomId;
        else if (window.Kind == ChatWindowKind.Direct)
            request.PeerNo = conv.Key["dm:".Length..];
        else return;

        conv.HistoryLoading = true;
        conv.LoadingOlderHistory = true;
        conv.PendingHistory.Clear();
        if (!_client.Send(Packet.Create(PacketType.HistoryReq).WithData(request)))
        {
            conv.HistoryLoading = false;
            conv.LoadingOlderHistory = false;
        }
    }

    /// <summary>삭제 메뉴를 보여 줄지: 내가 보낸 지 24시간 안의 메시지, 관리자는 모든 메시지 (최종 판단은 서버).</summary>
    // [주의] 이 검사는 "메뉴를 보일지" 정할 뿐이다. 서버가 같은 규칙을 다시 검사한다(MessageService.DeleteWindow/EditWindow). item.Time은 UTC라서 DateTime.UtcNow와 비교한다.
    private bool CanDelete(ChatItem item)
        => item.CanInteract && (_me.IsAdmin || (item.Kind == ChatItemKind.Mine && DateTime.UtcNow - item.Time < TimeSpan.FromHours(24)));

    private bool CanEdit(ChatItem item)
        => item.CanInteract && item.Kind == ChatItemKind.Mine && DateTime.UtcNow - item.Time <= TimeSpan.FromMinutes(5);

    private void EditMessage(ChatWindow window, ChatItem item)
    {
        if (!CanEdit(item)) return;
        using var form = new MessageEditForm(item.Text);
        if (form.ShowDialog(window) != DialogResult.OK || form.EditedText.Length == 0 || form.EditedText == item.Text) return;
        _client.Send(Packet.Create(PacketType.MessageEditReq)
            .WithData(new EditMessageRequest { MessageId = item.MessageId, Text = form.EditedText }));
    }

    /// <summary>글 보내기 (방: ChatMsg + 방 번호, 1:1: Whisper + 상대 사원번호). 답장이면 원래 메시지 번호를 붙인다.</summary>
    private void SendText(PacketType type, string text, string target, long? replyId)
    {
        Packet packet = Packet.Create(type, body: text, target: target);
        // 서버는 번호만 보고 원래 메시지를 직접 찾아 요약을 붙인다(클라이언트가 보낸 요약은 믿지 않는다)
        if (replyId is long id && id > 0) packet.ReplyTo = new ReplyInfo { MessageId = id };
        _client.Send(packet);
    }

    private IEnumerable<ChatWindow> OpenWindows()
    {
        foreach (ChatWindow w in _directWindows.Values) yield return w;
        foreach (ChatWindow w in _roomWindows.Values) yield return w;
    }

    private static void BringToFront(Form form)
    {
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Show();
        form.Activate();
    }

    /// <summary>"암호화 연결됨 · 서버:포트" — 사용자가 지금 연결이 TLS로 보호되는지 한눈에 알 수 있게 한다.</summary>
    // [왜] 이모지(자물쇠) 대신 글자: WinForms Label(GDI)은 컬러 이모지를 그리지 못해 PC에 따라 네모(□)로 보일 수 있다.
    private string ConnectedStatusText() =>
        $"{(_client.IsEncrypted ? "암호화 연결됨" : "연결됨(암호화 안 됨)")} · {_client.Host}:{_client.Port}";

    // [주의] 메뉴·타이머·메뉴 그림은 컨트롤 트리에 없어 창과 함께 해제되지 않는다.
    //        로그아웃 → 다시 로그인을 반복하면 그만큼 윈도우 핸들이 쌓이므로 직접 정리한다.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _idleTimer.Dispose();
            _rejoinTimer.Dispose();
            foreach (ContextMenuStrip? menu in new[] { _statusMenu, _trayMenu })
            {
                if (menu is null) continue;
                foreach (ToolStripItem item in menu.Items)
                {
                    if (item is ToolStripMenuItem m)
                        foreach (ToolStripItem sub in m.DropDownItems) sub.Image?.Dispose();
                    item.Image?.Dispose();
                }
                menu.Dispose();
            }
            Icon?.Dispose();
        }
        base.Dispose(disposing);                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }
}
