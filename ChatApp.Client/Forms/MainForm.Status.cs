using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Net.Sockets;
using System.Security.Authentication;
using ChatApp.Client.Chat;
using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Security;
using ChatApp.Common.Transfer;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] 내 상태(온라인·자리 비움 등)와 상태 메시지, 10분 무입력 자동 자리 비움, 트레이 아이콘, 화면 오른쪽 아래 알림 창.
// MainForm은 C#의 partial class(클래스를 여러 파일로 나누는 기능)로 쪼개 두었다.
// 이 파일도 MainForm.cs와 "같은 클래스"다. 필드(_client, _me 등)와 메서드를 파일 구분 없이 그대로 쓴다.
// 나눈 기준은 "기능별"이고, 동작은 나누기 전과 완전히 같다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   "내가 지금 어떤 상태인가"와 "알림은 어떻게 전달하나"를 맡는 파일: 상태 메뉴(온라인/자리 비움/회의 중/방해 금지), 상태 메시지, 10분 무입력 자동 자리 비움, 시스템 트레이 아이콘, 오른쪽 아래 토스트 알림, 비밀번호 변경, 관리자 창 열기.
//
// ■ 상태가 정해지는 규칙
//   - 상태 메뉴에서 직접 고름 → SetPresence(auto:false): _autoAway=false (직접 고른 것은 자동으로 안 바뀜)
//   - 30초 타이머(CheckIdle): 온라인이고 PC 입력이 10분 없으면 → 자리 비움(auto:true, _autoAway=true)
//   - 다시 입력이 생기면(1분 미만) 자동 자리 비움이었을 때만 → 온라인으로 복귀
//   - 서버는 로그인할 때마다 "온라인"으로 시작하므로, 재접속하면 내가 고른 상태를 다시 보낸다(MainForm.Network.cs의 HandleRelogin)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 입력 없음 감지(GetLastInputInfo, P/Invoke) ......... "GetLastInputInfo idle time C# P/Invoke"
//   - NotifyIcon(트레이)와 ContextMenuStrip ............... "NotifyIcon tray icon menu WinForms"
//   - GDI 핸들 누수(Icon.FromHandle, DestroyIcon) ......... "Icon.FromHandle GetHicon DestroyIcon leak"
//   - 상태를 "직접 선택"과 "자동"으로 구분하는 플래그 ...... "manual vs automatic state flag presence"
//   - 토스트 알림 창 한 번에 하나 .......................... "toast notification single instance form"
//   - 메뉴 권한은 편의, 진짜 권한은 서버 ................... "client side authorization is not security"
//
// ■ 면접 예상 질문과 답
//   Q1. "자리 비움"을 어떻게 자동으로 감지하나요?
//       A. 윈도우의 GetLastInputInfo(user32.dll)로 "마지막 키보드·마우스 입력 이후 지난 시간"을 얻습니다(NativeMethods.IdleTime). 30초마다 UI 타이머가 확인하고, 온라인 상태에서 10분 이상이면 자동 자리 비움으로 바꿔 서버에 알립니다. 입력이 다시 생기면(1분 미만) 되돌립니다.
//   Q2. 사용자가 직접 고른 "자리 비움"이나 "회의 중"은 왜 자동으로 안 바뀌나요?
//       A. 자동으로 만든 자리 비움(_autoAway)만 자동으로 되돌리기 때문입니다. 사용자가 일부러 "회의 중"을 골랐는데 마우스를 움직였다고 온라인으로 바뀌면 사용자 의도를 덮어쓰는 것이므로, "누가 바꿨는지"를 플래그로 구분합니다.
//   Q3. 트레이 아이콘을 만들 때 주의한 점은요?
//       A. (1) X 버튼은 닫기가 아니라 숨기기(OnMainClosing), 종료는 트레이 메뉴에서만. (2) NotifyIcon은 Dispose하지 않으면 프로그램이 끝난 뒤에도 유령 아이콘이 남는다. (3) 툴팁 Text는 최대 63자라 짧게 유지(UpdateTrayText). (4) 코드로 그린 아이콘은 Icon.FromHandle이 핸들을 소유하지 않으므로 Clone한 뒤 원래 핸들을 DestroyIcon으로 돌려줘 GDI 핸들 누수를 막는다.
//   Q4. 관리자 메뉴를 숨기는 것으로 권한이 지켜지나요?
//       A. 아닙니다. 메뉴는 로그인 때 받은 Role이 ADMIN일 때만 보여 주는 화면 편의입니다. 메뉴를 억지로 띄우거나 패킷을 직접 만들어 보내도, 서버가 관리 요청마다 역할을 다시 확인해 거부합니다("클라이언트 검사는 보안이 아니다").
//   Q5. 비밀번호를 바꾸면 자동 재접속 정보는 어떻게 되나요?
//       A. 서버는 비밀번호 변경 때 그 사용자의 자동 로그인 토큰을 모두 폐기합니다. 그래서 클라이언트도 LastTokenLogin을 지우고, LastLogin의 비밀번호를 새 값으로 바꿔 재접속이 계속 되게 합니다. 안 바꾸면 끊긴 뒤 예전 비밀번호로 재로그인을 시도해 실패합니다.
//   Q6. 알림(토스트) 정책은요?
//       A. 방해 금지 상태면 토스트를 띄우지 않고, 한 번에 하나만 보여 줍니다(새 것이 오면 이전 것을 닫음). 소리는 회의 중·방해 금지에서 끄고, 토스트 클릭은 그 대화창을 엽니다. 어떤 메시지를 알릴지의 규칙은 AddMessage(MainForm.Network.cs)에 있습니다.
//   Q7. 한계는요? (솔직하게 답하기)
//       A. (1) 자리 비움은 PC 전체 입력 기준이라 "다른 프로그램을 열심히 쓰는 중"이어도 이 앱의 입력이 아니면 온라인으로 유지된다(의도된 방식). 반대로 화면 잠금 중에는 입력이 없어 자리 비움이 된다. (2) 토스트가 하나뿐이라 연속으로 오면 이전 알림이 사라진다. (3) 상태 문자열(Theme.PresenceText)이 클라이언트에 있어 언어 변경이 어렵다.
//
// ■ 문제가 났을 때 어디를 볼까
//   - 자동 자리 비움이 안 된다/너무 빨리 된다 .. AwayAfter(10분), _idleTimer(30초), NativeMethods.IdleTime
//   - 종료했는데 트레이 아이콘이 남는다 ........ OnMainClosing의 _tray.Dispose, 프로세스 비정상 종료
//   - 토스트가 안 뜬다 ......................... 상태가 방해 금지인지, ShowToast 호출 조건(AddMessage)
//   - 상태가 재접속 후 온라인으로 돌아간다 ...... HandleRelogin의 StatusUpdate 재전송 조건(_presence != Online)
//   - 핸들이 계속 늘어난다 ..................... StatusDotImage/아이콘/메뉴 Dispose(MainForm.Dispose)
// ───────────────────────────────────────────────────────────────────────────

