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
// [파일 요약] 1:1 대화창 열기, 서버에 저장된 지난 대화(History) 받아 채우기, 접속하지 않은 동안 온 1:1 요약 처리.
// MainForm은 C#의 partial class(클래스를 여러 파일로 나누는 기능)로 쪼개 두었다.
// 이 파일도 MainForm.cs와 "같은 클래스"다. 필드(_client, _me 등)와 메서드를 파일 구분 없이 그대로 쓴다.
// 나눈 기준은 "기능별"이고, 동작은 나누기 전과 완전히 같다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   1:1 대화창을 열고(OpenDirect), 서버에 저장된 지난 대화를 받아 채우고(HandleHistory), 오프라인 중 온 1:1 요약을 알리는(HandleUnreadDirect) 파일.
//
// ■ 방과 1:1의 차이 (이 파일을 읽는 열쇠)
//   방 ..... 입장할 때 서버가 지난 대화를 알아서 보내 준다(RoomRes + History).
//   1:1 .... 서버는 "누가 어떤 1:1 창을 열었는지" 모르므로 클라이언트가 창을 처음 열 때 HistoryReq로 요청해야 한다. (한 번만: conv.HistoryRequested)
//   그래서 1:1 창은 "열기 = 대화 객체 확보 + 지난 대화 요청 + 창 만들고 이벤트 연결"의 세 단계다.
//
// ■ 지난 대화 받기 흐름
//   서버가 HistoryBatch를 여러 개로 나눠 보냄(패킷 1MB 제한, 사진 미리보기 때문) → 묶음마다 PendingHistory에 쌓기 → Last가 오면
//   → 한꺼번에 conv.InsertHistory(중복 제거·시간순 병합) → 읽음 위치 갱신(RecomputeUnread) → 보고 있으면 MarkRead
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 묶음 수신 후 한 번에 반영(batching) ........... "buffer chunks then apply once pagination"
//   - 지연 요청(처음 열 때만) / 중복 요청 방지 플래그 .. "request once flag lazy load"
//   - 읽지 않은 수 요약(unread summary) ............. "unread count summary on login"
//   - 패턴 매칭으로 안전한 기본값 만들기 ............. "C# is { Length: > 0 } pattern null-safe default"
//
// ■ 면접 예상 질문과 답
//   Q1. 지난 대화를 한 묶음씩 화면에 넣지 않고 모았다가 넣는 이유는요?
//       A. 사진 미리보기가 많으면 서버가 패킷 하나(최대 1MB)에 다 못 담아 여러 묶음으로 나눠 보냅니다. 묶음마다 앞에 끼워 넣으면 순서가 꼬이고 화면이 여러 번 다시 그려집니다. 마지막 묶음(Last)까지 PendingHistory에 모았다가 InsertHistory로 한 번에 넣어 순서 보장과 한 번의 다시 그리기를 얻습니다.
//   Q2. 연결이 끊겨 묶음을 받다 말면 어떻게 되나요?
//       A. ShowDisconnected가 모든 대화의 PendingHistory를 비웁니다. 다시 로그인하면 서버가 처음부터 다시 보내므로 남겨 두면 같은 말풍선이 두 번 보입니다. (그래도 겹치는 것은 InsertHistory의 DedupKey가 한 번 더 걸러 줍니다.)
//   Q3. 1:1 지난 대화를 창 열 때마다 다시 요청하지 않는 이유는요?
//       A. 창을 닫아도 Conversation은 메인 창에 남아 있어 내용이 그대로입니다. HistoryRequested 플래그로 처음 한 번만 요청합니다. 재접속 때는 끊긴 사이 온 메시지를 채우려고 열어 본 1:1만 다시 요청합니다(HandleRelogin).
//   Q4. 로그인했을 때 "접속하지 않은 동안 온 메시지"는 어떻게 알려 주나요?
//       A. 서버가 로그인 직후 사람별 안 읽은 수와 마지막 메시지 요약(UnreadDirect 목록)을 보냅니다. 동료 목록에 숫자를 표시하고, 합계를 상태 줄에 쓰며, 가장 최근 한 건을 토스트로 보여 줍니다. 대화 전문은 창을 열 때 HistoryReq로 받습니다(요약과 전문을 분리해 로그인을 가볍게 유지).
//   Q5. 재접속 때는 왜 같은 알림을 다시 띄우지 않나요?
//       A. 재접속하면 서버가 이 요약을 또 보내는데, 이미 알려 준 내용이라 소리·토스트는 _suppressUnreadToast로 한 번 건너뜁니다(숫자만 갱신). Math.Max(conv.Unread, u.Count)로 이미 올라간 숫자를 줄이지도 않습니다.
//   Q6. 한계는요? (솔직하게 답하기)
//       A. (1) 1:1 지난 대화 요청과 이전 페이지 요청이 서로 다른 모양이다(첫 요청은 target만, 이전 페이지는 HistoryRequest 객체) — 하나로 맞추면 단순해진다. (2) KnownPeer가 "이름 직급" 문자열을 마지막 공백으로 쪼개 이름·직급을 추측한다(이름에 공백이 있으면 틀릴 수 있음) — 서버가 사원번호와 이름/직급을 따로 주면 안전하다. (3) 파일 말풍선이 있는 기록은 _fileItems에 등록하는데, 대화를 지워도 남을 수 있어 방을 나갈 때 따로 정리한다(HandleRoomLeft).
//
// ■ 문제가 났을 때 어디를 볼까
//   - 1:1 창에 지난 대화가 안 보인다 ........ HistoryReq 전송 여부, 서버 응답 HistoryBatch, HistoryRequested 플래그, 마지막 묶음(Last) 도착
//   - 지난 대화가 두 번 보인다 ............. PendingHistory 비우기, Conversation.DedupKey
//   - 위로 스크롤해도 더 안 불러온다 ........ HistoryHasMore, HistoryLoading 플래그가 풀렸는지
//   - 안 읽은 숫자가 0으로 안 바뀐다 ........ MarkRead(포커스 있는 창), HandleUnreadDirect의 Math.Max
//   - 오프라인 상대 이름이 이상하게 보인다 ... KnownPeer의 이름/직급 분리
// ───────────────────────────────────────────────────────────────────────────

