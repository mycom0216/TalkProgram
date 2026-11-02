// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] StatsControls.cs - 관리자 "통계" 탭에서 쓰는 그림 컨트롤 3개
// - StatCard : 숫자 하나를 크게 보여 주는 카드 (제목 / 큰 숫자 / 작은 설명)
// - BarChart : 막대그래프 (최근 7일 메시지, 시간대별 로그인)
// - UsageBar : 사용량 막대 (디스크 사용률, 80%·90%를 넘으면 주황·빨강)
// - 외부 차트 라이브러리를 쓰지 않고 OnPaint에서 직접 그린다.
//   [왜] NuGet 패키지 하나 없이 배포 파일을 작게 유지하고, 테마 색을 그대로 쓰기 위해서.
// - 스레드: 값 설정과 그리기 모두 UI 스레드에서만.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   관리자 통계 탭의 그림 컨트롤 3개를 라이브러리 없이 직접 그린다: StatCard(숫자 카드), BarChart(막대그래프), UsageBar(사용량 막대). 모두 StatsControlBase(공통: 더블 버퍼, DPI 계산, 글꼴 3종 관리, 카드 배경)를 상속한다. 데이터는 AdminForm.ShowStats가 SetValue/SetData로 넣고, 컨트롤은 Invalidate로 다시 그리기만 한다.
//
// ■ 구조
//   - StatsControlBase: S(값) = 96dpi 기준 길이 × 현재 DPI / 96. 글꼴(작게/크게/굵게)은 처음 쓸 때 만들고(??=) 글꼴이 바뀌거나 컨트롤이 닫힐 때 Dispose한다.
//   - StatCard: 제목 / 큰 숫자 / 설명 세 줄 + 왼쪽 색 띠(상태에 따라 색이 바뀜: 접속 중=초록, 승인 대기 있음=주황 등은 AdminForm이 정함)
//   - BarChart: 막대 폭은 "칸 폭(slot) = 그림 영역 폭 / 막대 수". 높이는 최댓값 대비 비율. 마우스를 올린 막대는 위 작은 글자에 "라벨 · 값"으로 보인다. 이름이 겹치면 몇 칸 건너뛰어 쓴다(labelEvery), 강조 막대(오늘/지금 시간)의 이름은 항상 쓴다.
//   - UsageBar: 전체 사용률 막대(80% 이상 주황, 90% 이상 빨강) 안에 "일부"(사내톡 파일)를 진한 색으로 겹쳐 그린다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 차트 라이브러리 없이 GDI+로 그리기 ................. "WinForms custom chart OnPaint GDI+ bar chart"
//   - 좌표 계산: 칸(slot)과 간격(gap), 값 → 높이 비율 ....... "bar chart layout slot gap scale max"
//   - 글꼴 지연 생성(??=)과 Dispose .................... "lazy font creation dispose OnFontChanged"
//   - AccessibleName(화면 낭독기·UI 자동 테스트) ........... "AccessibleName custom control UI Automation"
//   - 막대 크기가 0이어도 보이게 하는 최소 높이 ........... "minimum bar height tiny values visible"
//   - 임계값 색(경고 주황/위험 빨강) ...................... "threshold coloring warning critical gauge"
//
// ■ 면접 예상 질문과 답
//   Q1. 차트 라이브러리를 쓰지 않고 직접 그린 이유는요?
//       A. 막대그래프 2개와 막대 하나뿐이라 라이브러리(NuGet)를 추가하면 배포 파일만 커집니다. 직접 그리면 테마 색을 그대로 쓰고 의존성이 없으며, 단순한 모양은 코드가 짧습니다. 대신 축·확대·범례 같은 복잡한 기능이 필요해지면 라이브러리가 낫습니다.
//   Q2. 값이 1이라도 막대가 보이게 한 방법은요? 값이 모두 0이면요?
//       A. 값이 0보다 크면 최소 2(DPI 반영)픽셀 높이를 보장합니다. 합계가 0이거나 데이터가 없으면 막대 대신 "자료가 없습니다" 안내를 가운데에 씁니다.
//   Q3. 막대가 많을 때 이름이 겹치지 않게 하는 방법은요?
//       A. "00시" 글자 폭 × 1.2 ÷ 칸 폭으로 "몇 칸마다 이름을 쓸지"(labelEvery)를 계산합니다. 강조 막대 근처는 겹쳐 보이지 않게 건너뛰고, 강조 막대 이름은 항상 씁니다. 막대 위 숫자는 칸이 충분히 넓을 때만 보입니다.
//   Q4. 화면 낭독기·자동 UI 테스트는 직접 그린 그림을 어떻게 읽나요?
//       A. 직접 그린 컨트롤은 안에 글자 컨트롤이 없어서 읽을 내용이 없습니다. 그래서 값을 설정할 때마다 AccessibleName에 "제목: 값 설명" 문자열을 넣어 둡니다. UI 자동 테스트가 이 이름으로 값을 확인할 수 있습니다.
//   Q5. 이 컨트롤들은 서버를 아나요?
//       A. 모릅니다. 값(숫자·문자열)만 받아 그립니다. 시간대 변환 같은 계산은 AdminForm과 Common의 AdminStatsView가 하고, 컨트롤은 받은 값을 그리는 일만 합니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - BarChart.SetData는 labels와 values의 길이가 같다고 가정한다(검사하지 않음). 길이가 다르면 마우스를 올릴 때 _labels[_hover]에서 범위 오류가 날 수 있다. 호출하는 AdminForm은 항상 같은 길이로 넘긴다(코드로 확인함).
//   - 크기를 DPI로 바꾸는 S()가 DeviceDpi를 쓰는 방식이라, 다른 파일의 LogicalToDeviceUnits와 계산 방식이 다르다(결과는 거의 같음).
//   - 막대는 값에 따라 높이만 달라지고 눈금(축)은 최댓값 한 줄과 절반 기준선뿐이다(간단한 대시보드용 설계).
// ───────────────────────────────────────────────────────────────────────────
using System.Drawing.Drawing2D;