public sealed partial class MainForm
{
    // ───────────────────────── 상태 표시·자리 비움 ─────────────────────────

    private void BuildStatusMenu()
    {
        foreach (Presence p in Enum.GetValues<Presence>())
        {
            var item = new ToolStripMenuItem(Theme.PresenceText(p)) { Tag = p, Image = StatusDotImage(Theme.PresenceColor(p)) };
            item.Click += (_, _) => SetPresence(p, auto: false);
            _statusMenu.Items.Add(item);
        }
        _statusMenu.Items.Add(new ToolStripSeparator());
        _statusMenu.Items.Add("상태 메시지 바꾸기...", null, (_, _) => ChangeStatusMessage());
        _statusMenu.Items.Add(new ToolStripSeparator());
        _statusMenu.Items.Add("비밀번호 변경...", null, (_, _) => ChangePassword());
        _statusMenu.Items.Add("내 휴대폰 번호 설정...", null, (_, _) => EditPhone());
        _statusMenu.Items.Add("프로필 사진 바꾸기...", null, (_, _) => ChangeProfilePhoto());
        _statusMenu.Items.Add("프로필 사진 지우기", null, (_, _) => RemoveProfilePhoto());
        _statusMenu.Items.Add("배경 사진 바꾸기...", null, (_, _) => UploadPhoto(PhotoKind.Background, this));
        _statusMenu.Items.Add("내 프로필 보기", null, (_, _) => ShowProfile(_me));
        // [주의] 관리자 메뉴는 로그인할 때 받은 권한(Role)이 ADMIN일 때만 보인다. 이건 화면 편의일 뿐이고,
        //        진짜 권한 확인은 서버가 관리 요청마다 다시 한다(메뉴를 억지로 띄워도 서버가 거절).
        if (_me.IsAdmin)
            _statusMenu.Items.Add("관리자 메뉴...", null, (_, _) => OpenAdmin(showPending: false));
        _statusMenu.Items.Add(new ToolStripSeparator());
        _statusMenu.Items.Add("전체 대화 검색...", null, (_, _) => OpenSearch());
        _statusMenu.Items.Add("동료 찾기 (오프라인 포함)...", null, (_, _) => FindPeople());
        _statusMenu.Items.Add("친구 추가 (아이디·휴대폰 번호)...", null, (_, _) => OpenAddFriend());
        _statusMenu.Items.Add(new ToolStripSeparator());
        _statusMenu.Items.Add("로그아웃", null, (_, _) => Logout());
        // 메뉴를 열 때 지금 상태에 체크 표시
        // [읽는 법] 메뉴가 열리기 직전에 매번 실행된다. Tag에 Presence를 넣어 둔 항목만 골라 "지금 내 상태와 같으면 체크"한다. 항목 패턴 { Tag: Presence p }는 "Tag가 Presence이면 p에 담는다"는 뜻.
        _statusMenu.Opening += (_, _) =>
        {
            foreach (ToolStripItem i in _statusMenu.Items)
                if (i is ToolStripMenuItem { Tag: Presence p } m) m.Checked = p == _presence;
        };
    }