/// <summary>메인 창(partial) - 1:1 대화창 열기, 지난 대화 받아 채우기, 접속 전 온 1:1 요약 처리 부분.</summary>
public sealed partial class MainForm
{
    // ───────────────────────── 1:1 대화 ─────────────────────────

    /// <summary>1:1 창을 열거나 이미 열린 창을 돌려준다.</summary>
    private ChatWindow OpenDirect(UserInfo user, bool activate)
    {
        if (_directWindows.TryGetValue(user.EmployeeNo, out ChatWindow? existing) && !existing.IsDisposed)
        {
            if (activate) BringToFront(existing);
            return existing;
        }

        Conversation conv = GetConversation(Conversation.DirectKey(user.EmployeeNo), user.DisplayName);
        if (!conv.HistoryRequested)
        {
            // 이 사람과의 1:1 대화를 처음 열 때 한 번만 지난 대화를 요청한다 (방은 입장할 때 서버가 알아서 보내 줌)
            // [읽는 법] 요청을 보내기 "전에" 플래그를 켠다. 같은 창을 연달아 열어도 요청이 두 번 나가지 않게 하는 순서다(UI 스레드 하나라 이 순서면 충분).
            conv.HistoryRequested = true;
            _client.Send(Packet.Create(PacketType.HistoryReq, target: user.EmployeeNo));
        }
        var window = new ChatWindow(ChatWindowKind.Direct, conv, user.EmployeeNo);
        bool online = _users.Any(u => u.EmployeeNo == user.EmployeeNo);
        window.SetHeader(user.DisplayName, online ? UserSubtitle(user) : "접속하지 않음 · 보낸 메시지는 다음에 접속하면 전달됩니다");
        window.SendRequested += (_, text, replyId) => SendText(PacketType.Whisper, text, user.EmployeeNo, replyId);
        window.FilesRequested += (_, paths) => SendFiles(conv, paths, null, user.EmployeeNo);
        WireWindow(window);
        window.FormClosed += (_, _) =>
        {
            if (_directWindows.TryGetValue(user.EmployeeNo, out ChatWindow? w) && w == window) _directWindows.Remove(user.EmployeeNo);
        };
        _directWindows[user.EmployeeNo] = window;

        window.SetOnline(_connected && online);
        window.SetInputEnabled(_connected);
        if (activate) window.Show();
        else window.ShowInactive();
        return window;
    }

    // ───────────────────────── 지난 대화 (DB 기록) ─────────────────────────

