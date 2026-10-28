using System.Net.Sockets;
using System.Security.Authentication;
using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Security;

namespace ChatApp.Client.Forms;

/// <summary>
/// 로그인 화면 (메신저 스타일).
/// ┌ 하늘색 그라데이션: 말풍선 로고 · 사내톡 ┐
/// │ 사원번호 / 비밀번호                     │
/// │ □ 사원번호 기억하기                      │
/// │ [        로그인        ]                │
/// │ 회원가입 · 서버 설정 ▾  (누르면 서버 주소·포트 펼침)
/// 로그인에 성공하면 DialogResult.OK로 닫히고, ChatClient.Me에 내 정보가 들어 있다.
/// </summary>
public sealed class LoginForm : Form
{
    private readonly ChatClient _client;
    private readonly ClientSettings _settings;

    private readonly TextBox _employeeNo = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly TextBox _host = new();
    private readonly TextBox _port = new();
    private readonly CheckBox _remember = new() { Text = "사원번호 기억하기", AutoSize = true };
    private readonly CheckBox _autoLogin = new() { Text = "자동 로그인 (이 기기에서)", AutoSize = true };
    private readonly CheckBox _useTls = new() { Text = "보안 연결(TLS) 사용", AutoSize = true };
    private readonly Button _loginButton = new();
    private readonly LinkLabel _registerLink = new() { Text = "회원가입", AutoSize = true };
    private readonly LinkLabel _serverLink = new() { Text = "서버 설정 ▾", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true };
    private readonly TableLayoutPanel _serverPanel = new();

    private int S(int v) => LogicalToDeviceUnits(v);

    // [왜] 입력칸마다 new Font를 만들면 창을 닫아도 GDI 글꼴이 남는다(로그아웃·재로그인 때마다 쌓임). 하나를 함께 쓴다.
    private static readonly Font InputFont = new(Theme.Body.FontFamily, 11F);

    public LoginForm(ChatClient client, ClientSettings settings)
    {
        _client = client;
        _settings = settings;

        Text = $"{Theme.AppName} - 로그인";
        Font = Theme.Body;
        BackColor = Theme.Surface;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        BuildLayout();

        _host.Text = settings.Host;
        _port.Text = settings.Port.ToString();
        _remember.Checked = settings.RememberEmployeeNo;
        _useTls.Checked = settings.UseTls;
        if (settings.RememberEmployeeNo) _employeeNo.Text = settings.LastEmployeeNo;
        _autoLogin.Checked = settings.GetLoginToken(settings.Host, settings.Port, settings.LastEmployeeNo) is not null;

        AcceptButton = _loginButton; // Enter = 로그인
        _loginButton.Click += OnLoginClick;
        _registerLink.LinkClicked += (_, _) => OnRegisterClick();
        _serverLink.LinkClicked += (_, _) => ToggleServerPanel();

        _client.PacketReceived += OnPacketReceived;
        _client.Disconnected += OnDisconnected;
        FormClosed += (_, _) =>
        {
            _client.PacketReceived -= OnPacketReceived;
            _client.Disconnected -= OnDisconnected;
        };
        Shown += (_, _) =>
        {
            if (_autoLogin.Checked && _employeeNo.TextLength > 0) BeginInvoke(new Action(() => OnLoginClick(this, EventArgs.Empty)));
            else (_employeeNo.Text.Length == 0 ? _employeeNo : _password).Focus();
        };
    }

    // ───────── 화면 구성 ─────────