namespace ChatApp.Client.Ui;

/// <summary>공통: 더블 버퍼(깜빡임 방지) + DPI 크기 계산.</summary>
internal abstract class StatsControlBase : Control
{
    /// <summary>깜빡임 없이 직접 그리도록 더블 버퍼링 등을 설정하고 배경을 흰색(Theme.Surface)으로 한다.</summary>
    protected StatsControlBase()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
    }

    /// <summary>96dpi 기준 길이를 지금 화면 배율로.</summary>
    // [읽는 법] 96dpi(100% 배율)를 기준으로 쓴 숫자를 지금 화면에 맞게 키운다. 예: 150% 배율(144dpi)에서 S(10) = 15. 모든 그리기 좌표가 이 함수를 거쳐서 배율이 달라도 비율이 같다.
    protected int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    /// <summary>제목·설명 같은 작은 글자용 글꼴 (기본 글꼴의 0.85배). 처음 쓸 때 만들어 재사용한다.</summary>
    // [개념] ??= : 값이 null일 때만 만들어 넣는다(지연 생성). 처음 그릴 때 한 번 만들고 이후에는 재사용. 글꼴이 바뀌면(OnFontChanged) 모두 지워 다시 만든다. Dispose에서도 해제.
    protected Font SmallFont => _small ??= new Font(Font.FontFamily, Font.Size * 0.85f);

    /// <summary>카드의 큰 숫자용 굵은 글꼴 (기본 글꼴의 1.9배). 처음 쓸 때 만들어 재사용한다.</summary>
    protected Font BigFont => _big ??= new Font(Font.FontFamily, Font.Size * 1.9f, FontStyle.Bold);

    /// <summary>제목용 굵은 글꼴 (기본 글꼴과 같은 크기). 처음 쓸 때 만들어 재사용한다.</summary>
    protected Font BoldFont => _bold ??= new Font(Font.FontFamily, Font.Size, FontStyle.Bold);
    private Font? _small, _big, _bold;

    /// <summary>글꼴이 바뀌면 그 글꼴로 만든 파생 글꼴(작게·크게·굵게)을 해제해 다음에 다시 만들게 한다.</summary>
    // [주의] 부모 글꼴이 바뀌면 이 컨트롤의 Font도 바뀐다. 파생 글꼴(작게/크게/굵게)은 Font 기준으로 만든 것이라 먼저 해제하고 base를 부른다.
    protected override void OnFontChanged(EventArgs e)
    {
        DisposeFonts();
        base.OnFontChanged(e);
    }

    /// <summary>컨트롤을 버릴 때 파생 글꼴을 해제한다.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeFonts();
        base.Dispose(disposing);
    }

    private void DisposeFonts()
    {
        _small?.Dispose(); _big?.Dispose(); _bold?.Dispose();
        _small = _big = _bold = null;
    }

    /// <summary>둥근 흰 카드 배경 + 연한 테두리.</summary>
    protected void DrawCardBackground(Graphics g)
    {
        Rectangle r = new(0, 0, Width - 1, Height - 1);
        Theme.FillRounded(g, Theme.Surface, r, S(10));
        Theme.DrawRounded(g, Theme.Border, r, S(10));
    }
}

