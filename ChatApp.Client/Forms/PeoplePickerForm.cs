using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] PeoplePickerForm.cs - "사람 고르기" 창 (회사 전체 사용자 목록에서 고른다)
// - 쓰임: ① 채팅방에 초대할 사람 고르기(여러 명, 체크) ② 접속하지 않은 동료에게 1:1 보내기(한 명)
// - 목록: 창이 뜨면 서버에 DirectoryReq를 보내고, Directory(사용 중인 모든 사람)를 받아 채운다.
//         접속 중인 동료 목록(UserList)과 달리 오프라인인 사람도 들어 있다.
// - 스레드: 화면은 UI 스레드. 서버 응답은 수신 스레드에서 와서 RunOnUi로 넘긴다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   "회사 사람 고르기" 창 하나로 여러 용도를 처리한다: ① 방에 초대(여러 명, 체크) ② 1:1 대화 상대 고르기(한 명, 더블클릭) ③ 방장 넘기기 같은 "주어진 사람들 중 고르기". 생성자 매개변수(multiSelect, people, okText, hint, skipText, maxSelect)로 모양과 동작이 달라진다.
//
// ■ 흐름
//   ① people을 직접 받았으면 서버에 묻지 않고 그 목록을 쓴다. 아니면 창이 뜬 뒤(Shown) DirectoryReq를 보내 "사용 중인 모든 사람"을 받는다(오프라인인 사람 포함).
//   ② 받은 목록에서 exclude(나 자신, 이미 방에 있는 사람)를 뺀 뒤 부서·이름순으로 정렬해 _all에 보관한다.
//   ③ Fill(): 찾기 글자로 거른 사람만 ListView에 채운다. 체크한 사람은 _checked(사원번호 집합)에 따로 기억해서, 걸러져 안 보여도 선택이 유지된다.
//   ④ UpdateStatus(): 상태 줄, 확인 버튼 켜짐/꺼짐, [전체 선택] 표시를 한곳에서 계산한다.
//   ⑤ Accept(): 고른 사람을 Selected에 담고 OK로 닫는다. 남은 자리보다 많이 골랐으면 ConfirmOverLimit(방장·관리자만 줌)으로 "정원을 늘릴까요?"를 묻는다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 상태를 집합으로 따로 기억(화면 목록과 선택을 분리) .. "keep selection state separate from filtered list view"
//   - 재진입 방지 플래그(_filling, _syncingSelectAll) ....... "suppress event while programmatically updating UI flag"
//   - 델리게이트로 "물어보기" 주입(ConfirmOverLimit) ........ "Func callback inject policy decision into dialog"
//   - ListView 체크박스와 ItemChecked ....................... "ListView CheckBoxes ItemChecked event"
//   - 선택 인원 상한(maxSelect)과 서버 상한 .................. "client limit mirrors server limit"
//   - DesignerSerializationVisibility.Hidden (WFO1000) ...... "WFO1000 DesignerSerializationVisibility Hidden"
//
// ■ 면접 예상 질문과 답
//   Q1. 찾기로 목록이 줄어도 이미 체크한 사람이 안 사라지는 이유는요?
//       A. 체크 상태를 ListView 항목이 아니라 _checked 집합(사원번호)에 따로 저장하기 때문입니다. Fill()은 목록을 지우고 다시 채울 뿐이라, 다시 채울 때 집합에 있는 사람에게 Checked를 켜 줍니다. 확인(Accept)도 화면 항목이 아니라 이 집합 기준으로 고릅니다.
//   Q2. _filling 같은 bool 플래그는 왜 필요한가요?
//       A. 코드로 목록을 채우거나 전체 선택할 때도 ItemChecked 이벤트가 줄마다 발생합니다. 그 이벤트가 다시 상태를 바꾸고 화면을 갱신하면 느리고 꼬이므로, 일괄 작업 중에는 플래그로 이벤트를 무시하고 끝난 뒤 UpdateStatus를 한 번만 부릅니다.
//   Q3. 한 창으로 여러 상황을 처리한 이유와 단점은요?
//       A. 목록 불러오기·찾기·선택 UI가 같아서 중복을 줄이려는 것입니다. 대신 생성자 매개변수가 많아지고(옵션 8개) 분기(_multi)가 늘어 읽기 어려워지는 단점이 있습니다. 더 커지면 용도별로 작은 클래스로 나누는 것이 좋습니다.
//   Q4. 정원보다 많이 고르면 어떻게 되나요?
//       A. 일반 멤버는 확인 버튼이 꺼지고 "몇 명을 빼 주세요"가 보입니다. 방장·관리자는 ConfirmOverLimit가 주어져 "정원을 늘리고 초대"로 진행할 수 있습니다. 요청당 초대 인원 상한(50명)은 호출하는 쪽(MainForm.Rooms)이 maxSelect에 반영하고, 서버도 같은 상한으로 자릅니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - 화면의 상한과 정원 확인은 편의이고, 정원 초과는 서버가 최종 거절한다(클라이언트 값은 신뢰하지 않는다).
//   - 서버가 목록을 한 번에 전부 준다(DirectoryReq). 회사 규모가 커지면 목록 전송량·ListView 채우기 시간이 늘 수 있다(지금 규모에서는 문제 없음).
//   - people을 직접 받은 경로는 OrderBy(Name), 서버에서 받은 경로는 OrderBy(Department).ThenBy(Name)으로 정렬 기준이 다르다.
// ───────────────────────────────────────────────────────────────────────────

