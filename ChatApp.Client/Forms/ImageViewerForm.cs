using ChatApp.Client.Ui;

namespace ChatApp.Client.Forms;

//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   대화에 올라온 사진을 크게 보고 ‹ › 버튼·← → 키로 앞뒤 사진으로 넘기는 전체 화면 창. 이 창은 "그림 보여 주기 + 어느 번호로 이동하려는지 알리기"만 한다. 사진 목록 관리, 서버에서 받아 오기(다운로드), 캐시는 MainForm.Files가 한다(NavigationRequested → LoadImageViewerItem).
//
// ■ 흐름
//   ① MainForm이 ShowItem(index, count, 제목)으로 현재 위치를 알린다 → 이전 그림을 비우고 "불러오는 중…" 표시
//   ② 사진 파일이 준비되면(이미 내 PC에 있거나 다운로드 완료) ShowImage(path)로 그림을 보여 준다
//   ③ 이동 버튼/키 → Navigate(±1) → CurrentIndex를 계산하고 NavigationRequested로 "이 번호로 이동하고 싶다"만 알린다 → MainForm이 다음 사진을 준비해 다시 ①
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 이미지 수명 관리(Image/Bitmap Dispose) ....... "GDI+ Image Dispose PictureBox leak"
//   - Image.FromFile의 파일 잠금과 복사 해결 ........ "Image.FromFile locks file copy to Bitmap"
//   - 순환 인덱스 계산 ((i + step + n) % n) ........ "circular index modulo negative"
//   - 키 처리(KeyPreview, Handled) ................. "WinForms KeyPreview KeyDown Handled"
//   - 이벤트로 이동 요청만 알리기 ................... "event requests navigation parent loads data"
//   - 예외 필터로 파일 오류만 잡기 .................. "catch when IOException OutOfMemoryException image load"
//
// ■ 면접 예상 질문과 답
//   Q1. 사진을 보여 줄 때 왜 파일을 바로 쓰지 않고 Bitmap으로 한 번 복사하나요?
//       A. Image.FromFile은 그림을 다 쓸 때까지 파일을 잠가 둡니다(다른 곳에서 파일 교체·삭제가 안 됨). 원본을 using으로 열어 Bitmap으로 복사한 뒤 바로 닫으면 파일 잠금이 풀리고, 복사본만 화면에 계속 보여 줄 수 있습니다.
//   Q2. 사진을 넘길 때 메모리는 어떻게 관리하나요?
//       A. 새 사진을 보여 주기 직전/직후에 이전 Bitmap을 Dispose하고(ShowLoading/ShowImage), 창이 닫힐 때도 정리합니다. Bitmap은 관리되지 않는 GDI+ 메모리를 쓰므로 Dispose하지 않으면 사진을 많이 넘길수록 메모리가 쌓입니다.
//   Q3. 마지막 사진에서 "다음"을 누르면요?
//       A. 인덱스를 (현재 + 이동량 + 전체 개수) % 전체 개수로 계산해서 처음으로 돌아갑니다(순환). 한 장뿐이면 버튼을 끄고 이동하지 않습니다.
//   Q4. 이 창이 사진 목록을 직접 갖지 않는 이유는요?
//       A. 사진 목록은 대화(Conversation)에서 나오고 다운로드·캐시는 MainForm.Files의 책임입니다. 이 창은 "몇 번째 / 전체 몇 장"과 그림만 알면 되므로 서버와 파일 저장 방식이 바뀌어도 영향을 받지 않습니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - 이전/다음 버튼의 글꼴(new Font("Segoe UI", 24F))은 창이 닫힐 때 해제하지 않는다(버튼 두 개 분, 작아서 GC 대상). 필드로 만들어 FormClosed에서 해제하면 일관된다.
//   - ShowLoading이 캡션을 "  ·  "로 잘라 앞부분만 쓴다. 파일 이름에 같은 구분 문자열이 들어 있으면 캡션이 잘려 보일 수 있다(드문 경우).
//   - 매우 큰 사진은 Bitmap 복사 때 메모리를 한 번 더 쓴다(OutOfMemoryException은 잡아서 "표시할 수 없습니다"로 안내).
//
// ───────────────────────────────────────────────────────────────────────────

/// <summary>대화에서 받은 사진 원본을 크게 보고 앞뒤 사진으로 이동하는 창.</summary>
internal sealed class ImageViewerForm : Form
{
    private readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(25, 25, 28) };
    private readonly Label _caption = new() { Dock = DockStyle.Bottom, Height = 36, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, BackColor = Color.FromArgb(40, 40, 44) };
    private readonly Button _previous = new() { Text = "‹", Dock = DockStyle.Left, Width = 54, Font = new Font("Segoe UI", 24F), FlatStyle = FlatStyle.Flat };
    private readonly Button _next = new() { Text = "›", Dock = DockStyle.Right, Width = 54, Font = new Font("Segoe UI", 24F), FlatStyle = FlatStyle.Flat };
    // [왜] 지금 화면에 보여 주는 Bitmap을 필드로 들고 있다가, 다음 그림으로 바꿀 때/창을 닫을 때 Dispose하기 위해서다(PictureBox는 Image를 대신 해제해 주지 않는다).
    private Image? _image;
    private int _count;

    // [읽는 법] 인수는 "이동하려는 사진 번호". 이 창은 번호만 알리고, 그 사진을 준비해서 ShowItem/ShowImage로 다시 알려 주는 일은 MainForm이 한다.
    public event Action<int>? NavigationRequested;
    public int CurrentIndex { get; private set; }

    public ImageViewerForm()
    {
        Text = "사진 보기";
        BackColor = Color.FromArgb(25, 25, 28);
        StartPosition = FormStartPosition.CenterParent;
        // [왜] 사진을 크게 보는 용도라 처음부터 최대화한다. 그림은 PictureBoxSizeMode.Zoom으로 비율을 유지한 채 창에 맞춘다.
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
        // [주의] 순서가 중요하다: PictureBox가 참조를 놓게(null) 한 뒤 Dispose해야, 이미 해제된 그림을 그리려다 나는 오류를 피한다.
        FormClosed += (_, _) => { _picture.Image = null; _image?.Dispose(); _image = null; };
    }

    // [읽는 법] Math.Clamp로 번호를 0~(개수-1) 범위에 맞춘다. 전달받은 개수가 바뀌었거나 번호가 틀려도 창이 죽지 않게 하는 방어다.
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
            // [왜] using으로 연 원본(source)은 이 블록을 벗어나면 닫히며 파일 잠금이 풀린다. 복사본(loaded)은 파일과 무관하므로 계속 화면에 둘 수 있다.
            Image loaded = new Bitmap(source); // 파일 핸들을 놓은 뒤에도 창이 계속 표시되게 복사한다
            // [주의] 새 그림을 먼저 화면에 걸고 그다음에 이전 것을 Dispose한다(순서 반대면 그리는 중 해제된 그림을 쓸 위험).
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
        // [개념] C#의 % 는 음수 결과가 나올 수 있다(-1 % 5 = -1). 개수를 한 번 더해서 음수가 되지 않게 한 뒤 나머지를 구하면 "처음 ↔ 마지막"으로 이어지는 순환 이동이 된다.
        CurrentIndex = (CurrentIndex + step + _count) % _count;
        NavigationRequested?.Invoke(CurrentIndex);
    }
}
