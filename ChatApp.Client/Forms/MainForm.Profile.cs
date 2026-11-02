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
// [파일 요약] 친구(추가·삭제·목록), 프로필 카드, 프로필 사진·배경 사진 올리기와 지우기, 휴대폰 번호 설정, 목록 오른쪽 클릭 메뉴.
// MainForm은 C#의 partial class(클래스를 여러 파일로 나누는 기능)로 쪼개 두었다.
// 이 파일도 MainForm.cs와 "같은 클래스"다. 필드(_client, _me 등)와 메서드를 파일 구분 없이 그대로 쓴다.
// 나눈 기준은 "기능별"이고, 동작은 나누기 전과 완전히 같다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   "사람"에 관한 기능 모음: 친구 추가/삭제, 프로필 카드, 프로필·배경 사진 올리기/지우기, 휴대폰 번호, 목록 오른쪽 클릭 메뉴, 로그아웃, 새 버전 확인. 모두 "요청 전송 → 서버 방송 → 화면 갱신" 패턴.
//
// ■ 사진 올리기 흐름
//   파일 고르기 → ImageTools.MakeJpeg로 클라이언트에서 줄이기(프로필 160×160 / 배경 720×960, 최대 256KB / 600KB) → Packet.Bin에 JPEG 바이트 → ProfilePhotoUpdate 전송
//   → 서버가 검증·저장하고 "버전 번호"를 올려 방송(ProfilePhoto) → AvatarCache.Handle이 새 버전만 내려받아 캐시
//   ※ 지우기는 같은 패킷에서 Bin이 없는 경우(Bin 없음 = 지우기).
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 클라이언트에서 미리 줄이기(용량·해상도 한도) ......... "client side image resize before upload"
//   - 버전 번호로 캐시 무효화(cache busting) ............... "cache invalidation version number avatar"
//   - 튜플 반환과 분해((UserInfo, bool)) ................... "C# tuple deconstruction"
//   - 폼 한 개만 띄우기(싱글턴 창) ......................... "single instance form IsDisposed BringToFront"
//   - 버전 비교(Assembly 버전, Version.TryParse) ........... "Assembly version compare update check"
//   - GDI+ OutOfMemoryException = 잘못된 이미지 형식 ........ "GDI+ OutOfMemoryException invalid image"
//
// ■ 면접 예상 질문과 답
//   Q1. 사진을 서버에서 줄이지 않고 클라이언트에서 줄이는 이유는요?
//       A. 업로드·저장·배포 용량을 줄이려고 클라이언트가 먼저 규격(160px, 256KB)에 맞춥니다. 하지만 클라이언트를 믿을 수 없으므로 서버가 크기·형식을 다시 검사합니다(서버 한도 초과나 이미지가 아닌 데이터는 거부). 클라이언트의 가공은 "편의와 대역폭 절약", 서버 검사는 "안전"입니다.
//   Q2. 프로필 사진이 바뀌면 다른 사람들 화면은 어떻게 갱신되나요?
//       A. 사진을 통째로 방송하지 않고 "사진 버전 번호"만 UserInfo에 실어 보냅니다. 클라이언트의 AvatarCache가 가진 버전과 다르면 그 사람 사진만 서버에서 내려받아 메모리·디스크에 캐시합니다. 접속자 목록이 올 때마다 모든 사진을 다시 받지 않게 하는 캐시 무효화 방식입니다.
//   Q3. 접속하지 않은 사람의 프로필 카드도 볼 수 있나요? 정보는 어디서 오나요?
//       A. 카드를 열 때 아는 정보(접속자 → 친구 → 이전에 본 사람 순)로 먼저 열고, 서버에 ProfileReq를 보내 최신 상태 메시지와 사진 버전을 받아 채웁니다(ProfileRes). 그래서 말풍선의 사진을 눌렀을 때 접속하지 않은 사람도 카드가 뜹니다.
//   Q4. 새 버전 알림은 어떻게 동작하나요? 안전한가요?
//       A. 로그인 후 ServerInfoReq로 서버가 최신 클라이언트 버전과 설치 파일 위치를 알려 주고, 내 어셈블리 버전과 비교해 낮으면 상태 줄·토스트·메뉴로 알립니다. 한계: 설치 파일 위치는 서버 설정(ClientUpdateUrl)에서 오고 클라이언트가 그 주소를 ShellExecute로 엽니다. TLS 지문 고정으로 서버를 믿는 만큼은 안전하지만, 설치 파일의 서명·해시를 검증하지는 않습니다. 운영에서는 공유 폴더 권한과 코드 서명으로 보완해야 합니다.
//   Q5. 로그아웃은 어떻게 구현했나요?
//       A. LogoutRequested = true, _exiting = true를 켜고 창을 닫습니다. OnMainClosing이 전송·타이머·창을 정리하고 Disconnect로 Logout 패킷을 보냅니다. Program이 LogoutRequested를 보고 프로그램을 끝내지 않고 로그인 창으로 돌아갑니다. 로그인 정보(LastLogin)는 새 ChatClient와 함께 버려집니다.
//   Q6. 한계는요? (솔직하게 답하기)
//       A. (1) 친구 삭제 확인 대화상자 코드가 두 곳(카드 액션, 목록 메뉴)에 복사돼 있다. (2) 내 사진을 바꾼 직후 상태 줄에 "바꾸는 중"만 보이고 실패하면 서버 오류 패킷으로 알게 된다(전용 실패 처리 없음). (3) 새 버전 비교가 Major.Minor.Build만 본다(Revision 무시).
//
// ■ 문제가 났을 때 어디를 볼까
//   - 사진을 올렸는데 안 바뀐다 ............. ImageTools.MakeJpeg 결과(null이면 너무 큼), ProfilePhotoUpdate 전송, 서버 응답(ProfilePhoto), AvatarCache.Handle
//   - 프로필 카드에 상태 메시지가 비어 있다 .. ProfileReq/ProfileRes, _knownPeers, RefreshProfileCard
//   - 친구 추가가 안 된다 ................... FriendAdd 응답(FriendList 갱신 또는 Error), AddFriendForm이 기다리는 중인지(IsWaiting)
//   - 새 버전 알림이 안 뜬다 ................ 서버 ServerConfig의 LatestClientVersion, 내 exe 버전, _updateNotified(한 번만)
//   - 로그아웃 후 프로그램이 종료된다 ......... LogoutRequested를 Program이 처리하는지
// ───────────────────────────────────────────────────────────────────────────

