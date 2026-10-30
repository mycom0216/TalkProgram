using ChatApp.Client.Ui;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] UiHelper.cs - 화면 공통 도우미 (글꼴·색, 입력 표 배치, 버튼, 대화상자 머리, UI 스레드로 넘기기)
// - 역할: 로그인·회원가입·방 만들기 대화상자가 같은 모양과 배치를 쓰도록 공통 코드를 모아 둔다.
// - 스레드: RunOnUi만 "아무 스레드"에서 불러도 되고, 나머지는 모두 UI 스레드 전용이다.
// - 연결: LoginForm, RegisterForm, CreateRoomForm, MainForm 등에서 사용. 색은 Ui/Theme.cs와 맞춘다.
// [왜] Visual Studio 디자이너(.Designer.cs) 대신 코드로 화면을 만든다.
//   - 화면 구조가 코드에 그대로 보여 공부·리뷰하기 쉽고, Git에서 변경 내용(diff)을 읽기 쉽다.
//   - 공통 배치를 함수로 만들어 두면 창마다 같은 코드를 반복하지 않아도 된다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   여러 창이 같이 쓰는 "화면 부품 공장". 입력 표(CreateFormTable → AddFieldRow/AddSpanRow), 버튼(CreateButton/CreateButtonRow), 대화상자 머리(AddDialogHeader)를 함수로 만들어 두고, 마지막의 RunOnUi가 "수신 스레드 → UI 스레드" 전환을 책임진다. 이 파일에서 가장 중요한 면접 주제는 RunOnUi다.
//
// ■ 읽는 순서
//   ① 상수(글꼴·색) → ② 표 만들기와 줄 추가 → ③ 버튼 → ④ 대화상자 머리(그라데이션 + 제목) → ⑤ RunOnUi
//   대화상자를 만들 때의 정해진 흐름: table = CreateFormTable() → AddFieldRow/AddSpanRow로 줄 쌓기 → Controls.Add(table) → AddDialogHeader(this, table, 제목). (머리는 반드시 표를 다 채운 뒤에 붙인다.)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - UI 스레드 규칙과 BeginInvoke ............... "WinForms cross-thread operation BeginInvoke vs Invoke"
//   - Invoke 데드락 ............................. "Control.Invoke deadlock UI thread waiting"
//   - TableLayoutPanel 자동 크기 ................. "TableLayoutPanel AutoSize column row"
//   - LogicalToDeviceUnits(고DPI) ............... "WinForms LogicalToDeviceUnits PerMonitorV2"
//   - static readonly Font 공유 .................. "share Font instance static readonly GDI handle"
//   - 후위 증가(RowCount++) ...................... "C# postfix increment returns old value"
//   - params 배열, 선택적 매개변수, 오버로딩 ......... "C# params optional parameters overload"
//   - 디자이너 대신 코드로 UI 만들기 ............... "WinForms code-only UI vs Designer"
//
// ■ 면접 예상 질문과 답
//   Q1. 수신 스레드에서 화면을 바꿀 때 왜 Invoke가 아니라 BeginInvoke를 쓰나요?
//       A. WinForms 컨트롤은 만든 스레드(UI 스레드)에서만 건드릴 수 있어서 작업을 UI 스레드의 메시지 큐에 넣어야 합니다. Invoke는 끝날 때까지 기다리므로, UI 스레드가 마침 수신 스레드가 쥔 자원을 기다리는 중이면 서로 기다리는 교착 상태가 될 수 있고, UI가 느릴 때 수신도 같이 밀립니다. BeginInvoke는 큐에 넣고 바로 돌아와 수신 스레드가 막히지 않습니다.
//   Q2. RunOnUi에서 IsDisposed/IsHandleCreated를 확인하고 예외도 잡는 이유는요?
//       A. 창이 이미 닫혔거나 아직 핸들이 없으면 BeginInvoke가 예외를 던집니다. 그런데 확인한 직후에 UI 스레드가 창을 닫을 수도 있어서(경쟁 상태) 확인만으로는 막을 수 없습니다. 그래서 두 가지(사전 확인 + 예외 처리)를 같이 씁니다. 보여 줄 곳이 없으면 작업을 버려도 문제가 없습니다.
//   Q3. 디자이너 파일(.Designer.cs)을 쓰지 않고 코드로 화면을 만든 이유는요?
//       A. 화면 구조가 코드에 그대로 보여 읽고 리뷰하기 쉽고, 변경 내용(diff)이 사람이 읽을 만하게 나옵니다. 공통 배치를 함수로 만들어 중복도 줄입니다. 대신 화면을 눈으로 보며 고치는 편리함은 포기합니다.
//   Q4. 폰트를 static으로 하나만 만드는 이유는요?
//       A. Font는 GDI 핸들 같은 운영체제 자원을 씁니다. 창을 열 때마다 새로 만들면 핸들이 쌓이므로, 프로그램 전체에서 공유하는 읽기 전용 값으로 둡니다(프로그램이 끝날 때까지 쓰므로 Dispose하지 않아도 된다).
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - RunOnUi는 핸들이 아직 없으면 작업을 "버린다". 따라서 창이 화면에 만들어지기(Show) 전에 도착한 패킷은 처리되지 않는다. 대부분의 창이 Shown 이벤트에서 요청을 보내는 것도 이 때문에 안전하다.
//   - 색·글꼴이 UiHelper(BaseFont, Primary 등)와 Ui/Theme(Body, Primary 등) 두 군데에 나뉘어 있다. UiHelper.Primary는 Theme.Primary를 가리키지만 글꼴은 서로 별개라 창마다 모양이 약간 다를 수 있다.
//   - CreateButton의 MinimumSize(96, 34)와 CreateFormTable의 Padding(24, 20, 24, 16) 등은 원시 숫자라 고DPI에서는 AutoSize/배율 처리에 기대고 있다(입력칸 폭은 LogicalToDeviceUnits로 맞춤).
// ───────────────────────────────────────────────────────────────────────────

