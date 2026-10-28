using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ChangePasswordForm.cs - 비밀번호 변경 대화상자
// - 두 가지로 쓰인다.
//   ① 일반: 메인 창의 내 이름 메뉴 → "비밀번호 변경". 현재 비밀번호 + 새 비밀번호를 입력.
//   ② 강제: 관리자가 초기화한 "임시 비밀번호"로 로그인한 직후(LoginForm). 바꾸기 전에는 채팅을 쓸 수 없다.
//      현재 비밀번호(= 방금 입력한 임시 비밀번호)는 이미 알고 있으므로 입력칸을 숨기고 자동으로 넣는다.
// - 스레드: 화면은 UI 스레드. 응답(ChangePasswordRes)은 수신 스레드에서 와서 RunOnUi로 넘긴다.
// - 성공하면 NewPassword에 새 비밀번호가 남아 있다 → 부르는 쪽이 자동 재접속용 LastLogin을 고친다.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ChangePasswordForm : Form
{
    private readonly ChatClient _client;
    private readonly TextBox _current = new() { UseSystemPasswordChar = true, MaxLength = 64 };
    private readonly TextBox _new = new() { UseSystemPasswordChar = true, MaxLength = 64 };
    private readonly TextBox _confirm = new() { UseSystemPasswordChar = true, MaxLength = 64 };
    private readonly Button _save = UiHelper.CreateButton("변경", primary: true);
    private readonly Button _cancel = UiHelper.CreateButton("취소");
    private readonly Label _status = new() { AutoSize = true, ForeColor = UiHelper.Muted, Margin = new Padding(0, 8, 0, 0) };

    /// <summary>성공한 새 비밀번호 (자동 재접속용).</summary>
    public string NewPassword { get; private set; } = "";

    /// <param name="currentPassword">강제 변경일 때 방금 로그인한 임시 비밀번호. null이면 일반 변경(입력칸 보임).</param>
    public ChangePasswordForm(ChatClient client, string? currentPassword = null)
    {
        _client = client;
        bool forced = currentPassword is not null;

        Text = $"{Theme.AppName} - 비밀번호 변경";
        Font = UiHelper.BaseFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = forced; // 로그인 창 대신 뜨는 창이라 작업 표시줄에 보여야 찾기 쉽다
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        var table = UiHelper.CreateFormTable();
        if (forced)
        {
            _current.Text = currentPassword;
            UiHelper.AddSpanRow(table, new Label
            {
                Text = "관리자가 발급한 임시 비밀번호로 로그인했습니다.\n계속하려면 새 비밀번호로 바꾸세요.",
                AutoSize = true,
                ForeColor = UiHelper.Danger,
                Margin = new Padding(0, 0, 0, 10),
            });
        }
        else
        {
            UiHelper.AddFieldRow(table, "현재 비밀번호", _current, 220);
        }
        UiHelper.AddFieldRow(table, "새 비밀번호", _new, 220);
        UiHelper.AddFieldRow(table, "새 비밀번호 확인", _confirm, 220);
        UiHelper.AddSpanRow(table, new Label
        {
            Text = "영문+숫자 8~64자",
            AutoSize = true,
            ForeColor = UiHelper.Muted,
            Margin = new Padding(0, 4, 0, 0),
        });
        UiHelper.AddSpanRow(table, _status);
        if (forced) _cancel.Text = "로그아웃";
        UiHelper.AddSpanRow(table, UiHelper.CreateButtonRow(_cancel, _save));
        Controls.Add(table);
        UiHelper.AddDialogHeader(this, table, "비밀번호 변경");

        AcceptButton = _save;
        CancelButton = _cancel;
        _save.Click += (_, _) => Submit();
        _cancel.Click += (_, _) => Close();

        _client.PacketReceived += OnPacketReceived;
        FormClosed += (_, _) => _client.PacketReceived -= OnPacketReceived;
        Shown += (_, _) => (forced ? _new : _current).Focus();
    }

    private void Submit()
    {
        // [왜] 화면에서 먼저 검사해 서버 왕복 없이 바로 알려 준다. 최종 검사는 서버(AuthService.ChangePassword)가 다시 한다.
        if (_current.Text.Length == 0) { ShowStatus("현재 비밀번호를 입력하세요.", true); return; }
        if (_new.Text.Length is < 8 or > 64 || !_new.Text.Any(char.IsLetter) || !_new.Text.Any(char.IsDigit))
        {
            ShowStatus("새 비밀번호는 영문과 숫자를 포함해 8~64자로 입력하세요.", true);
            return;
        }
        if (_new.Text != _confirm.Text) { ShowStatus("새 비밀번호 확인이 일치하지 않습니다.", true); return; }

        _save.Enabled = false;
        ShowStatus("변경 중...", false);
        var request = new ChangePasswordRequest { CurrentPassword = _current.Text, NewPassword = _new.Text };
        if (!_client.Send(Packet.Create(PacketType.ChangePasswordReq).WithData(request)))
        {
            _save.Enabled = true;
            ShowStatus("서버와 연결이 끊어졌습니다.", true);
        }
    }

    // [스레드] 수신 스레드에서 불린다 → UI 스레드로 넘긴다.
    private void OnPacketReceived(Packet packet)
    {
        if (packet.Type == PacketType.ChangePasswordRes)
            UiHelper.RunOnUi(this, () => HandleResponse(packet));
    }

    private void HandleResponse(Packet packet)
    {
        _save.Enabled = true;
        if (!packet.Success)
        {
            ShowStatus(packet.Body ?? "비밀번호를 바꾸지 못했습니다.", true);
            return;
        }
        NewPassword = _new.Text;
        MessageBox.Show(this, packet.Body ?? "비밀번호를 바꿨습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowStatus(string message, bool error)
    {
        _status.Text = message;
        _status.ForeColor = error ? UiHelper.Danger : UiHelper.Muted;
    }
}
