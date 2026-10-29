// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] StatsControls.cs - 관리자 "통계" 탭에서 쓰는 그림 컨트롤 3개
// - StatCard : 숫자 하나를 크게 보여 주는 카드 (제목 / 큰 숫자 / 작은 설명)
// - BarChart : 막대그래프 (최근 7일 메시지, 시간대별 로그인)
// - UsageBar : 사용량 막대 (디스크 사용률, 80%·90%를 넘으면 주황·빨강)
// - 외부 차트 라이브러리를 쓰지 않고 OnPaint에서 직접 그린다.
//   [왜] NuGet 패키지 하나 없이 배포 파일을 작게 유지하고, 테마 색을 그대로 쓰기 위해서.
// - 스레드: 값 설정과 그리기 모두 UI 스레드에서만.
// ─────────────────────────────────────────────────────────────────────────────
using System.Drawing.Drawing2D;

namespace ChatApp.Client.Ui;

/// <summary>공통: 더블 버퍼(깜빡임 방지) + DPI 크기 계산.</summary>
internal abstract class StatsControlBase : Control
{
    protected StatsControlBase()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
    }

    /// <summary>96dpi 기준 길이를 지금 화면 배율로.</summary>
    protected int S(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    protected Font SmallFont => _small ??= new Font(Font.FontFamily, Font.Size * 0.85f);
    protected Font BigFont => _big ??= new Font(Font.FontFamily, Font.Size * 1.9f, FontStyle.Bold);
    protected Font BoldFont => _bold ??= new Font(Font.FontFamily, Font.Size, FontStyle.Bold);
    private Font? _small, _big, _bold;

    protected override void OnFontChanged(EventArgs e)
    {
        DisposeFonts();
        base.OnFontChanged(e);
    }

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

    public StatCard()
    {
        Size = new Size(170, 92);
        Margin = new Padding(6);
    }

    public void SetValue(string title, string value, string detail, Color? accent = null)
    {
        _title = title; _value = value; _detail = detail; _accent = accent ?? Theme.Primary;
        AccessibleName = $"{title}: {value} {detail}".Trim(); // 화면 낭독기·UI 자동 테스트용
        Invalidate();
    }

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

    public BarChart()
    {
        Size = new Size(360, 220);
        Margin = new Padding(6);
    }

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

    private Rectangle BarRect(int i, Rectangle plot, int max)
    {
        float slot = plot.Width / (float)_values.Length;
        int gap = Math.Max(S(2), (int)(slot * 0.2f));
        int h = max == 0 ? 0 : (int)Math.Round(plot.Height * (_values[i] / (double)max));
        if (_values[i] > 0) h = Math.Max(h, S(2)); // 1개라도 있으면 보이게
        int x = plot.X + (int)(slot * i) + gap / 2;
        return new Rectangle(x, plot.Bottom - h, Math.Max(1, (int)slot - gap), h);
    }

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

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; Invalidate(); }
    }

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
        int labelEvery = Math.Max(1, (int)Math.Ceiling(TextRenderer.MeasureText("00시", SmallFont).Width * 1.2 / slot));
        bool showValues = slot >= TextRenderer.MeasureText("0000", SmallFont).Width;

        using var normal = new SolidBrush(Color.FromArgb(150, 195, 245));
        using var strong = new SolidBrush(Theme.Primary);
        using var hover = new SolidBrush(Theme.SkyDark);
        for (int i = 0; i < _values.Length; i++)
        {
            Rectangle bar = BarRect(i, plot, max);
            Brush brush = i == _hover ? hover : i == _highlight ? strong : normal;
            if (bar.Height > 0) g.FillRectangle(brush, bar);

            var cell = new Rectangle(plot.X + (int)(slot * i) - S(10), 0, (int)slot + S(20), SmallFont.Height);
            if (showValues && _values[i] > 0)
            {
                cell.Y = bar.Y - SmallFont.Height;
                TextRenderer.DrawText(g, _values[i].ToString("N0"), SmallFont, cell, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            }
            if (i % labelEvery == 0 || i == _highlight)
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

    public UsageBar()
    {
        Size = new Size(300, 110);
        Margin = new Padding(6);
    }

    public void SetData(string title, double usedRatio, double partRatio, string detail)
    {
        _title = title; _ratio = Math.Clamp(usedRatio, 0, 1); _part = Math.Clamp(partRatio, 0, _ratio); _detail = detail;
        AccessibleName = $"{title}: {detail}";
        Invalidate();
    }

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
