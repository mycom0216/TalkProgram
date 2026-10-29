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

/// <summary>
/// 메인 창 (세로로 긴 친구 목록 창).
/// ┌ 그라데이션 머리: 내 아바타 · 이름 직급 · 부서 · ● 온라인 ┐
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
public sealed class MainForm : Form
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

    private int S(int v) => LogicalToDeviceUnits(v);

    public MainForm(ChatClient client, ClientSettings settings)
    {
        _client = client;
        _settings = settings;
        _me = client.Me ?? throw new InvalidOperationException("로그인 정보가 없습니다.");
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
        _roomList.ItemActivated += item => { if (item.Tag is RoomInfo room) EnterRoom(room); };
        _roomList.ItemContextRequested += (item, location) => { if (item.Tag is RoomInfo room) ShowRoomListMenu(room, location); };
        _usersTab.Click += (_, _) => ShowTab(rooms: false);
        _roomsTab.Click += (_, _) => ShowTab(rooms: true);
        _createRoomButton.Click += (_, _) => CreateRoom();

        // 상태 표시: 머리 부분(내 이름·상태)을 누르면 상태 메뉴
        _statusMessage = _me.StatusMessage;
        BuildStatusMenu();
        _header.Cursor = Cursors.Hand;
        _header.MouseClick += (_, e) => { if (_connected) _statusMenu.Show(_header, e.Location); };
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
        _userList.EmptyText = "아직 접속한 동료가 없습니다.";

        // 채팅방 목록 + [새 채팅방]
        _roomList.Dock = DockStyle.Fill;
        _roomList.EmptyText = "채팅방이 없습니다.";
        _createRoomButton.Text = "+  새 채팅방 만들기";
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

        Controls.Add(_userList);
        Controls.Add(_roomPanel);
        Controls.Add(tabBar);
        Controls.Add(_header);
        Controls.Add(_statusLabel);
        _userList.BringToFront();
        _roomPanel.BringToFront();
    }

    private void PaintHeader(object? sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        int size = S(56);
        var avatar = new Rectangle(S(16), (_header.Height - size) / 2, size, size);
        using (var ring = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
            g.FillEllipse(ring, Rectangle.Inflate(avatar, S(3), S(3)));
        Theme.DrawAvatar(g, avatar, _me.Name, Theme.AvatarColor(_me.EmployeeNo));

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
        _userList.Visible = !rooms;
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
        _client.ReleaseHeld(); // 로그인 직후 받아 둔 방 입장 결과·접속자·방 목록을 이제 전달받는다
        _client.Send(Packet.Create(PacketType.ServerInfoReq)); // 파일 최대 크기·최신 버전 확인

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

    // ───────────────────────── 수신 (수신 스레드 → UI 스레드) ─────────────────────────

    private void OnPacketReceived(Packet packet)
    {
        // 파일 조각 등은 수신 스레드에서 바로 파일에 쓴다(수천 개의 조각을 UI 스레드로 넘기지 않는다)
        if (_transfers.TryHandle(packet)) return;
        UiHelper.RunOnUi(this, () => HandlePacket(packet));
    }

    private void OnDisconnected(string reason)
    {
        _transfers.CancelAll(reason); // 진행 중인 파일 전송 실패 처리 (이벤트는 UI 스레드로 넘어간다)
        UiHelper.RunOnUi(this, () => ShowDisconnected(reason));
    }

    /// <summary>
    /// 연결이 끊겼을 때 (UI 스레드). 다른 곳에서 로그인해서 밀려난 경우가 아니면 자동 재접속을 시작한다.
    /// [왜] 예전에는 팝업을 띄우고 "프로그램을 다시 실행하세요"로 끝났다. 와이파이가 잠깐 끊기거나 서버를 재시작할 때마다
    ///      사용자가 직접 다시 켜야 했으므로, 메신저답게 알아서 다시 붙도록 바꿨다.
    /// </summary>
    private void ShowDisconnected(string reason)
    {
        _connected = false;
        _header.Invalidate();
        SetStatus($"● {reason}", Theme.Danger);
        // 받다 만 지난 대화 묶음은 버린다 (다시 로그인하면 서버가 처음부터 다시 보낸다 → 섞이면 같은 말풍선이 두 번 보임)
        foreach (Conversation c in _conversations.Values) c.PendingHistory.Clear();
        _createRoomButton.Enabled = false;

        // 밀려났거나(KickedOut) 로그인 정보가 없으면 재접속하지 않는다.
        // [주의] 밀려났는데도 재접속하면, 두 PC가 서로를 계속 밀어내는 "핑퐁"이 생긴다.
        bool canReconnect = !_client.KickedOut && _client.LastLogin is not null;
        string tail = canReconnect ? "자동으로 다시 연결합니다." : "프로그램을 다시 실행해 접속하세요.";
        foreach (ChatWindow window in OpenWindows())
        {
            // 말풍선마다 화면 배치 정보를 갖고 있으므로 창마다 새로 만든다(같은 객체를 공유하면 위치가 꼬인다)
            window.Conversation.Add(new ChatItem { Kind = ChatItemKind.Error, Text = $"{reason} {tail}" });
            window.SetInputEnabled(false);
        }

        if (!canReconnect)
        {
            MessageBox.Show(this, $"{reason}\n프로그램을 다시 실행해 접속하세요.", Theme.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _ = ReconnectLoopAsync(); // [개념] _ = : 기다리지 않고 시작만 한다(결과를 버린다는 표시). 루프는 UI 스레드에서 await로 돈다.
    }

    /// <summary>
    /// 자동 재접속 루프. 2 → 4 → 8 → 16 → 30초(이후 계속 30초) 간격으로 다시 연결을 시도하고,
    /// 연결되면 저장해 둔 로그인 정보로 다시 로그인한다. 결과는 LoginRes(HandleRelogin)에서 이어서 처리한다.
    ///
    /// [개념] async/await: await Task.Delay(1000)은 1초 동안 "UI 스레드를 놓아 주고" 기다린다.
    ///   Thread.Sleep처럼 창을 멈추지 않으므로 기다리는 동안에도 창을 옮기거나 닫을 수 있다.
    ///   await가 끝나면 다시 UI 스레드에서 이어서 실행되므로(WinForms의 동기화 컨텍스트) 화면을 바로 바꿔도 된다.
    /// [왜] 간격을 점점 늘린다(지수 백오프): 서버가 꺼져 있을 때 수백 명이 1초마다 접속을 시도하면,
    ///   서버가 다시 켜지는 순간 접속이 한꺼번에 몰려 또 쓰러질 수 있다.
    /// </summary>
    private async Task ReconnectLoopAsync()
    {
        if (_reconnecting) return;
        _reconnecting = true;
        try
        {
            while (!IsDisposed && !_connected)
            {
                int attempt = ++_reconnectAttempt;
                int delay = Math.Min(30, 1 << Math.Min(attempt, 5)); // 2, 4, 8, 16, 30, 30, ...
                for (int left = delay; left > 0; left--)
                {
                    SetStatus($"● 연결 끊김 · {left}초 후 다시 연결 ({attempt}번째 시도)", Theme.Danger);
                    await Task.Delay(1000);
                    if (IsDisposed) return; // 기다리는 동안 창을 닫음
                }

                SetStatus("● 다시 연결하는 중...", Theme.Notice);
                try
                {
                    await _client.ConnectAsync(_client.Host, _client.Port);
                }
                catch (UntrustedCertificateException ex)
                {
                    // [주의] 재접속했더니 서버 인증서가 바뀌었다 = 서버 인증서를 새로 만들었거나, 누군가 가짜 서버로 가로채는 중.
                    //        조용히 다시 시도하거나 자동으로 믿으면 안 된다. 멈추고 사용자에게 알린다.
                    SetStatus("● 서버 인증서가 바뀌어 연결을 멈췄습니다", Theme.Danger);
                    MessageBox.Show(this,
                        "서버의 보안 인증서가 이전과 다릅니다.\n누군가 중간에서 연결을 가로채고 있을 수 있어 연결하지 않았습니다.\n\n" +
                        $"새 지문(SHA-256):\n{TlsHelper.Format(ex.Fingerprint)}\n\n" +
                        "관리자에게 서버 인증서를 바꿨는지 확인한 뒤, 프로그램을 다시 실행해 로그인하세요.",
                        Theme.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                catch (Exception ex) when (ex is SocketException or TimeoutException or IOException or AuthenticationException)
                {
                    continue; // 서버가 아직 꺼져 있음(또는 아직 준비 중) → 다음 시도
                }

                if (IsDisposed) { _client.Disconnect(); return; } // 연결하는 사이 창을 닫음

                // 연결됨 → 다시 로그인. 이후는 LoginRes가 오면 HandleRelogin에서 처리한다.
                // (그 전에 또 끊기면 Disconnected → ShowDisconnected → 새 루프가 다시 시작된다)
                if (_client.LastTokenLogin is { } tokenLogin)
                    _client.Send(Packet.Create(PacketType.TokenLoginReq).WithData(tokenLogin));
                else if (_client.LastLogin is { } lastLogin)
                    _client.Send(Packet.Create(PacketType.LoginReq).WithData(lastLogin));
                else
                {
                    SetStatus("● 다시 로그인해야 합니다", Theme.Danger);
                    _client.Disconnect();
                }
                return;
            }
        }
        finally
        {
            _reconnecting = false;
        }
    }

    /// <summary>
    /// 재접속 후 다시 로그인한 결과 (UI 스레드).
    /// 성공하면: 입력을 다시 열고, 참여하던 방에 다시 입장을 요청하고(서버가 놓친 대화를 지난 대화로 보내 줌), 보관된 패킷을 받는다.
    /// </summary>
    private void HandleRelogin(Packet packet)
    {
        if (!packet.Success)
        {
            // 비밀번호가 바뀌었거나 계정이 정지된 경우 → 더 시도해도 소용없다
            SetStatus($"● 다시 로그인하지 못했습니다: {packet.Body}", Theme.Danger);
            _client.Disconnect();
            MessageBox.Show(this, $"다시 로그인하지 못했습니다.\n{packet.Body}\n프로그램을 다시 실행해 로그인하세요.", Theme.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _connected = true;
        _reconnectAttempt = 0;
        // 끊긴 사이 관리자가 이름·직급·권한을 바꿨을 수 있다 → 서버가 준 최신 정보로
        if (packet.GetData<UserInfo>() is { } latest && latest.EmployeeNo == _me.EmployeeNo)
        {
            _me = latest;
            _client.Me = latest;
            Text = $"{Theme.AppName} - {_me.DisplayName}";
        }
        _header.Invalidate();
        SetStatus(ConnectedStatusText(), Theme.SubText);
        _createRoomButton.Enabled = true;

        foreach (ChatWindow window in OpenWindows())
        {
            window.Conversation.Add(new ChatItem { Kind = ChatItemKind.System, Text = "다시 연결되었습니다." });
            // 방 창은 서버가 그 방을 다시 연결해 준 뒤(RoomRes)에 입력을 연다. 기본 방과 1:1은 바로.
            bool waitRoom = window.Kind == ChatWindowKind.Room && _roomWindows.FirstOrDefault(kv => kv.Value == window).Key is int rid && rid != RoomInfo.LobbyId;
            window.SetInputEnabled(!waitRoom);
        }
        // 끊기기 직전에 보낸 "읽음"이 서버에 닿지 못했을 수 있다 → 다시 보낼 수 있게 초기화하고, 보고 있는 창은 바로 다시 알린다
        foreach (Conversation c in _conversations.Values) c.LastMarkedRead = default;
        _suppressUnreadToast = true; // 재접속 때 다시 오는 "안 읽은 1:1" 요약으로 알림 창을 또 띄우지 않는다

        // 기본 방과 내가 멤버인 방은 서버가 로그인 때 자동으로 다시 연결하고 놓친 대화도 보내 준다(RoomRes + History).
        // 3초 뒤에도 소식이 없는 방(서버가 다시 시작되며 닫혔거나, 내보내진 방)만 직접 입장을 요청해 결과를 확인한다.
        _rejoinRooms.Clear();
        foreach (int roomId in _joinedRooms.Keys.Where(id => id != RoomInfo.LobbyId)) _rejoinRooms.Add(roomId);
        _rejoinTimer.Stop();
        if (_rejoinRooms.Count > 0) _rejoinTimer.Start();

        // 1:1은 서버가 자동으로 보내 주지 않으므로, 열어 본 적 있는 1:1의 지난 대화를 다시 요청한다
        // (끊겨 있던 동안 온 메시지가 채워지고, 같은 메시지는 번호로 걸러진다)
        foreach (Conversation conv in _conversations.Values.Where(c => c.HistoryRequested && c.Key.StartsWith("dm:")))
            _client.Send(Packet.Create(PacketType.HistoryReq, target: conv.Key["dm:".Length..]));

        // 서버는 로그인할 때마다 "온라인"으로 시작하므로, 내가 고른 상태(회의 중 등)를 다시 알린다
        if (_presence != Presence.Online)
            _client.Send(Packet.Create(PacketType.StatusUpdate).WithData(new StatusUpdate { Presence = _presence }));

        // [주의] ChatClient는 LoginRes 성공 직후부터 패킷을 보관한다. 여기서 풀어 주지 않으면 이후 패킷이 영영 오지 않는다.
        _client.ReleaseHeld();
        if (_lastActiveWindow is { IsDisposed: false, ContainsFocus: true } focused) MarkRead(focused.Conversation);
    }

    /// <summary>다시 로그인한 뒤 3초가 지나도 서버가 다시 연결해 주지 않은 방 → 직접 입장을 요청해 본다.</summary>
    private void RejoinMissingRooms()
    {
        _rejoinTimer.Stop();
        if (!_connected) return;
        foreach (int roomId in _rejoinRooms.ToList())
            _client.Send(Packet.Create(PacketType.RoomJoin, target: roomId.ToString()));
        // 결과는 HandleRoomResponse: 성공이면 _rejoinRooms에서 빠지고, 실패면 그 방 대화창에 알린다
    }

    private void HandlePacket(Packet packet)
    {
        switch (packet.Type)
        {
            case PacketType.ChatMsg: HandleChat(packet); break;
            case PacketType.Whisper: HandleWhisper(packet); break;
            case PacketType.LoginRes: HandleRelogin(packet); break; // 자동 재접속 후 다시 로그인한 결과
            case PacketType.FileMsg: HandleFileMessage(packet); break;
            case PacketType.History: HandleHistory(packet); break;
            case PacketType.RoomFilesRes: HandleRoomFilesResponse(packet); break;
            case PacketType.ServerInfo:
                if (packet.GetData<ServerInfo>() is { } info)
                {
                    if (info.MaxFileSize > 0) _transfers.MaxFileSize = info.MaxFileSize;
                    CheckClientVersion(info);
                }
                break;
            case PacketType.RoomUpdate: HandleRoomUpdate(packet); break;
            case PacketType.ReadUpdate: HandleReadUpdate(packet); break;
            case PacketType.ReactionUpdate: HandleReactionUpdate(packet); break;
            case PacketType.MessageDeleted: HandleMessageDeleted(packet); break;
            case PacketType.MessageEdited: HandleMessageEdited(packet); break;
            case PacketType.TypingUpdate: HandleTypingUpdate(packet); break;
            case PacketType.UnreadDirect: HandleUnreadDirect(packet); break;
            case PacketType.SearchRes:
            case PacketType.Directory:
                break; // 검색 창·사람 고르기 창이 직접 받는다
            case PacketType.SystemMsg: HandleSystem(packet); break;
            case PacketType.RoomRes: HandleRoomResponse(packet); break;
            case PacketType.RoomLeft: HandleRoomLeft(packet); break;

            case PacketType.RoomList:
                _rooms = packet.GetData<List<RoomInfo>>() ?? [];
                foreach (RoomInfo room in _rooms.Where(r => _joinedRooms.ContainsKey(r.RoomId)))
                {
                    // [주의] 방 목록에는 멤버 명단이 없다(가볍게 보내려고). 읽음 표시에 쓰는 명단은 이전 것을 이어받는다.
                    room.Members ??= _joinedRooms[room.RoomId].Members;
                    _joinedRooms[room.RoomId] = room; // 인원수 등 최신 정보로
                    UpdateRoomWindowHeader(room);
                }
                RefreshRoomList();
                break;

            case PacketType.UserList:
                _users = packet.GetData<List<UserInfo>>() ?? [];
                foreach (UserInfo u in _users) _knownPeers[u.EmployeeNo] = u;
                foreach (var (no, window) in _directWindows)
                {
                    UserInfo? user = _users.FirstOrDefault(u => u.EmployeeNo == no);
                    window.SetOnline(user is not null && _connected);
                    if (user is not null) window.SetHeader(user.DisplayName, UserSubtitle(user)); // 상대 상태(회의 중 등)도 보이게
                }
                foreach (RoomInfo room in _joinedRooms.Values) UpdateRoomMentionCandidates(room);
                RefreshUserList();
                break;


            case PacketType.AdminNotify:
                // 새 가입 신청 등. 관리자 창이 열려 있으면 그 창이 직접 새로 고친다(AdminForm도 이 패킷을 구독).
                SetStatus($"● {packet.Body}", Theme.Notice);
                if (_me.IsAdmin)
                    ShowToast("관리자 알림", packet.Body ?? "", Theme.AppName, () => OpenAdmin(showPending: true));
                break;

            case PacketType.Error:
                var error = new ChatItem { Kind = ChatItemKind.Error, Text = packet.Body ?? "오류가 발생했습니다." };
                if (_lastActiveWindow is { IsDisposed: false }) _lastActiveWindow.Conversation.Add(error);
                else SetStatus($"● {error.Text}", Theme.Danger);
                break;
        }
    }

    private void HandleChat(Packet packet)
    {
        // 어느 방의 메시지인지는 Target(방 번호)
        if (!int.TryParse(packet.Target, out int roomId) || !_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
        bool mine = packet.SenderId == _me.EmployeeNo;
        Conversation conv = GetRoomConversation(room);
        AddMessage(conv, _roomWindows.GetValueOrDefault(roomId), new ChatItem
        {
            Kind = mine ? ChatItemKind.Mine : ChatItemKind.Other,
            Sender = packet.Sender ?? "",
            SenderKey = packet.SenderId ?? "",
            Text = packet.Body ?? "",
            Time = packet.Timestamp,
            Mentioned = !mine && MentionsMe(packet.Body),
            MessageId = packet.MessageId,
            ReplyTo = packet.ReplyTo,
        });
        RecomputeUnread(conv);
    }

    private void HandleWhisper(Packet packet)
    {
        bool mine = packet.SenderId == _me.EmployeeNo;

        // 상대방: 내가 보낸 귓속말이면 받는 사람(Data), 받은 귓속말이면 보낸 사람
        UserInfo? other = mine
            ? packet.GetData<UserInfo>()
            : _users.FirstOrDefault(u => u.EmployeeNo == packet.SenderId)
              ?? new UserInfo { EmployeeNo = packet.SenderId ?? "", Name = packet.Sender ?? "", Position = "" };
        if (other is null || other.EmployeeNo.Length == 0) return;
        _knownPeers.TryAdd(other.EmployeeNo, other);

        // 받은 메시지인데 창이 없으면 자동으로 띄운다(포커스는 빼앗지 않음)
        ChatWindow window = OpenDirect(other, activate: false);
        AddMessage(window.Conversation, window, new ChatItem
        {
            Kind = mine ? ChatItemKind.Mine : ChatItemKind.Other,
            Sender = packet.Sender ?? "",
            SenderKey = packet.SenderId ?? "",
            Text = packet.Body ?? "",
            Time = packet.Timestamp,
            MessageId = packet.MessageId,
            ReplyTo = packet.ReplyTo,
        });
        RecomputeUnread(window.Conversation);
    }

    private void HandleSystem(Packet packet)
    {
        string text = packet.Body ?? "";
        bool notice = text.StartsWith("[공지]");
        if (notice) SetStatus(text, Theme.Notice);

        if (int.TryParse(packet.Target, out int roomId))
        {
            // 방 안의 입장·퇴장 알림 → 그 방 대화에
            if (_joinedRooms.TryGetValue(roomId, out RoomInfo? room))
                GetRoomConversation(room).Add(new ChatItem { Kind = ChatItemKind.System, Text = text, Time = packet.Timestamp });
        }
        else
        {
            // 서버 공지 → 참여 중인 모든 방 대화에 (말풍선마다 배치 정보가 있어 방마다 새로 만든다)
            foreach (RoomInfo room in _joinedRooms.Values)
                GetRoomConversation(room).Add(new ChatItem { Kind = notice ? ChatItemKind.Notice : ChatItemKind.System, Text = text, Time = packet.Timestamp });
        }
    }

    /// <summary>대화에 메시지를 넣고, 창이 안 보이면 안 읽은 수를 올리고, 받은 메시지면 깜빡임·알림음.</summary>
    private void AddMessage(Conversation conv, ChatWindow? window, ChatItem item)
    {
        conv.Add(item);
        if (item.Kind == ChatItemKind.Mine) return;

        bool reading = window is { Visible: true, ContainsFocus: true };
        if (!reading)
        {
            conv.Unread++;
            RefreshUserList();
            RefreshRoomList();
        }
        if (window is not null && !reading) NativeMethods.Flash(window);
        if (reading)
        {
            MarkRead(conv); // 보고 있는 창에 온 메시지는 바로 읽음 → 보낸 사람 화면의 숫자가 줄어든다
            return;
        }

        // 알림 창 + 소리: 1:1 메시지와, 방에서 나를 부른(@이름) 메시지만. (방 메시지마다 알리면 너무 시끄럽다)
        bool direct = conv.Key.StartsWith("dm:");
        if (!direct && int.TryParse(conv.Key["room:".Length..], out int roomId) && _settings.MutedRoomIds.Contains(roomId)) return;
        if (!direct && !item.Mentioned) return;
        if (_presence is not (Presence.Busy or Presence.DoNotDisturb)) SystemSounds.Asterisk.Play(); // 회의 중·방해 금지면 소리 끔
        string title = direct ? item.Sender : $"{conv.Title} · {item.Sender}";
        string text = item.Mentioned ? $"[나를 불렀어요] {item.Text}" : item.Text;
        ShowToast(title, text, item.SenderKey, () => OpenConversation(conv));
    }

    /// <summary>"@이름" 또는 "@사원번호"가 들어 있으면 나를 부른 것.</summary>
    private bool MentionsMe(string? text)
        => text is not null &&
           (text.Contains("@" + _me.Name, StringComparison.OrdinalIgnoreCase) || text.Contains("@" + _me.EmployeeNo, StringComparison.OrdinalIgnoreCase));

    /// <summary>알림 창을 눌렀을 때: 그 대화의 창을 연다.</summary>
    private void OpenConversation(Conversation conv)
    {
        if (conv.Key.StartsWith("room:") && int.TryParse(conv.Key["room:".Length..], out int roomId) && _joinedRooms.TryGetValue(roomId, out RoomInfo? room))
            OpenRoomWindow(room);
        else if (conv.Key.StartsWith("dm:") && _directWindows.TryGetValue(conv.Key["dm:".Length..], out ChatWindow? window))
            BringToFront(window);
    }

    // ───────────────────────── 상태 표시·자리 비움 ─────────────────────────

    private void BuildStatusMenu()
    {
        foreach (Presence p in Enum.GetValues<Presence>())
        {
            var item = new ToolStripMenuItem(Theme.PresenceText(p)) { Tag = p, Image = StatusDotImage(Theme.PresenceColor(p)) };
            item.Click += (_, _) => SetPresence(p, auto: false);
            _statusMenu.Items.Add(item);
        }
        _statusMenu.Items.Add(new ToolStripSeparator());
        _statusMenu.Items.Add("상태 메시지 바꾸기...", null, (_, _) => ChangeStatusMessage());
        _statusMenu.Items.Add(new ToolStripSeparator());
        _statusMenu.Items.Add("비밀번호 변경...", null, (_, _) => ChangePassword());
        // [주의] 관리자 메뉴는 로그인할 때 받은 권한(Role)이 ADMIN일 때만 보인다. 이건 화면 편의일 뿐이고,
        //        진짜 권한 확인은 서버가 관리 요청마다 다시 한다(메뉴를 억지로 띄워도 서버가 거절).
        if (_me.IsAdmin)
            _statusMenu.Items.Add("관리자 메뉴...", null, (_, _) => OpenAdmin(showPending: false));
        _statusMenu.Items.Add(new ToolStripSeparator());
        _statusMenu.Items.Add("전체 대화 검색...", null, (_, _) => OpenSearch());
        _statusMenu.Items.Add("동료 찾기 (오프라인 포함)...", null, (_, _) => FindPeople());
        _statusMenu.Items.Add(new ToolStripSeparator());
        _statusMenu.Items.Add("로그아웃", null, (_, _) => Logout());
        // 메뉴를 열 때 지금 상태에 체크 표시
        _statusMenu.Opening += (_, _) =>
        {
            foreach (ToolStripItem i in _statusMenu.Items)
                if (i is ToolStripMenuItem { Tag: Presence p } m) m.Checked = p == _presence;
        };
    }

    /// <summary>메뉴에 넣을 작은 색 점 그림.</summary>
    private Bitmap StatusDotImage(Color color)
    {
        int size = S(12);
        var bmp = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(bmp);
        Theme.DrawStatusDot(g, new Rectangle(0, 0, size - 1, size - 1), color);
        return bmp;
    }

    /// <summary>내 상태를 바꾸고 서버에 알린다. auto = 10분 무입력으로 자동 전환된 경우.</summary>
    private void SetPresence(Presence presence, bool auto)
    {
        _presence = presence;
        _autoAway = auto && presence == Presence.Away;
        _header.Invalidate();
        if (_connected)
            _client.Send(Packet.Create(PacketType.StatusUpdate).WithData(new StatusUpdate { Presence = presence })); // 메시지는 null = 그대로
    }

    /// <summary>내 비밀번호 바꾸기. 성공하면 자동 재접속에 쓸 비밀번호도 새 것으로 바꾼다.</summary>
    private void ChangePassword()
    {
        using var form = new ChangePasswordForm(_client);
        if (form.ShowDialog(this) == DialogResult.OK && _client.LastLogin is LoginRequest last)
        {
            _client.LastLogin = new LoginRequest { EmployeeNo = last.EmployeeNo, Password = form.NewPassword, ProtocolVersion = ProtocolInfo.Version };
            _client.LastTokenLogin = null; // 서버가 비밀번호 변경과 함께 기존 bearer 토큰을 폐기함
        }
    }

    /// <summary>관리자 창 열기 (이미 열려 있으면 앞으로). showPending = 승인 대기 목록부터.</summary>
    private void OpenAdmin(bool showPending)
    {
        if (_adminForm is null || _adminForm.IsDisposed)
        {
            _adminForm = new AdminForm(_client, _me.EmployeeNo);
            _adminForm.FormClosed += (_, _) => _adminForm = null;
            _adminForm.Show(); // 모달이 아니게: 관리 창을 띄워 둔 채로 채팅도 할 수 있다
        }
        else
        {
            if (_adminForm.WindowState == FormWindowState.Minimized) _adminForm.WindowState = FormWindowState.Normal;
            _adminForm.Activate();
        }
        if (showPending) _adminForm.ShowPending();
    }

    private void ChangeStatusMessage()
    {
        using var form = new StatusMessageForm(_statusMessage);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        _statusMessage = form.Message;
        _header.Invalidate();
        _client.Send(Packet.Create(PacketType.StatusUpdate).WithData(new StatusUpdate { Presence = _presence, StatusMessage = _statusMessage }));
    }

    /// <summary>
    /// 30초마다: PC를 10분 동안 안 쓰면 자동 "자리 비움", 다시 쓰기 시작하면 자동 "온라인".
    /// [왜] 직접 고른 상태(회의 중, 직접 고른 자리 비움)는 건드리지 않는다. 자동으로 바꾼 것만 자동으로 되돌린다.
    /// </summary>
    private void CheckIdle()
    {
        if (!_connected) return;
        TimeSpan idle = NativeMethods.IdleTime();
        if (_presence == Presence.Online && idle >= AwayAfter) SetPresence(Presence.Away, auto: true);
        else if (_autoAway && idle < TimeSpan.FromMinutes(1)) SetPresence(Presence.Online, auto: false);
    }

    /// <summary>동료 목록·1:1 창에 보일 한 줄: 상태 메시지(없으면 부서·사번), 온라인이 아니면 앞에 상태.</summary>
    private static string UserSubtitle(UserInfo user)
    {
        string sub = user.StatusMessage.Length > 0 ? user.StatusMessage : $"{user.Department} · {user.EmployeeNo}";
        return user.Presence == Presence.Online ? sub : $"{Theme.PresenceText(user.Presence)} · {sub}";
    }

    // ───────────────────────── 트레이·알림 창 ─────────────────────────

    private void BuildTray()
    {
        _tray.Icon = Icon;
        _tray.Text = Theme.AppName;
        var menu = new ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => ShowFromTray());
        var status = new ToolStripMenuItem("상태");
        foreach (Presence p in Enum.GetValues<Presence>())
            status.DropDownItems.Add(Theme.PresenceText(p), StatusDotImage(Theme.PresenceColor(p)), (_, _) => SetPresence(p, auto: false));
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("로그아웃", null, (_, _) => { ShowFromTray(); Logout(); });
        menu.Items.Add("종료", null, (_, _) => { _exiting = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _trayMenu = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
        _tray.Visible = true;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>
    /// 앱 아이콘(파란 둥근 사각형 + 흰 말풍선 로고)을 코드로 그린다. 창 제목 표시줄과 트레이에 같이 쓴다.
    /// [개념] Icon.FromHandle(bitmap.GetHicon()): 그림(Bitmap)을 윈도우 아이콘 핸들로 바꾼다. 프로그램 실행 중 한 번만 만든다.
    /// </summary>
    private static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Theme.FillRounded(g, Theme.Primary, new Rectangle(0, 0, 31, 31), 8);
            Theme.DrawLogo(g, new Rectangle(5, 7, 22, 19));
        }
        // [주의] Icon.FromHandle은 핸들을 소유하지 않아 Dispose해도 GDI 핸들이 남는다.
        //        복제본(Clone)은 자기 핸들을 가지므로, 원래 핸들은 바로 돌려준다(DestroyIcon).
        IntPtr handle = bmp.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    /// <summary>
    /// 화면 오른쪽 아래 알림 창. 방해 금지면 띄우지 않는다. 한 번에 하나만 (이전 것은 닫음).
    /// </summary>
    private void ShowToast(string title, string text, string avatarKey, Action onClick)
    {
        if (_presence == Presence.DoNotDisturb) return;
        _toast?.Close();
        var toast = new ToastForm(title, text, avatarKey);
        toast.Click += (_, _) => onClick();
        toast.FormClosed += (_, _) => { if (_toast == toast) _toast = null; };
        _toast = toast;
        toast.Show();
    }

    /// <summary>안 읽은 메시지 수를 트레이 아이콘 툴팁에 (마우스를 올리면 보임).</summary>
    private void UpdateTrayText()
    {
        int unread = _conversations.Values.Sum(c => c.Unread);
        // [주의] NotifyIcon.Text는 최대 63자. 넘으면 예외가 나므로 짧게 유지한다.
        _tray.Text = unread > 0 ? $"{Theme.AppName} - 안 읽은 메시지 {unread}개" : Theme.AppName;
    }

    // ───────────────────────── 채팅방 ─────────────────────────

    private void HandleRoomResponse(Packet packet)
    {
        if (!packet.Success)
        {
            // 재접속 후 다시 들어가려던 방이 사라짐(서버 재시작 등) → 팝업 대신 그 방 대화창에 알린다
            if (int.TryParse(packet.Target, out int failedId) && _rejoinRooms.Remove(failedId))
            {
                if (_joinedRooms.Remove(failedId, out RoomInfo? gone))
                {
                    GetRoomConversation(gone).Add(new ChatItem { Kind = ChatItemKind.Error, Text = $"'{gone.Name}' 방에 다시 들어가지 못했습니다: {packet.Body}" });
                    if (_roomWindows.TryGetValue(failedId, out ChatWindow? goneWindow))
                    {
                        goneWindow.ShowLeaveButton = false;
                        goneWindow.ShowInviteButton = false;
                        goneWindow.SetInputEnabled(false); // 참여하지 않은 방이 되었으므로 보낼 수 없다
                    }
                }
                RefreshRoomList();
                return;
            }
            _openRoomOnJoin = false;
            MessageBox.Show(this, packet.Body ?? "요청을 처리하지 못했습니다.", "채팅방", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        RoomInfo? room = packet.GetData<RoomInfo>();
        if (room is null) return;
        _rejoinRooms.Remove(room.RoomId);
        bool isNew = !_joinedRooms.ContainsKey(room.RoomId);
        bool requested = _openRoomOnJoin;

        // 이미 열려 있던 창이면(재접속) 입력을 다시 연다
        if (_roomWindows.TryGetValue(room.RoomId, out ChatWindow? openWindow))
        {
            openWindow.SetInputEnabled(true);
            UpdateRoomWindowHeader(room);
        }

        // 다른 방에서는 나가지 않는다 (여러 방 동시 참여)
        _joinedRooms[room.RoomId] = room;
        Conversation conv = GetRoomConversation(room);
        conv.Title = room.Name;
        if (!_rooms.Any(r => r.RoomId == room.RoomId)) _rooms.Add(room); // 비공개 방은 방 목록이 늦게 올 수 있다
        if (_openRoomOnJoin)
        {
            _openRoomOnJoin = false;
            OpenRoomWindow(room);
        }
        else if (isNew && !requested && !room.IsLobby && !string.IsNullOrEmpty(packet.Body))
        {
            // 누가 나를 초대함 → 알려 주고, 누르면 그 방을 연다
            SetStatus($"● {packet.Body}", Theme.Notice);
            ShowToast("채팅방 초대", packet.Body, $"room:{room.RoomId}", () => OpenRoomWindow(room));
        }
        RecomputeUnread(conv);
        RefreshRoomList();
    }

    /// <summary>방 멤버가 바뀜(입장·초대·퇴장) → 머리의 인원, 읽음 숫자 계산에 쓰는 명단을 바꾼다.</summary>
    private void HandleRoomUpdate(Packet packet)
    {
        if (packet.GetData<RoomInfo>() is not { } room || !_joinedRooms.ContainsKey(room.RoomId)) return;
        _joinedRooms[room.RoomId] = room;
        int index = _rooms.FindIndex(r => r.RoomId == room.RoomId);
        if (index >= 0) _rooms[index] = room;
        UpdateRoomWindowHeader(room);
        UpdateRoomMentionCandidates(room);
        RecomputeUnread(GetRoomConversation(room));
        RefreshRoomList();
    }

    /// <summary>[나가기]가 처리됨 → 창을 닫고 참여 목록에서 뺀다.</summary>
    private void HandleRoomLeft(Packet packet)
    {
        if (!int.TryParse(packet.Target, out int roomId)) return;
        _joinedRooms.Remove(roomId);
        if (_roomWindows.TryGetValue(roomId, out ChatWindow? window)) window.CloseByProgram();
        if (_conversations.Remove(Conversation.RoomKey(roomId), out Conversation? removed))
        {
            // 다시 들어가면 새 대화로 시작. 그 대화의 파일 말풍선도 더 이상 추적하지 않는다
            foreach (string id in _fileItems.Where(kv => kv.Value.Conv == removed).Select(kv => kv.Key).ToList())
                _fileItems.Remove(id);
        }
        SetStatus(packet.Body ?? "방에서 나왔습니다.", Theme.SubText);
        RefreshRoomList();
    }

    /// <summary>방 목록에서 더블클릭: 참여 중이면 창만 열고, 아니면 서버에 입장 요청.</summary>
    /// <summary>방 목록에서 오른쪽 클릭: 열기 / 들어가기, 나가기, 방 삭제(방장·관리자).</summary>
    private void ShowRoomListMenu(RoomInfo room, Point location)
    {
        bool joined = _joinedRooms.ContainsKey(room.RoomId);
        RoomInfo current = _joinedRooms.GetValueOrDefault(room.RoomId) ?? room;
        bool canDelete = !room.IsLobby && (current.OwnerNo == _me.EmployeeNo || _me.IsAdmin);

        var menu = new ContextMenuStrip();
        menu.Items.Add(joined ? "열기" : "들어가기", null, (_, _) => EnterRoom(room));
        if (!room.IsLobby && joined)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("방 나가기", null, (_, _) => ConfirmLeaveRoom(room.RoomId, this)).Enabled = _connected;
        }
        if (canDelete)
        {
            if (!joined) menu.Items.Add(new ToolStripSeparator());
            ToolStripItem delete = menu.Items.Add("방 삭제", null, (_, _) => ConfirmDeleteRoom(room.RoomId, this));
            delete.ForeColor = Theme.Danger;
            delete.Enabled = _connected;
        }
        // [주의] 메뉴는 닫힐 때 바로 해제한다(그때그때 만드는 메뉴라 남겨 두면 창 핸들이 쌓인다).
        //        Closed 직후에는 아직 클릭 처리가 끝나지 않았을 수 있어, 한 박자 뒤(BeginInvoke)에 해제한다.
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(_roomList, location);
    }

    /// <summary>방 나가기 확인. 방장이면 먼저 방장을 넘길 사람을 고른다. 창 [나가기]·방 메뉴·방 목록 메뉴가 함께 쓴다.</summary>
    private void ConfirmLeaveRoom(int roomId, IWin32Window owner)
    {
        if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? current) || current.IsLobby) return;
        // 방장이 나가려면 먼저 방장을 넘길 사람을 고른다 (고르지 않으면 서버가 가장 오래된 멤버에게 자동으로 넘긴다)
        if (current.OwnerNo == _me.EmployeeNo && (current.Members?.Count ?? 0) > 1)
        {
            string? next = PickNewOwner(current, owner, "나가기 전에 방장을 넘길 사람을 고르세요",
                                        skipText: "그냥 나가기 (자동으로 넘기기)");
            if (next is null) return; // 취소 = 나가지 않음
            // "그냥 나가기"("")면 넘기지 않고 나간다 → 서버가 가장 먼저 들어온 멤버를 새 방장으로 정하고 방에 알린다
            if (next.Length > 0)
                _client.Send(Packet.Create(PacketType.RoomTransferOwner, body: next, target: roomId.ToString()));
            _client.Send(Packet.Create(PacketType.RoomLeave, target: roomId.ToString())); // 같은 연결이라 순서대로 처리된다
            return;
        }
        string note = "\n다른 멤버가 남아 있으면 방은 그대로 남습니다. (방을 없애려면 방장이 [방 삭제])";
        if (current.IsPrivate) note += "\n비공개 방이라 다시 들어오려면 멤버의 초대가 필요합니다.";
        if (MessageBox.Show(owner, $"'{current.Name}' 방에서 나갈까요?{note}", "채팅방 나가기",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            _client.Send(Packet.Create(PacketType.RoomLeave, target: roomId.ToString()));
        // 창은 서버가 RoomLeft로 확인해 주면 닫힌다
    }

    /// <summary>방 삭제 확인 (방장·관리자). 참여하지 않은 방도 관리자는 목록에서 바로 지울 수 있다.</summary>
    private void ConfirmDeleteRoom(int roomId, IWin32Window owner)
    {
        RoomInfo? current = _joinedRooms.GetValueOrDefault(roomId) ?? _rooms.FirstOrDefault(r => r.RoomId == roomId);
        if (current is null || current.IsLobby) return;
        if (MessageBox.Show(owner,
                $"'{current.Name}' 방을 삭제할까요?\n\n멤버 {current.MemberCount}명 모두가 방에서 빠지고, 방 목록에서 사라집니다.\n(대화 기록은 서버에 남습니다. 되돌릴 수 없습니다)",
                "방 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            _client.Send(Packet.Create(PacketType.RoomDelete, target: roomId.ToString()));
        // 결과: 접속 중인 멤버 모두에게 RoomLeft → 창이 닫히고 목록에서 빠진다 (서버가 방 목록도 새로 보낸다)
    }

    private void EnterRoom(RoomInfo room)
    {
        if (_joinedRooms.TryGetValue(room.RoomId, out RoomInfo? joined))
        {
            OpenRoomWindow(joined);
            return;
        }
        _openRoomOnJoin = true;
        if (!_client.Send(Packet.Create(PacketType.RoomJoin, target: room.RoomId.ToString())))
            _openRoomOnJoin = false;
    }

    private void CreateRoom()
    {
        using var form = new CreateRoomForm();
        if (form.ShowDialog(this) != DialogResult.OK) return;

        var request = new CreateRoomRequest { Name = form.RoomName, MaxUsers = form.MaxUsers, IsPrivate = form.IsPrivate };
        if (form.IsPrivate)
        {
            // 비공개 방은 목록에 안 보이므로 함께할 사람을 바로 고르게 한다 (건너뛰어도 나중에 [초대]로 부를 수 있다)
            using var picker = new PeoplePickerForm(_client, $"'{form.RoomName}' 방에 초대할 사람 (건너뛰려면 취소)", multiSelect: true, [_me.EmployeeNo]);
            if (picker.ShowDialog(this) == DialogResult.OK)
                request.InviteNos = picker.Selected.Select(u => u.EmployeeNo).Take(Math.Max(0, form.MaxUsers - 1)).ToList();
        }

        _openRoomOnJoin = true;
        if (!_client.Send(Packet.Create(PacketType.RoomCreate).WithData(request)))
            _openRoomOnJoin = false;
    }

    /// <summary>방 창 머리를 눌렀을 때의 메뉴: 멤버 보기, 방장 넘기기(방장·관리자만).</summary>
    private ContextMenuStrip BuildRoomMenu(int roomId, ChatWindow window)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("멤버 보기", null, (_, _) => ShowMembers(roomId, window));
        menu.Items.Add("방 파일 모아보기", null, (_, _) => OpenRoomFiles(window));
        var mute = new ToolStripMenuItem("방 알림 끄기") { CheckOnClick = true, Checked = _settings.MutedRoomIds.Contains(roomId) };
        mute.CheckedChanged += (_, _) =>
        {
            if (mute.Checked)
            {
                if (!_settings.MutedRoomIds.Contains(roomId)) _settings.MutedRoomIds.Add(roomId);
            }
            else _settings.MutedRoomIds.Remove(roomId);
            _settings.Save();
        };
        menu.Items.Add(mute);
        var notice = menu.Items.Add("공지 고정·수정...", null, (_, _) => EditRoomNotice(roomId, window));
        var transfer = menu.Items.Add("방장 넘기기...", null, (_, _) =>
        {
            if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
            string? next = PickNewOwner(room, window, "새 방장을 고르세요");
            if (next is not null)
                _client.Send(Packet.Create(PacketType.RoomTransferOwner, body: next, target: roomId.ToString()));
        });
        menu.Items.Add(new ToolStripSeparator());
        var leave = menu.Items.Add("방 나가기", null, (_, _) => ConfirmLeaveRoom(roomId, window));
        var delete = menu.Items.Add("방 삭제", null, (_, _) => ConfirmDeleteRoom(roomId, window));
        delete.ForeColor = Theme.Danger;
        menu.Opening += (_, _) =>
        {
            RoomInfo? room = _joinedRooms.GetValueOrDefault(roomId);
            leave.Enabled = room is not null && _connected;
            delete.Visible = room is not null && (room.OwnerNo == _me.EmployeeNo || _me.IsAdmin);
            delete.Enabled = _connected;
            notice.Enabled = room is not null && _connected && (room.OwnerNo == _me.EmployeeNo || _me.IsAdmin);
            transfer.Enabled = room is not null && _connected && (room.OwnerNo == _me.EmployeeNo || _me.IsAdmin) && (room.Members?.Count ?? 0) > 1;
        };
        return menu;
    }

    /// <summary>방 멤버 중 새 방장 고르기 (나 제외). 고르지 않으면 null.</summary>
    /// <remarks>skipText를 주면 "고르지 않고 진행" 버튼이 생기고, 누르면 빈 문자열("")을 돌려준다.</remarks>
    private string? PickNewOwner(RoomInfo room, IWin32Window owner, string title, string? skipText = null)
    {
        var members = (room.Members ?? [])
            .Where(m => m.EmployeeNo != _me.EmployeeNo && m.EmployeeNo != room.OwnerNo)
            .Select(m => new UserInfo { EmployeeNo = m.EmployeeNo, Name = m.DisplayName, Department = _users.Any(u => u.EmployeeNo == m.EmployeeNo) ? "접속 중" : "" })
            .ToList();
        if (members.Count == 0)
        {
            MessageBox.Show(owner, "방장을 넘길 다른 멤버가 없습니다.", "방장 넘기기", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        using var picker = new PeoplePickerForm(_client, $"'{room.Name}' - {title}", multiSelect: false, [],
            people: members, okText: "방장 넘기기", hint: "더블클릭하거나 고른 뒤 [방장 넘기기]", skipText: skipText);
        DialogResult result = picker.ShowDialog(owner);
        if (result == DialogResult.Ignore) return "";
        return result == DialogResult.OK && picker.Selected.Count > 0 ? picker.Selected[0].EmployeeNo : null;
    }

    /// <summary>방 멤버 목록 (방장, 접속 중 표시).</summary>
    private void ShowMembers(int roomId, IWin32Window owner)
    {
        if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room) || room.Members is null) return;
        var online = _users.Select(u => u.EmployeeNo).ToHashSet();
        online.Add(_me.EmployeeNo);
        IEnumerable<string> lines = room.Members
            .OrderByDescending(m => m.EmployeeNo == room.OwnerNo).ThenByDescending(m => online.Contains(m.EmployeeNo)).ThenBy(m => m.DisplayName)
            .Select(m => $"{(online.Contains(m.EmployeeNo) ? "●" : "○")} {m.DisplayName} ({m.EmployeeNo}){(m.EmployeeNo == room.OwnerNo ? "  [방장]" : "")}");
        MessageBox.Show(owner, $"멤버 {room.Members.Count}명 (● 접속 중)\n\n{string.Join("\n", lines)}", $"'{room.Name}' 멤버",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>[초대] → 사람 고르기 → 서버에 초대 요청 (이미 멤버인 사람은 목록에서 뺀다).</summary>
    private void InviteToRoom(int roomId, IWin32Window owner)
    {
        if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
        var members = room.Members?.Select(m => m.EmployeeNo) ?? [];
        using var picker = new PeoplePickerForm(_client, $"'{room.Name}' 방에 초대", multiSelect: true, members.Append(_me.EmployeeNo));
        if (picker.ShowDialog(owner) != DialogResult.OK) return;
        _client.Send(Packet.Create(PacketType.RoomInvite).WithData(new RoomInviteRequest
        {
            RoomId = roomId,
            EmployeeNos = picker.Selected.Select(u => u.EmployeeNo).ToList(),
        }));
        // 결과: 초대된 사람에게 RoomRes, 방 사람들에게 SystemMsg("…님을 초대했습니다") + RoomUpdate
    }

    /// <summary>
    /// 방 대화창 열기. X로 닫으면 창만 닫히고 방에는 남는다(다시 더블클릭하면 이전 대화와 함께 열림).
    /// 방에서 나가려면 창 위쪽 [나가기].
    /// </summary>
    private void OpenRoomWindow(RoomInfo room)
    {
        if (_roomWindows.TryGetValue(room.RoomId, out ChatWindow? existing) && !existing.IsDisposed)
        {
            BringToFront(existing);
            return;
        }

        int roomId = room.RoomId;
        Conversation conv = GetRoomConversation(room);
        var window = new ChatWindow(ChatWindowKind.Room, conv, $"room:{roomId}");
        UpdateRoomMentionCandidates(room);
        window.ShowLeaveButton = !room.IsLobby;
        window.ShowInviteButton = !room.IsLobby;
        window.SendRequested += (_, text, replyId) => SendText(PacketType.ChatMsg, text, roomId.ToString(), replyId);
        window.InviteRequested += _ => InviteToRoom(roomId, window);
        window.ShowDeleteButton = !room.IsLobby && (room.OwnerNo == _me.EmployeeNo || _me.IsAdmin);
        if (!room.IsLobby) window.HeaderMenu = BuildRoomMenu(roomId, window);
        window.DeleteRoomRequested += _ => ConfirmDeleteRoom(roomId, window);
        window.LeaveRequested += _ => ConfirmLeaveRoom(roomId, window);
        window.FilesRequested += (_, paths) => SendFiles(conv, paths, roomId, null);
        WireWindow(window);
        window.FormClosed += (_, _) =>
        {
            if (_roomWindows.TryGetValue(roomId, out ChatWindow? w) && w == window) _roomWindows.Remove(roomId);
        };

        _roomWindows[roomId] = window;
        UpdateRoomWindowHeader(room);
        UpdateRoomMentionCandidates(room);
        window.SetInputEnabled(_connected);
        window.Show();
    }

    private void UpdateRoomWindowHeader(RoomInfo room)
    {
        if (!_roomWindows.TryGetValue(room.RoomId, out ChatWindow? window)) return;
        if (room.IsLobby)
        {
            window.SetPinnedNotice(null);
            window.SetHeader(room.Name, $"{room.MemberCount}명 접속 중 · 모두가 함께하는 기본 방");
            return;
        }
        // 멤버(접속하지 않은 사람 포함)와 지금 접속한 사람을 따로 보여 준다
        window.ShowDeleteButton = room.OwnerNo == _me.EmployeeNo || _me.IsAdmin; // 방장이 바뀌면 버튼도 따라간다
        window.SetPinnedNotice(room.PinnedNotice);
        string max = room.MaxUsers > 0 ? $"/{room.MaxUsers}" : "";
        string kind = room.IsPrivate ? "비공개 · " : "";
        window.SetHeader(room.Name, $"{kind}멤버 {room.MemberCount}{max}명 · 접속 {room.OnlineCount}명 · 방장 {room.OwnerName}");
    }

    private void EditRoomNotice(int roomId, IWin32Window owner)
    {
        if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
        using var form = new RoomNoticeForm(room.Name, room.PinnedNotice);
        if (form.ShowDialog(owner) != DialogResult.OK) return;
        _client.Send(Packet.Create(PacketType.RoomNotice).WithData(new RoomNoticeRequest { RoomId = roomId, Text = form.NoticeText }));
    }

    private void UpdateRoomMentionCandidates(RoomInfo room)
    {
        if (!_roomWindows.TryGetValue(room.RoomId, out ChatWindow? window)) return;
        IEnumerable<(string EmployeeNo, string DisplayName)> candidates = room.IsLobby
            ? _users.Select(u => (u.EmployeeNo, u.DisplayName))
            : (room.Members ?? []).Select(m => (m.EmployeeNo, m.DisplayName));
        window.SetMentionCandidates(candidates, _me.EmployeeNo);
    }

    private Conversation GetRoomConversation(RoomInfo room)
        => GetConversation(Conversation.RoomKey(room.RoomId), room.Name);

    // ───────────────────────── 1:1 대화 ─────────────────────────

    /// <summary>1:1 창을 열거나 이미 열린 창을 돌려준다.</summary>
    private ChatWindow OpenDirect(UserInfo user, bool activate)
    {
        if (_directWindows.TryGetValue(user.EmployeeNo, out ChatWindow? existing) && !existing.IsDisposed)
        {
            if (activate) BringToFront(existing);
            return existing;
        }

        Conversation conv = GetConversation(Conversation.DirectKey(user.EmployeeNo), user.DisplayName);
        if (!conv.HistoryRequested)
        {
            // 이 사람과의 1:1 대화를 처음 열 때 한 번만 지난 대화를 요청한다 (방은 입장할 때 서버가 알아서 보내 줌)
            conv.HistoryRequested = true;
            _client.Send(Packet.Create(PacketType.HistoryReq, target: user.EmployeeNo));
        }
        var window = new ChatWindow(ChatWindowKind.Direct, conv, user.EmployeeNo);
        bool online = _users.Any(u => u.EmployeeNo == user.EmployeeNo);
        window.SetHeader(user.DisplayName, online ? UserSubtitle(user) : "접속하지 않음 · 보낸 메시지는 다음에 접속하면 전달됩니다");
        window.SendRequested += (_, text, replyId) => SendText(PacketType.Whisper, text, user.EmployeeNo, replyId);
        window.FilesRequested += (_, paths) => SendFiles(conv, paths, null, user.EmployeeNo);
        WireWindow(window);
        window.FormClosed += (_, _) =>
        {
            if (_directWindows.TryGetValue(user.EmployeeNo, out ChatWindow? w) && w == window) _directWindows.Remove(user.EmployeeNo);
        };
        _directWindows[user.EmployeeNo] = window;

        window.SetOnline(_connected && online);
        window.SetInputEnabled(_connected);
        if (activate) window.Show();
        else window.ShowInactive();
        return window;
    }

    // ───────────────────────── 지난 대화 (DB 기록) ─────────────────────────

    /// <summary>
    /// 서버가 보낸 지난 대화 묶음. Last가 올 때까지 대화의 PendingHistory에 모았다가 한꺼번에 맨 앞에 넣는다.
    /// (묶음마다 넣으면 순서가 꼬이고 화면도 여러 번 다시 그려진다)
    /// </summary>
    private void HandleHistory(Packet packet)
    {
        if (packet.GetData<HistoryBatch>() is not { } batch) return;

        Conversation? conv = null;
        if (batch.RoomId is int roomId)
        {
            if (_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) conv = GetRoomConversation(room);
        }
        else if (batch.PeerNo is string peer)
        {
            _conversations.TryGetValue(Conversation.DirectKey(peer), out conv);
        }
        if (conv is null) return; // 그사이 방에서 나갔거나 대화가 사라짐

        conv.HistoryHasMore = batch.HasMore;

        foreach (HistoryItem h in batch.Items)
        {
            bool mine = h.SenderNo == _me.EmployeeNo;
            var item = new ChatItem
            {
                Kind = mine ? ChatItemKind.Mine : ChatItemKind.Other,
                Sender = h.SenderName,
                SenderKey = h.SenderNo,
                Text = h.Body,
                Time = h.Time,
                File = h.File is null ? null : new ChatFile { Info = h.File },
                MessageId = h.MessageId,
                ReplyTo = h.ReplyTo,
                Deleted = h.Deleted,
                Edited = h.Edited,
                Reactions = h.Reactions ?? [],
            };
            if (h.File is not null) _fileItems.TryAdd(h.File.FileId, (item, conv)); // 기록 속 파일도 클릭해서 받을 수 있게
            conv.PendingHistory.Add(item);
        }
        if (!batch.Last) return;

        // 1:1: 상대가 어디까지 읽었는지 (내 메시지 옆 "1" 표시)
        if (batch.PeerLastReadAt is DateTime peerRead && (conv.PeerLastReadAt is null || peerRead > conv.PeerLastReadAt))
            conv.PeerLastReadAt = peerRead;

        var all = conv.PendingHistory.ToList();
        conv.PendingHistory.Clear();
        conv.HistoryLoading = false;
        conv.InsertHistory(all);
        conv.LoadingOlderHistory = false;
        RecomputeUnread(conv);
        if (OpenWindows().FirstOrDefault(w => w.Conversation == conv) is { ContainsFocus: true }) MarkRead(conv);
    }

    // ───────────────────────── 파일·이미지·동영상 ─────────────────────────

    /// <summary>전송 엔진의 이벤트(작업/수신 스레드) → UI 스레드에서 말풍선 갱신.</summary>
    private void WireTransfers()
    {
        _transfers.UploadProgress += (id, percent) => UpdateFile(id, f => { f.State = FileState.Uploading; f.Percent = percent; });
        _transfers.UploadSent += id => UpdateFile(id, f => f.State = FileState.Processing);
        _transfers.UploadFailed += (id, reason) => UpdateFile(id, f => { f.State = FileState.Failed; f.Error = reason; });
        _transfers.DownloadProgress += (id, percent) => UpdateFile(id, f =>
        {
            // [주의] 진행률 이벤트는 BeginInvoke로 늦게 도착한다. 그사이 받기를 취소했다면 "받는 중"으로 되돌리면 안 된다.
            if (!_transfers.IsDownloading(id)) return;
            f.State = FileState.Downloading;
            f.Percent = percent;
        });
        _transfers.DownloadCompleted += (id, path) => UiHelper.RunOnUi(this, () => OnDownloadCompleted(id, path));
        _transfers.DownloadFailed += (id, reason) => UiHelper.RunOnUi(this, () =>
        {
            if (!_fileItems.TryGetValue(id, out var entry) || entry.Item.File is not ChatFile file) return;
            file.State = FileState.Ready; // 다시 시도할 수 있게
            entry.Conv.NotifyChanged(entry.Item);
            entry.Conv.Add(new ChatItem { Kind = ChatItemKind.Error, Text = $"'{file.Info.FileName}'을(를) 받지 못했습니다: {reason}" });
        });
    }

    private void UpdateFile(string fileId, Action<ChatFile> change) => UiHelper.RunOnUi(this, () =>
    {
        if (!_fileItems.TryGetValue(fileId, out var entry) || entry.Item.File is not ChatFile file) return;
        if (file.State is FileState.Failed or FileState.Canceled) return; // 실패·취소 뒤에 늦게 온 진행률은 무시
        change(file);
        entry.Conv.NotifyChanged(entry.Item);
    });

    /// <summary>
    /// 대화창에서 고른 파일 보내기. 사진 미리보기는 작업 스레드에서 만들고(큰 사진도 UI가 멈추지 않게),
    /// 다 되면 UI 스레드에서 "보내는 중" 말풍선을 넣고 전송을 시작한다.
    /// </summary>
    private void SendFiles(Conversation conv, List<string> paths, int? roomId, string? toEmployeeNo)
    {
        // 너무 큰 파일은 미리 걸러서 바로 알려 준다
        foreach (string path in paths.ToList())
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= _transfers.MaxFileSize) continue; // 없는 파일은 StartUpload가 알려 준다
            long size = info.Length;
            paths.Remove(path);
            conv.Add(new ChatItem
            {
                Kind = ChatItemKind.Error,
                Text = $"{Path.GetFileName(path)}: 파일이 너무 큽니다 ({FileRules.FormatSize(size)}). 최대 {FileRules.FormatSize(_transfers.MaxFileSize)}까지 보낼 수 있습니다.",
            });
        }
        if (paths.Count == 0) return;

        Task.Run(() => paths.Select(path => (Path: path, Thumb: ImageHelper.CreateThumbnail(path))).ToList())
            .ContinueWith(task => UiHelper.RunOnUi(this, () =>
            {
                if (task.Result is not { } prepared) return;
                foreach (var (path, thumb) in prepared) StartUpload(conv, path, thumb, roomId, toEmployeeNo);
            }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void StartUpload(Conversation conv, string path, byte[]? thumbnail, int? roomId, string? toEmployeeNo)
    {
        string fileId;
        try
        {
            fileId = _transfers.StartUpload(path, roomId, toEmployeeNo, thumbnail);
        }
        catch (ArgumentException ex)
        {
            conv.Add(new ChatItem { Kind = ChatItemKind.Error, Text = ex.Message });
            return;
        }

        var info = new FileInfo(path);
        string name = FileRules.SanitizeFileName(info.Name);
        var item = new ChatItem
        {
            Kind = ChatItemKind.Mine,
            Sender = _me.DisplayName,
            SenderKey = _me.EmployeeNo,
            Text = $"[파일] {name}",
            File = new ChatFile
            {
                Info = new SharedFile
                {
                    FileId = fileId, FileName = name, Size = info.Length, Kind = FileRules.GetKind(name),
                    RoomId = roomId, ToEmployeeNo = toEmployeeNo, SenderNo = _me.EmployeeNo, SenderName = _me.DisplayName,
                    Thumbnail = thumbnail,
                },
                State = FileState.Uploading,
                LocalPath = path, // 내가 보낸 파일은 원본을 바로 열 수 있다
            },
        };
        _fileItems[fileId] = (item, conv);
        conv.Add(item);
    }

    /// <summary>서버가 파일을 저장하고 대화에 올렸다 (방: Target = 방 번호, 1:1: Target = 받는 사람).</summary>
    private void HandleFileMessage(Packet packet)
    {
        if (packet.GetData<SharedFile>() is not { } shared || !FileRules.IsValidFileId(shared.FileId)) return;
        bool mine = packet.SenderId == _me.EmployeeNo;

        // 내가 보낸 파일: "보내는 중" 말풍선을 완료로 바꾼다
        if (mine && _fileItems.TryGetValue(shared.FileId, out var pending) && pending.Item.File is ChatFile own)
        {
            own.Info = shared;
            own.State = FileState.Ready;
            pending.Conv.NotifyChanged(pending.Item);
            return;
        }

        Conversation conv;
        ChatWindow? window;
        if (shared.RoomId is int roomId)
        {
            if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
            conv = GetRoomConversation(room);
            window = _roomWindows.GetValueOrDefault(roomId);
        }
        else
        {
            string otherNo = mine ? shared.ToEmployeeNo ?? "" : packet.SenderId ?? "";
            if (otherNo.Length == 0) return;
            UserInfo other = _users.FirstOrDefault(u => u.EmployeeNo == otherNo)
                             ?? new UserInfo { EmployeeNo = otherNo, Name = mine ? otherNo : packet.Sender ?? otherNo, Position = "" };
            window = OpenDirect(other, activate: false);
            conv = window.Conversation;
        }

        var item = new ChatItem
        {
            Kind = mine ? ChatItemKind.Mine : ChatItemKind.Other,
            Sender = packet.Sender ?? "",
            SenderKey = packet.SenderId ?? "",
            Text = $"[파일] {shared.FileName}",
            Time = packet.Timestamp,
            File = new ChatFile { Info = shared },
        };
        _fileItems[shared.FileId] = (item, conv);
        AddMessage(conv, window, item);
    }

    /// <summary>파일 말풍선 클릭(열기) / 오른쪽 클릭 메뉴.</summary>
    private void OnFileCommand(ChatWindow window, ChatItem item, FileCommand command)
    {
        if (item.File is not ChatFile file) return;
        _fileItems.TryAdd(file.Info.FileId, (item, window.Conversation));

        switch (command)
        {
            case FileCommand.Cancel:
                CancelTransfer(window, item);
                break;

            case FileCommand.Open:
                // 보내는 중·받는 중에 누르면 취소할지 묻는다 (실수로 누를 수 있으니 확인)
                if (file.CanCancel)
                {
                    string what = file.State == FileState.Uploading ? "보내기" : "받기";
                    if (MessageBox.Show(window, $"'{file.Info.FileName}' {what}를 취소할까요?", "전송 취소",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        CancelTransfer(window, item);
                    return;
                }
                if (file.HasLocalFile)
                {
                    if (file.Info.Kind == FileKind.Image) OpenImageViewer(window.Conversation, item);
                    else OpenLocalFile(window, file.LocalPath!, file.Info.FileName);
                    return;
                }
                if (file.IsBusy || file.State == FileState.Failed) return;

                // 예전에 받아 둔 파일이 있으면 다시 받지 않는다
                string cache = CachePath(file.Info);
                if (File.Exists(cache) && new FileInfo(cache).Length == file.Info.Size)
                {
                    file.LocalPath = cache;
                    window.Conversation.NotifyChanged(item);
                    if (file.Info.Kind == FileKind.Image) OpenImageViewer(window.Conversation, item);
                    else OpenLocalFile(window, cache, file.Info.FileName);
                    return;
                }
                StartDownload(window, item, cache, openWhenDone: true);
                break;

            case FileCommand.SaveAs:
                if (file.IsBusy) return;
                using (var dialog = new SaveFileDialog { FileName = file.Info.FileName, Title = "다른 이름으로 저장", OverwritePrompt = true })
                {
                    string ext = FileRules.Extension(file.Info.FileName);
                    dialog.Filter = ext.Length > 0 ? $"{ext.TrimStart('.').ToUpperInvariant()} 파일 (*{ext})|*{ext}|모든 파일 (*.*)|*.*" : "모든 파일 (*.*)|*.*";
                    if (dialog.ShowDialog(window) != DialogResult.OK) return;

                    if (file.HasLocalFile)
                    {
                        try
                        {
                            File.Copy(file.LocalPath!, dialog.FileName, overwrite: true);
                            window.Conversation.Add(new ChatItem { Kind = ChatItemKind.System, Text = $"저장했습니다: {dialog.FileName}" });
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            MessageBox.Show(window, ex.Message, "저장 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        return;
                    }
                    StartDownload(window, item, dialog.FileName, openWhenDone: false);
                }
                break;

            case FileCommand.ShowInFolder:
                if (file.HasLocalFile)
                    Process.Start("explorer.exe", $"/select,\"{file.LocalPath}\"");
                break;
        }
    }

    private void OpenRoomFiles(ChatWindow window)
    {
        if (window.Kind != ChatWindowKind.Room) return;
        if (!int.TryParse(window.Conversation.Key["room:".Length..], out int roomId)) return;
        if (_roomFilesForms.TryGetValue(roomId, out RoomFilesForm? existing) && !existing.IsDisposed)
        {
            existing.Activate();
            return;
        }
        using var form = new RoomFilesForm(window.Conversation.Title, window.Conversation.Items);
        _roomFilesForms[roomId] = form;
        form.LoadMoreRequested += (before, beforeFileId) =>
        {
            bool sent = _client.Send(Packet.Create(PacketType.RoomFilesReq).WithData(
                new RoomFilesRequest { RoomId = roomId, Before = before, BeforeFileId = beforeFileId }));
            if (!sent) form.AddPage([], hasMore: false, error: "서버 연결이 끊겨 파일 목록을 요청하지 못했습니다.");
        };
        form.FormClosed += (_, _) => _roomFilesForms.Remove(roomId);
        form.OpenRequested += item => OnFileCommand(window, item, FileCommand.Open);
        form.SaveRequested += item => OnFileCommand(window, item, FileCommand.SaveAs);
        form.RequestNextPage();
        form.ShowDialog(window);
    }

    private void HandleRoomFilesResponse(Packet packet)
    {
        RoomFilesBatch? batch = packet.GetData<RoomFilesBatch>();
        if (batch is null || !_roomFilesForms.TryGetValue(batch.RoomId, out RoomFilesForm? form) || form.IsDisposed) return;
        form.AddPage(batch.Files, batch.HasMore, packet.Success ? null : packet.Body ?? "파일 목록 조회에 실패했습니다.");
    }

    /// <summary>
    /// 보내기·받기 취소. 보내기는 "취소됨"으로 남고(원본은 내 PC에 그대로), 받기는 처음 상태로 돌아가 다시 받을 수 있다.
    /// </summary>
    private void CancelTransfer(ChatWindow window, ChatItem item)
    {
        ChatFile file = item.File!;
        if (file.State == FileState.Uploading && _transfers.CancelUpload(file.Info.FileId))
        {
            file.State = FileState.Canceled;
        }
        else if (file.State == FileState.Downloading && _transfers.CancelDownload(file.Info.FileId))
        {
            file.State = FileState.Ready;
            file.Percent = 0;
            window.Conversation.Add(new ChatItem { Kind = ChatItemKind.System, Text = $"'{file.Info.FileName}' 받기를 취소했습니다." });
        }
        window.Conversation.NotifyChanged(item);
    }

    private void StartDownload(ChatWindow window, ChatItem item, string destination, bool openWhenDone)
    {
        ChatFile file = item.File!;
        if (!_connected) return;
        if (!_transfers.StartDownload(file.Info.FileId, destination)) return; // 이미 받는 중
        file.OpenWhenDownloaded = openWhenDone;
        file.State = FileState.Downloading;
        file.Percent = 0;
        window.Conversation.NotifyChanged(item);
    }

    private void OnDownloadCompleted(string fileId, string path)
    {
        if (!_fileItems.TryGetValue(fileId, out var entry) || entry.Item.File is not ChatFile file) return;
        file.State = FileState.Ready;
        file.LocalPath = path;
        entry.Conv.NotifyChanged(entry.Item);

        if (file.OpenWhenDownloaded)
        {
            file.OpenWhenDownloaded = false;
            ChatWindow? owner = OpenWindows().FirstOrDefault(w => w.Conversation == entry.Conv);
            if (file.Info.Kind == FileKind.Image) OpenImageViewer(entry.Conv, entry.Item);
            else OpenLocalFile(owner ?? (IWin32Window)this, path, file.Info.FileName);
        }
        else if (_imageViewer is { IsDisposed: false, Visible: true } && _imageViewerConversation == entry.Conv &&
                 _imageViewerItems.ElementAtOrDefault(_imageViewer.CurrentIndex) == entry.Item)
            _imageViewer.ShowImage(path);
        else
        {
            entry.Conv.Add(new ChatItem { Kind = ChatItemKind.System, Text = $"저장했습니다: {path}" });
        }
    }

    private void OpenImageViewer(Conversation conversation, ChatItem selected)
    {
        _imageViewerItems = conversation.Items.Where(i => i.File?.Info.Kind == FileKind.Image).ToList();
        if (_imageViewerItems.Count == 0) return;
        int index = _imageViewerItems.IndexOf(selected);
        if (index < 0) return;
        _imageViewerConversation = conversation;
        if (_imageViewer is null || _imageViewer.IsDisposed)
        {
            _imageViewer = new ImageViewerForm();
            _imageViewer.NavigationRequested += LoadImageViewerItem;
            _imageViewer.FormClosed += (_, _) => { _imageViewer = null; _imageViewerConversation = null; _imageViewerItems.Clear(); };
        }
        if (!_imageViewer.Visible) _imageViewer.Show(this);
        _imageViewer.ShowItem(index, _imageViewerItems.Count, selected.File!.Info.FileName);
        _imageViewer.BringToFront();
        LoadImageViewerItem(index);
    }

    private void LoadImageViewerItem(int index)
    {
        if (_imageViewer is not { IsDisposed: false } viewer || _imageViewerConversation is not { } conversation ||
            index < 0 || index >= _imageViewerItems.Count) return;
        ChatItem item = _imageViewerItems[index];
        if (item.File is not ChatFile file) return;
        viewer.ShowItem(index, _imageViewerItems.Count, file.Info.FileName);
        if (file.HasLocalFile) { viewer.ShowImage(file.LocalPath!); return; }
        string cache = CachePath(file.Info);
        if (File.Exists(cache) && new FileInfo(cache).Length == file.Info.Size)
        {
            file.LocalPath = cache;
            viewer.ShowImage(cache);
            return;
        }
        if (!_connected || file.IsBusy || file.State == FileState.Failed) return;
        if (!_transfers.StartDownload(file.Info.FileId, cache)) return;
        file.State = FileState.Downloading;
        file.Percent = 0;
        conversation.NotifyChanged(item);
    }

    /// <summary>받은 파일 보관 위치: %LocalAppData%\ChatApp\files\파일ID\원래이름</summary>
    private static string CachePath(SharedFile info)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ChatApp", "files", info.FileId, FileRules.SanitizeFileName(info.FileName));

    /// <summary>윈도우에 연결된 프로그램으로 연다(사진 → 사진 앱, 동영상 → 미디어 플레이어). 실행 파일은 한 번 더 묻는다.</summary>
    private static void OpenLocalFile(IWin32Window owner, string path, string fileName)
    {
        if (FileRules.IsExecutable(fileName) &&
            MessageBox.Show(owner, $"'{fileName}'은(는) 실행 파일입니다.\n보낸 사람을 믿을 수 있을 때만 여세요. 열까요?", "실행 파일 열기",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(owner, $"파일을 열 수 없습니다: {ex.Message}\n오른쪽 클릭 → 폴더에서 보기로 직접 열어 보세요.", Theme.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ───────────────────────── 읽음 표시 (카카오톡의 노란 숫자) ─────────────────────────

    /// <summary>
    /// 대화창을 보고 있음 → 서버에 "여기까지 읽음"을 알린다. 서버는 저장하고 방 사람들(1:1이면 상대)에게 ReadUpdate로 알린다.
    /// 기본 방은 인원이 많아 읽음 표시를 하지 않는다.
    /// </summary>
    private void MarkRead(Conversation conv)
    {
        if (!_connected || conv.LatestMessageTime is not DateTime upTo || upTo <= conv.LastMarkedRead) return;
        ReadMark mark;
        if (TryGetRoomId(conv, out int roomId))
        {
            if (roomId == RoomInfo.LobbyId || !_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
            mark = new ReadMark { RoomId = roomId, UpTo = upTo };
            SetMemberRead(room, _me.EmployeeNo, upTo); // 내 화면의 숫자는 서버 응답을 기다리지 않고 바로 줄인다
        }
        else if (conv.Key.StartsWith("dm:"))
        {
            mark = new ReadMark { PeerNo = conv.Key["dm:".Length..], UpTo = upTo };
        }
        else return;

        conv.LastMarkedRead = upTo;
        _client.Send(Packet.Create(PacketType.ReadMark).WithData(mark));
        RecomputeUnread(conv);
    }

    /// <summary>누가 어디까지 읽었다 (방: 멤버 한 명, 1:1: 상대).</summary>
    private void HandleReadUpdate(Packet packet)
    {
        if (packet.GetData<ReadUpdate>() is not { } update) return;
        if (update.RoomId is int roomId)
        {
            if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
            SetMemberRead(room, update.EmployeeNo, update.LastReadAt);
            RecomputeUnread(GetRoomConversation(room));
        }
        else if (update.PeerNo is string peer && _conversations.TryGetValue(Conversation.DirectKey(peer), out Conversation? conv))
        {
            if (conv.PeerLastReadAt is null || update.LastReadAt > conv.PeerLastReadAt) conv.PeerLastReadAt = update.LastReadAt;
            RecomputeUnread(conv);
        }
    }

    private static void SetMemberRead(RoomInfo room, string employeeNo, DateTime upTo)
    {
        RoomMemberInfo? member = room.Members?.FirstOrDefault(m => m.EmployeeNo == employeeNo);
        if (member is not null && (member.LastReadAt is null || upTo > member.LastReadAt)) member.LastReadAt = upTo;
    }

    /// <summary>
    /// 말풍선마다 "안 읽은 사람 수"를 다시 계산한다.
    /// 방: 멤버마다 "여기까지 읽음" 시각 = max(마지막 읽음, 들어온 시각). 그 시각이 메시지보다 앞인 멤버 수 − (보낸 사람이 거기 들어 있으면 1).
    /// 1:1: 내 메시지인데 상대가 읽은 시각보다 뒤면 1.
    /// </summary>
    // [왜] 멤버 시각을 정렬해 두고 이진 탐색으로 센다. 메시지 수 × 멤버 수만큼 비교하면 큰 방에서 느려진다.
    private void RecomputeUnread(Conversation conv)
    {
        bool changed = false;
        void Set(ChatItem item, int count)
        {
            if (item.UnreadCount == count) return;
            item.UnreadCount = count;
            changed = true;
        }

        if (TryGetRoomId(conv, out int roomId))
        {
            if (roomId == RoomInfo.LobbyId || !_joinedRooms.TryGetValue(roomId, out RoomInfo? room) || room.Members is not { Count: > 0 } members)
                return;
            var readUpTo = new Dictionary<string, DateTime>();
            foreach (RoomMemberInfo m in members)
                readUpTo[m.EmployeeNo] = m.LastReadAt is DateTime r && r > m.JoinedAt ? r : m.JoinedAt;
            DateTime[] sorted = readUpTo.Values.Order().ToArray();
            foreach (ChatItem item in conv.Items)
            {
                if (item.IsSystemLike) continue;
                int count = CountBefore(sorted, item.Time);
                if (readUpTo.TryGetValue(item.SenderKey, out DateTime senderRead) && senderRead < item.Time) count--; // 보낸 사람은 읽은 것으로
                Set(item, Math.Max(0, count));
            }
        }
        else if (conv.Key.StartsWith("dm:"))
        {
            DateTime peerRead = conv.PeerLastReadAt ?? DateTime.MinValue;
            foreach (ChatItem item in conv.Items)
                if (!item.IsSystemLike) Set(item, item.Kind == ChatItemKind.Mine && peerRead < item.Time ? 1 : 0);
        }
        if (changed) conv.NotifyReadStateChanged();
    }

    /// <summary>정렬된 배열에서 time보다 앞(작은)인 값의 개수.</summary>
    private static int CountBefore(DateTime[] sorted, DateTime time)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid] < time) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static bool TryGetRoomId(Conversation conv, out int roomId)
    {
        roomId = 0;
        return conv.Key.StartsWith("room:") && int.TryParse(conv.Key["room:".Length..], out roomId);
    }

    // ───────────────────────── 공감·삭제 ─────────────────────────

    /// <summary>방이면 방 대화, 1:1이면 그 상대와의 대화 (없으면 null).</summary>
    private Conversation? FindConversation(int? roomId, string? peerNo)
    {
        if (roomId is int id) return _joinedRooms.TryGetValue(id, out RoomInfo? room) ? GetRoomConversation(room) : null;
        return peerNo is not null && _conversations.TryGetValue(Conversation.DirectKey(peerNo), out Conversation? conv) ? conv : null;
    }

    private void HandleReactionUpdate(Packet packet)
    {
        if (packet.GetData<ReactionUpdate>() is not { } update) return;
        Conversation? conv = FindConversation(update.RoomId, update.PeerNo);
        if (conv?.FindMessage(update.MessageId) is not { } item) return; // 아직 불러오지 않은 옛 메시지
        item.Reactions = update.Reactions;
        conv.NotifyChanged(item);
    }

    private void HandleMessageDeleted(Packet packet)
    {
        if (packet.GetData<MessageRef>() is not { } deleted) return;
        Conversation? conv = FindConversation(deleted.RoomId, deleted.PeerNo);
        if (conv is null) return;
        if (conv.FindMessage(deleted.MessageId) is { } item)
        {
            item.Deleted = true;
            item.Reactions = [];
            conv.NotifyChanged(item);
        }
        // 이 메시지에 단 답장들의 인용도 바꾼다 (지워진 내용이 인용으로 남아 보이지 않게)
        foreach (ChatItem reply in conv.Items.Where(i => i.ReplyTo?.MessageId == deleted.MessageId))
        {
            reply.ReplyTo!.Text = "삭제된 메시지입니다.";
            conv.NotifyChanged(reply);
        }
    }

    private void HandleMessageEdited(Packet packet)
    {
        if (packet.GetData<MessageRef>() is not { } edited) return;
        Conversation? conv = FindConversation(edited.RoomId, edited.PeerNo);
        if (conv?.FindMessage(edited.MessageId) is not { } item || item.Deleted) return;
        item.Text = packet.Body ?? "";
        item.Edited = true;
        conv.NotifyChanged(item);
    }

    private void HandleTypingUpdate(Packet packet)
    {
        if (packet.GetData<TypingInfo>() is not { } typing) return;
        Conversation? conv = FindConversation(typing.RoomId, typing.PeerNo);
        if (conv is null) return;
        ChatWindow? window = OpenWindows().FirstOrDefault(w => w.Conversation == conv);
        window?.SetTypingIndicator(typing.EmployeeNo, typing.DisplayName, typing.IsTyping);
    }

    // ───────────────────────── 접속하지 않은 동안 온 1:1 ─────────────────────────

    /// <summary>로그인 직후 서버가 알려 준 "안 읽은 1:1" 요약 → 동료 목록에 숫자로 보이고, 알림 창으로 알린다.</summary>
    private void HandleUnreadDirect(Packet packet)
    {
        List<UnreadDirect> list = packet.GetData<List<UnreadDirect>>() ?? [];
        if (list.Count == 0) return;
        foreach (UnreadDirect u in list)
        {
            UserInfo peer = KnownPeer(u.PeerNo, u.PeerName);
            Conversation conv = GetConversation(Conversation.DirectKey(u.PeerNo), peer.DisplayName);
            conv.Unread = Math.Max(conv.Unread, u.Count);
        }
        RefreshUserList();

        bool quiet = _suppressUnreadToast;
        _suppressUnreadToast = false;
        if (quiet) return; // 자동 재접속: 숫자만 갱신하고 소리·알림 창은 띄우지 않는다

        int total = list.Sum(u => u.Count);
        UnreadDirect first = list.OrderByDescending(u => u.LastTime).First();
        SetStatus($"● 접속하지 않은 동안 1:1 메시지 {total}개가 왔습니다.", Theme.Notice);
        if (_presence is not (Presence.Busy or Presence.DoNotDisturb)) SystemSounds.Asterisk.Play();
        string title = list.Count == 1 ? first.PeerName : $"{first.PeerName} 외 {list.Count - 1}명";
        ShowToast(title, $"[안 읽은 메시지 {total}개] {first.LastText}", first.PeerNo,
            () => OpenDirect(KnownPeer(first.PeerNo, first.PeerName), activate: true));
    }

    /// <summary>사원번호로 사람 정보 찾기. 모르면 표시 이름("홍길동 대리")으로 만든다.</summary>
    private UserInfo KnownPeer(string employeeNo, string? displayName)
    {
        if (_users.FirstOrDefault(u => u.EmployeeNo == employeeNo) is { } online) return online;
        if (_knownPeers.TryGetValue(employeeNo, out UserInfo? known)) return known;
        string name = displayName?.Trim() is { Length: > 0 } d ? d : employeeNo;
        int space = name.LastIndexOf(' ');
        var peer = new UserInfo
        {
            EmployeeNo = employeeNo,
            Name = space > 0 ? name[..space] : name,
            Position = space > 0 ? name[(space + 1)..] : "",
        };
        _knownPeers[employeeNo] = peer;
        return peer;
    }

    // ───────────────────────── 전체 검색·동료 찾기·로그아웃·업데이트 ─────────────────────────

    private void OpenSearch()
    {
        if (_searchForm is null || _searchForm.IsDisposed)
        {
            _searchForm = new SearchForm(_client);
            _searchForm.HitActivated += OpenSearchHit;
            _searchForm.FormClosed += (_, _) => _searchForm = null;
            _searchForm.Show();
        }
        else
        {
            BringToFront(_searchForm);
        }
    }

    /// <summary>검색 결과를 더블클릭 → 그 대화창을 열고, 불러온 대화 안에 있으면 그 메시지로 이동한다.</summary>
    private void OpenSearchHit(SearchHit hit)
    {
        ChatWindow? window = null;
        if (hit.RoomId is int roomId)
        {
            if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room))
            {
                SetStatus("● 지금은 참여하지 않은 방입니다.", Theme.SubText);
                return;
            }
            OpenRoomWindow(room);
            window = _roomWindows.GetValueOrDefault(roomId);
        }
        else if (hit.PeerNo is string peer)
        {
            window = OpenDirect(KnownPeer(peer, hit.PeerName), activate: true);
        }
        window?.FocusMessage(hit.MessageId);
    }

    /// <summary>접속하지 않은 사람까지 회사 전체에서 골라 1:1 대화 열기.</summary>
    private void FindPeople()
    {
        using var picker = new PeoplePickerForm(_client, "동료 찾기 (접속하지 않은 사람 포함)", multiSelect: false, [_me.EmployeeNo]);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected.Count == 0) return;
        UserInfo user = picker.Selected[0];
        _knownPeers[user.EmployeeNo] = user;
        OpenDirect(user, activate: true);
    }

    /// <summary>로그아웃 → 연결을 끊고 로그인 창으로 돌아간다 (Program이 LogoutRequested를 본다).</summary>
    private void Logout()
    {
        if (MessageBox.Show(this, "로그아웃할까요?\n열려 있는 대화창이 모두 닫히고 로그인 화면으로 돌아갑니다.", Theme.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        LogoutRequested = true;
        _exiting = true;
        Close();
    }

    /// <summary>
    /// 서버가 알려 준 최신 클라이언트 버전과 내 버전 비교. 낮으면 상태 줄·알림 창으로 알리고, 설치 파일 위치가 있으면 메뉴에서 열 수 있게 한다.
    /// </summary>
    // [개념] Assembly 버전: csproj의 <Version>1.3.0</Version>이 빌드할 때 exe에 들어간다.
    private void CheckClientVersion(ServerInfo info)
    {
        if (info.ProtocolVersion > ProtocolInfo.Version)
            SetStatus("● 서버가 더 새 버전입니다. 사내톡을 업데이트하세요.", Theme.Notice);

        if (_updateNotified || string.IsNullOrWhiteSpace(info.LatestClientVersion) ||
            !Version.TryParse(info.LatestClientVersion, out Version? latest))
            return;
        Version mine = typeof(MainForm).Assembly.GetName().Version ?? new Version(0, 0);
        static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
        if (Norm(mine) >= Norm(latest)) return;

        _updateNotified = true;
        _updateUrl = string.IsNullOrWhiteSpace(info.ClientUpdateUrl) ? null : info.ClientUpdateUrl;
        string how = _updateUrl is null ? "관리자에게 설치 파일을 받으세요." : "내 이름을 눌러 메뉴 → \"새 버전 설치\"";
        SetStatus($"● 새 버전 {Norm(latest).ToString(3)}이 있습니다 (지금 {Norm(mine).ToString(3)}). {how}", Theme.Notice);
        if (_updateUrl is not null)
        {
            _statusMenu.Items.Insert(0, new ToolStripSeparator());
            _statusMenu.Items.Insert(0, new ToolStripMenuItem($"새 버전 {Norm(latest).ToString(3)} 설치...", null, (_, _) => OpenUpdate()));
        }
        ShowToast("새 버전이 있습니다", $"사내톡 {Norm(latest).ToString(3)}이 배포되었습니다. {how}", Theme.AppName,
            () => { if (_updateUrl is not null) OpenUpdate(); });
    }

    private void OpenUpdate()
    {
        if (_updateUrl is null) return;
        try
        {
            // 설치 파일(공유 폴더의 exe) 또는 다운로드 웹 페이지를 윈도우에 연결된 프로그램으로 연다
            Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"설치 파일을 열 수 없습니다: {ex.Message}\n위치: {_updateUrl}", Theme.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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

    private int UnreadOf(string key) => _conversations.TryGetValue(key, out Conversation? c) ? c.Unread : 0;

    private void RefreshUserList()
    {
        var others = _users.Where(u => u.EmployeeNo != _me.EmployeeNo).ToList();
        var items = new List<CardItem>();
        foreach (var dept in others.GroupBy(u => u.Department).OrderBy(g => g.Key))
        {
            items.Add(new CardItem { IsHeader = true, Key = $"dept:{dept.Key}", Title = $"{dept.Key} ({dept.Count()})" });
            foreach (UserInfo user in dept.OrderBy(u => u.Name))
            {
                int unread = UnreadOf(Conversation.DirectKey(user.EmployeeNo));
                items.Add(new CardItem
                {
                    Key = $"user:{user.EmployeeNo}",
                    Title = user.DisplayName,
                    Subtitle = UserSubtitle(user),
                    StatusColor = Theme.PresenceColor(user.Presence),
                    AvatarText = user.Name,
                    AvatarColor = Theme.AvatarColor(user.EmployeeNo),
                    ShowStatus = true,
                    Online = true,
                    Badge = unread > 0 ? unread.ToString() : null,
                    BadgeAlert = unread > 0,
                    Tag = user,
                });
            }
        }
        // 접속하지 않은 사람과의 1:1 (안 읽은 메시지가 있거나 이번에 대화한 사람) → 목록 아래에 따로
        var onlineNos = others.Select(u => u.EmployeeNo).ToHashSet();
        var offline = _conversations.Values
            .Where(c => c.Key.StartsWith("dm:") && !onlineNos.Contains(c.Key["dm:".Length..]) && (c.Unread > 0 || c.Items.Count > 0))
            .Select(c => (Conv: c, User: KnownPeer(c.Key["dm:".Length..], c.Title)))
            .OrderByDescending(x => x.Conv.Unread).ThenBy(x => x.User.Name).ToList();
        if (offline.Count > 0)
        {
            items.Add(new CardItem { IsHeader = true, Key = "dept:~offline", Title = $"접속하지 않음 · 대화 중 ({offline.Count})" });
            foreach (var (conv, user) in offline)
            {
                items.Add(new CardItem
                {
                    Key = $"user:{user.EmployeeNo}",
                    Title = user.DisplayName,
                    Subtitle = conv.Unread > 0 ? "안 읽은 메시지가 있어요" : "접속하지 않음 · 메시지는 다음 접속 때 전달",
                    StatusColor = Theme.Offline,
                    AvatarText = user.Name,
                    AvatarColor = Theme.AvatarColor(user.EmployeeNo),
                    ShowStatus = true,
                    Online = false,
                    Badge = conv.Unread > 0 ? conv.Unread.ToString() : null,
                    BadgeAlert = conv.Unread > 0,
                    Tag = user,
                });
            }
        }
        _userList.SetItems(items);
        _usersTab.Text = $"동료 {others.Count}";
        UpdateTrayText();
    }

    private void RefreshRoomList()
    {
        var items = _rooms.Select(room =>
        {
            bool current = _joinedRooms.ContainsKey(room.RoomId); // 참여 중인 방
            int unread = current ? UnreadOf(Conversation.RoomKey(room.RoomId)) : 0;
            string sub = room.IsLobby ? "모두가 함께하는 기본 방" : $"방장 {room.OwnerName}";
            return new CardItem
            {
                Key = $"room:{room.RoomId}",
                Title = room.IsLobby ? $"{room.Name} (기본)" : room.Name,
                Subtitle = current ? $"● 참여 중 · {sub}" : sub,
                AvatarText = "#",
                AvatarColor = Theme.AvatarColor($"room:{room.RoomId}"),
                Badge = unread > 0 ? unread.ToString() : room.CapacityText,
                BadgeAlert = unread > 0,
                Highlight = current,
                Tag = room,
            };
        });
        _roomList.SetItems(items);
        _roomsTab.Text = $"채팅방 {_rooms.Count}";
        UpdateTrayText();
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
