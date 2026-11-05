using ChatApp.Client.Chat;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;

namespace ChatApp.Client.Forms;

//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   방에서 주고받은 파일·사진·동영상을 표로 모아 보여 주는 창. 처음에는 "지금 불러온 대화"에 있는 파일만 보여 주고, [이전 파일 더 불러오기]로 서버에 이전 파일을 요청한다(RoomFilesReq). 열기/저장 같은 실제 동작은 이벤트로 MainForm에 맡긴다.
//
// ■ 페이지 넘기기(커서 방식) 흐름
//   ① 지금 가진 파일 중 "가장 오래된 것"의 (시각, 파일 번호)를 구한다(OldestTime / OldestFileId)
//   ② LoadMoreRequested(시각, 번호)로 MainForm에 알리면 RoomFilesReq(Before, BeforeFileId)를 서버에 보낸다
//   ③ 서버 응답이 오면 MainForm이 AddPage(files, hasMore, error)를 부른다 → 표에 이어 붙이고 버튼 글자를 갱신
//   - "몇 번째 쪽"이 아니라 "이 파일보다 오래된 것"이라고 요청하는 방식이라, 그사이 새 파일이 올라와도 목록이 밀리거나 중복되지 않는다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 커서 기반 페이지네이션 vs 오프셋 ........... "cursor pagination vs offset pagination"
//   - 중복 방지 HashSet(FileId) ................. "HashSet dedupe incoming pages"
//   - ListView 인덱스와 데이터 목록을 나란히 유지 .. "ListView index parallel list mapping"
//   - LINQ(Where/OrderByDescending/Sum/Min) ....... "C# LINQ OrderByDescending Sum nullable Min"
//   - switch 식(KindText) ....................... "C# switch expression"
//   - 이벤트로 열기/저장을 위임 ................... "event delegation open save request to parent"
//
// ■ 면접 예상 질문과 답
//   Q1. 이전 파일을 불러올 때 왜 "페이지 번호"가 아니라 시각+파일 번호를 보내나요?
//       A. 페이지 번호(오프셋) 방식은 그사이 새 파일이 올라오면 같은 항목이 다시 오거나 빠질 수 있습니다. "이 항목보다 오래된 것 N개"를 요청하는 커서 방식은 새 데이터가 들어와도 결과가 안정적입니다. 시각이 같을 수 있어 파일 번호를 함께 보내 순서를 확정합니다.
//   Q2. 같은 파일이 두 번 표시되는 것은 어떻게 막나요?
//       A. _fileIds(HashSet, 대소문자 무시)에 FileId를 넣어 보면서 이미 있으면 건너뜁니다. 처음 보여 준 대화 속 파일과 서버에서 받은 페이지가 겹쳐도 한 번만 나옵니다.
//   Q3. 표에서 선택한 줄과 실제 파일은 어떻게 연결하나요?
//       A. _items와 ListView 줄을 같은 순서로 같이 추가해서, 줄 번호가 곧 _items의 번호가 되게 합니다(SelectedIndices[0] → _items[번호]). 한쪽에만 추가하거나 정렬을 따로 바꾸면 어긋나므로 AddItems 한 곳에서만 추가합니다.
//   Q4. 서버 요청과 응답 처리는 어디서 하나요?
//       A. 이 창은 요청 이벤트(LoadMoreRequested)만 올리고 응답은 MainForm이 받아 AddPage로 넣어 줍니다. 그래서 이 창은 네트워크를 모릅니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - 오른쪽 클릭마다 new ContextMenuStrip()을 만들고 닫아도 Dispose하지 않는다(메뉴 항목이 적고 GC가 정리하지만, 한 번 만들어 재사용하거나 Closed에서 Dispose하면 깔끔하다).
//   - MinimumSize/Size와 열 너비에 LogicalToDeviceUnits를 쓰지 않아 다른 창과 달리 원시 픽셀 값이다. 고DPI 화면에서 상대적으로 작게 보일 수 있다(실제 화면에서 확인하지는 않음).
//   - 한 번 불러온 뒤 오류가 나면 버튼이 "다시 시도"로 바뀌어 같은 지점부터 다시 요청한다.
//
// ───────────────────────────────────────────────────────────────────────────

/// <summary>현재 불러온 방 대화에서 파일·사진·동영상을 모아 보여 준다.</summary>
internal sealed class RoomFilesForm : Form
{
    private readonly ListView _files = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
    private readonly List<ChatItem> _items;
    // [왜] FileId로 중복을 걸러내는 집합. 대소문자 구분 없이 비교(OrdinalIgnoreCase)해서 같은 번호가 대소문자만 달라도 한 번만 표시한다.
    private readonly HashSet<string> _fileIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Button _loadMore = new() { Text = "이전 파일 더 불러오기", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(8, 8, 8, 8) };
    /// <summary>목록에서 파일을 열려 할 때(더블클릭, 메뉴 "열기") 발생한다. 인수는 해당 파일 말풍선.</summary>
    public event Action<ChatItem>? OpenRequested;

    /// <summary>목록에서 "다른 이름으로 저장"을 고를 때 발생한다. 인수는 해당 파일 말풍선.</summary>
    public event Action<ChatItem>? SaveRequested;

    /// <summary>더 오래된 파일 목록을 서버에 요청해야 할 때 발생한다. 인수는 가진 것 중 가장 오래된 파일의 (시각, 번호).</summary>
    // [읽는 법] 인수 = (가장 오래된 파일의 시각, 그 파일 번호). 둘 다 null이면 "처음부터"(가진 파일이 하나도 없을 때). 서버가 이보다 오래된 파일을 찾아 보낸다.
    public event Action<DateTime?, string?>? LoadMoreRequested;

