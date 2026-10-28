using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] AdminForm.cs - 관리자 창 (관리자만 메인 창 메뉴에서 열 수 있음)
// - [회원] 탭: 회원 목록(상태별 보기, 검색) + 승인/거절/정지/퇴사/사용 재개/잠금 해제/비밀번호 초기화/강제 로그아웃/관리자 지정·해제
// - [감사 로그] 탭: 누가 언제 무엇을 했는지 검색 (사원번호·동작으로 거르기)
// - 스레드: 화면은 UI 스레드. 서버 응답(AdminUserList 등)은 수신 스레드에서 와서 RunOnUi로 넘긴다.
// [주의] 메뉴를 관리자에게만 보여 주는 것은 "편의"다. 실제 권한 확인은 서버(PacketHandler.HandleAdmin)가 매번 한다.
//        누가 클라이언트를 고쳐 이 창을 띄워도 서버가 거절하고 감사 로그에 남긴다.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class AdminForm : Form
{
    private readonly ChatClient _client;
    private readonly string _myNo;

    // 회원 탭
    private readonly ComboBox _statusFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly TextBox _search = new() { Width = 180, PlaceholderText = "이름·사원번호·부서 검색" };
    private readonly ListView _users = CreateList();
    private readonly Label _userCount = new() { AutoSize = true, ForeColor = UiHelper.Muted, Margin = new Padding(8, 8, 0, 0) };
    private readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
    private List<AdminUserInfo> _allUsers = [];

    // 감사 로그 탭
    private readonly TextBox _auditNo = new() { Width = 120, PlaceholderText = "사원번호" };
    private readonly ComboBox _auditAction = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox _auditLimit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    private readonly ListView _audit = CreateList();

    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };

    // 상태 필터 (화면 글자, 서버에 보낼 값)
    private static readonly (string Text, string? Value)[] Filters =
        [("전체", null), ("승인 대기", "PENDING"), ("사용 중", "ACTIVE"), ("정지", "SUSPENDED"), ("퇴사", "RESIGNED")];

    public AdminForm(ChatClient client, string myEmployeeNo)
    {
        _client = client;
        _myNo = myEmployeeNo;

        Text = $"{Theme.AppName} - 관리자";
        Font = UiHelper.BaseFont;
        StartPosition = FormStartPosition.CenterScreen;
        Size = LogicalToDeviceUnits(new Size(980, 620));
        MinimumSize = LogicalToDeviceUnits(new Size(760, 420));
        BackColor = Color.White;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildUsersTab());
        tabs.TabPages.Add(BuildAuditTab());
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedIndex == 1 && _audit.Items.Count == 0) RequestAudit(); };
        Controls.Add(tabs);
        Controls.Add(_status);

        _client.PacketReceived += OnPacketReceived;
        FormClosed += (_, _) => _client.PacketReceived -= OnPacketReceived;
        Shown += (_, _) => RequestUsers();
    }

    /// <summary>승인 대기 목록을 바로 보여 준다 (새 가입 신청 알림을 눌렀을 때).</summary>
    public void ShowPending()
    {
        if (_statusFilter.SelectedIndex == 1) RequestUsers();
        else _statusFilter.SelectedIndex = 1; // → SelectedIndexChanged에서 RequestUsers
    }

    /// <summary>바깥(메인 창)에서 "목록이 바뀌었을 수 있음"을 알릴 때.</summary>
    public void RefreshUsers() => RequestUsers();

    // ───────── 화면 구성 ─────────

    private static ListView CreateList() => new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        MultiSelect = false,
    };

    private TabPage BuildUsersTab()
    {
        var page = new TabPage("회원") { BackColor = Color.White };

        foreach (var (text, _) in Filters) _statusFilter.Items.Add(text);
        _statusFilter.SelectedIndex = 0;
        _statusFilter.SelectedIndexChanged += (_, _) => RequestUsers();
        _search.TextChanged += (_, _) => ShowUsers();
        var refresh = UiHelper.CreateButton("새로고침");
        refresh.Margin = new Padding(8, 0, 0, 0);
        refresh.Click += (_, _) => RequestUsers();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4), WrapContents = false };
        top.Controls.Add(new Label { Text = "상태", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        top.Controls.Add(_statusFilter);
        top.Controls.Add(new Label { Text = "검색", AutoSize = true, Margin = new Padding(12, 8, 4, 0) });
        top.Controls.Add(_search);
        top.Controls.Add(refresh);
        top.Controls.Add(_userCount);

        foreach (var (title, width) in new[] { ("사원번호", 90), ("이름", 80), ("직급", 60), ("부서", 100), ("이메일", 190),
                                               ("상태", 80), ("권한", 60), ("접속", 50), ("잠금", 60), ("가입일", 90), ("마지막 로그인", 130) })
            _users.Columns.Add(title, LogicalToDeviceUnits(width));
        _users.SelectedIndexChanged += (_, _) => UpdateActionButtons();

        // 동작 버튼들 (선택한 회원 상태에 따라 켜고 끈다)
        AddAction("가입 승인", AdminActionType.Approve);
        AddAction("가입 거절", AdminActionType.Reject);
        AddAction("정지", AdminActionType.Suspend);
        AddAction("퇴사 처리", AdminActionType.Resign);
        AddAction("사용 재개", AdminActionType.Activate);
        AddAction("잠금 해제", AdminActionType.Unlock);
        AddAction("비밀번호 초기화", AdminActionType.ResetPassword);
        AddAction("강제 로그아웃", AdminActionType.Kick);
        AddAction("관리자 지정", AdminActionType.GrantAdmin);
        AddAction("관리자 해제", AdminActionType.RevokeAdmin);
        UpdateActionButtons();

        // [주의] Dock 순서: 나중에 넣은 컨트롤이 먼저 자리를 잡는다. Fill(목록)을 맨 앞에 넣어야 위·아래 막대를 침범하지 않는다.
        page.Controls.Add(_users);
        page.Controls.Add(top);
        page.Controls.Add(_actions);
        return page;
    }

    private void AddAction(string text, AdminActionType type)
    {
        var button = UiHelper.CreateButton(text);
        button.Margin = new Padding(4);
        button.Tag = type;
        button.Click += (_, _) => RunAction(type);
        _actions.Controls.Add(button);
    }

    private TabPage BuildAuditTab()
    {
        var page = new TabPage("감사 로그") { BackColor = Color.White };

        _auditAction.Items.Add("전체 동작");
        foreach (string action in AuditActions.All) _auditAction.Items.Add($"{AuditActions.Text(action)} ({action})");
        _auditAction.SelectedIndex = 0;
        foreach (int n in new[] { 100, 200, 500, 1000 }) _auditLimit.Items.Add(n);
        _auditLimit.SelectedIndex = 1;
        var search = UiHelper.CreateButton("조회", primary: true);
        search.Margin = new Padding(8, 0, 0, 0);
        search.Click += (_, _) => RequestAudit();
        _auditNo.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RequestAudit(); } };

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4), WrapContents = false };
        top.Controls.Add(_auditNo);
        top.Controls.Add(new Label { Text = "동작", AutoSize = true, Margin = new Padding(12, 8, 4, 0) });
        top.Controls.Add(_auditAction);
        top.Controls.Add(new Label { Text = "최근", AutoSize = true, Margin = new Padding(12, 8, 4, 0) });
        top.Controls.Add(_auditLimit);
        top.Controls.Add(new Label { Text = "건", AutoSize = true, Margin = new Padding(4, 8, 0, 0) });
        top.Controls.Add(search);

        foreach (var (title, width) in new[] { ("시각", 140), ("동작", 120), ("한 사람", 90), ("대상", 90), ("내용", 330), ("IP", 110) })
            _audit.Columns.Add(title, LogicalToDeviceUnits(width));

        page.Controls.Add(_audit);
        page.Controls.Add(top);
        return page;
    }

    // ───────── 요청 보내기 ─────────

    private void RequestUsers()
    {
        string? filter = Filters[Math.Max(0, _statusFilter.SelectedIndex)].Value;
        if (_client.Send(Packet.Create(PacketType.AdminUserListReq, filter)))
            ShowStatus("회원 목록을 불러오는 중...", error: false);
    }

    private void RequestAudit()
    {
        var query = new AuditQuery
        {
            EmployeeNo = _auditNo.Text.Trim(),
            // 첫 칸은 "전체 동작", 나머지는 AuditActions.All과 같은 순서
            Action = _auditAction.SelectedIndex > 0 ? AuditActions.All[_auditAction.SelectedIndex - 1] : null,
            Limit = _auditLimit.SelectedItem is int n ? n : 200,
        };
        if (_client.Send(Packet.Create(PacketType.AdminAuditReq).WithData(query)))
            ShowStatus("감사 로그를 불러오는 중...", error: false);
    }

    private AdminUserInfo? Selected => _users.SelectedItems.Count > 0 ? _users.SelectedItems[0].Tag as AdminUserInfo : null;

    /// <summary>선택한 회원에게 관리 동작. 되돌리기 어려운 동작은 확인을 받고, 정지·강제 로그아웃은 사유를 묻는다.</summary>
    private void RunAction(AdminActionType type)
    {
        if (Selected is not AdminUserInfo user) return;
        string who = $"{user.Name} {user.Position}({user.EmployeeNo})";
        string what = AdminAction.Text(type);
        string? reason = null;

        if (type is AdminActionType.Suspend or AdminActionType.Kick or AdminActionType.Resign)
        {
            // [왜] 사유는 감사 로그에 남고, 정지·강제 로그아웃이면 당사자 화면에도 보인다 ("왜 끊겼지?"를 줄인다).
            reason = Prompt($"{what} 사유 (선택, 최대 {AdminAction.MaxReasonLength}자)", what);
            if (reason is null) return; // 취소
        }
        else if (type is AdminActionType.Reject or AdminActionType.ResetPassword or AdminActionType.GrantAdmin or AdminActionType.RevokeAdmin)
        {
            string extra = type switch
            {
                AdminActionType.Reject => "\n가입 신청이 삭제됩니다.",
                AdminActionType.ResetPassword => "\n임시 비밀번호가 발급되고, 지금 비밀번호는 더 이상 쓸 수 없습니다.",
                AdminActionType.GrantAdmin => "\n이 사람도 회원 관리와 감사 로그를 볼 수 있게 됩니다.",
                _ => "",
            };
            if (MessageBox.Show(this, $"{who}\n'{what}'을(를) 할까요?{extra}", what, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
        }

        var action = new AdminAction { Type = type, EmployeeNo = user.EmployeeNo, Reason = reason };
        if (_client.Send(Packet.Create(PacketType.AdminActionReq).WithData(action)))
            ShowStatus($"{who} {what} 중...", error: false);
    }

    // ───────── 응답 받기 ─────────

    // [스레드] 수신 스레드 → UI 스레드로 넘긴다.
    private void OnPacketReceived(Packet packet)
    {
        if (packet.Type is PacketType.AdminUserList or PacketType.AdminActionRes or PacketType.AdminAudit or PacketType.AdminNotify)
            UiHelper.RunOnUi(this, () => HandlePacket(packet));
    }

    private void HandlePacket(Packet packet)
    {
        switch (packet.Type)
        {
            case PacketType.AdminUserList:
                _allUsers = packet.GetData<List<AdminUserInfo>>() ?? [];
                ShowUsers();
                ShowStatus($"회원 {_allUsers.Count}명을 불러왔습니다. ({DateTime.Now:HH:mm:ss})", error: false);
                break;

            case PacketType.AdminActionRes:
                ShowStatus(packet.Body ?? "", error: !packet.Success);
                if (!packet.Success)
                {
                    MessageBox.Show(this, packet.Body, "처리하지 못했습니다", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (packet.GetData<AdminActionResult>() is { TempPassword: string temp } result)
                    ShowTempPassword(result.EmployeeNo, temp);
                RequestUsers(); // 바뀐 상태를 다시 불러온다
                break;

            case PacketType.AdminAudit:
                ShowAudit(packet.GetData<List<AuditEntry>>() ?? []);
                break;

            case PacketType.AdminNotify:
                RequestUsers(); // 새 가입 신청 등 → 목록 새로 고침
                break;
        }
    }

    /// <summary>받은 회원 목록을 검색어로 걸러 표시한다 (검색은 서버에 다시 묻지 않고 화면에서).</summary>
    private void ShowUsers()
    {
        string keyword = _search.Text.Trim();
        string? selectedNo = Selected?.EmployeeNo;

        // [개념] BeginUpdate/EndUpdate: 수백 줄을 넣는 동안 매번 다시 그리지 않고, 다 넣은 뒤 한 번만 그린다(깜빡임·속도 개선).
        _users.BeginUpdate();
        _users.Items.Clear();
        foreach (AdminUserInfo u in _allUsers)
        {
            if (keyword.Length > 0 && !(u.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                        || u.EmployeeNo.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                        || u.Department.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                continue;

            var item = new ListViewItem([
                u.EmployeeNo, u.Name, u.Position, u.Department, u.Email,
                AdminUserInfo.StatusText(u.Status), u.Role == "ADMIN" ? "관리자" : "",
                u.Online ? "●" : "", u.LockMinutesLeft > 0 ? $"{u.LockMinutesLeft}분" : "",
                u.CreatedAt.ToString("yyyy-MM-dd"), u.LastLoginAt?.ToString("yyyy-MM-dd HH:mm") ?? "-",
            ]) { Tag = u };
            // 눈에 띄어야 할 줄에 색: 승인 대기 = 파랑, 정지·퇴사 = 회색, 잠김 = 빨강
            if (u.Status == "PENDING") item.ForeColor = Theme.Primary;
            else if (u.Status is "SUSPENDED" or "RESIGNED") item.ForeColor = Color.Gray;
            if (u.LockMinutesLeft > 0) item.ForeColor = UiHelper.Danger;
            _users.Items.Add(item);
            if (u.EmployeeNo == selectedNo) item.Selected = true; // 새로 고쳐도 선택 유지
        }
        _users.EndUpdate();

        int pending = _allUsers.Count(u => u.Status == "PENDING");
        _userCount.Text = $"{_users.Items.Count}명 표시{(pending > 0 ? $" · 승인 대기 {pending}명" : "")}";
        UpdateActionButtons();
    }

    /// <summary>선택한 회원의 상태에 맞는 버튼만 켠다 (예: 승인은 승인 대기일 때만).</summary>
    private void UpdateActionButtons()
    {
        AdminUserInfo? u = Selected;
        bool me = u?.EmployeeNo == _myNo;
        foreach (Button b in _actions.Controls.OfType<Button>())
        {
            b.Enabled = u is not null && b.Tag is AdminActionType type && type switch
            {
                AdminActionType.Approve or AdminActionType.Reject => u.Status == "PENDING",
                AdminActionType.Suspend => u.Status == "ACTIVE" && !me,
                AdminActionType.Resign => u.Status != "RESIGNED" && !me,
                AdminActionType.Activate => u.Status is "SUSPENDED" or "RESIGNED",
                AdminActionType.Unlock => u.LockMinutesLeft > 0,
                AdminActionType.ResetPassword => u.Status != "PENDING",
                AdminActionType.Kick => u.Online && !me,
                AdminActionType.GrantAdmin => u.Role != "ADMIN" && u.Status == "ACTIVE",
                AdminActionType.RevokeAdmin => u.Role == "ADMIN" && !me,
                _ => false,
            };
        }
    }

    private void ShowAudit(List<AuditEntry> entries)
    {
        _audit.BeginUpdate();
        _audit.Items.Clear();
        foreach (AuditEntry e in entries)
        {
            var item = new ListViewItem([
                e.Time.ToString("yyyy-MM-dd HH:mm:ss"), AuditActions.Text(e.Action),
                e.ActorNo ?? "-", e.TargetNo ?? "-", e.Detail ?? "", e.ClientIp ?? "",
            ]);
            // 보안상 눈여겨볼 기록은 빨간색
            if (e.Action is AuditActions.LoginFail or AuditActions.Denied) item.ForeColor = UiHelper.Danger;
            _audit.Items.Add(item);
        }
        _audit.EndUpdate();
        ShowStatus($"감사 로그 {entries.Count}건 ({DateTime.Now:HH:mm:ss})", error: false);
    }

    /// <summary>임시 비밀번호를 보여 주고 클립보드에 복사한다.</summary>
    // [왜] 임시 비밀번호는 서버 로그·감사 로그 어디에도 남기지 않는다. 지금 이 화면이 유일하게 보이는 곳이다.
    private void ShowTempPassword(string employeeNo, string temp)
    {
        try { Clipboard.SetText(temp); } catch (System.Runtime.InteropServices.ExternalException) { /* 다른 프로그램이 클립보드를 쓰는 중 */ }
        MessageBox.Show(this,
            $"{employeeNo}의 임시 비밀번호:\n\n    {temp}\n\n클립보드에 복사했습니다. 본인에게 직접(메신저 밖에서) 전달하세요.\n" +
            "이 창을 닫으면 다시 볼 수 없습니다. 본인은 다음 로그인 때 새 비밀번호로 바꿔야 합니다.",
            "비밀번호 초기화", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? UiHelper.Danger : UiHelper.Muted;
    }

    /// <summary>한 줄 입력 대화상자. 취소하면 null, 빈칸이면 "".</summary>
    private string? Prompt(string label, string title)
    {
        using var form = new Form
        {
            Text = title,
            Font = UiHelper.BaseFont,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.White,
        };
        var input = new TextBox { MaxLength = AdminAction.MaxReasonLength };
        var ok = UiHelper.CreateButton("확인", primary: true);
        var cancel = UiHelper.CreateButton("취소");
        var table = UiHelper.CreateFormTable();
        UiHelper.AddFieldRow(table, label, input, 280);
        UiHelper.AddSpanRow(table, UiHelper.CreateButtonRow(cancel, ok));
        form.Controls.Add(table);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        ok.Click += (_, _) => { form.DialogResult = DialogResult.OK; form.Close(); };
        cancel.Click += (_, _) => form.Close();
        return form.ShowDialog(this) == DialogResult.OK ? input.Text.Trim() : null;
    }
}