    private void BuildLayout()
    {
        _employeeNo.Name = "EmployeeNo";
        _password.Name = "Password";
        _host.Name = "ServerHost";
        _port.Name = "ServerPort";
        _remember.Name = "RememberEmployeeNo";
        _autoLogin.Name = "AutoLogin";
        _useTls.Name = "UseTls";
        _loginButton.Name = "LoginButton";
        _status.Name = "LoginStatus";
        int width = S(340);
        int fieldWidth = width - S(64);

        var header = new GradientPanel { Location = Point.Empty, Size = new Size(width, S(180)) };
        header.Paint += PaintHeader;

        var body = new TableLayoutPanel
        {
            Location = new Point(0, header.Height),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(S(32), S(22), S(32), S(18)),
            MinimumSize = new Size(width, 0),
            MaximumSize = new Size(width, 0),
            BackColor = Theme.Surface,
        };

        AddCaption(body, "사원번호");
        AddInput(body, _employeeNo, fieldWidth);
        AddCaption(body, "비밀번호");
        AddInput(body, _password, fieldWidth);

        _remember.ForeColor = Theme.SubText;
        _remember.Font = Theme.Small;
        _remember.Margin = new Padding(0, S(8), 0, 0);
        body.Controls.Add(_remember);
        _autoLogin.ForeColor = Theme.SubText;
        _autoLogin.Font = Theme.Small;
        _autoLogin.Margin = new Padding(0, S(5), 0, 0);
        body.Controls.Add(_autoLogin);

        _loginButton.Text = "로그인";
        _loginButton.FlatStyle = FlatStyle.Flat;
        _loginButton.FlatAppearance.BorderSize = 0;
        _loginButton.FlatAppearance.MouseOverBackColor = Theme.SkyDark;
        _loginButton.BackColor = Theme.Primary;
        _loginButton.ForeColor = Color.White;
        _loginButton.Font = Theme.BodyBold;
        _loginButton.Size = new Size(fieldWidth, S(44));
        _loginButton.Margin = new Padding(0, S(14), 0, 0);
        _loginButton.Cursor = Cursors.Hand;
        body.Controls.Add(_loginButton);

        _status.Font = Theme.Small;
        _status.MaximumSize = new Size(fieldWidth, 0); // 긴 안내는 줄바꿈
        _status.Margin = new Padding(0, S(8), 0, 0);
        body.Controls.Add(_status);

        // 회원가입 · 서버 설정
        foreach (LinkLabel link in new[] { _registerLink, _serverLink })
        {
            link.LinkColor = Theme.Primary;
            link.ActiveLinkColor = Theme.SkyDark;
            link.LinkBehavior = LinkBehavior.HoverUnderline;
            link.Font = Theme.Small;
            link.Margin = new Padding(0);
        }
        var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, S(10), 0, 0), WrapContents = false };
        links.Controls.Add(_registerLink);
        links.Controls.Add(new Label { Text = "·", AutoSize = true, ForeColor = Theme.SubText, Font = Theme.Small, Margin = new Padding(S(6), 0, S(6), 0) });
        links.Controls.Add(_serverLink);
        body.Controls.Add(links);

        // 서버 주소·포트 (기본은 접혀 있음)
        _serverPanel.AutoSize = true;
        _serverPanel.ColumnCount = 2;
        _serverPanel.Margin = new Padding(0, S(10), 0, 0);
        _serverPanel.Visible = false;
        AddServerRow("서버 주소", _host, fieldWidth - S(70));
        AddServerRow("포트", _port, S(80));
        _useTls.Font = Theme.Small;
        _useTls.ForeColor = Theme.SubText;
        _useTls.Margin = new Padding(0, S(4), 0, 0);
        _serverPanel.Controls.Add(_useTls);
        _serverPanel.SetColumnSpan(_useTls, 2); // 두 칸을 합쳐 한 줄 전체에
        body.Controls.Add(_serverPanel);

        Controls.Add(header);
        Controls.Add(body);
    }

    private void AddCaption(TableLayoutPanel body, string text)
        => body.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = Theme.SubText,
            Font = Theme.SmallBold,
            Margin = new Padding(0, S(6), 0, S(3)),
        });

    private void AddInput(TableLayoutPanel body, TextBox box, int width)
    {
        box.Font = InputFont;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Width = width;
        box.Margin = new Padding(0);
        body.Controls.Add(box);
    }

    private void AddServerRow(string label, TextBox box, int width)
    {
        _serverPanel.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = Theme.SubText,
            Font = Theme.Small,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, S(3), S(8), S(3)),
        });
        box.Width = width;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Margin = new Padding(0, S(3), 0, S(3));
        _serverPanel.Controls.Add(box);
    }

    private void PaintHeader(object? sender, PaintEventArgs e)
    {
        if (sender is not Control header) return;
        Graphics g = e.Graphics;

        int logo = S(70);
        Theme.DrawLogo(g, new Rectangle((header.Width - logo) / 2, S(26), logo, logo));

        var center = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, Theme.AppName, Theme.Big, new Rectangle(0, S(102), header.Width, S(40)), Color.White, center);
        TextRenderer.DrawText(g, "우리 회사 메신저", Theme.Small, new Rectangle(0, S(146), header.Width, S(20)), Theme.HeaderSubText, center);
    }

    private void ToggleServerPanel()
    {
        _serverPanel.Visible = !_serverPanel.Visible;
        _serverLink.Text = _serverPanel.Visible ? "서버 설정 ▴" : "서버 설정 ▾";
        if (_serverPanel.Visible) _host.Focus();
    }

    // ───────── 로그인 / 회원가입 ─────────

    private async void OnLoginClick(object? sender, EventArgs e)
    {
        string no = _employeeNo.Text.Trim();
        if (no.Length == 0)
        {
            ShowStatus("사원번호를 입력하세요.", error: true);
            return;
        }
        int.TryParse(_port.Text.Trim(), out int selectedPort);
        string host = _host.Text.Trim();
        string? rememberedToken = _autoLogin.Checked ? _settings.GetLoginToken(host, selectedPort, no) : null;
        bool usingToken = !string.IsNullOrEmpty(rememberedToken);
        if (!usingToken && _password.Text.Length == 0) { ShowStatus("비밀번호를 입력하거나 자동 로그인을 선택하세요.", error: true); return; }

        // 암호화하지 않고 접속하면 비밀번호가 네트워크에 그대로 보인다 → 한 번 더 확인 (같은 PC 테스트 서버는 묻지 않음)
        if (!_useTls.Checked && !IsLocalHost(host) &&
            MessageBox.Show(this,
                "보안 연결(TLS)을 끄고 접속하면 비밀번호와 대화 내용이 암호화되지 않고 전송됩니다.\n" +
                "같은 네트워크의 누군가가 엿볼 수 있습니다. 테스트 서버가 아니라면 TLS를 켜세요.\n\n그래도 암호화 없이 로그인할까요?",
                "암호화되지 않은 연결", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        SetBusy(true, "로그인 중...");
        if (!await EnsureConnectedAsync())
        {
            SetBusy(false);
            return;
        }

        Packet loginPacket;
        if (usingToken)
        {
            if (!_useTls.Checked) { SetBusy(false); ShowStatus("자동 로그인 토큰을 보호하려면 TLS 보안 연결을 켜세요.", error: true); return; }
            _pendingLogin = null;
            _tokenLoginPending = true;
            loginPacket = Packet.Create(PacketType.TokenLoginReq).WithData(new TokenLoginRequest { EmployeeNo = no, Token = rememberedToken!, DeviceId = _settings.DeviceId, ProtocolVersion = ProtocolInfo.Version });
        }
        else
        {
            _tokenLoginPending = false;
            _client.LastTokenLogin = null;
            _pendingLogin = new LoginRequest { EmployeeNo = no, Password = _password.Text, DeviceId = _settings.DeviceId, ProtocolVersion = ProtocolInfo.Version };
            loginPacket = Packet.Create(PacketType.LoginReq).WithData(_pendingLogin);
        }
        if (!_client.Send(loginPacket))
        {
            SetBusy(false);
            ShowStatus("서버와 연결이 끊어졌습니다. 다시 시도하세요.", error: true);
        }
        // 응답(LoginRes)은 수신 스레드 → OnPacketReceived에서 처리
    }

    private async void OnRegisterClick()
    {
        SetBusy(true, "서버에 연결 중...");
        bool connected = await EnsureConnectedAsync();
        SetBusy(false);
        if (!connected) return;
        ShowStatus("", error: false);

        using var form = new RegisterForm(_client);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            _employeeNo.Text = form.RegisteredEmployeeNo;
            _password.Clear();
            _password.Focus();
            ShowStatus("회원가입이 완료되었습니다. 로그인하세요.", error: false);
        }
    }

    /// <summary>아직 연결 전이면 입력한 서버로 연결한다.</summary>
    private async Task<bool> EnsureConnectedAsync()
    {
        if (_client.IsConnected) return true;

        string host = _host.Text.Trim();
        if (host.Length == 0 || !int.TryParse(_port.Text.Trim(), out int port) || port is < 1 or > 65535)
        {
            if (!_serverPanel.Visible) ToggleServerPanel();
            ShowStatus("서버 주소와 포트를 확인하세요.", error: true);
            return false;
        }

        _client.UseTls = _useTls.Checked;
        _client.PinnedFingerprint = _settings.GetTrustedFingerprint(host, port);
        try
        {
            try
            {
                await _client.ConnectAsync(host, port);
            }
            catch (UntrustedCertificateException ex)
            {
                // 처음 보는(또는 바뀐) 서버 인증서 → 지문을 보여 주고 사용자가 신뢰하면 저장한 뒤 "한 번만" 다시 연결한다
                if (!ConfirmCertificate(host, port, ex))
                {
                    ShowStatus("서버 인증서를 신뢰하지 않아 연결하지 않았습니다.", error: true);
                    return false;
                }
                _settings.SetTrustedFingerprint(host, port, ex.Fingerprint);
                _client.PinnedFingerprint = ex.Fingerprint;
                await _client.ConnectAsync(host, port);
            }
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            if (!_serverPanel.Visible) ToggleServerPanel(); // 주소를 고칠 수 있게 펼쳐 준다
            ShowStatus($"서버({host}:{port})에 연결할 수 없습니다. 서버가 켜져 있는지, 주소가 맞는지 확인하세요.", error: true);
            return false;
        }
        catch (AuthenticationException ex) // UntrustedCertificateException(다시 연결했는데 또 다른 인증서)도 여기로 온다
        {
            if (!_serverPanel.Visible) ToggleServerPanel(); // 보안 연결 체크를 고칠 수 있게
            ShowStatus(ex.Message, error: true);
            return false;
        }

        _settings.Host = host;
        _settings.Port = port;
        // [왜] TLS를 끈 상태는 저장하지 않는다. 테스트 때 한 번 끈 것이 계속 남아 실제 서버에도 평문으로 붙는 사고를 막는다.
        _settings.UseTls = true;
        _settings.Save();
        return true;
    }

    /// <summary>
    /// 서버 인증서 지문을 보여 주고 신뢰할지 묻는다. (SSH로 처음 접속할 때 "이 호스트를 믿겠습니까?"와 같은 단계)
    /// </summary>
    // [왜] 자체 서명 인증서는 운영체제가 진짜인지 판단해 주지 못한다. 그래서 "서버 콘솔에 찍힌 지문"과
    //      "내 화면에 보이는 지문"이 같은지 사람이 한 번 확인한다. 같으면 중간에 가짜 서버가 끼어 있지 않다는 뜻이다.
    //      한 번 신뢰하면 저장해 두고, 다음부터는 자동으로 비교한다(다르면 연결 거부).
    private bool ConfirmCertificate(string host, int port, UntrustedCertificateException ex)
    {
        string fingerprint = TlsHelper.Format(ex.Fingerprint);
        if (ex.Changed)
        {
            // [주의] 예전에 믿었던 지문과 다르다 → 가장 위험한 경우. 기본 버튼을 "아니요"로 두어 Enter로 실수로 넘어가지 않게 한다.
            return MessageBox.Show(this,
                $"경고: {host}:{port} 서버의 보안 인증서가 이전과 다릅니다!\n\n" +
                "관리자가 서버 인증서를 새로 만든 경우가 아니라면, 누군가 중간에서 연결을 가로채고 있을 수 있습니다.\n\n" +
                $"새 인증서: {ex.Subject}\nSHA-256 지문:\n{fingerprint}\n\n" +
                "관리자에게 서버 콘솔의 지문(/tls 명령)과 같은지 확인했습니까? 새 인증서를 신뢰할까요?",
                Theme.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        return MessageBox.Show(this,
            $"{host}:{port} 서버에 처음 접속합니다.\n\n" +
            $"서버 인증서: {ex.Subject}\nSHA-256 지문:\n{fingerprint}\n\n" +
            "이 지문이 서버 콘솔(또는 관리자가 알려 준 값)과 같은지 확인하세요.\n이 서버를 신뢰하고 연결할까요?",
            Theme.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    // ── 수신 스레드에서 호출됨 → UI 스레드로 넘긴다 ──
    // 보낸 로그인 요청 (성공하면 ChatClient.LastLogin에 넣어 자동 재접속에 쓴다)
    private LoginRequest? _pendingLogin;
    private bool _tokenLoginPending;

    private static bool IsLocalHost(string host)
        => host is "localhost" or "127.0.0.1" or "::1";

    /// <summary>
    /// 로그인을 기다리는 중에 연결이 끊김(서버가 옛 버전이라 끊었거나, 서버가 꺼짐) → "로그인 중..."에 멈춰 있지 않게 풀어 준다.
    /// </summary>
    private void OnDisconnected(string reason) => UiHelper.RunOnUi(this, () =>
    {
        if (_loginButton.Enabled) return; // 기다리는 중이 아니면 다음 로그인 때 다시 연결하면 된다
        SetBusy(false);
        ShowStatus(reason, error: true);
    });

    private void OnPacketReceived(Packet packet)
    {
        if (packet.Type != PacketType.LoginRes) return;
        UiHelper.RunOnUi(this, () => HandleLoginResponse(packet));
    }

    private void HandleLoginResponse(Packet packet)
    {
        SetBusy(false);
        if (!packet.Success)
        {
            if (_tokenLoginPending)
            {
                _settings.SetLoginToken(_host.Text, int.TryParse(_port.Text, out int p) ? p : _settings.Port, _employeeNo.Text, null);
                _settings.Save();
                _autoLogin.Checked = false;
                _tokenLoginPending = false;
                ShowStatus(packet.Body ?? "자동 로그인이 만료되었습니다. 비밀번호를 입력하세요.", error: true);
                _password.Focus();
                return;
            }
            ShowStatus(packet.Body ?? "로그인에 실패했습니다.", error: true);
            _password.SelectAll();
            _password.Focus();
            return;
        }

        _client.Me = packet.GetData<UserInfo>();
        _client.LastLogin = _pendingLogin; // 자동 재접속 때 다시 로그인하려고 (메모리에만)
        int.TryParse(_port.Text, out int loginPort);
        if (_autoLogin.Checked && _client.Me is { MustChangePassword: false } && !string.IsNullOrEmpty(packet.Target))
        {
            _settings.SetLoginToken(_host.Text, loginPort, _client.Me.EmployeeNo, packet.Target);
            _client.LastTokenLogin = new TokenLoginRequest { EmployeeNo = _client.Me.EmployeeNo, Token = packet.Target, DeviceId = _settings.DeviceId, ProtocolVersion = ProtocolInfo.Version };
        }
        else if (!_autoLogin.Checked)
            _settings.SetLoginToken(_host.Text, loginPort, _employeeNo.Text, null);
        _settings.RememberEmployeeNo = _remember.Checked || _autoLogin.Checked;
        _settings.LastEmployeeNo = _settings.RememberEmployeeNo ? _employeeNo.Text.Trim() : "";
        _settings.Save();
        _tokenLoginPending = false;

        // 관리자가 초기화한 임시 비밀번호로 로그인함 → 새 비밀번호로 바꿔야 채팅 화면으로 넘어간다
        if (_client.Me is { MustChangePassword: true } && _pendingLogin is not null)
        {
            using var change = new ChangePasswordForm(_client, currentPassword: _pendingLogin.Password);
            if (change.ShowDialog(this) != DialogResult.OK)
            {
                _client.Disconnect(); // 바꾸지 않으면 로그인하지 않은 것으로 (서버도 이 상태에선 채팅을 막는다)
                _client.Me = null;
                _password.Clear();
                ShowStatus("비밀번호를 바꿔야 로그인할 수 있습니다.", error: true);
                return;
            }
            // 자동 재접속은 새 비밀번호로 해야 한다
            _client.LastLogin = new LoginRequest { EmployeeNo = _pendingLogin.EmployeeNo, Password = change.NewPassword, ProtocolVersion = ProtocolInfo.Version };
            _client.Me.MustChangePassword = false;
        }

        _settings.RememberEmployeeNo = _remember.Checked;
        _settings.LastEmployeeNo = _remember.Checked ? _employeeNo.Text.Trim() : "";
        _settings.Save();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _loginButton.Enabled = !busy;
        _loginButton.BackColor = busy ? Theme.Offline : Theme.Primary;
        _registerLink.Enabled = !busy;
        UseWaitCursor = busy;
        if (message is not null) ShowStatus(message, error: false);
    }

    private void ShowStatus(string message, bool error)
    {
        _status.Text = message;
        _status.ForeColor = error ? Theme.Danger : Theme.SubText;
    }
}
