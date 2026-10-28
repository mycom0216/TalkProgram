using ChatApp.Client.Ui;

namespace ChatApp.Client.Forms;

/// <summary>대화에서 받은 사진 원본을 크게 보고 앞뒤 사진으로 이동하는 창.</summary>
internal sealed class ImageViewerForm : Form
{
    private readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(25, 25, 28) };
    private readonly Label _caption = new() { Dock = DockStyle.Bottom, Height = 36, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, BackColor = Color.FromArgb(40, 40, 44) };
    private readonly Button _previous = new() { Text = "‹", Dock = DockStyle.Left, Width = 54, Font = new Font("Segoe UI", 24F), FlatStyle = FlatStyle.Flat };
    private readonly Button _next = new() { Text = "›", Dock = DockStyle.Right, Width = 54, Font = new Font("Segoe UI", 24F), FlatStyle = FlatStyle.Flat };
    private Image? _image;
    private int _count;

    public event Action<int>? NavigationRequested;
    public int CurrentIndex { get; private set; }

    public ImageViewerForm()
    {
        Text = "사진 보기";
        BackColor = Color.FromArgb(25, 25, 28);
        StartPosition = FormStartPosition.CenterParent;
        WindowState = FormWindowState.Maximized;
        KeyPreview = true;
        _previous.FlatAppearance.BorderSize = 0;
        _next.FlatAppearance.BorderSize = 0;
        _previous.ForeColor = Color.White;
        _next.ForeColor = Color.White;
        _previous.Click += (_, _) => Navigate(-1);
        _next.Click += (_, _) => Navigate(1);
        Controls.Add(_picture);
        Controls.Add(_caption);
        Controls.Add(_previous);
        Controls.Add(_next);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Left) { Navigate(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { Navigate(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) Close();
        };
        FormClosed += (_, _) => { _picture.Image = null; _image?.Dispose(); _image = null; };
    }

    public void ShowItem(int index, int count, string title)
    {
        CurrentIndex = Math.Clamp(index, 0, Math.Max(0, count - 1));
        _count = count;
        _caption.Text = $"{title}  ·  {CurrentIndex + 1}/{_count}";
        _previous.Enabled = _count > 1;
        _next.Enabled = _count > 1;
        ShowLoading();
    }

    public void ShowLoading()
    {
        _picture.Image = null;
        _image?.Dispose();
        _image = null;
        _caption.Text = $"{_caption.Text.Split("  ·  ")[0]}  ·  불러오는 중…";
    }

    public void ShowImage(string path)
    {
        try
        {
            using Image source = Image.FromFile(path);
            Image loaded = new Bitmap(source); // 파일 핸들을 놓은 뒤에도 창이 계속 표시되게 복사한다
            _picture.Image = loaded;
            _image?.Dispose();
            _image = loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            ShowLoading();
            _caption.Text = "사진을 표시할 수 없습니다.";
        }
    }

    private void Navigate(int step)
    {
        if (_count < 2) return;
        CurrentIndex = (CurrentIndex + step + _count) % _count;
        NavigationRequested?.Invoke(CurrentIndex);
    }
}
