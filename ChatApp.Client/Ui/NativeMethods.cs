// ─────────────────────────────────────────────────────────────────────────────
// NativeMethods.cs
// 역할  : .NET에 없는 윈도우 기능(작업 표시줄 아이콘 깜빡이기)을 Windows API로 직접 호출한다.
// 스레드: UI 스레드에서 호출 (form.Handle은 창을 만든 스레드에서 쓰는 것이 원칙).
// 연결  : MainForm.AddMessage가 새 메시지를 받았는데 그 대화창을 보고 있지 않을 때 Flash(window)를 부른다.
// [개념] "NativeMethods"는 외부(네이티브) 함수 선언을 한곳에 모으는 클래스의 관례적 이름이다.
// ─────────────────────────────────────────────────────────────────────────────
using System.Runtime.InteropServices;

namespace ChatApp.Client.Ui;

/// <summary>새 메시지가 왔을 때 작업 표시줄 아이콘을 깜빡이게 한다 (Windows API).</summary>
internal static class NativeMethods
{
    // [개념] StructLayout(LayoutKind.Sequential): 필드를 "적힌 순서 그대로" 메모리에 배치하라는 지시.
    //        C/C++로 만든 Windows API는 구조체의 메모리 모양이 정확히 같아야 읽을 수 있으므로,
    //        .NET이 마음대로 필드 순서를 바꾸지 못하게 고정한다. (윈도우 SDK의 FLASHWINFO 구조체와 같은 모양)
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;    // 이 구조체의 크기(바이트). API가 버전 확인용으로 쓴다
        public IntPtr hwnd;    // 깜빡일 창의 핸들(윈도우가 창마다 붙이는 번호)
        public uint dwFlags;   // 무엇을 어떻게 깜빡일지 (아래 상수)
        public uint uCount;    // 깜빡일 횟수
        public uint dwTimeout; // 깜빡임 간격(ms). 0이면 커서 깜빡임 속도와 같게
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

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime; // 마지막으로 키보드·마우스 입력이 있었던 시각 (부팅 후 밀리초)
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
