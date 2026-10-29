using ChatApp.Common.Models;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] RoomCapacityForm.cs - 방 정원(최대 인원) 바꾸기 창 (방장·관리자)
// - 지금 멤버 수보다 작게는 못 줄인다(이미 들어온 사람을 내보내지 않음). 최대 CreateRoomRequest.MaxUsersLimit(1000)명.
// - 결과는 MainForm이 RoomCapacity 패킷으로 서버에 보낸다. 서버가 다시 확인한다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>방 정원 입력 창. OK면 Capacity에 새 정원.</summary>
internal sealed class RoomCapacityForm : Form
{
    private readonly NumericUpDown _value = new() { Width = 100, TextAlign = HorizontalAlignment.Right, Name = "RoomCapacity" };
    public int Capacity => (int)_value.Value;

    public RoomCapacityForm(string roomName, int current, int memberCount)
    {
        Text = "방 정원 바꾸기";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = UiHelper.BaseFont;
        ClientSize = new Size(380, 170);

        int min = Math.Max(CreateRoomRequest.MinUsers, memberCount);
        _value.Minimum = min;
        _value.Maximum = CreateRoomRequest.MaxUsersLimit;
        _value.Value = Math.Clamp(current <= 0 ? min : current, min, CreateRoomRequest.MaxUsersLimit);

        var hint = new Label
        {
            Text = $"'{roomName}' 방의 최대 인원\n지금 멤버 {memberCount}명 · {min}~{CreateRoomRequest.MaxUsersLimit}명",
            Dock = DockStyle.Top, Height = 56, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0),
        };
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(12, 4, 0, 0) };
        row.Controls.Add(_value);
        row.Controls.Add(new Label { Text = "명", AutoSize = true, Margin = new Padding(4, 6, 0, 0) });

        var save = new Button { Text = "바꾸기", DialogResult = DialogResult.OK, Width = 90, Height = 30, Name = "RoomCapacityOk" };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Width = 75, Height = 30 };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 8, 0) };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);

        Controls.Add(row);
        Controls.Add(hint);
        Controls.Add(actions);
        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) => { _value.Select(0, _value.Text.Length); _value.Focus(); };
    }
}
