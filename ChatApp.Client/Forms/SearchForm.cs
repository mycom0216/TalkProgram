using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] SearchForm.cs - "전체 대화 검색" 창 (서버 DB에서 찾는다)
// - 대화창의 Ctrl+F는 "지금 불러온 대화" 안에서만 찾는다. 이 창은 서버에 저장된 내 모든 대화
//   (내가 멤버인 방 + 내 1:1)에서 찾는다. 범위는 서버가 정한다(남의 방·남의 1:1은 절대 나오지 않음).
// - 결과를 더블클릭하면 그 대화창을 연다 (HitActivated 이벤트 → MainForm).
// - 스레드: 화면은 UI 스레드. 서버 응답(SearchRes)은 수신 스레드에서 와서 RunOnUi로 넘긴다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   서버 DB 전체에서 내 대화를 찾는 검색 창. 검색어를 서버(SearchReq)에 보내고 결과(SearchRes)를 목록에 보여 준다. 결과를 더블클릭/Enter하면 HitActivated 이벤트로 메인 창이 그 대화를 연다. (대화창 Ctrl+F는 "지금 불러온 대화 안"만 찾고, 이 창은 서버에 저장된 전체를 찾는다.)
//
// ■ 흐름
//   ① Search(): 검색어 길이(최소 SearchRequest.MinKeywordLength자) 확인 → SearchReq 전송 → _waiting = true, 버튼 비활성화
//   ② 응답이 수신 스레드로 오면 RunOnUi로 넘겨 처리. _waiting이 아니면(내가 보낸 요청이 아니면) 무시
//   ③ ShowResult(): 방/1:1 구분 표시("# 방이름" 또는 "1:1 상대"), 시각은 내 PC 시간으로, 줄바꿈은 공백으로 바꿔 한 줄로 보여 준다
//   ④ 결과 수가 MaxResults(100)에 닿았으면 "최근 N개만 보여 줍니다. 검색어를 더 자세히…" 안내
//
// ■ 검색 범위는 누가 정하나 (중요)
//   화면은 키워드만 보낸다. "어느 방·어느 1:1을 찾을지"는 서버가 로그인한 사용자 기준으로 정한다(내가 멤버인 방 + 내 1:1). 그래서 클라이언트를 고쳐도 남의 방·남의 대화는 나오지 않는다.
//   서버는 같은 사용자의 검색을 2초 안에 다시 받으면 거절한다(MessageService.HandleSearch의 LastSearchTicks). 이 창은 응답이 올 때까지 버튼을 꺼 두는 것으로 연타를 줄인다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 서버 쪽 범위 제한(권한은 서버가 결정) .......... "server-side authorization scope search results"
//   - 요청 빈도 제한(Rate limiting) ................... "rate limiting per session cooldown"
//   - 결과 상한과 "더 자세히 입력" 안내 ............... "limit search results pagination top N"
//   - 느슨한 결합: 이벤트로 열기만 요청 .............. "event callback open item parent form"
//   - 응답 기다림 플래그로 내 응답만 처리 ............. "pending flag match response to request"
//
// ■ 면접 예상 질문과 답
//   Q1. 검색을 클라이언트가 아니라 서버에서 하는 이유는요?
//       A. 클라이언트에는 최근 불러온 일부 대화만 있습니다. 모든 대화를 찾으려면 DB에서 해야 하고, 누가 어느 대화를 볼 수 있는지(멤버 여부)는 서버만 정확히 알기 때문에 범위도 서버가 정합니다.
//   Q2. 남의 대화가 검색되지 않는다는 걸 어떻게 보장하나요?
//       A. 요청에는 키워드만 담고, 서버가 세션의 로그인 사용자를 기준으로 "내가 멤버인 방 + 내 1:1"만 조회합니다. 클라이언트가 보낸 값으로 범위를 정하지 않습니다.
//   Q3. 검색 결과를 더블클릭하면 어떻게 그 대화가 열리나요?
//       A. 이 창은 HitActivated 이벤트로 SearchHit(방/상대 정보)를 메인 창에 넘기기만 합니다. 대화창을 열고 관리하는 책임은 메인 창에 있어서 이 창은 대화창 코드를 몰라도 됩니다.
//   Q4. 검색을 너무 자주 누르면요?
//       A. 화면에서 응답 전까지 버튼을 끄고, 서버도 2초 제한을 걸어 DB 부하를 막습니다. 서버가 거절하면 Error 패킷이 와서 이 창의 상태 줄에 표시됩니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - 서버가 보낸 Error 패킷은 이 창이 기다리는 중이어도 메인 창의 일반 오류 처리(MainForm.Network의 case PacketType.Error)에도 도달한다. AddFriendForm은 IsWaiting으로 메인 창이 넘기게 하지만 이 창에는 그런 장치가 없어서, 검색이 거절되면 마지막으로 활성화된 대화창에도 오류 줄이 하나 들어갈 수 있다(코드로 확인함, 실제 화면에서 재현해 본 것은 아님).
//   - 응답이 오지 않으면 _waiting이 풀리지 않아 [검색]이 계속 꺼진다.
//   - 검색어는 서버에서 부분 일치로 찾는다고 알려져 있지만(헤더 설명 기준), 대량 데이터에서의 속도는 서버 쿼리/색인에 달려 있다(이 파일의 책임이 아님).
// ───────────────────────────────────────────────────────────────────────────

