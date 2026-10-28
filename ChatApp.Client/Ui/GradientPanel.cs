// ─────────────────────────────────────────────────────────────────────────────
// GradientPanel.cs
// 역할  : 메인 창·대화창 위쪽 "머리 영역"의 하늘색 그라데이션 배경을 그리는 패널.
//         패널 자체는 배경만 칠하고, 그 위의 아바타·이름 같은 글자는 사용하는 쪽(MainForm, ChatWindow)이
//         Paint 이벤트에 연결한 메서드에서 직접 그린다.
// 스레드: UI 스레드 전용 (WinForms 컨트롤은 만든 스레드에서만 다뤄야 한다).
// 연결  : 실제 색·그라데이션 그리기는 Theme.FillGradient에 맡긴다(색을 한 곳에서 관리).
// ─────────────────────────────────────────────────────────────────────────────
namespace ChatApp.Client.Ui;

/// <summary>하늘색 그라데이션 배경 패널. 글자·아바타는 Paint 이벤트에서 직접 그린다.</summary>
internal sealed class GradientPanel : Panel
{
    public GradientPanel()
    {
        // [개념] SetStyle로 컨트롤의 그리기 방식을 바꾼다.
        //  - OptimizedDoubleBuffer : "더블 버퍼링". 화면에 바로 그리지 않고 메모리 속 그림(버퍼)에 다 그린 뒤
        //                            한 번에 화면으로 복사한다 → 다시 그릴 때 깜빡임이 없다.
        //  - AllPaintingInWmPaint  : 배경 지우기(WM_ERASEBKGND)를 따로 하지 않고 그리기(WM_PAINT) 한 번에 처리 → 깜빡임 감소.
        //  - UserPaint             : 운영체제 대신 우리 코드(OnPaint 계열)가 직접 그린다.
        //  - ResizeRedraw          : 크기가 바뀌면 전체를 다시 그린다(그라데이션은 크기에 따라 모양이 달라지므로 필수).
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    // [왜] 배경 칠하기(OnPaintBackground)를 재정의해 단색 대신 그라데이션을 칠한다.
    //      글자는 이 위에 Paint 이벤트(OnPaint)에서 그려지므로 순서가 자연스럽게 "배경 → 글자"가 된다.
    protected override void OnPaintBackground(PaintEventArgs e) => Theme.FillGradient(e.Graphics, ClientRectangle);
}