    /// <summary>메뉴에 넣을 작은 색 점 그림.</summary>
    private Bitmap StatusDotImage(Color color)
    {
        int size = S(12);
        var bmp = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(bmp);
        Theme.DrawStatusDot(g, new Rectangle(0, 0, size - 1, size - 1), color);
        return bmp;
    }

    /// <summary>내 상태를 바꾸고 서버에 알린다. auto = 10분 무입력으로 자동 전환된 경우.</summary>
    private void SetPresence(Presence presence, bool auto)
    {
        _presence = presence;
        _autoAway = auto && presence == Presence.Away;
        _header.Invalidate();
        if (_connected)
            _client.Send(Packet.Create(PacketType.StatusUpdate).WithData(new StatusUpdate { Presence = presence })); // 메시지는 null = 그대로
    }

    /// <summary>내 비밀번호 바꾸기. 성공하면 자동 재접속에 쓸 비밀번호도 새 것으로 바꾼다.</summary>
    private void ChangePassword()
    {
        using var form = new ChangePasswordForm(_client);
        if (form.ShowDialog(this) == DialogResult.OK && _client.LastLogin is LoginRequest last)
        {
            _client.LastLogin = new LoginRequest { EmployeeNo = last.EmployeeNo, Password = form.NewPassword, ProtocolVersion = ProtocolInfo.Version };
            // [읽는 법] 서버가 비밀번호 변경 때 이 사용자의 자동 로그인 토큰을 모두 지운다(UserRepository.UpdatePassword). 낡은 토큰으로 재접속하면 실패하므로 클라이언트도 버리고, 아래 LastLogin(새 비밀번호)으로 재로그인한다.
            _client.LastTokenLogin = null; // 서버가 비밀번호 변경과 함께 기존 bearer 토큰을 폐기함
        }
    }

    /// <summary>관리자 창 열기 (이미 열려 있으면 앞으로). showPending = 승인 대기 목록부터.</summary>
    private void OpenAdmin(bool showPending)
    {
        if (_adminForm is null || _adminForm.IsDisposed)
        {
            _adminForm = new AdminForm(_client, _me.EmployeeNo);
            _adminForm.FormClosed += (_, _) => _adminForm = null;
            _adminForm.Show(); // 모달이 아니게: 관리 창을 띄워 둔 채로 채팅도 할 수 있다
        }
        else
        {
            if (_adminForm.WindowState == FormWindowState.Minimized) _adminForm.WindowState = FormWindowState.Normal;
            _adminForm.Activate();
        }
        if (showPending) _adminForm.ShowPending();
    }

    private void ChangeStatusMessage()
    {
        using var form = new StatusMessageForm(_statusMessage);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        _statusMessage = form.Message;
        _header.Invalidate();
        _client.Send(Packet.Create(PacketType.StatusUpdate).WithData(new StatusUpdate { Presence = _presence, StatusMessage = _statusMessage }));
        RefreshProfileCard();
    }

    /// <summary>
    /// 30초마다: PC를 10분 동안 안 쓰면 자동 "자리 비움", 다시 쓰기 시작하면 자동 "온라인".
    /// [왜] 직접 고른 상태(회의 중, 직접 고른 자리 비움)는 건드리지 않는다. 자동으로 바꾼 것만 자동으로 되돌린다.
    /// </summary>
    private void CheckIdle()
    {
        if (!_connected) return;
        TimeSpan idle = NativeMethods.IdleTime();
        if (_presence == Presence.Online && idle >= AwayAfter) SetPresence(Presence.Away, auto: true);
        // [읽는 법] 복귀 조건: 자동으로 자리 비움이 된 경우에만(_autoAway), 입력이 1분 안에 있었을 때 온라인으로. 직접 고른 자리 비움(_autoAway=false)은 그대로 둔다. 30초 타이머라 복귀는 최대 30초 늦다.
        else if (_autoAway && idle < TimeSpan.FromMinutes(1)) SetPresence(Presence.Online, auto: false);
    }

