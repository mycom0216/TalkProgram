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
    private bool _waiting;

    /// <summary>결과를 더블클릭(또는 Enter)함 → 메인 창이 그 대화를 연다.</summary>
    public event Action<SearchHit>? HitActivated;

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
            string where = hit.RoomId is not null ? $"# {hit.RoomName ?? $"방 {hit.RoomId}"}" : $"1:1 {hit.PeerName ?? hit.PeerNo}";
            var row = new ListViewItem(hit.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm")) { Tag = hit };
            row.SubItems.Add(where);
            row.SubItems.Add(hit.SenderName);
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
