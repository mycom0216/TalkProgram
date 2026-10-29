using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] PhoneForm.cs - 내 휴대폰 번호 등록·변경·삭제 창
// - 등록해 두면 다른 사람이 [친구 추가 → 휴대폰 번호]로 나를 찾을 수 있다. 번호 자체는 아무에게도 보이지 않는다.
// - 창이 뜨면 PhoneInfoReq로 지금 번호를 받아 오고, 저장·삭제 결과도 PhoneInfo로 받는다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>내 휴대폰 번호 설정 창.</summary>
internal sealed class PhoneForm : Form
{
    private readonly ChatClient _client;
    private readonly TextBox _phone = new() { MaxLength = 20, PlaceholderText = "010-1234-5678", Name = "MyPhone" };
    private readonly Button _save = UiHelper.CreateButton("저장", primary: true);
    private readonly Button _clear = UiHelper.CreateButton("번호 지우기");
    private readonly Label _status = new() { AutoSize = true, ForeColor = UiHelper.Muted, Margin = new Padding(0, 8, 0, 0) };

    public PhoneForm(ChatClient client)
    {
        _client = client;
        Text = $"{Theme.AppName} - 내 휴대폰 번호";
        Font = UiHelper.BaseFont;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.White;

        var table = UiHelper.CreateFormTable();
        UiHelper.AddSpanRow(table, new Label
        {
            AutoSize = true,
            ForeColor = UiHelper.Muted,
            Margin = new Padding(0, 0, 0, 10),
            Text = "등록하면 동료가 [친구 추가 → 휴대폰 번호]로 나를 찾을 수 있습니다.\n번호는 다른 사람에게 보이지 않습니다. (검색 결과에도 010-****-5678처럼 가려짐)",
        });
        UiHelper.AddFieldRow(table, "휴대폰 번호", _phone, 200);
        UiHelper.AddSpanRow(table, _status);
        var close = UiHelper.CreateButton("닫기");
        close.Click += (_, _) => Close();
        UiHelper.AddSpanRow(table, UiHelper.CreateButtonRow(close, _clear, _save));
        UiHelper.AddDialogHeader(this, table, "내 휴대폰 번호");
        Controls.Add(table);
        AcceptButton = _save;
        CancelButton = close;

        _save.Click += (_, _) =>
        {
            if (PhoneRules.Normalize(_phone.Text) is null)
            {
                ShowStatus("휴대폰 번호 형식이 올바르지 않습니다. (예: 010-1234-5678)", error: true);
                return;
            }
            Send(_phone.Text.Trim());
        };
        _clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "휴대폰 번호를 지울까요?\n지우면 다른 사람이 번호로 나를 찾을 수 없습니다.", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Send("");
        };

        _client.PacketReceived += OnPacketReceived;
        FormClosed += (_, _) => _client.PacketReceived -= OnPacketReceived;
        Shown += (_, _) =>
        {
            ShowStatus("지금 등록된 번호를 불러오는 중...", error: false);
            if (!_client.Send(Packet.Create(PacketType.PhoneInfoReq))) ShowStatus("서버와 연결이 끊어졌습니다.", error: true);
        };
    }

    private void Send(string phone)
    {
        if (!_client.Send(Packet.Create(PacketType.PhoneUpdate, body: phone)))
        {
            ShowStatus("서버와 연결이 끊어졌습니다.", error: true);
            return;
        }
        _save.Enabled = _clear.Enabled = false;
        ShowStatus("저장하는 중...", error: false);
    }

    // [스레드] 수신 스레드 → UI 스레드
    private void OnPacketReceived(Packet packet)
    {
        if (packet.Type != PacketType.PhoneInfo) return;
        UiHelper.RunOnUi(this, () =>
        {
            _save.Enabled = _clear.Enabled = true;
            if (!packet.Success)
            {
                ShowStatus(packet.Body ?? "저장하지 못했습니다.", error: true);
                return;
            }
            string phone = packet.GetData<PhoneInfo>()?.Phone ?? "";
            _phone.Text = phone;
            _clear.Enabled = phone.Length > 0;
            ShowStatus(packet.Body ?? (phone.Length > 0 ? "등록된 번호입니다." : "아직 등록한 번호가 없습니다."), error: false);
        });
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? UiHelper.Danger : UiHelper.Muted;
    }
}