/// <summary>회사 사람 고르기. OK면 Selected에 고른 사람들.</summary>
public sealed class PeoplePickerForm : Form
{
    private readonly ChatClient _client;
    private readonly bool _multi;
    // [왜] HashSet: "이 사원번호가 제외 대상인가?"를 목록 길이와 상관없이 빠르게 판단(Contains가 평균 O(1)).
    private readonly HashSet<string> _exclude;
    private readonly TextBox _filter = new() { Dock = DockStyle.Top, PlaceholderText = "이름·부서·사원번호로 찾기" };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = false,
        HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = UiHelper.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _ok;

    private List<UserInfo> _all = [];
    private readonly HashSet<string> _checked = [];
    // [왜] 일괄 작업 중 ItemChecked 이벤트를 무시하는 플래그. 아래 _syncingSelectAll도 같은 아이디어(한 방향 갱신 중에 반대 방향 이벤트가 되돌려 부르는 것을 막음).
    private bool _filling;  // 목록을 다시 채우는 중에는 체크 이벤트를 무시한다
    // [전체 선택] 체크 상자: 지금 목록에 보이는 사람(찾기로 걸러진 결과)을 한꺼번에 선택·해제한다
    private readonly CheckBox _selectAll = new() { Text = "전체 선택", Dock = DockStyle.Top, Height = 28, Visible = false };
    private bool _syncingSelectAll; // 개별 체크에 맞춰 [전체 선택] 표시만 바꾸는 중 (그때는 일괄 선택을 하지 않는다)
    private int _maxSelect = int.MaxValue; // 한 번에 고를 수 있는 최대 인원 (방 남은 자리)
    private readonly string _okText;

    /// <summary>
    /// 남은 자리보다 많이 골랐을 때 물어볼 함수 (방장·관리자만 줌). 고른 인원을 받아 true면 그대로 진행한다(호출한 쪽이 정원을 늘린다).
    /// 없으면 남은 자리보다 많이 고르면 [초대]가 꺼진다.
    /// </summary>
    // [왜] Form의 public 속성은 디자이너가 코드로 저장하려 해서 WFO1000 오류가 난다. 코드에서만 쓰는 속성이라 저장 대상에서 뺀다.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    // [개념] Func<int, bool>: 정책 판단을 호출한 쪽에서 주입받는 방식. 이 창은 "방장인지, 정원을 늘릴 수 있는지"를 몰라도 되고, 묻는 일만 한다.
    public Func<int, bool>? ConfirmOverLimit { get; set; }
    // [왜] "아직 못 받아서 0명"과 "받았는데 진짜 0명"을 구분해야 안내 문구를 다르게 보여 줄 수 있다.
    private bool _received; // 서버에서 목록을 받았는지 (받기 전의 0명과 진짜 0명을 구분)
    private readonly string? _hint; // 한 명 고르기의 안내 문구 (없으면 1:1 안내)

