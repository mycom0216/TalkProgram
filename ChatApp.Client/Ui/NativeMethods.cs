// ─────────────────────────────────────────────────────────────────────────────
// NativeMethods.cs
// 역할  : .NET에 없는 윈도우 기능(작업 표시줄 아이콘 깜빡이기)을 Windows API로 직접 호출한다.
// 스레드: UI 스레드에서 호출 (form.Handle은 창을 만든 스레드에서 쓰는 것이 원칙).
// 연결  : MainForm.AddMessage가 새 메시지를 받았는데 그 대화창을 보고 있지 않을 때 Flash(window)를 부른다.
// [개념] "NativeMethods"는 외부(네이티브) 함수 선언을 한곳에 모으는 클래스의 관례적 이름이다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   .NET이 제공하지 않는 윈도우 기능 3가지를 user32.dll에서 직접 불러 쓴다(P/Invoke): ① FlashWindowEx(작업 표시줄 아이콘 깜빡임) ② GetLastInputInfo(PC 전체의 마지막 키보드·마우스 입력 시각 → 자리 비움 자동 감지) ③ DestroyIcon(GetHicon으로 만든 아이콘 핸들 해제).
//
// ■ 어디서 쓰나 (코드로 확인함)
//   - Flash: MainForm.Network의 새 메시지 처리에서, 그 대화를 지금 보고 있지 않을 때
//   - IdleTime: MainForm.Status의 자리 비움 자동 감지(입력이 일정 시간 없으면 자리 비움으로 바꿈)
//   - DestroyIcon: MainForm.Status가 상태 점 아이콘을 만들 때 쓴 핸들을 되돌려 줌(GDI 핸들 누수 방지)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - P/Invoke(DllImport) ............................ "C# P/Invoke DllImport user32 extern"
//   - 구조체 레이아웃과 cbSize ........................ "StructLayout Sequential cbSize WinAPI struct"
//   - ref 인자로 구조체 포인터 전달 ..................... "P/Invoke ref struct pointer marshaling"
//   - 마지막 입력 시각(GetLastInputInfo)과 TickCount 래핑 .. "GetLastInputInfo idle time TickCount wraparound"
//   - GDI 핸들 누수(Icon.FromHandle, DestroyIcon) ........ "Icon.FromHandle GetHicon DestroyIcon leak"
//
// ■ 면접 예상 질문과 답
//   Q1. P/Invoke가 뭔가요? 위험한 점은요?
//       A. C# 안에서 윈도우 DLL의 C 함수를 부르는 기능입니다. 구조체 모양(필드 순서·크기)이 틀리면 메모리를 잘못 읽어 예측하기 어려운 오류가 나므로 StructLayout으로 순서를 고정하고, 크기(cbSize)를 API에 알려 줍니다. 이 프로젝트는 윈도우 전용(WinForms)이라 이식성 문제는 없습니다.
//   Q2. 자리 비움 자동 감지는 어떻게 하나요? 왜 메신저 창의 입력만 보지 않나요?
//       A. 메신저 창에 입력이 없어도 다른 프로그램으로 일하고 있을 수 있어서, 윈도우가 기록하는 "PC 전체의 마지막 입력 시각"(GetLastInputInfo)을 읽어 지금 시각과의 차이를 구합니다.
//   Q3. TickCount 뺄셈에 unchecked를 쓰는 이유는요?
//       A. 시스템 시간 값(밀리초)이 약 49일마다 0으로 돌아갑니다. 부호 없는 32비트 뺄셈은 돌아간 경우에도 올바른 차이가 나오는데, checked 환경에서는 넘침 예외가 날 수 있어 unchecked로 명시합니다.
//   Q4. Icon.FromHandle로 만든 아이콘은 Dispose하면 되지 않나요?
//       A. Icon.FromHandle은 핸들을 "빌려" 쓸 뿐 소유하지 않아서 Dispose해도 핸들이 해제되지 않습니다. 직접 DestroyIcon을 불러야 합니다(복제본을 만든 뒤 원래 핸들을 돌려주는 방식).
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - Flash는 UI 스레드에서만 호출해야 한다(form.Handle 때문). 호출하는 쪽(MainForm)이 UI 스레드에서 실행되므로 현재는 문제가 없다.
//   - GetLastInputInfo는 원격 데스크톱/가상 세션에서는 값이 세션 기준이라, 서버 PC에서 도는 경우와 사용자 PC에서 도는 경우에 감지 결과가 다를 수 있다(일반 PC 사용에서는 해당 없음).
// ───────────────────────────────────────────────────────────────────────────
using System.Runtime.InteropServices;

namespace ChatApp.Client.Ui;

