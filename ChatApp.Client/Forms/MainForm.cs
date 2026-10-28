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
    private readonly UserInfo _me;
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
    private readonly HashSet<int> _rejoinRooms = []; // 재접속 후 다시 입장을 요청한 방 (실패해도 팝업 대신 대화창에 알림)

    // 파일 ID → (말풍선, 대화). 진행률 갱신과 내가 보낸 파일의 확인(FileMsg)에 쓴다
    private readonly Dictionary<string, (ChatItem Item, Conversation Conv)> _fileItems = [];

    private int S(int v) => LogicalToDeviceUnits(v);

    public MainForm(ChatClient client)
    {
        _client = client;
        _me = client.Me ?? throw new InvalidOperationException("로그인 정보가 없습니다.");
        _transfers = new FileTransferService(client.Send);
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
        _client.Send(Packet.Create(PacketType.ServerInfoReq)); // 파일 최대 크기 확인
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
        _toast?.Close();
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
            for (int attempt = 1; !IsDisposed && !_connected; attempt++)
            {
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
                _client.Send(Packet.Create(PacketType.LoginReq).WithData(_client.LastLogin!));
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
        _header.Invalidate();
        SetStatus(ConnectedStatusText(), Theme.SubText);
        _createRoomButton.Enabled = true;

        foreach (ChatWindow window in OpenWindows())
            window.Conversation.Add(new ChatItem { Kind = ChatItemKind.System, Text = "다시 연결되었습니다." });

        // 기본 방은 서버가 로그인 때 자동으로 넣어 준다. 나머지 방은 다시 입장을 요청한다.
        // (서버가 이전 연결의 참여를 옮겨 두었으면 "이미 참여 중"으로 바로 성공하고, 서버가 재시작됐다면 방이 사라져 실패할 수 있다)
        _rejoinRooms.Clear();
        foreach (int roomId in _joinedRooms.Keys.Where(id => id != RoomInfo.LobbyId).ToList())
        {
            _rejoinRooms.Add(roomId);
            _client.Send(Packet.Create(PacketType.RoomJoin, target: roomId.ToString()));
        }

        // 서버는 로그인할 때마다 "온라인"으로 시작하므로, 내가 고른 상태(회의 중 등)를 다시 알린다
        if (_presence != Presence.Online)
            _client.Send(Packet.Create(PacketType.StatusUpdate).WithData(new StatusUpdate { Presence = _presence }));

        // [주의] ChatClient는 LoginRes 성공 직후부터 패킷을 보관한다. 여기서 풀어 주지 않으면 이후 패킷이 영영 오지 않는다.
        _client.ReleaseHeld();
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
            case PacketType.ServerInfo:
                if (packet.GetData<ServerInfo>() is { MaxFileSize: > 0 } info) _transfers.MaxFileSize = info.MaxFileSize;
                break;
            case PacketType.SystemMsg: HandleSystem(packet); break;
            case PacketType.RoomRes: HandleRoomResponse(packet); break;
            case PacketType.RoomLeft: HandleRoomLeft(packet); break;

            case PacketType.RoomList:
                _rooms = packet.GetData<List<RoomInfo>>() ?? [];
                foreach (RoomInfo room in _rooms.Where(r => _joinedRooms.ContainsKey(r.RoomId)))
                {
                    _joinedRooms[room.RoomId] = room; // 인원수 등 최신 정보로
                    UpdateRoomWindowHeader(room);
                }
                RefreshRoomList();
                break;

            case PacketType.UserList:
                _users = packet.GetData<List<UserInfo>>() ?? [];
                foreach (var (no, window) in _directWindows)
                {
                    UserInfo? user = _users.FirstOrDefault(u => u.EmployeeNo == no);
                    window.SetOnline(user is not null);
                    if (user is not null) window.SetHeader(user.DisplayName, UserSubtitle(user)); // 상대 상태(회의 중 등)도 보이게
                }
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
        });
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

        // 받은 메시지인데 창이 없으면 자동으로 띄운다(포커스는 빼앗지 않음)
        ChatWindow window = OpenDirect(other, activate: false);
        AddMessage(window.Conversation, window, new ChatItem
        {
            Kind = mine ? ChatItemKind.Mine : ChatItemKind.Other,
            Sender = packet.Sender ?? "",
            SenderKey = packet.SenderId ?? "",
            Text = packet.Body ?? "",
            Time = packet.Timestamp,
        });
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
        if (reading) return;

        // 알림 창 + 소리: 1:1 메시지와, 방에서 나를 부른(@이름) 메시지만. (방 메시지마다 알리면 너무 시끄럽다)
        bool direct = conv.Key.StartsWith("dm:");
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
            _client.LastLogin = new LoginRequest { EmployeeNo = last.EmployeeNo, Password = form.NewPassword };
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
        menu.Items.Add("종료", null, (_, _) => { _exiting = true; Close(); });
        _tray.ContextMenuStrip = menu;
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
        return Icon.FromHandle(bmp.GetHicon());
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
                    if (_roomWindows.TryGetValue(failedId, out ChatWindow? goneWindow)) goneWindow.ShowLeaveButton = false;
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

        // 이미 열려 있던 창이면(재접속) 입력을 다시 연다
        if (_roomWindows.TryGetValue(room.RoomId, out ChatWindow? openWindow))
        {
            openWindow.SetInputEnabled(true);
            UpdateRoomWindowHeader(room);
        }

        // 다른 방에서는 나가지 않는다 (여러 방 동시 참여)
        _joinedRooms[room.RoomId] = room;
        GetRoomConversation(room);
        if (_openRoomOnJoin)
        {
            _openRoomOnJoin = false;
            OpenRoomWindow(room);
        }
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

        _openRoomOnJoin = true;
        var request = new CreateRoomRequest { Name = form.RoomName, MaxUsers = form.MaxUsers };
        if (!_client.Send(Packet.Create(PacketType.RoomCreate).WithData(request)))
            _openRoomOnJoin = false;
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
        window.ShowLeaveButton = !room.IsLobby;
        window.SendRequested += (_, text) =>
            _client.Send(Packet.Create(PacketType.ChatMsg, body: text, target: roomId.ToString()));
        window.LeaveRequested += _ =>
        {
            if (MessageBox.Show(window, $"'{room.Name}' 방에서 나갈까요?", "채팅방 나가기",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                _client.Send(Packet.Create(PacketType.RoomLeave, target: roomId.ToString()));
            // 창은 서버가 RoomLeft로 확인해 주면 닫힌다
        };
        window.FilesRequested += (_, paths) => SendFiles(conv, paths, roomId, null);
        WireWindow(window);
        window.FormClosed += (_, _) =>
        {
            if (_roomWindows.TryGetValue(roomId, out ChatWindow? w) && w == window) _roomWindows.Remove(roomId);
        };

        _roomWindows[roomId] = window;
        UpdateRoomWindowHeader(room);
        window.SetInputEnabled(_connected);
        window.Show();
    }

    private void UpdateRoomWindowHeader(RoomInfo room)
    {
        if (!_roomWindows.TryGetValue(room.RoomId, out ChatWindow? window)) return;
        string owner = room.OwnerName is null ? "모두가 함께하는 기본 방" : $"방장 {room.OwnerName}";
        window.SetHeader(room.Name, $"{room.CapacityText} 참여 중 · {owner}");
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
        window.SetHeader(user.DisplayName, UserSubtitle(user));
        window.SendRequested += (_, text) =>
            _client.Send(Packet.Create(PacketType.Whisper, body: text, target: user.EmployeeNo));
        window.FilesRequested += (_, paths) => SendFiles(conv, paths, null, user.EmployeeNo);
        WireWindow(window);
        window.FormClosed += (_, _) => _directWindows.Remove(user.EmployeeNo);
        _directWindows[user.EmployeeNo] = window;

        window.SetOnline(_connected && _users.Any(u => u.EmployeeNo == user.EmployeeNo));
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
            };
            if (h.File is not null) _fileItems.TryAdd(h.File.FileId, (item, conv)); // 기록 속 파일도 클릭해서 받을 수 있게
            conv.PendingHistory.Add(item);
        }
        if (!batch.Last) return;

        var all = conv.PendingHistory.ToList();
        conv.PendingHistory.Clear();
        conv.InsertHistory(all);
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
                if (file.HasLocalFile) { OpenLocalFile(window, file.LocalPath!, file.Info.FileName); return; }
                if (file.IsBusy || file.State == FileState.Failed) return;

                // 예전에 받아 둔 파일이 있으면 다시 받지 않는다
                string cache = CachePath(file.Info);
                if (File.Exists(cache) && new FileInfo(cache).Length == file.Info.Size)
                {
                    file.LocalPath = cache;
                    window.Conversation.NotifyChanged(item);
                    OpenLocalFile(window, cache, file.Info.FileName);
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
            ChatWindow? owner = OpenWindows().FirstOrDefault(w => w.Conversation == entry.Conv);
            OpenLocalFile(owner ?? (IWin32Window)this, path, file.Info.FileName);
        }
        else
        {
            entry.Conv.Add(new ChatItem { Kind = ChatItemKind.System, Text = $"저장했습니다: {path}" });
        }
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
        window.FileCommandRequested += OnFileCommand;
        window.ReadAll += w =>
        {
            _lastActiveWindow = w;
            if (w.Conversation.Unread == 0) return;
            w.Conversation.Unread = 0;
            RefreshUserList();
            RefreshRoomList();
        };
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

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }
}
