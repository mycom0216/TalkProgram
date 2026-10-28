using ChatApp.Client.Chat;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;

namespace ChatApp.Client.Forms;

/// <summary>현재 불러온 방 대화에서 파일·사진·동영상을 모아 보여 준다.</summary>
internal sealed class RoomFilesForm : Form
{
    private readonly ListView _files = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
    private readonly List<ChatItem> _items;
    private readonly HashSet<string> _fileIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Button _loadMore = new() { Text = "이전 파일 더 불러오기", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(8, 8, 8, 8) };
    public event Action<ChatItem>? OpenRequested;
    public event Action<ChatItem>? SaveRequested;
    public event Action<DateTime?, string?>? LoadMoreRequested;

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
        AddItems(items);
        _files.DoubleClick += (_, _) => { if (_files.SelectedIndices.Count > 0) OpenRequested?.Invoke(_items[_files.SelectedIndices[0]]); };
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

    public void RequestNextPage() => BeginLoadMore();

    private void BeginLoadMore()
    {
        if (!_loadMore.Enabled) return;
        _loadMore.Enabled = false;
        _loadMore.Text = "불러오는 중...";
        LoadMoreRequested?.Invoke(OldestTime(), OldestFileId());
    }

    public void AddPage(IEnumerable<SharedFile> files, bool hasMore, string? error = null)
    {
        var additions = files.Select(file => new ChatItem
        {
            Kind = ChatItemKind.Other, Sender = file.SenderName, SenderKey = file.SenderNo,
            Time = file.SentAt, File = new ChatFile { Info = file },
        }).ToList();
        AddItems(additions);
        _loadMore.Enabled = error is not null || hasMore;
        _loadMore.Text = error is not null ? "다시 시도" : hasMore ? "이전 파일 더 불러오기" : "모든 파일을 불러왔습니다";
        _status.Text = error ?? $"파일 {_items.Count}개";
    }

    private void AddItems(IEnumerable<ChatItem> items)
    {
        foreach (ChatItem item in items.Where(x => x.File is not null).OrderByDescending(x => x.Time))
        {
            if (!_fileIds.Add(item.File!.Info.FileId)) continue;
            _items.Add(item);
            _files.Items.Add(new ListViewItem([item.File.Info.FileName, item.File.Info.Kind.ToString(), item.Sender,
                item.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm")]));
        }
    }

    private DateTime? OldestTime() => _items.Where(x => x.File is not null).Select(x => (DateTime?)x.Time).Min();
    private string? OldestFileId() => _items.Where(x => x.File is not null).OrderBy(x => x.Time).FirstOrDefault()?.File?.Info.FileId;
}