/// <summary>새 메시지가 왔을 때 작업 표시줄 아이콘을 깜빡이게 한다 (Windows API).</summary>
internal static class NativeMethods
{
    // [개념] StructLayout(LayoutKind.Sequential): 필드를 "적힌 순서 그대로" 메모리에 배치하라는 지시.
    //        C/C++로 만든 Windows API는 구조체의 메모리 모양이 정확히 같아야 읽을 수 있으므로,
    //        .NET이 마음대로 필드 순서를 바꾸지 못하게 고정한다. (윈도우 SDK의 FLASHWINFO 구조체와 같은 모양)
    /// <summary>FlashWindowEx에 넘기는 창 깜빡임 설정 구조체 (Windows SDK의 FLASHWINFO와 같은 모양).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        /// <summary>이 구조체의 크기(바이트). API가 버전 확인용으로 쓴다.</summary>
        public uint cbSize;

        /// <summary>깜빡일 창의 핸들(윈도우가 창마다 붙이는 번호).</summary>
        public IntPtr hwnd;

        /// <summary>무엇을 어떻게 깜빡일지 정하는 플래그 (FLASHW_ 상수를 OR로 합친 값).</summary>
        public uint dwFlags;

        /// <summary>깜빡일 횟수.</summary>
        public uint uCount;

        /// <summary>깜빡임 간격(ms). 0이면 커서 깜빡임 속도와 같게.</summary>
        public uint dwTimeout;
    }

    // [개념] P/Invoke(Platform Invoke): DllImport로 윈도우의 user32.dll 안에 있는 C 함수 FlashWindowEx를
    //        C# 메서드처럼 부를 수 있게 선언한다. 몸체가 없는 extern 메서드이며 실제 코드는 DLL에 있다.
    // [개념] ref: 구조체를 복사하지 않고 "주소"로 넘긴다 (C 함수가 포인터(FLASHWINFO*)를 받기 때문).
    // [개념] MarshalAs(UnmanagedType.Bool): C의 BOOL(4바이트 정수)을 C#의 bool로 바꿔 받는 방법을 지정한다.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    // 윈도우 SDK 헤더(WinUser.h)에 정의된 값과 같아야 한다
    private const uint FLASHW_ALL = 3;        // 제목 표시줄 + 작업 표시줄
    private const uint FLASHW_TIMERNOFG = 12; // 창이 앞으로 올 때까지 계속

    /// <summary>창이 활성화될 때까지 작업 표시줄 아이콘과 제목 표시줄을 깜빡인다.</summary>
    public static void Flash(Form form)
    {
        // [주의] 이미 닫힌 창이나 아직 실제 윈도우 창(핸들)이 만들어지지 않은 폼은 깜빡일 수 없다.
        //        핸들이 없을 때 form.Handle을 읽으면 창이 강제로 만들어지므로 먼저 확인한다.
        if (form.IsDisposed || !form.IsHandleCreated) return;
        var info = new FLASHWINFO
        {
            // [개념] Marshal.SizeOf: 이 구조체가 네이티브 메모리에서 차지하는 크기를 계산 (32/64비트에 따라 다름)
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = form.Handle,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG, // 비트 OR로 두 옵션을 합친다
            uCount = uint.MaxValue,                  // 사실상 무한 (TIMERNOFG라 사용자가 창을 보면 멈춤)
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    }

    // ───────── 자리 비움 자동 감지 ─────────

    /// <summary>GetLastInputInfo에 넘기고 결과를 돌려받는 마지막 입력 시각 구조체 (Windows SDK의 LASTINPUTINFO와 같은 모양).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        /// <summary>이 구조체의 크기(바이트). 호출 전에 채워 넣는다.</summary>
        public uint cbSize;

        /// <summary>마지막으로 키보드·마우스 입력이 있었던 시각 (부팅 후 밀리초).</summary>
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>
    /// 사용자가 키보드·마우스를 마지막으로 쓴 뒤 지난 시간 (이 프로그램뿐 아니라 PC 전체 기준).
    /// [왜] 메신저 창에 입력이 없어도 다른 프로그램으로 일하는 중일 수 있으므로, 윈도우가 기록하는 "PC 전체 마지막 입력"을 본다.
    /// [개념] unchecked: TickCount는 약 49일마다 0으로 돌아간다. uint 뺄셈을 넘침 검사 없이 하면 돌아간 경우에도 차이가 맞게 나온다.
    /// </summary>
    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    /// <summary>GetHicon()으로 만든 아이콘 핸들을 돌려준다. 안 하면 프로그램이 끝날 때까지 GDI 핸들이 남는다.</summary>
    // [개념] Icon.FromHandle은 핸들을 "빌려" 쓸 뿐 소유하지 않아서, Icon.Dispose()로는 핸들이 해제되지 않는다.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr handle);
}