    /// <summary>방 파일 모아보기 창을 만든다. items(지금 불러온 대화)에서 파일 말풍선만 골라 먼저 채운다.</summary>
    public RoomFilesForm(string roomName, IEnumerable<ChatItem> items)
    {
        _items = [];
        Text = $"{roomName} - 파일 모아보기";
        Font = Theme.Body;
        BackColor = Theme.Surface;
        MinimumSize = new Size(560, 380);
        Size = new Size(760, 520);
        StartPosition = FormStartPosition.CenterParent;
        _files.Columns.Add("이름", 260);
        _files.Columns.Add("종류", 90);
        _files.Columns.Add("보낸 사람", 110);
        _files.Columns.Add("날짜", 150);
        _files.Columns.Add("용량", 90, HorizontalAlignment.Right); // 숫자는 오른쪽 맞춤이 비교하기 쉽다
        // [읽는 법] 생성 시 받은 항목(지금 불러온 대화)에서 파일 말풍선만 골라 먼저 채운다. 서버에서 오는 이전 파일은 나중에 AddPage가 이어 붙인다.
        AddItems(items);
        _files.DoubleClick += (_, _) => { if (_files.SelectedIndices.Count > 0) OpenRequested?.Invoke(_items[_files.SelectedIndices[0]]); };
        // [읽는 법] 오른쪽 버튼으로 눌린 줄을 선택 상태로 만든 뒤 메뉴를 띄운다. 줄 번호(index)를 지역 변수에 복사해 두는 이유: 메뉴 항목을 누르는 시점에 선택이 바뀌어도 "눌렀던 그 줄"을 쓰기 위해서다(클로저 캡처).
        _files.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || _files.GetItemAt(e.X, e.Y) is not { } row) return;
            row.Selected = true;
            int index = row.Index;
            var menu = new ContextMenuStrip();
            menu.Items.Add("열기", null, (_, _) => OpenRequested?.Invoke(_items[index]));
            menu.Items.Add("다른 이름으로 저장...", null, (_, _) => SaveRequested?.Invoke(_items[index]));
            menu.Show(_files, e.Location);
        };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        _loadMore.Click += (_, _) => BeginLoadMore();
        footer.Controls.Add(_loadMore);
        footer.Controls.Add(_status);
        Controls.Add(_files);
        Controls.Add(footer);
    }

    /// <summary>더 오래된 파일 목록을 요청한다 (이미 요청 중이면 무시).</summary>
    public void RequestNextPage() => BeginLoadMore();

    // [왜] 버튼을 즉시 끄고 "불러오는 중..."으로 바꿔 같은 요청이 겹치지 않게 한다. 응답이 오면 AddPage가 다시 켠다.
    private void BeginLoadMore()
    {
        if (!_loadMore.Enabled) return;
        _loadMore.Enabled = false;
        _loadMore.Text = "불러오는 중...";
        LoadMoreRequested?.Invoke(OldestTime(), OldestFileId());
    }

    /// <summary>서버에서 받은 파일 한 묶음을 목록에 이어 붙이고 버튼·상태 줄을 갱신한다. error가 있으면 "다시 시도" 상태가 된다.</summary>
    public void AddPage(IEnumerable<SharedFile> files, bool hasMore, string? error = null)
    {
        var additions = files.Select(file => new ChatItem
        {
            Kind = ChatItemKind.Other, Sender = file.SenderName, SenderKey = file.SenderNo,
            Time = file.SentAt, File = new ChatFile { Info = file },
        }).ToList();
        AddItems(additions);
        // [읽는 법] 버튼 상태 규칙: 오류 → "다시 시도"(켬) / 더 있음 → "이전 파일 더 불러오기"(켬) / 끝 → "모든 파일을 불러왔습니다"(끔).
        _loadMore.Enabled = error is not null || hasMore;
        _loadMore.Text = error is not null ? "다시 시도" : hasMore ? "이전 파일 더 불러오기" : "모든 파일을 불러왔습니다";
        _status.Text = error ?? $"파일 {_items.Count}개 · 합계 {FileRules.FormatSize(_items.Sum(x => x.File?.Info.Size ?? 0))}";
    }

    private void AddItems(IEnumerable<ChatItem> items)
    {
        foreach (ChatItem item in items.Where(x => x.File is not null).OrderByDescending(x => x.Time))
        {
            // [개념] HashSet.Add는 "새로 추가되면 true, 이미 있으면 false"를 돌려준다. 확인과 추가를 한 번에 하는 관용 표현.
            if (!_fileIds.Add(item.File!.Info.FileId)) continue;
            _items.Add(item);
            _files.Items.Add(new ListViewItem([item.File.Info.FileName, KindText(item.File.Info.Kind), item.Sender,
                item.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), FileRules.FormatSize(item.File.Info.Size)]));
        }
    }

    private static string KindText(FileKind kind) => kind switch
    {
        FileKind.Image => "사진",
        FileKind.Video => "동영상",
        _ => "파일",
    };

    // [읽는 법] 가진 파일 중 가장 오래된 시각. (DateTime?)로 바꿔 Min()을 쓰면 항목이 없을 때 예외 대신 null이 나온다. 아래 OldestFileId는 같은 기준(시각 오름차순 첫 번째)의 파일 번호.
    private DateTime? OldestTime() => _items.Where(x => x.File is not null).Select(x => (DateTime?)x.Time).Min();
    private string? OldestFileId() => _items.Where(x => x.File is not null).OrderBy(x => x.Time).FirstOrDefault()?.File?.Info.FileId;
}
