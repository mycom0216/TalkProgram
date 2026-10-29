using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Forms;

/// <summary>
/// 회원가입 화면. 가입에 성공하면 DialogResult.OK로 닫힌다.
/// 이메일은 [아이디] @ [도메인 선택] 형태이고, 도메인 목록은 서버 설정(AllowedEmailDomains)에서 받아 온다.
/// </summary>
public sealed class RegisterForm : Form
{
    /// <summary>도메인 제한이 없을 때 고를 수 있는 자주 쓰는 메일 (목록에 없으면 직접 입력).</summary>
    private static readonly string[] PopularDomains =
        ["naver.com", "gmail.com", "daum.net", "hanmail.net", "nate.com", "kakao.com", "yahoo.com", "outlook.com"];

    private readonly ChatClient _client;

    private readonly TextBox _employeeNo = new() { MaxLength = 20 };
    private readonly TextBox _emailId = new() { MaxLength = 64 };
    private readonly ComboBox _emailDomain = new() { DropDownStyle = ComboBoxStyle.DropDown, MaxLength = 60 };
    private readonly TextBox _name = new() { MaxLength = 30 };
    private readonly TextBox _department = new() { MaxLength = 30 };
    private readonly TextBox _position = new() { MaxLength = 20 };
    private readonly TextBox _phone = new() { MaxLength = 20, PlaceholderText = "선택 · 010-1234-5678" };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, MaxLength = 64 };
    private readonly TextBox _passwordConfirm = new() { UseSystemPasswordChar = true, MaxLength = 64 };
    private readonly Button _submitButton = UiHelper.CreateButton("가입", primary: true);
    private readonly Button _cancelButton = UiHelper.CreateButton("취소");
    private readonly Label _domainHint = new() { AutoSize = true, ForeColor = UiHelper.Muted, Margin = new Padding(0, 0, 0, 12) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = UiHelper.Muted, Margin = new Padding(0, 10, 0, 0) };

    public string RegisteredEmployeeNo { get; private set; } = "";

    public RegisterForm(ChatClient client)
    {
        _client = client;

        Text = $"{Theme.AppName} - 회원가입";
        Font = UiHelper.BaseFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        // 이메일: [아이디] @ [도메인▼]
        _emailId.Width = LogicalToDeviceUnits(120);
        _emailId.Margin = new Padding(0);
        _emailDomain.Width = LogicalToDeviceUnits(120);
        _emailDomain.Margin = new Padding(0);
        var emailRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0),
        };
        emailRow.Controls.Add(_emailId);
        emailRow.Controls.Add(new Label { Text = "@", AutoSize = true, Margin = new Padding(4, 5, 4, 0) });
        emailRow.Controls.Add(_emailDomain);

        var table = UiHelper.CreateFormTable();
        _domainHint.Text = "가입 가능한 이메일을 불러오는 중...";
        UiHelper.AddSpanRow(table, _domainHint);
        UiHelper.AddFieldRow(table, "사원번호", _employeeNo, 260);
        UiHelper.AddFieldRow(table, "이메일", emailRow);
        UiHelper.AddFieldRow(table, "이름", _name, 260);
        UiHelper.AddFieldRow(table, "부서", _department, 260);
        UiHelper.AddFieldRow(table, "직급", _position, 260);
        UiHelper.AddFieldRow(table, "휴대폰 (선택)", _phone, 260);
        UiHelper.AddFieldRow(table, "비밀번호", _password, 260);
        UiHelper.AddFieldRow(table, "비밀번호 확인", _passwordConfirm, 260);
        UiHelper.AddSpanRow(table, new Label
        {
            Text = "사원번호: 영문·숫자 4~20자 / 비밀번호: 영문+숫자 8자 이상",
            ForeColor = UiHelper.Muted,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0),
        });
        UiHelper.AddSpanRow(table, UiHelper.CreateButtonRow(_cancelButton, _submitButton));
        UiHelper.AddSpanRow(table, _status);
        Controls.Add(table);
        UiHelper.AddDialogHeader(this, table, "회원가입");

        AcceptButton = _submitButton;
        CancelButton = _cancelButton;
        _submitButton.Click += OnSubmitClick;
        _cancelButton.Click += (_, _) => Close();
        _emailId.Leave += (_, _) => SplitPastedEmail();

        _client.PacketReceived += OnPacketReceived;
        FormClosed += (_, _) => _client.PacketReceived -= OnPacketReceived;
        Shown += (_, _) =>
        {
            _client.Send(Packet.Create(PacketType.ServerInfoReq)); // 가입 가능한 도메인 요청
            _employeeNo.Focus();
        };
    }

    /// <summary>아이디 칸에 "hong@company.kr"처럼 통째로 붙여 넣으면 아이디/도메인으로 나눈다.</summary>
    private void SplitPastedEmail()
    {
        string text = _emailId.Text.Trim();
        int at = text.IndexOf('@');
        if (at < 0) return;

        string domain = text[(at + 1)..].ToLowerInvariant();
        _emailId.Text = text[..at];

        if (_emailDomain.DropDownStyle == ComboBoxStyle.DropDownList)
        {
            int index = _emailDomain.Items.IndexOf(domain);
            if (index >= 0) _emailDomain.SelectedIndex = index;
            else ShowStatus($"@{domain}은(는) 가입할 수 없는 도메인입니다. 목록에서 고르세요.", error: true);
        }
        else
        {
            _emailDomain.Text = domain;
        }
    }

    private void OnSubmitClick(object? sender, EventArgs e)
    {
        SplitPastedEmail();

        // 빈칸·비밀번호 확인은 화면에서 먼저 거르고, 형식 검사는 서버가 최종 판단한다
        Control[] required = [_employeeNo, _emailId, _emailDomain, _name, _department, _position, _password];
        Control? empty = required.FirstOrDefault(c => c.Text.Trim().Length == 0);
        if (empty is not null)
        {
            ShowStatus("모든 항목을 입력하세요.", error: true);
            empty.Focus();
            return;
        }
        if (_password.Text != _passwordConfirm.Text)
        {
            ShowStatus("비밀번호가 일치하지 않습니다.", error: true);
            _passwordConfirm.SelectAll();
            _passwordConfirm.Focus();
            return;
        }

        // 휴대폰 번호는 선택. 적었으면 형식만 먼저 확인한다 (친구가 번호로 나를 찾을 때만 쓰고 공개하지 않음)
        if (_phone.Text.Trim().Length > 0 && PhoneRules.Normalize(_phone.Text) is null)
        {
            ShowStatus("휴대폰 번호 형식이 올바르지 않습니다. (예: 010-1234-5678)", error: true);
            _phone.Focus();
            return;
        }

        var request = new RegisterRequest
        {
            ProtocolVersion = ProtocolInfo.Version,
            EmployeeNo = _employeeNo.Text.Trim(),
            Email = $"{_emailId.Text.Trim()}@{_emailDomain.Text.Trim().TrimStart('@')}",
            Name = _name.Text.Trim(),
            Department = _department.Text.Trim(),
            Position = _position.Text.Trim(),
            Password = _password.Text,
            Phone = _phone.Text.Trim().Length > 0 ? _phone.Text.Trim() : null,
        };

        _submitButton.Enabled = false;
        ShowStatus("가입 요청 중...", error: false);
        if (!_client.Send(Packet.Create(PacketType.RegisterReq).WithData(request)))
        {
            _submitButton.Enabled = true;
            ShowStatus("서버와 연결이 끊어졌습니다.", error: true);
        }
    }

    // ── 수신 스레드에서 호출됨 → UI 스레드로 넘긴다 ──
    private void OnPacketReceived(Packet packet)
    {
        if (packet.Type == PacketType.ServerInfo)
            UiHelper.RunOnUi(this, () => ApplyServerInfo(packet.GetData<ServerInfo>()));
        else if (packet.Type == PacketType.RegisterRes)
            UiHelper.RunOnUi(this, () => HandleRegisterResponse(packet));
    }

    /// <summary>서버가 알려 준 도메인 목록으로 선택 상자를 채운다.</summary>
    private void ApplyServerInfo(ServerInfo? info)
    {
        if (info is null) return;

        _emailDomain.Items.Clear();
        if (info.AnyEmailDomain)
        {
            // 제한 없음: 자주 쓰는 메일 중에서 고르거나 직접 입력
            _emailDomain.DropDownStyle = ComboBoxStyle.DropDown;
            _emailDomain.Items.AddRange(PopularDomains);
            _emailDomain.SelectedIndex = 0;
            _domainHint.Text = "개인 이메일(네이버, 지메일, 다음 등)로 가입할 수 있습니다.\n목록에 없는 메일은 도메인을 직접 입력하세요.";
        }
        else
        {
            // 허용 목록 중에서만 고르게 한다 (오타로 인한 가입 실패 방지)
            _emailDomain.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (string domain in info.AllowedEmailDomains) _emailDomain.Items.Add(domain);
            if (_emailDomain.Items.Count > 0) _emailDomain.SelectedIndex = 0;
            _domainHint.Text = $"사내 이메일({info.DomainsText})로만 가입할 수 있습니다.";
        }

        if (!info.AutoApprove)
            _domainHint.Text += "\n가입 후 관리자 승인이 끝나야 로그인할 수 있습니다.";
    }

    private void HandleRegisterResponse(Packet packet)
    {
        _submitButton.Enabled = true;
        if (!packet.Success)
        {
            ShowStatus(packet.Body ?? "회원가입에 실패했습니다.", error: true);
            return;
        }

        RegisteredEmployeeNo = _employeeNo.Text.Trim();
        MessageBox.Show(this, packet.Body ?? "회원가입이 완료되었습니다.", "회원가입", MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowStatus(string message, bool error)
    {
        _status.Text = message;
        _status.ForeColor = error ? UiHelper.Danger : UiHelper.Muted;
    }
}
