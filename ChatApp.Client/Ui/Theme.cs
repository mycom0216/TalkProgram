// ─────────────────────────────────────────────────────────────────────────────
// Theme.cs
// 역할  : "사내톡" 하늘색 테마의 색·글꼴과, 여러 화면이 함께 쓰는 그리기 도우미
//         (그라데이션, 둥근 사각형, 이니셜 아바타, 접속 상태 점, 로고)를 한곳에 모은 정적 클래스.
// 스레드: 그리기 메서드는 UI 스레드의 OnPaint 안에서만 호출된다.
//         readonly 색·글꼴은 프로그램 시작 시 한 번 만들어지고 이후 읽기만 하므로 공유해도 안전하다.
// 연결  : CardListView, BubbleChatView, GradientPanel, MainForm, ChatWindow, LoginForm 등 거의 모든 화면이 사용.
// [왜] 색을 화면마다 따로 적으면 테마를 바꿀 때 수십 군데를 고쳐야 한다. 한곳에 모으면 여기만 바꾸면 된다.
// ─────────────────────────────────────────────────────────────────────────────
using System.Drawing.Drawing2D;
using ChatApp.Common.Models;

namespace ChatApp.Client.Ui;

/// <summary>
/// "사내톡" 하늘색 테마: 색, 글꼴, 공통 그리기(그라데이션, 둥근 사각형, 아바타, 로고).
/// 2000년대 메신저 느낌을 내되 로고·이름은 이 프로젝트 고유의 것을 쓴다.
/// </summary>
internal static class Theme
{
    // [개념] const는 컴파일할 때 값이 박히는 상수(문자열·숫자만 가능),
    //        static readonly는 프로그램 실행 중 한 번 만들어지는 상수(Color, Font 같은 객체도 가능).
    public const string AppName = "사내톡";
    private const string FontName = "맑은 고딕";