/// <summary>화면 공통 스타일과 배치 도우미 (디자이너 없이 코드로 화면을 만든다).</summary>
internal static class UiHelper
{
    // [개념] static readonly: 프로그램 전체에서 하나만 만들어 같이 쓰는 읽기 전용 값.
    //   Font는 윈도우 GDI 자원을 쓰므로 창마다 새로 만들기보다 한 번 만들어 공유하는 편이 가볍다.
    // [주의] 이 글꼴 3개는 프로그램 내내 쓰는 공유 객체다. 창이 닫힐 때 절대 Dispose하면 안 된다(다른 창이 같은 객체를 쓰고 있다). Dispose가 필요한 것은 창이 따로 만든 글꼴뿐이다.
    public static readonly Font BaseFont = new("맑은 고딕", 10F);
    public static readonly Font BoldFont = new("맑은 고딕", 10F, FontStyle.Bold);
    public static readonly Font TitleFont = new("맑은 고딕", 15F, FontStyle.Bold);

    public static readonly Color Primary = Theme.Primary;                 // 하늘색 테마의 파랑
    public static readonly Color Whisper = Color.FromArgb(124, 58, 237);  // 보라: 귓속말
    public static readonly Color Muted = Color.FromArgb(107, 114, 128);   // 회색: 시스템 메시지
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);    // 빨강: 오류
    public static readonly Color HeaderBack = Color.FromArgb(239, 244, 252);

    /// <summary>2열(라벨 | 입력칸) 입력 표. 내용 크기에 맞춰 자동으로 커진다.</summary>
    // [개념] TableLayoutPanel: 컨트롤을 엑셀 표처럼 행·열 칸에 배치하는 컨테이너.
    //   좌표(Location)를 일일이 계산하지 않아도 칸에 맞춰 정렬되고, 글꼴 크기/DPI가 바뀌어도 자동으로 다시 배치된다.
    public static TableLayoutPanel CreateFormTable()
    {
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(24, 20, 24, 16),
            Location = new Point(0, 0), // Dock 대신 고정 위치: 창(AutoSize)이 표 크기에 맞춰진다
        };
        // 두 열 모두 "내용 크기에 맞춤": 라벨 열은 가장 긴 라벨 폭, 입력 열은 가장 넓은 입력칸 폭이 된다.
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return table;
    }

    /// <summary>두 칸을 차지하는 한 줄(제목, 버튼 줄, 안내 문구 등).</summary>
    public static void AddSpanRow(TableLayoutPanel table, Control control)
    {
        // [개념] table.RowCount++ : 현재 행 수를 row에 담은 "다음에" 1 늘린다(후위 증가). 즉 새 행의 번호를 얻는다.
        // [읽는 법] 아래 AddFieldRow(오버로드 2개)와 같은 패턴: 새 줄 번호를 얻고 → 줄 높이 규칙 추가(자동) → 컨트롤을 (열, 줄)에 넣는다.
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2); // 0열부터 2칸을 합쳐서 차지 (엑셀의 "셀 병합")
    }

    /// <summary>"라벨 | 입력칸" 한 줄.</summary>
    public static void AddFieldRow(TableLayoutPanel table, string label, Control input, int inputWidth)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left, // 칸 안에서 왼쪽 + 세로 가운데 정렬 (위아래 Anchor가 없으면 세로 가운데)
            Margin = new Padding(0, 6, 12, 6),
        }, 0, row);

        // [개념] LogicalToDeviceUnits: "100% 화면 배율 기준 픽셀"을 현재 모니터 배율(DPI)에 맞는 실제 픽셀로 바꾼다.
        //   예) 150% 배율 모니터에서 220 → 330. 이걸 안 하면 고해상도 모니터에서 입력칸이 글자보다 작아 보인다.
        // [왜] 호출하는 쪽은 100% 배율 기준 숫자(220 등)만 넘기고, 실제 화면 배율에 맞춘 픽셀로 바꾸는 일은 여기서 한 번에 한다.
        input.Width = table.LogicalToDeviceUnits(inputWidth);
        input.Margin = new Padding(0, 4, 0, 4);
        table.Controls.Add(input, 1, row);
    }

    /// <summary>"라벨 | 여러 컨트롤 묶음" 한 줄 (폭은 묶음이 스스로 정한다).</summary>
    // [개념] 메서드 오버로딩: 이름은 같고 매개변수가 다른 메서드. 폭(inputWidth)을 안 넘기면 이 버전이 불린다.
    //   회원가입 화면의 [아이디] @ [도메인] 처럼 여러 컨트롤을 담은 패널을 넣을 때 쓴다.
    public static void AddFieldRow(TableLayoutPanel table, string label, Control input)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 12, 6),
        }, 0, row);
        input.Margin = new Padding(0, 4, 0, 4);
        table.Controls.Add(input, 1, row);
    }

    /// <summary>공통 버튼. primary = true면 파란 배경의 강조 버튼(가입, 로그인, 만들기 등 주요 동작).</summary>
    // [개념] 선택적 매개변수(bool primary = false): 호출할 때 생략하면 기본값 false가 쓰인다.
    public static Button CreateButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(96, 34),
            Margin = new Padding(6, 10, 0, 0),
            FlatStyle = FlatStyle.System, // 윈도우 기본 버튼 모양
        };
        if (primary)
        {
            // [왜] FlatStyle.System은 배경색을 바꿀 수 없어서, 색을 칠하려면 Flat으로 바꾸고 테두리를 없앤다.
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Primary;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderSize = 0;
        }
        return button;
    }

    /// <summary>오른쪽 정렬 버튼 줄.</summary>
    // [개념] params Button[]: 버튼을 개수 제한 없이 쉼표로 나열해 넘길 수 있다. 예) CreateButtonRow(cancel, ok)
    // [개념] FlowLayoutPanel: 컨트롤을 한 방향으로 차례차례 늘어놓는 컨테이너. RightToLeft면 오른쪽부터 채운다.
    public static FlowLayoutPanel CreateButtonRow(params Button[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
        };
        // RightToLeft라 마지막 버튼이 맨 오른쪽에 오도록 역순으로 넣는다
        // (윈도우 대화상자 관례: [취소] [확인] 처럼 주요 버튼이 오른쪽 끝)
        for (int i = buttons.Length - 1; i >= 0; i--)
            row.Controls.Add(buttons[i]);
        return row;
    }

    /// <summary>
    /// 대화상자 위쪽에 하늘색 그라데이션 머리를 붙인다. 표(table)를 다 채우고 폼에 넣은 뒤 호출한다.
    /// </summary>
    // [주의] 호출 순서가 중요하다: 표에 모든 줄을 넣은 "뒤에" 불러야 GetPreferredSize가 최종 폭을 알려 준다.
    // [읽는 법] header.Width = table.GetPreferredSize(...).Width : 표가 완성된 뒤의 "원하는 폭"을 물어 머리 폭을 맞추고, 표 폭이 바뀔 때(SizeChanged)도 따라간다. 표에 줄을 더 넣기 전에 부르면 폭이 틀려진다.
    public static void AddDialogHeader(Form form, TableLayoutPanel table, string title)
    {
        var header = new GradientPanel { Location = Point.Empty, Height = form.LogicalToDeviceUnits(60) };

        // [개념] Paint 이벤트: 윈도우가 "이 영역을 다시 그려라"라고 할 때마다(처음 표시, 가려졌다 보임 등) 불린다.
        //   여기에 그리는 코드를 넣어야 창을 움직이거나 가렸다가 다시 봐도 글자가 사라지지 않는다.
        // [개념] TextRenderer.DrawText: GDI 방식 글자 그리기. WinForms 기본 컨트롤과 똑같은 모양으로 한글이 선명하게 그려진다.
        header.Paint += (_, e) => TextRenderer.DrawText(e.Graphics, title, Theme.Title,
            new Rectangle(form.LogicalToDeviceUnits(24), 0, header.Width, header.Height), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix); // NoPrefix: '&' 글자를 단축키 표시로 해석하지 않음

        // 표를 머리 높이만큼 아래로 내리고, 머리 폭은 표 폭에 맞춘다.
        table.Location = new Point(0, header.Height);
        header.Width = table.GetPreferredSize(Size.Empty).Width;
        table.SizeChanged += (_, _) => header.Width = table.Width; // 표 폭이 바뀌면 머리도 맞춘다
        form.Controls.Add(header);
    }

    /// <summary>
    /// 수신 스레드에서 받은 일을 UI 스레드로 넘긴다. 창이 이미 닫혔으면 조용히 무시.
    /// (BeginInvoke = 기다리지 않고 넘기기만 함 → 수신 스레드가 막히지 않는다)
    /// </summary>
    // [개념] WinForms의 UI 스레드 규칙
    //   - 컨트롤(창, 버튼, 글상자)은 자신을 만든 스레드(UI 스레드)에서만 건드려야 한다.
    //   - 수신 스레드 같은 다른 스레드에서 label.Text = "..." 처럼 직접 바꾸면 "크로스 스레드 작업이 잘못되었습니다"
    //     예외(InvalidOperationException)가 나거나, 운이 나쁘면 화면이 깨지는 등 예측 불가능하게 동작한다.
    //   - 해결: Invoke/BeginInvoke로 "이 작업을 UI 스레드에서 실행해 줘"라고 UI 스레드의 메시지 큐에 넣는다.
    // [개념] Invoke vs BeginInvoke
    //   - Invoke: UI 스레드가 작업을 끝낼 때까지 부른 스레드가 "기다린다"(동기).
    //   - BeginInvoke: 큐에 넣기만 하고 바로 돌아온다(비동기). 결과를 기다릴 필요가 없을 때 쓴다.
    // [왜] 수신 스레드에서는 BeginInvoke만 쓴다.
    //   Invoke를 쓰면, UI 스레드가 마침 수신 스레드를 기다리는 중(예: lock 대기)일 때 서로를 기다리는 "교착 상태(데드락)"가
    //   생길 수 있고, UI가 느리면 수신 스레드도 같이 느려져 메시지 수신이 밀린다.
    // [스레드] 아무 스레드에서나 불러도 안전하다.
    // [읽는 법] 사용법: UiHelper.RunOnUi(this, () => { 화면 바꾸는 코드 }); 모든 창의 OnPacketReceived가 이 한 줄로 UI 스레드에 넘긴다. 반환값이 없고(기다리지 않음), 창이 닫혔으면 조용히 버린다.
    public static void RunOnUi(Control control, Action action)
    {
        // [왜] 창이 닫혔거나(IsDisposed) 아직 화면에 만들어지지 않았으면(IsHandleCreated = false) BeginInvoke가 예외를 던진다.
        //   이 경우 화면에 보여 줄 곳이 없으니 작업을 버린다.
        if (control.IsDisposed || !control.IsHandleCreated) return;
        try
        {
            control.BeginInvoke(action);
        }
        // [주의] 위 검사와 BeginInvoke 사이의 아주 짧은 순간에 UI 스레드가 창을 닫을 수도 있다(경쟁 상태).
        //   검사만으로는 100% 막을 수 없으므로 예외도 함께 잡는다.
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // 창이 닫히는 중
        }
    }
}