/// <summary>전체 대화 검색 창. 검색어를 서버에 보내 결과를 목록으로 보여 준다.</summary>
public sealed class SearchForm : Form
{
    private readonly ChatClient _client;
    private readonly TextBox _keyword = new() { Dock = DockStyle.Fill, PlaceholderText = $"검색어 ({SearchRequest.MinKeywordLength}자 이상)" };
    private readonly Button _searchButton = UiHelper.CreateButton("검색", primary: true);
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = UiHelper.Muted, TextAlign = ContentAlignment.MiddleLeft };
    // [왜] 내가 검색을 보낸 뒤에만 true. 응답 패킷(SearchRes/Error)은 모든 구독자에게 오므로, "내 응답인지" 가리는 표시로 쓴다.
    private bool _waiting;

    /// <summary>결과를 더블클릭(또는 Enter)함 → 메인 창이 그 대화를 연다.</summary>
    // [개념] SearchHit에는 방 번호 또는 상대 사원번호가 들어 있다. 메인 창이 이것으로 알맞은 대화창을 연다(OpenSearchHit).
    public event Action<SearchHit>? HitActivated;

    /// <summary>검색 창을 만든다. client로 검색 요청을 서버에 보낸다.</summary>
    public SearchForm(ChatClient client)
    {
        _client = client;
        Text = $"{Theme.AppName} - 전체 대화 검색";
        Font = UiHelper.BaseFont;
        StartPosition = FormStartPosition.CenterScreen;
        Size = LogicalToDeviceUnits(new Size(720, 520));
        MinimumSize = LogicalToDeviceUnits(new Size(480, 320));
        BackColor = Color.White;

        _list.Columns.Add("시각", LogicalToDeviceUnits(120));
        _list.Columns.Add("대화", LogicalToDeviceUnits(140));
        _list.Columns.Add("보낸 사람", LogicalToDeviceUnits(110));
        _list.Columns.Add("내용", LogicalToDeviceUnits(320));
        _list.DoubleClick += (_, _) => ActivateSelected();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ActivateSelected(); } };
        // 내용 칸이 창 너비를 채우게
        // [읽는 법] 앞 세 칸 폭을 빼고 남는 폭을 "내용" 칸에 준다(최소 120). AdminForm.StretchColumn과 같은 아이디어의 간단한 버전.
        _list.Resize += (_, _) =>
        {
            int others = _list.Columns[0].Width + _list.Columns[1].Width + _list.Columns[2].Width;
            _list.Columns[3].Width = Math.Max(LogicalToDeviceUnits(120), _list.ClientSize.Width - others - 4);
        };

        _keyword.MaxLength = 50;
        _searchButton.Dock = DockStyle.Right;
        _searchButton.Click += (_, _) => Search();
        var top = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(34), Padding = new Padding(0, 0, 0, LogicalToDeviceUnits(6)) };
        top.Controls.Add(_keyword);
        top.Controls.Add(_searchButton);
        // [주의] Dock: 오른쪽 버튼(Right)을 먼저 자리 잡게 하고, 입력칸(Fill)을 맨 앞으로 보내 남은 자리를 채우게 한다.
        _keyword.BringToFront();

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(LogicalToDeviceUnits(10)) };
        body.Controls.Add(_list);
        body.Controls.Add(top);
        body.Controls.Add(_status);
        _list.BringToFront();
        Controls.Add(body);
        AcceptButton = _searchButton;

        _status.Text = "내가 참여한 방과 1:1 대화에서 찾습니다. 결과를 더블클릭하면 그 대화를 엽니다.";
        _client.PacketReceived += OnPacketReceived;
        FormClosed += (_, _) => _client.PacketReceived -= OnPacketReceived;
        Shown += (_, _) => _keyword.Focus();
    }

    /// <summary>검색어를 넣고 바로 찾는다 (메인 창에서 열 때).</summary>
    // [읽는 법] 메인 창의 검색 메뉴에서 입력한 글자를 가지고 이 창을 열 때 쓴다. 창이 이미 열려 있어도 같은 방식으로 다시 검색한다.
    public void SearchFor(string keyword)
    {
        _keyword.Text = keyword;
        Search();
    }

    private void Search()
    {
        string keyword = _keyword.Text.Trim();
        if (keyword.Length < SearchRequest.MinKeywordLength)
        {
            _status.Text = $"검색어를 {SearchRequest.MinKeywordLength}자 이상 입력하세요.";
            return;
        }
        if (!_client.Send(Packet.Create(PacketType.SearchReq).WithData(new SearchRequest { Keyword = keyword })))
        {
            _status.Text = "서버와 연결이 끊어져 검색할 수 없습니다.";
            return;
        }
        _waiting = true;
        _searchButton.Enabled = false;
        _status.Text = $"'{keyword}' 찾는 중...";
        // [왜] 서버가 연달아 검색을 막으므로(2초) 버튼도 잠깐 꺼 둔다. 응답이 오면 바로 켠다.
    }

    // [스레드] 수신 스레드 → UI 스레드
    private void OnPacketReceived(Packet packet)
    {
        if (packet.Type is not (PacketType.SearchRes or PacketType.Error)) return;
        UiHelper.RunOnUi(this, () =>
        {
            // [주의] Error 패킷은 다른 기능의 오류도 전부 여기로 온다. 내가 기다리는 중이 아니면 무시해야 엉뚱한 오류가 검색 상태 줄에 뜨지 않는다.
            if (!_waiting) return; // 다른 창의 오류 등
            _waiting = false;
            _searchButton.Enabled = true;
            if (packet.Type == PacketType.Error)
            {
                _status.Text = packet.Body ?? "검색하지 못했습니다.";
                return;
            }
            if (packet.GetData<SearchResult>() is { } result) ShowResult(result);
        });
    }

    private void ShowResult(SearchResult result)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (SearchHit hit in result.Hits)
        {
            // [읽는 법] 방 검색 결과는 RoomId가 있고, 1:1 결과는 RoomId가 null이라 상대 이름(없으면 사원번호)을 보여 준다. ?? 는 값이 null이면 오른쪽 값을 쓴다.
            string where = hit.RoomId is not null ? $"# {hit.RoomName ?? $"방 {hit.RoomId}"}" : $"1:1 {hit.PeerName ?? hit.PeerNo}";
            var row = new ListViewItem(hit.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm")) { Tag = hit };
            row.SubItems.Add(where);
            row.SubItems.Add(hit.SenderName);
            // [왜] ListView 한 칸은 한 줄만 보여 줄 수 있어서, 여러 줄 메시지는 줄바꿈을 공백으로 바꿔 한 줄로 만든다.
            row.SubItems.Add(hit.Body.ReplaceLineEndings(" "));
            _list.Items.Add(row);
        }
        _list.EndUpdate();
        _status.Text = result.Hits.Count == 0 ? $"'{result.Keyword}'이(가) 들어간 대화가 없습니다."
            : result.Hits.Count >= SearchRequest.MaxResults ? $"최근 {result.Hits.Count}개만 보여 줍니다. 검색어를 더 자세히 입력해 보세요."
            : $"{result.Hits.Count}개 찾음 · 더블클릭하면 그 대화를 엽니다.";
    }

    private void ActivateSelected()
    {
        if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is SearchHit hit) HitActivated?.Invoke(hit);
    }
}