    // ── 색 ──
    // [개념] Color.FromArgb(R, G, B): 빨강·초록·파랑을 0~255로 섞은 색. FromArgb(A, R, G, B)처럼 4개면 A는 투명도(0=투명, 255=불투명).
    public static readonly Color SkyLight = Color.FromArgb(125, 196, 255);   // 그라데이션 위
    public static readonly Color SkyDark = Color.FromArgb(40, 118, 228);     // 그라데이션 아래
    public static readonly Color Primary = Color.FromArgb(38, 120, 235);     // 버튼, 내 말풍선
    public static readonly Color Background = Color.FromArgb(238, 245, 255); // 목록 머리줄, 대화 배경
    public static readonly Color ChatBackground = Color.FromArgb(226, 239, 255);
    public static readonly Color Surface = Color.White;
    public static readonly Color Border = Color.FromArgb(200, 220, 243);
    public static readonly Color Text = Color.FromArgb(30, 41, 59);
    public static readonly Color SubText = Color.FromArgb(100, 116, 139);
    public static readonly Color HeaderSubText = Color.FromArgb(225, 239, 255);
    public static readonly Color Hover = Color.FromArgb(235, 244, 255);
    public static readonly Color Selected = Color.FromArgb(214, 232, 255);
    public static readonly Color Online = Color.FromArgb(34, 197, 94);
    public static readonly Color Offline = Color.FromArgb(148, 163, 184);
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);
    public static readonly Color Notice = Color.FromArgb(217, 119, 6);
    public static readonly Color Badge = Color.FromArgb(239, 68, 68);

    // 아바타 배경으로 돌아가며 쓰는 8가지 색 (사람마다 AvatarColor로 하나가 정해진다)
    private static readonly Color[] AvatarPalette =
    [
        Color.FromArgb(59, 130, 246), Color.FromArgb(16, 185, 129), Color.FromArgb(245, 158, 11),
        Color.FromArgb(236, 72, 153), Color.FromArgb(139, 92, 246), Color.FromArgb(20, 184, 166),
        Color.FromArgb(249, 115, 22), Color.FromArgb(99, 102, 241),
    ];

    // ── 글꼴 (pt 단위라 DPI에 맞춰 자동으로 커진다) ──
    // [개념] pt(포인트)는 인쇄 단위(1pt = 1/72인치)라서 화면 배율이 125%, 150%로 커지면 픽셀 크기도 함께 커진다.
    // [주의] Font도 GDI 자원이지만 여기 것들은 프로그램이 끝날 때까지 계속 쓰므로 일부러 Dispose하지 않는다.
    public static readonly Font Body = new(FontName, 10F);
    public static readonly Font BodyBold = new(FontName, 10F, FontStyle.Bold);
    public static readonly Font Small = new(FontName, 8.5F);
    public static readonly Font SmallBold = new(FontName, 8.5F, FontStyle.Bold);
    public static readonly Font Title = new(FontName, 12F, FontStyle.Bold);
    public static readonly Font Big = new(FontName, 22F, FontStyle.Bold);

    /// <summary>사람(사원번호)마다 항상 같은 아바타 색. string.GetHashCode는 실행마다 바뀌므로 직접 계산.</summary>
    // [개념] 해시로 색 고르기: 문자열을 숫자 하나(해시)로 바꾸고, 그 숫자를 색 개수로 나눈 나머지(%)를 색 번호로 쓴다.
    //        같은 사원번호는 항상 같은 숫자 → 항상 같은 색이 되어, 어느 PC에서 봐도 그 사람은 같은 색이다.
    // [주의] .NET의 string.GetHashCode()는 보안상 프로그램을 켤 때마다 결과가 달라지므로(무작위 시드) 쓰면 안 된다.
    //        그래서 "hash = hash * 31 + 글자" 방식(자바 String.hashCode와 같은 공식)을 직접 계산한다.
    // [개념] unchecked: 곱셈이 uint 범위를 넘쳐도 예외를 내지 말고 그냥 넘쳐서(오버플로) 돌게 하라는 뜻.
    public static Color AvatarColor(string key)
    {
        uint hash = 17;
        foreach (char c in key) hash = unchecked(hash * 31 + c);
        return AvatarPalette[hash % (uint)AvatarPalette.Length];
    }

    /// <summary>사각형 영역을 위(밝은 하늘색) → 아래(진한 파랑) 그라데이션으로 칠한다.</summary>
    public static void FillGradient(Graphics g, Rectangle r)
    {
        // [주의] 폭이나 높이가 0인 사각형으로 LinearGradientBrush를 만들면 예외가 난다(창을 최소화했을 때 등).
        if (r.Width <= 0 || r.Height <= 0) return;
        // [개념] LinearGradientBrush: 두 색 사이를 직선 방향으로 부드럽게 섞어 칠하는 붓. Vertical = 위→아래 방향.
        using var brush = new LinearGradientBrush(r, SkyLight, SkyDark, LinearGradientMode.Vertical);
        g.FillRectangle(brush, r);
    }

    /// <summary>
    /// [개념] GraphicsPath: 선·호(arc)를 이어 붙여 만든 "모양". 채우기(FillPath), 테두리(DrawPath), 잘라내기(SetClip)에 쓴다.
    /// 네 모서리에 1/4 원(90도 호)을 하나씩 붙여 둥근 사각형을 만든다.
    /// </summary>
    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        // d = 모서리 원의 지름. [주의] 사각형보다 큰 원을 그리면 모양이 깨지므로 너비·높이 중 작은 값을 넘지 않게,
        //     또 0이 되면 AddArc가 예외를 내므로 최소 1로 제한한다.
        float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        // AddArc(x, y, 너비, 높이, 시작 각도, 회전 각도): 각도는 3시 방향이 0도, 시계 방향으로 증가
        path.AddArc(r.X, r.Y, d, d, 180, 90);                  // 왼쪽 위 모서리
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);          // 오른쪽 위
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);   // 오른쪽 아래
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);          // 왼쪽 아래
        path.CloseFigure(); // 마지막 점과 첫 점을 이어 닫힌 도형으로 만든다
        return path;
    }

    /// <summary>둥근 사각형 채우기 (말풍선, 배지, 버튼 등).</summary>
    public static void FillRounded(Graphics g, Color color, Rectangle r, float radius)
    {
        // [개념] GraphicsPath와 SolidBrush 모두 using으로 그리기 직후 바로 해제한다(그리기는 초당 수십 번 일어날 수 있음).
        using var path = RoundedRect(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>둥근 사각형 테두리 (상대방 말풍선, 파일 카드 등).</summary>
    public static void DrawRounded(Graphics g, Color color, Rectangle r, float radius)
    {
        using var path = RoundedRect(r, radius);
        using var pen = new Pen(color);
        g.DrawPath(pen, path);
    }

    /// <summary>동그란 이니셜 아바타 (이름 첫 글자).</summary>
    // [왜] 사진 프로필 기능이 아직 없으므로, 색 동그라미 + 이름 첫 글자로 사람을 구분한다(카카오톡 기본 프로필과 비슷한 역할).
    public static void DrawAvatar(Graphics g, Rectangle r, string name, Color color)
    {
        // [개념] 안티앨리어싱(AntiAlias): 원·곡선의 가장자리 픽셀을 중간색으로 칠해 계단 현상 없이 매끄럽게 보이게 한다.
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(color)) g.FillEllipse(brush, r);

        // [개념] name[..1]은 C#의 범위(range) 문법으로 "처음부터 1글자" = name.Substring(0, 1)과 같다.
        string initial = name.Length > 0 ? name[..1] : "?";
        // 글자 크기를 아바타 높이의 40%로 맞춘다. GraphicsUnit.Pixel: pt가 아닌 픽셀 단위로 크기를 정한다(아바타 크기에 비례하도록).
        // 이 글꼴은 그때그때 만드는 것이므로 using으로 바로 해제한다.
        using var font = new Font(FontName, Math.Max(6f, r.Height * 0.40f), FontStyle.Bold, GraphicsUnit.Pixel);
        // NoPadding: TextRenderer가 기본으로 넣는 좌우 여백을 없애 정확히 가운데에 오게 한다.
        TextRenderer.DrawText(g, initial, font, r, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    /// <summary>접속 상태 점 (흰 테두리 + 초록/회색).</summary>
    public static void DrawStatusDot(Graphics g, Rectangle r, bool online) => DrawStatusDot(g, r, online ? Online : Offline);

    /// <summary>접속 상태별 색: 온라인 초록, 자리 비움 노랑, 회의 중·방해 금지 빨강.</summary>
    public static Color PresenceColor(Presence presence) => presence switch
    {
        Presence.Online => Online,
        Presence.Away => Away,
        _ => Danger, // Busy, DoNotDisturb
    };

    /// <summary>접속 상태를 화면에 보여 줄 한국어.</summary>
    public static string PresenceText(Presence presence) => presence switch
    {
        Presence.Online => "온라인",
        Presence.Away => "자리 비움",
        Presence.Busy => "회의 중",
        Presence.DoNotDisturb => "방해 금지",
        _ => "",
    };

    public static readonly Color Away = Color.FromArgb(234, 179, 8);       // 자리 비움 (노랑)
    public static readonly Color MentionBack = Color.FromArgb(255, 247, 214); // @멘션 말풍선 배경
    public static readonly Color SearchHit = Color.FromArgb(253, 230, 138);   // 검색 결과 강조

    /// <summary>색을 직접 정하는 상태 점 (흰 테두리 + 색).</summary>
    public static void DrawStatusDot(Graphics g, Rectangle r, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 흰 원을 먼저 크게 그리고, 그 안에 조금 작은 색 원을 그려서 "흰 테두리"처럼 보이게 한다
        g.FillEllipse(Brushes.White, r);
        // Rectangle.Inflate에 음수를 주면 사각형이 그만큼 안쪽으로 줄어든다 (테두리 두께 = 지름의 1/5)
        var inner = Rectangle.Inflate(r, -Math.Max(1, r.Width / 5), -Math.Max(1, r.Height / 5));
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, inner);
    }

    /// <summary>사내톡 로고: 흰 말풍선 안에 파란 점 세 개(입력 중…).</summary>
    // [왜] 로고를 이미지 파일 대신 코드로 그리면 어떤 화면 배율에서도 선명하고, 별도 리소스 파일이 필요 없다.
    //      또한 다른 회사 로고를 쓰지 않고 이 프로젝트 고유의 모양을 만든다.
    public static void DrawLogo(Graphics g, Rectangle r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 전체 높이의 80%는 말풍선 몸통, 아래 20%는 꼬리가 들어갈 자리
        var bubble = new Rectangle(r.X, r.Y, r.Width, (int)(r.Height * 0.8f));
        // 말풍선 왼쪽 아래의 꼬리(삼각형) 꼭짓점 3개. PointF = 소수점 좌표(더 정밀한 위치)
        PointF[] tail =
        [
            new(bubble.X + bubble.Width * 0.22f, bubble.Bottom - 3),
            new(bubble.X + bubble.Width * 0.14f, r.Bottom),
            new(bubble.X + bubble.Width * 0.44f, bubble.Bottom - 3),
        ];

        // 그림자: 반투명(알파 45) 진한 파랑 말풍선을 오른쪽 아래로 2~3px 밀어서 먼저 그린다 → 살짝 떠 있는 느낌
        using (var shadow = new SolidBrush(Color.FromArgb(45, 0, 40, 120)))
        {
            var sb = bubble;
            sb.Offset(2, 3); // Rectangle은 값 형식(struct)이라 복사본(sb)만 옮겨지고 bubble은 그대로다
            using var sp = RoundedRect(sb, bubble.Height * 0.38f);
            g.FillPath(shadow, sp);
        }

        // 흰 말풍선 몸통 + 꼬리
        using (var path = RoundedRect(bubble, bubble.Height * 0.38f)) g.FillPath(Brushes.White, path);
        g.FillPolygon(Brushes.White, tail);

        // 파란 점 3개를 가운데 정렬: 점 3개 + 간격 2개의 전체 폭을 구해 남는 공간을 반으로 나눠 시작 x를 정한다
        float dot = bubble.Height * 0.18f;
        float gap = dot * 0.8f;
        float startX = bubble.X + (bubble.Width - (dot * 3 + gap * 2)) / 2;
        float y = bubble.Y + (bubble.Height - dot) / 2;
        using var dotBrush = new SolidBrush(Primary);
        for (int i = 0; i < 3; i++)
            g.FillEllipse(dotBrush, startX + i * (dot + gap), y, dot, dot);
    }
}