/// <summary>메인 창(partial) - 친구 관리, 프로필 카드·사진, 휴대폰 번호 설정, 목록 오른쪽 클릭 메뉴 부분.</summary>
public sealed partial class MainForm
{
    // ───────────────────────── 친구 ─────────────────────────

    private bool IsFriend(string employeeNo) => _friends.Any(f => string.Equals(f.EmployeeNo, employeeNo, StringComparison.OrdinalIgnoreCase));

    /// <summary>친구 추가 창 (하나만 띄우고, 이미 열려 있으면 앞으로).</summary>
    private void OpenAddFriend()
    {
        if (_addFriendForm is { IsDisposed: false })
        {
            BringToFront(_addFriendForm);
            return;
        }
        _addFriendForm = new AddFriendForm(_client);
        _addFriendForm.ChatRequested += user =>
        {
            _knownPeers[user.EmployeeNo] = user;
            OpenDirect(user, activate: true);
        };
        _addFriendForm.Show(this);
    }

    // ───────────────────────── 프로필 (사진·배경·카드) ─────────────────────────

    private void ChangeProfilePhoto() => UploadPhoto(PhotoKind.Profile, this);
    private void RemoveProfilePhoto() => RemovePhoto(PhotoKind.Profile, this);

    /// <summary>사진을 골라 서버에 올린다. 프로필은 가운데 정사각형 160×160, 배경은 세로 3:4 720×960 JPEG로 줄인다.</summary>
    private void UploadPhoto(PhotoKind kind, IWin32Window owner)
    {
        string what = kind == PhotoKind.Background ? "배경 사진" : "프로필 사진";
        using var dialog = new OpenFileDialog
        {
            Title = $"{what} 고르기",
            Filter = "사진 (*.jpg;*.jpeg;*.png;*.bmp;*.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.gif|모든 파일 (*.*)|*.*",
        };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        byte[]? photo;
        try
        {
            photo = kind == PhotoKind.Background
                ? ImageTools.MakeJpeg(dialog.FileName, ProfileRules.BackgroundWidth, ProfileRules.BackgroundHeight, ProfileRules.MaxBackgroundBytes)
                : ImageTools.MakeJpeg(dialog.FileName, ProfileRules.PhotoSize, ProfileRules.PhotoSize, ProfileRules.MaxPhotoBytes);
        }
        // [읽는 법] 예외 필터(when): 사진 열기에서 흔한 "사용자 실수" 네 가지만 잡는다. 코드 버그(NullReference 등)는 잡지 않고 위로 올려 눈에 띄게 둔다. 이름과 달리 OutOfMemory는 "이미지 형식이 아님"일 때 GDI+가 던지는 예외다.
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // [개념] GDI+는 그림이 아닌 파일을 열면 OutOfMemoryException을 던진다(이름과 달리 "형식 오류" 뜻)
            MessageBox.Show(owner, "사진 파일을 열 수 없습니다. JPG·PNG 사진을 골라 주세요.", Theme.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (photo is null)
        {
            MessageBox.Show(owner, "사진을 충분히 작게 줄이지 못했습니다. 다른 사진을 골라 주세요.", Theme.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var packet = Packet.Create(PacketType.ProfilePhotoUpdate).WithData(new PhotoInfo { Kind = kind });
        // [읽는 법] 사진 바이트는 Packet.Bin에 싣는다(JSON 안에서 Base64 문자열로 직렬화되어 약 33% 커진다). 패킷 최대 1MB 한도 안에 들도록 사진을 256KB/600KB 이하로 줄여 둔 것이다.
        packet.Bin = photo;
        if (_client.Send(packet)) SetStatus($"● {what}을 바꾸는 중...", Theme.SubText);
        else SetStatus($"● 서버와 연결이 끊어져 {what}을 바꿀 수 없습니다.", Theme.Danger);
    }

    private void RemovePhoto(PhotoKind kind, IWin32Window owner)
    {
        bool background = kind == PhotoKind.Background;
        string what = background ? "배경 사진" : "프로필 사진";
        long version = background ? _me.BackgroundVersion : _me.PhotoVersion;
        if (version <= 0)
        {
            SetStatus($"● {what}이 없습니다.", Theme.SubText);
            return;
        }
        string after = background ? "기본 배경으로 돌아갑니다." : "이름 첫 글자 아바타로 돌아갑니다.";
        if (MessageBox.Show(owner, $"{what}을 지울까요?\n{after}", Theme.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _client.Send(Packet.Create(PacketType.ProfilePhotoUpdate).WithData(new PhotoInfo { Kind = kind })); // Bin 없음 = 지우기
    }

    /// <summary>내 정보 (상태·상태 메시지는 메인 창이 따로 들고 있으므로 합쳐서).</summary>
    private UserInfo MyProfile() => _me.WithStatus(_presence, _statusMessage);

    private static bool SameNo(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>사원번호로 프로필 카드 열기 (말풍선 사진을 눌렀을 때 등).</summary>
    private void ShowProfileByNo(string employeeNo)
    {
        if (SameNo(employeeNo, _me.EmployeeNo))
        {
            ShowProfile(_me);
            return;
        }
        // [왜] 접속하지 않은 사람은 접속자·친구 목록에 없을 수 있다. 우선 아는 만큼(사원번호) 카드를 열고,
        //      ShowProfile이 서버에 최신 정보를 물어 채운다(ProfileReq → ProfileRes).
        UserInfo user = _users.FirstOrDefault(u => SameNo(u.EmployeeNo, employeeNo))
                        ?? _friends.FirstOrDefault(u => SameNo(u.EmployeeNo, employeeNo))
                        ?? KnownPeer(employeeNo, employeeNo);
        ShowProfile(user);
    }

    /// <summary>프로필 카드 열기 (한 번에 하나만).</summary>
    private void ShowProfile(UserInfo user)
    {
        _profileCard?.Close();
        bool isMe = SameNo(user.EmployeeNo, _me.EmployeeNo);
        // [읽는 법] 튜플 분해: ProfileState가 (사용자 정보, 접속 여부) 두 값을 한 번에 돌려주고 두 변수에 나눠 담는다. 정보 출처 우선순위는 접속자 목록 → 친구 목록 → 알고 있던 정보.
        (UserInfo shown, bool online) = ProfileState(user.EmployeeNo, user);
        // [읽는 법] 카드는 한 번에 하나만: 위에서 이전 카드를 Close한다. FormClosed에서 ReferenceEquals로 비교하는 이유는, 새 카드를 만든 뒤 이전 카드의 닫힘 이벤트가 늦게 와서 새 카드를 null로 지워 버리는 것을 막기 위해서다.
        _profileCard = new ProfileCardForm(shown, isMe, !isMe && IsFriend(user.EmployeeNo), online, _avatars);
        _profileCard.ActionRequested += OnProfileAction;
        _profileCard.FormClosed += (sender, _) => { if (ReferenceEquals(sender, _profileCard)) _profileCard = null; };
        _profileCard.Show(this);
        // 다른 사람이면 최신 상태 메시지·사진 버전을 서버에 묻는다 (접속하지 않은 사람도 볼 수 있게)
        if (!isMe) _client.Send(Packet.Create(PacketType.ProfileReq, body: user.EmployeeNo));
    }

    /// <summary>그 사람의 최신 정보와 접속 여부 (접속자 목록 → 친구 목록 → 알고 있던 정보 순).</summary>
    private (UserInfo User, bool Online) ProfileState(string employeeNo, UserInfo fallback)
    {
        if (SameNo(employeeNo, _me.EmployeeNo)) return (MyProfile(), _connected);
        UserInfo? online = _users.FirstOrDefault(u => SameNo(u.EmployeeNo, employeeNo));
        return (online ?? _friends.FirstOrDefault(u => SameNo(u.EmployeeNo, employeeNo)) ?? fallback, online is not null);
    }

    /// <summary>열려 있는 프로필 카드를 최신 정보로 (목록·사진·상태 메시지가 바뀌었을 때).</summary>
    private void RefreshProfileCard()
    {
        if (_profileCard is not { IsDisposed: false } card) return;
        UserInfo fallback = _knownPeers.TryGetValue(card.EmployeeNo, out UserInfo? known) ? known : new UserInfo { EmployeeNo = card.EmployeeNo };
        (UserInfo user, bool online) = ProfileState(card.EmployeeNo, fallback);
        if (user.Name.Length == 0) return; // 정보를 모르면 그대로 둔다
        card.UpdateUser(user, !SameNo(card.EmployeeNo, _me.EmployeeNo) && IsFriend(card.EmployeeNo), online);
    }

    private void OnProfileAction(ProfileAction action, UserInfo user)
    {
        IWin32Window owner = _profileCard is { IsDisposed: false } card ? card : this;
        switch (action)
        {
            case ProfileAction.Chat:
                _knownPeers.TryAdd(user.EmployeeNo, user);
                OpenDirect(user, activate: true);
                break;
            case ProfileAction.AddFriend:
                _client.Send(Packet.Create(PacketType.FriendAdd, body: user.EmployeeNo));
                break;
            case ProfileAction.RemoveFriend:
                if (MessageBox.Show(owner, $"{user.DisplayName}님을 친구 목록에서 뺄까요?\n대화 내용은 그대로 남습니다.", Theme.AppName,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    _client.Send(Packet.Create(PacketType.FriendRemove, body: user.EmployeeNo));
                break;
            case ProfileAction.ChangePhoto: UploadPhoto(PhotoKind.Profile, owner); break;
            case ProfileAction.RemovePhoto: RemovePhoto(PhotoKind.Profile, owner); break;
            case ProfileAction.ChangeBackground: UploadPhoto(PhotoKind.Background, owner); break;
            case ProfileAction.RemoveBackground: RemovePhoto(PhotoKind.Background, owner); break;
            case ProfileAction.ChangeStatusMessage: ChangeStatusMessage(); break;
        }
    }

    /// <summary>내 휴대폰 번호 등록·변경 (친구가 번호로 나를 찾을 수 있게).</summary>
    private void EditPhone()
    {
        using var form = new PhoneForm(_client);
        form.ShowDialog(this);
    }

    /// <summary>동료·친구 목록에서 오른쪽 클릭: 1:1 대화, 친구 추가/삭제.</summary>
    private void ShowUserMenu(UserInfo user, Point location)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("프로필 보기", null, (_, _) => ShowProfile(user));
        menu.Items.Add("1:1 대화", null, (_, _) => OpenDirect(user, activate: true));
        menu.Items.Add(new ToolStripSeparator());
        if (IsFriend(user.EmployeeNo))
        {
            ToolStripItem remove = menu.Items.Add("친구 삭제", null, (_, _) =>
            {
                if (MessageBox.Show(this, $"{user.DisplayName}님을 친구 목록에서 뺄까요?\n대화 내용은 그대로 남습니다.", Theme.AppName,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    _client.Send(Packet.Create(PacketType.FriendRemove, body: user.EmployeeNo));
            });
            remove.ForeColor = Theme.Danger;
            remove.Enabled = _connected;
        }
        else
        {
            menu.Items.Add("친구 추가", null, (_, _) => _client.Send(Packet.Create(PacketType.FriendAdd, body: user.EmployeeNo))).Enabled = _connected;
        }
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(_userList, location);
    }

    /// <summary>로그아웃 → 연결을 끊고 로그인 창으로 돌아간다 (Program이 LogoutRequested를 본다).</summary>
    private void Logout()
    {
        if (MessageBox.Show(this, "로그아웃할까요?\n열려 있는 대화창이 모두 닫히고 로그인 화면으로 돌아갑니다.", Theme.AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        LogoutRequested = true;
        _exiting = true;
        Close();
    }

    /// <summary>
    /// 서버가 알려 준 최신 클라이언트 버전과 내 버전 비교. 낮으면 상태 줄·알림 창으로 알리고, 설치 파일 위치가 있으면 메뉴에서 열 수 있게 한다.
    /// </summary>
    // [개념] Assembly 버전: csproj의 <Version>1.3.0</Version>이 빌드할 때 exe에 들어간다.
    private void CheckClientVersion(ServerInfo info)
    {
        if (info.ProtocolVersion > ProtocolInfo.Version)
            SetStatus("● 서버가 더 새 버전입니다. 사내톡을 업데이트하세요.", Theme.Notice);

        if (_updateNotified || string.IsNullOrWhiteSpace(info.LatestClientVersion) ||
            !Version.TryParse(info.LatestClientVersion, out Version? latest))
            return;
        Version mine = typeof(MainForm).Assembly.GetName().Version ?? new Version(0, 0);
        // [읽는 법] 지역 정적 함수. 어셈블리 버전은 "1.3.0"(Build 있음)과 "1.3"(Build가 -1)처럼 자릿수가 다를 수 있어, 모자란 자리를 0으로 맞춰 비교한다.
        static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
        if (Norm(mine) >= Norm(latest)) return;

        _updateNotified = true;
        _updateUrl = string.IsNullOrWhiteSpace(info.ClientUpdateUrl) ? null : info.ClientUpdateUrl;
        string how = _updateUrl is null ? "관리자에게 설치 파일을 받으세요." : "내 이름을 눌러 메뉴 → \"새 버전 설치\"";
        SetStatus($"● 새 버전 {Norm(latest).ToString(3)}이 있습니다 (지금 {Norm(mine).ToString(3)}). {how}", Theme.Notice);
        if (_updateUrl is not null)
        {
            _statusMenu.Items.Insert(0, new ToolStripSeparator());
            _statusMenu.Items.Insert(0, new ToolStripMenuItem($"새 버전 {Norm(latest).ToString(3)} 설치...", null, (_, _) => OpenUpdate()));
        }
        ShowToast("새 버전이 있습니다", $"사내톡 {Norm(latest).ToString(3)}이 배포되었습니다. {how}", Theme.AppName,
            () => { if (_updateUrl is not null) OpenUpdate(); });
    }

    private void OpenUpdate()
    {
        if (_updateUrl is null) return;
        try
        {
            // 설치 파일(공유 폴더의 exe) 또는 다운로드 웹 페이지를 윈도우에 연결된 프로그램으로 연다
            // [주의] _updateUrl은 서버가 알려 준 값이다. 서버를 믿는다는 전제(TLS 지문 고정)에서만 안전하며, 설치 파일의 서명을 확인하지는 않는다. 배포 위치(공유 폴더/웹)의 쓰기 권한을 관리자만 갖도록 해야 한다.
            Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"설치 파일을 열 수 없습니다: {ex.Message}\n위치: {_updateUrl}", Theme.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
