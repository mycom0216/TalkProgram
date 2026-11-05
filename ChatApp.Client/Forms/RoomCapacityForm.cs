using ChatApp.Common.Models;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] RoomCapacityForm.cs - 방 정원(최대 인원) 바꾸기 창 (방장·관리자)
// - 지금 멤버 수보다 작게는 못 줄인다(이미 들어온 사람을 내보내지 않음). 최대 CreateRoomRequest.MaxUsersLimit(1000)명.
// - 결과는 MainForm이 RoomCapacity 패킷으로 서버에 보낸다. 서버가 다시 확인한다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   방 정원(최대 인원) 숫자 하나를 입력받는 창. 최소값을 "지금 멤버 수"로 올려서(Math.Max(MinUsers, memberCount)) 이미 들어와 있는 사람보다 적게는 줄일 수 없게 만든다. 결과(Capacity)는 부르는 쪽이 RoomCapacity 패킷으로 보내고 서버가 다시 확인한다.
//
// ■ 읽을 때 볼 곳
//   - 생성자의 min / Minimum / Maximum / Value 네 줄: 값의 허용 범위와 초기값(Clamp)을 한꺼번에 정한다.
//   - Clamp(현재값, min, max): 현재 정원이 0 이하(미설정)이면 min을, 범위를 벗어난 옛 값이면 가장 가까운 허용값으로 맞춘다. NumericUpDown은 Value가 범위 밖이면 예외를 던지므로 먼저 맞춰 주어야 한다.
//
// ■ 면접 예상 질문과 답
//   Q1. 정원을 현재 멤버 수보다 작게 못 줄이게 한 이유는요?
//       A. 이미 들어온 사람을 정원 변경으로 내보내는 일이 없게 하려는 정책입니다. 화면은 최소값으로 막고, 서버가 같은 규칙으로 다시 확인합니다(그 사이 멤버 수가 늘 수 있으므로).
//   Q2. 이 창에서 값 검사를 서버가 또 하는 이유는요?
//       A. 창이 열려 있는 동안 다른 사람이 들어와 멤버 수가 바뀔 수 있고, 클라이언트 값은 신뢰할 수 없기 때문입니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - ClientSize와 컨트롤 크기가 원시 픽셀이라 다른 창과 달리 DPI 배율을 직접 반영하지 않는다(실제 화면에서 확인하지는 않음).
// ───────────────────────────────────────────────────────────────────────────

/// <summary>방 정원 입력 창. OK면 Capacity에 새 정원.</summary>
internal sealed class RoomCapacityForm : Form
{
    private readonly NumericUpDown _value = new() { Width = 100, TextAlign = HorizontalAlignment.Right, Name = "RoomCapacity" };
    /// <summary>입력한 새 최대 인원(명). 저장(OK)으로 닫혔을 때 읽는다.</summary>
    public int Capacity => (int)_value.Value;

    /// <summary>정원 변경 창을 만든다. current는 현재 정원, memberCount는 지금 멤버 수(이보다 작게는 못 줄임).</summary>
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

        // [왜] 최소 정원 = max(방 최소 인원, 지금 멤버 수). 멤버 수보다 낮은 정원은 의미가 없어서 아예 입력 범위에서 뺀다.
        int min = Math.Max(CreateRoomRequest.MinUsers, memberCount);
        _value.Minimum = min;
        _value.Maximum = CreateRoomRequest.MaxUsersLimit;
        // [주의] NumericUpDown.Value에 범위 밖의 값을 넣으면 예외가 난다. 위에서 Minimum/Maximum을 정한 뒤 Clamp로 안전한 값을 넣는 순서가 중요하다.
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
