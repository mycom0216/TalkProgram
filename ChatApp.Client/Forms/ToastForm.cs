using ChatApp.Client.Ui;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ToastForm.cs - 화면 오른쪽 아래에 잠깐 떴다 사라지는 새 메시지 알림 창
// - 역할: 창이 트레이로 숨어 있거나 다른 일을 하는 중일 때 "누가 무슨 말을 했는지" 알려 준다. 누르면 그 대화창을 연다.
// - 스레드: UI 스레드 전용.
// - 연결: MainForm.ShowToast()가 만들고 띄운다. 한 번에 하나만 보이게 이전 알림은 닫는다.
// [왜] 포커스를 빼앗지 않는다(ShowWithoutActivation): 다른 프로그램에 글을 쓰다가 알림 때문에 입력이 끊기면 안 되기 때문.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   화면 오른쪽 아래에 5초간 떠 있다가 사라지는 새 메시지 알림 창. 테두리 없는 TopMost 창을 직접 그리고(OnPaint), 포커스를 가져오지 않는다(ShowWithoutActivation). 클릭하면 Click 이벤트가 나가고(MainForm이 그 대화를 연다) 창은 닫힌다.
//
// ■ 동작 규칙
//   - 위치: 주 모니터의 작업 영역(WorkingArea, 작업 표시줄 제외) 오른쪽 아래 + 12 여백(DPI 배율 반영).
//   - 닫힘: 타이머 5초. 마우스를 올려 두면(MouseEnter) 멈추고, 벗어나면(MouseLeave) 다시 시작한다(5초부터 새로 센다).
//   - 클릭: base.OnClick(e)로 이벤트를 먼저 내보낸 뒤 Close(). 순서가 바뀌면 이미 닫힌 창에서 이벤트가 나갈 수 있다.
//   - 한 번에 하나: 이전 알림 닫기는 MainForm.ShowToast가 한다(이 창은 모름).
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 포커스를 뺏지 않는 창(ShowWithoutActivation) ... "WinForms ShowWithoutActivation toast notification"
//   - 작업 영역(WorkingArea)과 TopMost ................ "Screen.WorkingArea TopMost notification window"
//   - 타이머로 자동 닫기 + 마우스 올리면 멈춤 .......... "auto close timer pause on hover"
//   - 직접 그리기 알림 카드 .......................... "owner drawn form OnPaint avatar text ellipsis"
//
// ■ 면접 예상 질문과 답
//   Q1. 알림이 떠도 쓰던 입력이 끊기지 않는 이유는요?
//       A. ShowWithoutActivation을 true로 해서 창을 보여 줄 때 활성화(포커스 이동)를 하지 않기 때문입니다. TopMost라 다른 창 위에는 보이지만 키보드 입력은 계속 원래 프로그램으로 갑니다.
//   Q2. 메시지 내용이 길면요?
//       A. 제목은 한 줄 말줄임, 본문은 두 줄 안에서 줄바꿈 + 말줄임(EndEllipsis)으로 잘라 보여 줍니다. 전체 내용은 알림을 눌러 대화창에서 봅니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - Screen.PrimaryScreen(주 모니터)에만 뜬다. 메인 창이 다른 모니터에 있어도 알림은 주 모니터 오른쪽 아래에 나타난다(코드 기준, 실제 화면에서 확인하지는 않음).
//   - 알림 안에 보이는 메시지 본문은 그대로 그려지므로, 화면 공유 중에는 내용이 노출될 수 있다(방해 금지 상태에서는 MainForm.ShowToast가 토스트를 아예 만들지 않는다 — 코드로 확인함).
// ───────────────────────────────────────────────────────────────────────────

/// <summary>화면 오른쪽 아래에 5초 동안 뜨는 새 메시지 알림 창. 누르면 Click으로 알리고 닫힌다.</summary>
internal sealed class ToastForm : Form
{
    private const int ShowMilliseconds = 5000;

    private readonly string _title;
    private readonly string _text;
    private readonly string _avatarKey;
    // [개념] System.Windows.Forms.Timer: UI 스레드에서 Tick이 불리는 타이머라 Close() 같은 화면 조작을 안전하게 할 수 있다(System.Threading.Timer는 다른 스레드에서 불린다). FormClosed에서 Dispose한다.
    private readonly System.Windows.Forms.Timer _closeTimer = new() { Interval = ShowMilliseconds };

    private int S(int v) => LogicalToDeviceUnits(v);

    /// <summary>알림 창을 만든다. title은 제목(보낸 사람/방), text는 본문, avatarKey는 아바타 색 키.</summary>
    public ToastForm(string title, string text, string avatarKey)
    {
        _title = title;
        _text = text;
        _avatarKey = avatarKey;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        // [읽는 법] 아래 설정이 한 세트: 테두리 없음 + 작업 표시줄에 안 보임 + 항상 위 + 직접 위치 지정(StartPosition.Manual) = 토스트 알림 모양.
        TopMost = true;                      // 다른 창 위에 보이게
        StartPosition = FormStartPosition.Manual;
        Size = new Size(S(320), S(84));
        BackColor = Theme.Surface;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;

        // 작업 표시줄 바로 위, 오른쪽 아래 구석
        // [읽는 법] ?? 오른쪽은 주 모니터를 못 구했을 때의 대비값. WorkingArea는 작업 표시줄을 뺀 영역이라 알림이 작업 표시줄 위에 겹치지 않는다.
        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Right - Width - S(12), area.Bottom - Height - S(12));

        // [읽는 법] 타이머는 창이 실제로 보인 뒤(Shown)에 시작한다. 마우스가 위에 있는 동안은 멈춰서 읽는 중에 사라지지 않는다.
        _closeTimer.Tick += (_, _) => Close();
        Shown += (_, _) => _closeTimer.Start();
        // 마우스를 올려 두면 읽는 중이므로 닫지 않는다
        MouseEnter += (_, _) => _closeTimer.Stop();
        MouseLeave += (_, _) => _closeTimer.Start();
        FormClosed += (_, _) => _closeTimer.Dispose();
    }

    /// <summary>[개념] true면 Show() 해도 이 창이 활성(포커스)되지 않는다. 지금 쓰고 있는 프로그램의 입력이 끊기지 않는다.</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>알림을 누르면 Click을 알린 뒤 창을 닫는다.</summary>
    // [주의] base.OnClick을 먼저 불러 구독자(MainForm)에게 Click을 알린 다음에 닫는다. 순서를 바꾸면 닫힌 창에서 이벤트가 나갈 수 있다.
    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e); // MainForm이 Click 이벤트로 대화창을 연다
        Close();
    }

    /// <summary>테두리, 왼쪽 하늘색 띠, 아바타, 제목, 본문을 직접 그린다.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        Theme.FillGradient(g, new Rectangle(0, 0, S(5), Height)); // 왼쪽 하늘색 띠

        int size = S(42);
        var avatar = new Rectangle(S(18), (Height - size) / 2, size, size);
        Theme.DrawAvatar(g, avatar, _title, Theme.AvatarColor(_avatarKey), _avatarKey);

        int x = avatar.Right + S(12), w = Width - x - S(12);
        TextRenderer.DrawText(g, _title, Theme.BodyBold, new Rectangle(x, S(16), w, S(20)), Theme.Text,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _text, Theme.Body, new Rectangle(x, S(38), w, S(36)), Theme.SubText,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
    }
}