/// <summary>숫자 카드: 제목 / 큰 숫자 / 설명.</summary>
internal sealed class StatCard : StatsControlBase
{
    private string _title = "", _value = "-", _detail = "";
    private Color _accent = Theme.Primary;

    /// <summary>숫자 카드를 기본 크기(170×92)와 바깥 여백으로 만든다.</summary>
    public StatCard()
    {
        Size = new Size(170, 92);
        Margin = new Padding(6);
    }

    /// <summary>카드의 제목, 큰 숫자, 설명을 바꾸고 다시 그린다. accent는 왼쪽 색 띠 색 (null이면 Theme.Primary).</summary>
    public void SetValue(string title, string value, string detail, Color? accent = null)
    {
        _title = title; _value = value; _detail = detail; _accent = accent ?? Theme.Primary;
        // [왜] 직접 그린 컨트롤은 화면 낭독기/UI 자동 테스트가 읽을 글자가 없다. 값이 바뀔 때마다 읽을 문자열을 따로 넣어 둔다.
        AccessibleName = $"{title}: {value} {detail}".Trim(); // 화면 낭독기·UI 자동 테스트용
        Invalidate();
    }

    /// <summary>카드 배경, 왼쪽 색 띠, 제목, 큰 숫자, 설명을 그린다.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        DrawCardBackground(g);
        // 왼쪽 색 띠
        using (var accent = new SolidBrush(_accent)) g.FillRectangle(accent, S(1), S(12), S(4), Height - S(24));

        int x = S(14), w = Width - x - S(8);
        TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, _title, SmallFont, new Rectangle(x, S(8), w, SmallFont.Height), Theme.SubText, flags);
        TextRenderer.DrawText(g, _value, BigFont, new Rectangle(x, S(8) + SmallFont.Height + S(2), w, BigFont.Height), Theme.Text, flags);
        TextRenderer.DrawText(g, _detail, SmallFont, new Rectangle(x, Height - SmallFont.Height - S(8), w, SmallFont.Height), Theme.SubText, flags);
    }
}

/// <summary>막대그래프. 막대 위에 값, 아래에 이름. 이름이 많으면 몇 칸 걸러 보여 준다.</summary>
internal sealed class BarChart : StatsControlBase
{
    private string _title = "";
    private string _empty = "자료가 없습니다.";
    private string[] _labels = [];
    private int[] _values = [];
    private int _highlight = -1; // 강조할 막대 (오늘, 지금 시간)
    private int _hover = -1;

    /// <summary>막대그래프를 기본 크기(360×220)와 바깥 여백으로 만든다.</summary>
    public BarChart()
    {
        Size = new Size(360, 220);
        Margin = new Padding(6);
    }

    /// <summary>그래프 제목과 막대별 이름·값(두 배열은 길이가 같아야 함)을 바꾸고 다시 그린다. highlight는 강조할 막대 번호(-1이면 없음).</summary>
    public void SetData(string title, string[] labels, int[] values, int highlight = -1, string? emptyText = null)
    {
        _title = title; _labels = labels; _values = values; _highlight = highlight;
        if (emptyText is not null) _empty = emptyText;
        AccessibleName = $"{title}: " + string.Join(", ", labels.Zip(values, (l, v) => $"{l} {v:N0}"));
        Invalidate();
    }

    private Rectangle PlotArea()
    {
        int top = S(14) + BoldFont.Height + SmallFont.Height + S(6);
        int bottom = Height - SmallFont.Height - S(14);
        return new Rectangle(S(14), top, Math.Max(1, Width - S(28)), Math.Max(1, bottom - top));
    }

