using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Forms;

//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   회원가입 창. 입력 → 화면에서 기본 확인(빈칸·비밀번호 일치·휴대폰 형식) → RegisterReq 전송 → 응답(RegisterRes)이 오면 성공 시 OK로 닫는다. 형식·중복·도메인 허용 여부의 최종 판단은 서버(AuthService)가 한다.
//
// ■ 흐름
//   ① 창이 보이면(Shown) ServerInfoReq로 "가입 가능한 이메일 도메인"과 "가입 즉시 승인 여부"를 서버에서 받아 온다(ApplyServerInfo)
//   ② 도메인 제한이 있으면 목록에서만 고르게(DropDownList, 오타 방지), 제한이 없으면 자주 쓰는 도메인 + 직접 입력(DropDown)
//   ③ 제출(OnSubmitClick): SplitPastedEmail(통째로 붙여 넣은 이메일 분리) → 빈칸 → 비밀번호 확인 → 휴대폰 형식 → 요청 전송, 그동안 가입 버튼은 꺼 둔다
//   ④ 응답은 수신 스레드로 오므로 RunOnUi로 UI 스레드에서 처리. 성공하면 RegisteredEmployeeNo를 저장해 로그인 창이 사원번호를 채워 주게 한다
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 화면 검증은 편의, 서버 검증이 최종 ......... "client-side vs server-side validation"
//   - 서버가 알려 주는 설정으로 화면 구성 ......... "server driven UI configuration"
//   - ComboBox DropDown / DropDownList 차이 ........ "ComboBox DropDownStyle DropDown vs DropDownList"
//   - 중복 클릭 방지(버튼 비활성화) ................ "disable button prevent double submit"
//   - 입력 길이 제한(MaxLength)과 서버 길이 검사 ... "MaxLength textbox client limit not security"
//
// ■ 면접 예상 질문과 답
//   Q1. 입력 검사를 화면에서 하는데 서버에서도 하나요?
//       A. 네. 화면은 빈칸·비밀번호 일치처럼 빠르게 알려 줄 수 있는 것만 검사하고, 사원번호·이메일·비밀번호 규칙은 서버가 다시 검사합니다. 화면 검사는 사용자 편의이고, 요청을 직접 만들어 보내는 경우까지 막는 것은 서버입니다.
//   Q2. 이메일 도메인 목록은 왜 서버에서 받아 오나요?
//       A. 회사마다 허용 도메인이 다르고 서버 설정(AllowedEmailDomains)으로 바뀌기 때문입니다. 클라이언트에 목록을 박아 두면 설정이 바뀔 때마다 다시 배포해야 합니다. 같은 이유로 "가입 즉시 승인인지, 관리자 승인이 필요한지"도 서버가 알려 주고 안내 문구를 바꿉니다.
//   Q3. 비밀번호가 화면에서 어떻게 다뤄지나요?
//       A. 입력칸은 UseSystemPasswordChar로 가려서 보이게 하고, 요청 객체에만 담아 전송합니다. 파일에는 저장하지 않습니다. (전송 구간은 TLS로 보호하는 것이 전제입니다 — LoginForm이 TLS를 끄면 경고합니다.)
//   Q4. 이메일을 통째로 붙여 넣으면요?
//       A. SplitPastedEmail이 "@" 앞은 아이디 칸에, 뒤는 도메인 칸에 나눠 넣습니다. 도메인 목록이 제한되어 있는데 목록에 없으면 가입할 수 없다고 알려 줍니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - 화면의 안내 문구("영문·숫자 4~20자 / 영문+숫자 8자 이상")는 서버 규칙을 손으로 옮겨 적은 것이다. 서버 규칙이 바뀌면 문구도 함께 고쳐야 한다.
//   - ServerInfoReq의 응답이 늦거나 오지 않으면 도메인 칸이 비어 있어 제출할 수 없다("불러오는 중..." 안내만 남음). 재요청 버튼은 없다.
//
// ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// 회원가입 화면. 가입에 성공하면 DialogResult.OK로 닫힌다.
/// 이메일은 [아이디] @ [도메인 선택] 형태이고, 도메인 목록은 서버 설정(AllowedEmailDomains)에서 받아 온다.
/// </summary>
public sealed class RegisterForm : Form
{
    /// <summary>도메인 제한이 없을 때 고를 수 있는 자주 쓰는 메일 (목록에 없으면 직접 입력).</summary>
    // [왜] AnyEmailDomain(제한 없음, 테스트용) 서버일 때 보여 줄 "자주 쓰는 메일" 후보. 목록에 없으면 사용자가 직접 입력할 수 있다.
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

    /// <summary>가입에 성공한 사원번호 (가입 전에는 빈 문자열). 로그인 창이 사원번호 칸을 채우는 데 쓴다.</summary>
    // [왜] private set: 바깥(LoginForm)은 읽기만, 값은 가입 성공 때 이 창만 채운다. 로그인 창이 사원번호 입력칸을 자동으로 채우는 데 쓴다.
    public string RegisteredEmployeeNo { get; private set; } = "";

    /// <summary>회원가입 창을 만든다. client로 가입 요청을 서버에 보낸다.</summary>
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
        // [읽는 법] 아이디 칸을 떠날 때(Leave) 한 번 나눠 본다. 제출 직전에도 한 번 더 호출해서 "붙여 넣고 바로 가입"도 처리한다.
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
        // [읽는 법] 필수 칸을 배열로 모아 FirstOrDefault로 "첫 번째 빈 칸"을 찾아 그 칸에 포커스를 준다(휴대폰만 선택 항목이라 배열에서 빠져 있다).
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
        // [개념] PhoneRules는 Common에 있어 클라이언트와 서버가 같은 규칙으로 번호를 정리·검사한다(한쪽만 고쳐서 어긋나는 일을 막는다).
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
            // [왜] 도메인 칸에 사용자가 "@"를 붙여 넣어도 "@@"가 되지 않게 앞의 @를 떼고 합친다.
            Email = $"{_emailId.Text.Trim()}@{_emailDomain.Text.Trim().TrimStart('@')}",
            Name = _name.Text.Trim(),
            Department = _department.Text.Trim(),
            Position = _position.Text.Trim(),
            Password = _password.Text,
            Phone = _phone.Text.Trim().Length > 0 ? _phone.Text.Trim() : null,
        };

        // [왜] 응답이 올 때까지 가입 버튼을 막아 같은 요청을 두 번 보내지 않게 한다. 응답(HandleRegisterResponse)이나 전송 실패에서 다시 켠다.
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

        // [읽는 법] 가입 즉시 승인이 아니면(관리자 승인 필요) 가입 후 바로 로그인할 수 없다는 점을 미리 알려 준다 → 로그인 화면에서의 혼란을 줄인다.
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