    /// <summary>동료 목록·1:1 창에 보일 한 줄: 상태 메시지(없으면 부서·사번), 온라인이 아니면 앞에 상태.</summary>
    private static string UserSubtitle(UserInfo user)
    {
        string sub = user.StatusMessage.Length > 0 ? user.StatusMessage : $"{user.Department} · {user.EmployeeNo}";
        return user.Presence == Presence.Online ? sub : $"{Theme.PresenceText(user.Presence)} · {sub}";
    }

    // ───────────────────────── 트레이·알림 창 ─────────────────────────

    private void BuildTray()
    {
        _tray.Icon = Icon;
        _tray.Text = Theme.AppName;
        var menu = new ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => ShowFromTray());
        var status = new ToolStripMenuItem("상태");
        foreach (Presence p in Enum.GetValues<Presence>())
            status.DropDownItems.Add(Theme.PresenceText(p), StatusDotImage(Theme.PresenceColor(p)), (_, _) => SetPresence(p, auto: false));
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("로그아웃", null, (_, _) => { ShowFromTray(); Logout(); });
        // [읽는 법] 진짜 종료의 유일한 길: _exiting을 켜야 OnMainClosing이 닫기를 막지 않는다(X 버튼은 숨기기). 로그아웃(Logout)도 같은 방식으로 _exiting을 켜고 닫는다.
        menu.Items.Add("종료", null, (_, _) => { _exiting = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _trayMenu = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
        _tray.Visible = true;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>
    /// 앱 아이콘(파란 둥근 사각형 + 흰 말풍선 로고)을 코드로 그린다. 창 제목 표시줄과 트레이에 같이 쓴다.
    /// [개념] Icon.FromHandle(bitmap.GetHicon()): 그림(Bitmap)을 윈도우 아이콘 핸들로 바꾼다. 프로그램 실행 중 한 번만 만든다.
    /// </summary>
    private static Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Theme.FillRounded(g, Theme.Primary, new Rectangle(0, 0, 31, 31), 8);
            Theme.DrawLogo(g, new Rectangle(5, 7, 22, 19));
        }
        // [주의] Icon.FromHandle은 핸들을 소유하지 않아 Dispose해도 GDI 핸들이 남는다.
        //        복제본(Clone)은 자기 핸들을 가지므로, 원래 핸들은 바로 돌려준다(DestroyIcon).
        // [읽는 법] 비트맵 → 윈도우 아이콘 핸들. Icon.FromHandle은 핸들을 빌려 쓸 뿐이라 Dispose해도 핸들이 남는다. Clone으로 자기 핸들을 가진 복사본을 만들고, 원래 핸들은 finally에서 DestroyIcon으로 돌려준다(누수 방지).
        IntPtr handle = bmp.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    /// <summary>
    /// 화면 오른쪽 아래 알림 창. 방해 금지면 띄우지 않는다. 한 번에 하나만 (이전 것은 닫음).
    /// </summary>
    private void ShowToast(string title, string text, string avatarKey, Action onClick)
    {
        // [읽는 법] 방해 금지 상태에서는 토스트 자체를 만들지 않는다(안 읽은 수와 작업 표시줄 깜빡임은 별도로 계속 동작). 소리 끄기는 AddMessage가 따로 결정한다.
        if (_presence == Presence.DoNotDisturb) return;
        _toast?.Close();
        var toast = new ToastForm(title, text, avatarKey);
        toast.Click += (_, _) => onClick();
        toast.FormClosed += (_, _) => { if (_toast == toast) _toast = null; };
        _toast = toast;
        toast.Show();
    }

    /// <summary>안 읽은 메시지 수를 트레이 아이콘 툴팁에 (마우스를 올리면 보임).</summary>
    private void UpdateTrayText()
    {
        int unread = _conversations.Values.Sum(c => c.Unread);
        // [주의] NotifyIcon.Text는 최대 63자. 넘으면 예외가 나므로 짧게 유지한다.
        _tray.Text = unread > 0 ? $"{Theme.AppName} - 안 읽은 메시지 {unread}개" : Theme.AppName;
    }
}