    /// <summary>고른 사람들 (한 명 고르기면 1명).</summary>
    public List<UserInfo> Selected { get; private set; } = [];

    /// <param name="title">창 제목 (예: "'개발팀' 방에 초대")</param>
    /// <param name="multiSelect">true = 체크로 여러 명, false = 한 명(더블클릭으로 바로 선택)</param>
    /// <param name="exclude">목록에서 뺄 사원번호 (나 자신, 이미 방에 있는 사람)</param>
    /// <param name="people">고를 사람을 직접 줄 때(예: 방 멤버 중 새 방장). 없으면 서버에서 회사 사람 전체를 받아 온다.</param>
    /// <param name="okText">확인 버튼 글자 (없으면 "초대"/"대화하기")</param>
    /// <param name="hint">아래쪽 안내 문구 (한 명 고르기)</param>
    /// <param name="skipText">세 번째 버튼 글자 (예: "그냥 나가기"). 누르면 아무도 고르지 않고 DialogResult.Ignore로 닫힌다.</param>
    public PeoplePickerForm(ChatClient client, string title, bool multiSelect, IEnumerable<string> exclude,
                            IEnumerable<UserInfo>? people = null, string? okText = null, string? hint = null, string? skipText = null,
                            int maxSelect = int.MaxValue)
    {
        _client = client;
        _multi = multiSelect;
        _exclude = exclude.ToHashSet();
        _hint = hint;

        Text = title;
        Font = UiHelper.BaseFont;
        StartPosition = FormStartPosition.CenterParent;
        Size = LogicalToDeviceUnits(new Size(460, 520));
        MinimumSize = LogicalToDeviceUnits(new Size(360, 360));
        ShowInTaskbar = false;
        MinimizeBox = false;
        BackColor = Color.White;

        _list.CheckBoxes = multiSelect;
        _list.MultiSelect = false;
        _list.Columns.Add("이름", LogicalToDeviceUnits(140));
        _list.Columns.Add("부서", LogicalToDeviceUnits(130));
        _list.Columns.Add("사원번호", LogicalToDeviceUnits(110));
        _maxSelect = Math.Max(0, maxSelect);
        _selectAll.Visible = multiSelect;
        // [읽는 법] 사용자가 [전체 선택]을 눌렀을 때만 일괄 선택(SetAllVisible). UpdateStatus가 코드로 체크 표시를 맞출 때는 _syncingSelectAll=true라 이 줄이 무시된다.
        _selectAll.CheckedChanged += (_, _) => { if (!_syncingSelectAll) SetAllVisible(_selectAll.Checked); };
        _list.ItemChecked += (_, e) =>
        {
            if (_filling || e.Item.Tag is not UserInfo u) return;
            if (e.Item.Checked) _checked.Add(u.EmployeeNo); else _checked.Remove(u.EmployeeNo);
            UpdateStatus();
        };
        // [읽는 법] 한 명 고르기 모드: 더블클릭이 곧 확인. 여러 명 모드에서는 체크로만 고른다.
        _list.DoubleClick += (_, _) => { if (!_multi) Accept(); };
        _list.SelectedIndexChanged += (_, _) => { if (!_multi) _ok!.Enabled = _list.SelectedItems.Count > 0; };

        _filter.TextChanged += (_, _) => Fill();

        _okText = okText ?? (multiSelect ? "초대" : "대화하기");
        _ok = UiHelper.CreateButton(_okText, primary: true);
        _ok.Name = "PickerOk";
        var cancel = UiHelper.CreateButton("취소");
        _ok.Enabled = false;
        _ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();
        FlowLayoutPanel buttons;
        if (skipText is not null)
        {
            var skip = UiHelper.CreateButton(skipText);
            skip.AutoSize = true;
            skip.Click += (_, _) => { DialogResult = DialogResult.Ignore; Close(); };
            buttons = UiHelper.CreateButtonRow(cancel, skip, _ok);
        }
        else
        {
            buttons = UiHelper.CreateButtonRow(cancel, _ok);
        }
        buttons.Dock = DockStyle.Bottom;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(LogicalToDeviceUnits(10)) };
        body.Controls.Add(_list);
        body.Controls.Add(_selectAll); // 찾기 칸 바로 아래 (Dock Top은 나중에 넣은 것이 더 위)
        body.Controls.Add(_filter);
        body.Controls.Add(_status);
        body.Controls.Add(buttons);
        // [주의] Dock 순서: 나중에 넣은 컨트롤이 먼저 자리를 잡는다. Fill인 목록을 맨 앞으로 보내야 위(찾기·전체 선택)·아래(상태·버튼) 막대를 침범하지 않고 남은 자리를 채운다.
        _list.BringToFront();
        Controls.Add(body);
        AcceptButton = _ok;
        CancelButton = cancel;