    // [읽는 법] i번째 막대의 사각형: 칸 폭(slot)에서 간격(gap, 칸의 20%)을 빼 막대 폭을, 값/최댓값 비율로 높이를 구한다. 위에서 아래로 그리므로 y는 plot.Bottom - 높이.
    private Rectangle BarRect(int i, Rectangle plot, int max)
    {
        float slot = plot.Width / (float)_values.Length;
        int gap = Math.Max(S(2), (int)(slot * 0.2f));
        int h = max == 0 ? 0 : (int)Math.Round(plot.Height * (_values[i] / (double)max));
        if (_values[i] > 0) h = Math.Max(h, S(2)); // 1개라도 있으면 보이게
        int x = plot.X + (int)(slot * i) + gap / 2;
        return new Rectangle(x, plot.Bottom - h, Math.Max(1, (int)slot - gap), h);
    }

    /// <summary>마우스가 올라간 막대를 찾아 기억하고, 바뀌었으면 다시 그린다 (위쪽 줄에 그 막대의 이름·값이 보인다).</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = -1;
        if (_values.Length > 0)
        {
            Rectangle plot = PlotArea();
            float slot = plot.Width / (float)_values.Length;
            int i = (int)((e.X - plot.X) / slot);
            if (e.X >= plot.X && i >= 0 && i < _values.Length && e.Y <= plot.Bottom + SmallFont.Height + S(4)) hit = i;
        }
        if (hit != _hover) { _hover = hit; Invalidate(); }
    }

    /// <summary>마우스가 그래프 밖으로 나가면 막대 강조를 지운다.</summary>
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; Invalidate(); }
    }

    /// <summary>카드 배경, 제목, 합계(또는 마우스를 올린 막대의 값), 기준선, 막대와 이름을 그린다. 자료가 없으면 안내 문구를 쓴다.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawCardBackground(g);

        TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, _title, BoldFont, new Rectangle(S(14), S(10), Width - S(28), BoldFont.Height), Theme.Text, left);
        int total = _values.Sum();
        string sub = _hover >= 0 ? $"{_labels[_hover]} · {_values[_hover]:N0}" : $"합계 {total:N0}";
        TextRenderer.DrawText(g, sub, SmallFont, new Rectangle(S(14), S(12) + BoldFont.Height, Width - S(28), SmallFont.Height), Theme.SubText, left);

        Rectangle plot = PlotArea();
        if (_values.Length == 0 || total == 0)
        {
            TextRenderer.DrawText(g, _empty, SmallFont, plot, Theme.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        int max = _values.Max();
        // 가는 기준선 (최대값의 절반)
        using (var grid = new Pen(Color.FromArgb(232, 238, 247)) { DashStyle = DashStyle.Dash })
        {
            g.DrawLine(grid, plot.X, plot.Bottom - plot.Height / 2, plot.Right, plot.Bottom - plot.Height / 2);
            g.DrawLine(grid, plot.X, plot.Top, plot.Right, plot.Top);
        }
        TextRenderer.DrawText(g, max.ToString("N0"), SmallFont, new Rectangle(plot.X, plot.Top - SmallFont.Height, plot.Width, SmallFont.Height),
            Theme.SubText, TextFormatFlags.Right | TextFormatFlags.NoPrefix);

        float slot = plot.Width / (float)_values.Length;
        // [읽는 법] 이름 하나가 차지하는 폭이 칸 폭보다 크면 겹친다 → "몇 칸에 한 번" 이름을 쓸지 올림 계산. 칸이 넓으면 1(모두 표시).
        int labelEvery = Math.Max(1, (int)Math.Ceiling(TextRenderer.MeasureText("00시", SmallFont).Width * 1.2 / slot));
        bool showValues = slot >= TextRenderer.MeasureText("0000", SmallFont).Width;

        using var normal = new SolidBrush(Color.FromArgb(150, 195, 245));
        using var strong = new SolidBrush(Theme.Primary);
        using var hover = new SolidBrush(Theme.SkyDark);
        for (int i = 0; i < _values.Length; i++)
        {
            Rectangle bar = BarRect(i, plot, max);
            // [읽는 법] 색 우선순위: 마우스를 올린 막대 > 강조 막대(오늘/지금 시간) > 보통. 브러시 3개는 그리기 한 번에 using으로 만들고 해제한다.
            Brush brush = i == _hover ? hover : i == _highlight ? strong : normal;
            if (bar.Height > 0) g.FillRectangle(brush, bar);

            var cell = new Rectangle(plot.X + (int)(slot * i) - S(10), 0, (int)slot + S(20), SmallFont.Height);
            if (showValues && _values[i] > 0)
            {
                cell.Y = bar.Y - SmallFont.Height;
                TextRenderer.DrawText(g, _values[i].ToString("N0"), SmallFont, cell, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            }
            // 강조 칸(오늘·지금 시간)의 이름은 항상 쓰고, 그 옆 칸 이름은 겹치지 않게 건너뛴다 ("14시15시"처럼 붙어 보이던 문제)
            bool nearHighlight = _highlight >= 0 && i != _highlight && Math.Abs(i - _highlight) < labelEvery;
            if ((i % labelEvery == 0 && !nearHighlight) || i == _highlight)
            {
                cell.Y = plot.Bottom + S(3);
                TextRenderer.DrawText(g, _labels[i], SmallFont, cell, i == _highlight ? Theme.Primary : Theme.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }
}

/// <summary>사용량 막대: 제목, 막대, 설명. 80% 넘으면 주황, 90% 넘으면 빨강.</summary>
internal sealed class UsageBar : StatsControlBase
{
    private string _title = "", _detail = "";
    private double _ratio; // 0~1
    private double _part;  // 막대 안에서 따로 표시할 부분 (예: 사내톡 파일), 0~1

    /// <summary>사용량 막대를 기본 크기(300×110)와 바깥 여백으로 만든다.</summary>
    public UsageBar()
    {
        Size = new Size(300, 110);
        Margin = new Padding(6);
    }

    /// <summary>제목, 사용 비율(usedRatio, 0~1), 그 안의 일부 비율(partRatio, 0~1), 설명 글을 바꾸고 다시 그린다.</summary>
    public void SetData(string title, double usedRatio, double partRatio, string detail)
    {
        // [읽는 법] 비율은 0~1로 자르고, 일부(_part)는 전체(_ratio)를 넘지 않게 자른다 → 서버 값이 이상해도 막대가 칸 밖으로 나가지 않는다.
        _title = title; _ratio = Math.Clamp(usedRatio, 0, 1); _part = Math.Clamp(partRatio, 0, _ratio); _detail = detail;
        AccessibleName = $"{title}: {detail}";
        Invalidate();
    }

    /// <summary>제목, 사용 비율(%), 막대(80% 넘으면 주황, 90% 넘으면 빨강), 설명 글을 그린다.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawCardBackground(g);

        TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, _title, BoldFont, new Rectangle(S(14), S(10), Width - S(28), BoldFont.Height), Theme.Text, left);
        TextRenderer.DrawText(g, $"{_ratio:P0}", BoldFont, new Rectangle(S(14), S(10), Width - S(28), BoldFont.Height),
            _ratio >= 0.9 ? Theme.Danger : _ratio >= 0.8 ? Theme.Notice : Theme.Primary, TextFormatFlags.Right | TextFormatFlags.NoPrefix);

        var track = new Rectangle(S(14), S(18) + BoldFont.Height, Width - S(28), S(14));
        Theme.FillRounded(g, Theme.Background, track, S(7));
        if (_ratio > 0)
        {
            Color fill = _ratio >= 0.9 ? Theme.Danger : _ratio >= 0.8 ? Theme.Notice : Color.FromArgb(150, 195, 245);
            Theme.FillRounded(g, fill, new Rectangle(track.X, track.Y, Math.Max(S(14), (int)(track.Width * _ratio)), track.Height), S(7));
        }
        if (_part > 0)
            Theme.FillRounded(g, Theme.Primary, new Rectangle(track.X, track.Y, Math.Max(S(6), (int)(track.Width * _part)), track.Height), S(7));

        var text = new Rectangle(S(14), track.Bottom + S(8), Width - S(28), Height - track.Bottom - S(12));
        TextRenderer.DrawText(g, _detail, SmallFont, text, Theme.SubText, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}