    /// <summary>
    /// 서버가 보낸 지난 대화 묶음. Last가 올 때까지 대화의 PendingHistory에 모았다가 한꺼번에 맨 앞에 넣는다.
    /// (묶음마다 넣으면 순서가 꼬이고 화면도 여러 번 다시 그려진다)
    /// </summary>
    private void HandleHistory(Packet packet)
    {
        if (packet.GetData<HistoryBatch>() is not { } batch) return;

        Conversation? conv = null;
        if (batch.RoomId is int roomId)
        {
            if (_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) conv = GetRoomConversation(room);
        }
        else if (batch.PeerNo is string peer)
        {
            _conversations.TryGetValue(Conversation.DirectKey(peer), out conv);
        }
        if (conv is null) return; // 그사이 방에서 나갔거나 대화가 사라짐

        conv.HistoryHasMore = batch.HasMore;

        foreach (HistoryItem h in batch.Items)
        {
            bool mine = h.SenderNo == _me.EmployeeNo;
            var item = new ChatItem
            {
                Kind = mine ? ChatItemKind.Mine : ChatItemKind.Other,
                Sender = h.SenderName,
                SenderKey = h.SenderNo,
                Text = h.Body,
                Time = h.Time,
                File = h.File is null ? null : new ChatFile { Info = h.File },
                MessageId = h.MessageId,
                ReplyTo = h.ReplyTo,
                Deleted = h.Deleted,
                Edited = h.Edited,
                Reactions = h.Reactions ?? [],
            };
            // [읽는 법] 지난 대화 속 파일도 말풍선을 눌러 받을 수 있도록 "파일 번호 → (말풍선, 대화)" 사전에 등록한다. TryAdd라서 이미 있으면(실시간으로 먼저 온 경우) 덮어쓰지 않는다.
            if (h.File is not null) _fileItems.TryAdd(h.File.FileId, (item, conv)); // 기록 속 파일도 클릭해서 받을 수 있게
            conv.PendingHistory.Add(item);
        }
        if (!batch.Last) return;

        // 1:1: 상대가 어디까지 읽었는지 (내 메시지 옆 "1" 표시)
        if (batch.PeerLastReadAt is DateTime peerRead && (conv.PeerLastReadAt is null || peerRead > conv.PeerLastReadAt))
            conv.PeerLastReadAt = peerRead;

        var all = conv.PendingHistory.ToList();
        conv.PendingHistory.Clear();
        conv.HistoryLoading = false;
        // [읽는 법] 위 PendingHistory를 모은 목록 all을 한 번에 넣는다. 복사본(ToList)을 만들어 Clear한 뒤 넣는 이유: 넣는 도중 새 묶음이 와서 PendingHistory가 바뀌어도 영향받지 않게.
        conv.InsertHistory(all);
        conv.LoadingOlderHistory = false;
        RecomputeUnread(conv);
        if (OpenWindows().FirstOrDefault(w => w.Conversation == conv) is { ContainsFocus: true }) MarkRead(conv);
    }

    // ───────────────────────── 접속하지 않은 동안 온 1:1 ─────────────────────────

    /// <summary>로그인 직후 서버가 알려 준 "안 읽은 1:1" 요약 → 동료 목록에 숫자로 보이고, 알림 창으로 알린다.</summary>
    private void HandleUnreadDirect(Packet packet)
    {
        List<UnreadDirect> list = packet.GetData<List<UnreadDirect>>() ?? [];
        if (list.Count == 0) return;
        foreach (UnreadDirect u in list)
        {
            UserInfo peer = KnownPeer(u.PeerNo, u.PeerName);
            Conversation conv = GetConversation(Conversation.DirectKey(u.PeerNo), peer.DisplayName);
            // [읽는 법] 서버가 센 값과 이미 화면에서 센 값 중 큰 쪽을 쓴다. 로그인 직후에 실시간 메시지가 먼저 도착해 이미 올라간 숫자가 요약 값(작은 값)으로 줄어드는 것을 막는다.
            conv.Unread = Math.Max(conv.Unread, u.Count);
        }
        RefreshUserList();

        bool quiet = _suppressUnreadToast;
        _suppressUnreadToast = false;
        if (quiet) return; // 자동 재접속: 숫자만 갱신하고 소리·알림 창은 띄우지 않는다

        int total = list.Sum(u => u.Count);
        UnreadDirect first = list.OrderByDescending(u => u.LastTime).First();
        SetStatus($"● 접속하지 않은 동안 1:1 메시지 {total}개가 왔습니다.", Theme.Notice);
        if (_presence is not (Presence.Busy or Presence.DoNotDisturb)) SystemSounds.Asterisk.Play();
        string title = list.Count == 1 ? first.PeerName : $"{first.PeerName} 외 {list.Count - 1}명";
        ShowToast(title, $"[안 읽은 메시지 {total}개] {first.LastText}", first.PeerNo,
            () => OpenDirect(KnownPeer(first.PeerNo, first.PeerName), activate: true));
    }

    /// <summary>사원번호로 사람 정보 찾기. 모르면 표시 이름("홍길동 대리")으로 만든다.</summary>
    private UserInfo KnownPeer(string employeeNo, string? displayName)
    {
        if (_users.FirstOrDefault(u => u.EmployeeNo == employeeNo) is { } online) return online;
        if (_knownPeers.TryGetValue(employeeNo, out UserInfo? known)) return known;
        // [읽는 법] 이름이 null/공백이면 사원번호를 이름 대신 쓴다. is { Length: > 0 } d는 "비어 있지 않으면 d에 담는" 패턴. 아래에서 마지막 공백을 기준으로 앞은 이름, 뒤는 직급으로 나눈다("홍길동 대리").
        string name = displayName?.Trim() is { Length: > 0 } d ? d : employeeNo;
        int space = name.LastIndexOf(' ');
        var peer = new UserInfo
        {
            EmployeeNo = employeeNo,
            Name = space > 0 ? name[..space] : name,
            Position = space > 0 ? name[(space + 1)..] : "",
        };
        _knownPeers[employeeNo] = peer;
        return peer;
    }
}
