using ChatApp.Client.Chat;
using ChatApp.Client.Ui;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ThreadForm.cs - 댓글 창 (카카오톡의 "댓글"처럼 메시지 하나에 달린 댓글만 모아 본다)
// - 맨 위에 원래 메시지, 그 아래 댓글들, 맨 아래 입력칸. 여기서 보낸 글은 원래 메시지에 대한 댓글이 된다.
// - 데이터는 대화창과 같은 Conversation을 본다. 대화에 댓글이 추가·수정·삭제되면 이 창도 바로 바뀐다.
// - 스레드: UI 스레드 전용. 실제 전송은 ChatWindow → MainForm이 한다(이 창은 "보내 달라"고만 알린다).
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class ThreadForm : Form
{
    private readonly Conversation _conv;
    private readonly long _rootId;
    private readonly BubbleChatView _view = new() { Dock = DockStyle.Fill, ShowThreadChips = false };
    private readonly Label _title = new();
    private readonly TextBox _input = new();
    private readonly Button _send = new();
    // 화면에 보이는 복사본 → 대화에 있는 진짜 말풍선 (공감·삭제·수정은 진짜 말풍선으로 알린다)
    private readonly Dictionary<ChatItem, ChatItem> _originals = [];

    /// <summary>댓글 보내기 (보낼 글). 원래 메시지 번호는 ChatWindow가 붙인다.</summary>
    public event Action<string>? SendRequested;
    public event Action<ChatItem, string>? ReactionRequested;
    public event Action<ChatItem>? DeleteRequested;
    public event Action<ChatItem>? EditRequested;

    private int S(int v) => LogicalToDeviceUnits(v);

    public ThreadForm(Conversation conv, ChatItem root, string roomTitle, string myKey, Func<ChatItem, bool> canDelete, Func<ChatItem, bool> canEdit)
    {
        _conv = conv;
        _rootId = root.MessageId;

        Text = $"댓글 - {roomTitle}";
        Font = Theme.Body;
        BackColor = Theme.Surface;
        Size = LogicalToDeviceUnits(new Size(400, 560));
        MinimumSize = LogicalToDeviceUnits(new Size(320, 360));
        StartPosition = FormStartPosition.WindowsDefaultLocation;

        _title.Dock = DockStyle.Top;
        _title.Height = S(40);
        _title.Padding = new Padding(S(12), 0, S(12), 0);
        _title.TextAlign = ContentAlignment.MiddleLeft;
        _title.Font = Theme.BodyBold;
        _title.ForeColor = Color.White;
        _title.BackColor = Theme.Primary;
        _title.AutoEllipsis = true;
        _title.UseMnemonic = false;

        _view.MyKey = myKey;
        _view.CanDelete = item => _originals.TryGetValue(item, out ChatItem? o) && canDelete(o);
        _view.CanEdit = item => _originals.TryGetValue(item, out ChatItem? o) && canEdit(o);
        _view.ReplyRequested += _ => _input.Focus(); // 이 창에서는 무엇을 눌러도 원래 메시지에 대한 댓글
        _view.ReactionRequested += (item, emoji) => { if (_originals.TryGetValue(item, out ChatItem? o)) ReactionRequested?.Invoke(o, emoji); };
        _view.DeleteRequested += item => { if (_originals.TryGetValue(item, out ChatItem? o)) DeleteRequested?.Invoke(o); };
        _view.EditRequested += item => { if (_originals.TryGetValue(item, out ChatItem? o)) EditRequested?.Invoke(o); };

        _input.Multiline = true;
        _input.AcceptsReturn = true;
        _input.BorderStyle = BorderStyle.None;
        _input.Font = Theme.Body;
        _input.MaxLength = 500;
        _input.Dock = DockStyle.Fill;
        _input.PlaceholderText = "댓글 입력 (Enter 보내기, Shift+Enter 줄바꿈)";
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Send(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
        };
        _send.Text = "댓글";
        _send.Dock = DockStyle.Right;
        _send.Width = S(64);
        _send.FlatStyle = FlatStyle.Flat;
        _send.FlatAppearance.BorderSize = 0;
        _send.BackColor = Theme.Primary;
        _send.ForeColor = Color.White;
        _send.Font = Theme.BodyBold;
        _send.Cursor = Cursors.Hand;
        _send.Click += (_, _) => Send();

        var inputBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(6)), BackColor = Theme.Surface };
        inputBox.Controls.Add(_input);
        inputBox.Controls.Add(_send);
        _input.BringToFront();
        var inputArea = new Panel { Dock = DockStyle.Bottom, Height = S(72), Padding = new Padding(S(8)), BackColor = Theme.Background };
        inputArea.Controls.Add(inputBox);

        Controls.Add(_view);
        Controls.Add(inputArea);
        Controls.Add(_title);
        _view.BringToFront();

        Rebuild();
        conv.ItemAdded += OnItemAdded;
        conv.ItemChanged += OnItemChanged;
        conv.Reloaded += Rebuild;
        FormClosed += (_, _) =>
        {
            conv.ItemAdded -= OnItemAdded;
            conv.ItemChanged -= OnItemChanged;
            conv.Reloaded -= Rebuild;
        };
        Shown += (_, _) => { _view.ScrollToBottom(); _input.Focus(); };
    }

    /// <summary>이 창이 보여 주는 원래 메시지 번호.</summary>
    public long RootId => _rootId;

    private bool InThread(ChatItem item) => item.MessageId == _rootId || item.ReplyTo?.MessageId == _rootId;

    private void OnItemAdded(ChatItem item) { if (InThread(item)) Rebuild(); }
    private void OnItemChanged(ChatItem item) { if (InThread(item)) Rebuild(); }

    /// <summary>대화에서 원래 메시지와 댓글을 다시 모아 복사본으로 보여 준다 (댓글 수가 많지 않아 통째로 다시 만들어도 가볍다).</summary>
    private void Rebuild()
    {
        _originals.Clear();
        var clones = new List<ChatItem>();
        foreach (ChatItem original in _conv.GetThread(_rootId))
        {
            ChatItem clone = original.CloneForView();
            _originals[clone] = original;
            clones.Add(clone);
        }
        ChatItem? root = _conv.FindMessage(_rootId);
        int count = clones.Count(c => c.MessageId != _rootId && !c.Deleted);
        string who = root is null ? "" : root.Kind == ChatItemKind.Mine ? "나" : root.Sender;
        string preview = root is null ? "" : root.DisplayText.ReplaceLineEndings(" ");
        _title.Text = $"댓글 {count}개 · {who}: {preview}";
        _view.SetItems(clones);
    }

    public void SetInputEnabled(bool enabled)
    {
        _input.Enabled = enabled;
        _send.Enabled = enabled;
        _send.BackColor = enabled ? Theme.Primary : Theme.Offline;
    }

    private void Send()
    {
        string text = _input.Text.Trim();
        if (text.Length == 0 || !_input.Enabled) return;
        // 원래 메시지가 그사이 지워졌어도 댓글은 달 수 있다 (카카오톡과 같음). 서버가 인용을 "삭제된 메시지입니다."로 붙인다.
        SendRequested?.Invoke(text);
        _input.Clear();
        _input.Focus();
    }
}