        if (people is not null)
        {
            // 목록을 직접 받은 경우: 서버에 묻지 않는다
            _received = true;
            // [읽는 법] 호출한 쪽이 사람 목록을 직접 준 경우(예: 방 멤버 중에서 새 방장 고르기). 서버에 요청하지 않으므로 PacketReceived 구독도 하지 않고 바로 return한다.
            _all = people.Where(u => !_exclude.Contains(u.EmployeeNo)).OrderBy(u => u.Name).ToList();
            Shown += (_, _) => { _filter.Focus(); Fill(); };
            return;
        }

        _status.Text = "회사 사람 목록을 불러오는 중...";
        _client.PacketReceived += OnPacketReceived;
        FormClosed += (_, _) => _client.PacketReceived -= OnPacketReceived;
        Shown += (_, _) =>
        {
            _filter.Focus();
            if (!_client.Send(Packet.Create(PacketType.DirectoryReq))) _status.Text = "서버와 연결이 끊어져 목록을 불러오지 못했습니다.";
        };
    }

    // [스레드] 수신 스레드 → UI 스레드
    private void OnPacketReceived(Packet packet)
    {
        if (packet.Type != PacketType.Directory) return;
        UiHelper.RunOnUi(this, () =>
        {
            _received = true;
            // [개념] ?? [] : 응답 데이터가 null이어도 빈 목록으로 처리해 아래 LINQ에서 NullReferenceException이 나지 않게 한다.
            _all = (packet.GetData<List<UserInfo>>() ?? [])
                .Where(u => !_exclude.Contains(u.EmployeeNo))
                .OrderBy(u => u.Department).ThenBy(u => u.Name).ToList();
            Fill();
        });
    }

    /// <summary>찾기 글자에 맞는 사람만 목록에 채운다. 체크한 사람은 걸러져 안 보여도 기억한다.</summary>
    private void Fill()
    {
        string q = _filter.Text.Trim();
        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (UserInfo u in _all.Where(u => q.Length == 0 ||
                     u.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                     u.Department.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                     u.EmployeeNo.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new ListViewItem(u.DisplayName) { Tag = u, Checked = _checked.Contains(u.EmployeeNo) };
            row.SubItems.Add(u.Department);
            row.SubItems.Add(u.EmployeeNo);
            _list.Items.Add(row);
        }
        _list.EndUpdate();
        _filling = false;
        UpdateStatus();
    }

    /// <summary>지금 목록에 보이는 사람을 모두 선택하거나 모두 해제한다 (찾기로 걸러져 안 보이는 사람의 선택은 그대로).</summary>
    // [왜] "보이는 사람만" 대상으로 한다. 찾기로 가려진 사람의 선택은 건드리지 않으므로, 부서로 찾아 전체 선택 → 다른 부서로 찾아 추가 선택 같은 사용이 가능하다.
    private void SetAllVisible(bool check)
    {
        _filling = true; // 줄마다 ItemChecked가 오지 않게 하고, 끝나고 한 번만 상태를 갱신한다
        _list.BeginUpdate();
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is not UserInfo u) continue;
            item.Checked = check;
            if (check) _checked.Add(u.EmployeeNo); else _checked.Remove(u.EmployeeNo);
        }
        _list.EndUpdate();
        _filling = false;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_multi)
        {
            if (_all.Count == 0 && _received)
            {
                _status.Text = "초대할 수 있는 사람이 없습니다. (회사 사람이 모두 이미 이 방 멤버입니다)";
                _ok.Enabled = false;
                return;
            }
            bool over = _checked.Count > _maxSelect;
            bool canRaise = ConfirmOverLimit is not null;
            _status.Text = $"{_list.Items.Count}명 표시 · {_checked.Count}명 선택" + (!over ? ""
                : canRaise ? $" · 남은 자리 {_maxSelect}명 → 정원을 늘려 초대할 수 있어요"
                : $" · 남은 자리는 {_maxSelect}명입니다. {_checked.Count - _maxSelect}명을 빼 주세요 (정원은 방장이 바꿀 수 있어요)");
            _status.ForeColor = over && !canRaise ? UiHelper.Danger : UiHelper.Muted;
            _ok.Enabled = _checked.Count > 0 && (!over || canRaise);
            // [읽는 법] 확인 버튼 글자가 상황에 따라 바뀐다(정원 초과 + 늘릴 권한 → "정원 늘리고 초대"). 글자가 길어지면 버튼 폭도 글자 크기에 맞춰 넓힌다(작아지지는 않게 Max).
            string okText = over && canRaise ? "정원 늘리고 초대" : _okText;
            if (_ok.Text != okText)
            {
                _ok.Text = okText;
                _ok.Width = Math.Max(_ok.Width, TextRenderer.MeasureText(okText, _ok.Font).Width + LogicalToDeviceUnits(28));
            }

            // [전체 선택] 표시를 지금 상태에 맞춘다: 보이는 사람이 모두 선택됐을 때만 체크
            bool allVisible = _list.Items.Count > 0 && _list.Items.Cast<ListViewItem>().All(i => i.Checked);
            _syncingSelectAll = true;
            _selectAll.Checked = allVisible;
            _syncingSelectAll = false;
            _selectAll.Enabled = _list.Items.Count > 0;
            _selectAll.Text = string.IsNullOrWhiteSpace(_filter.Text) ? $"전체 선택 ({_list.Items.Count}명)" : $"찾은 사람 전체 선택 ({_list.Items.Count}명)";
        }
        else
        {
            _status.Text = _hint is not null ? $"{_list.Items.Count}명 · {_hint}"
                : $"{_list.Items.Count}명 · 더블클릭하면 1:1 대화를 엽니다 (접속하지 않은 사람에게도 보낼 수 있어요)";
        }
    }

    private void Accept()
    {
        // [읽는 법] 여러 명 모드는 _checked 집합 기준(화면에서 걸러진 사람 포함), 한 명 모드는 ListView에서 선택된 한 줄(Take(1))이 기준이다.
        Selected = _multi
            ? _all.Where(u => _checked.Contains(u.EmployeeNo)).ToList()
            : _list.SelectedItems.Cast<ListViewItem>().Select(i => (UserInfo)i.Tag!).Take(1).ToList();
        if (Selected.Count == 0) return;
        if (_multi && Selected.Count > _maxSelect)
        {
            // 방장·관리자면 정원을 늘릴지 묻고, 아니면 여기까지 오지 않는다(버튼이 꺼져 있음)
            if (ConfirmOverLimit is null || !ConfirmOverLimit(Selected.Count)) return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
