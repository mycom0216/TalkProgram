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
// [파일 요약] 메시지에 붙는 기능: 읽음 표시(안 읽은 사람 수 계산), 공감(이모지), 삭제, 수정, 입력 중 표시.
// MainForm은 C#의 partial class(클래스를 여러 파일로 나누는 기능)로 쪼개 두었다.
// 이 파일도 MainForm.cs와 "같은 클래스"다. 필드(_client, _me 등)와 메서드를 파일 구분 없이 그대로 쓴다.
// 나눈 기준은 "기능별"이고, 동작은 나누기 전과 완전히 같다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   메시지에 붙는 부가 기능의 "서버 방송 → 화면 반영" 처리: 읽음 숫자(안 읽은 사람 수), 공감, 삭제, 수정, 입력 중 표시. 모두 같은 패턴이다: 방송 받기 → 대화 찾기 → 말풍선 찾기 → 값 바꾸기 → Notify로 창에 알림.
//
// ■ 읽음 표시 원리 (이 파일의 핵심)
//   ① 내가 대화를 보면 MarkRead가 "여기까지 읽음"(UpTo)을 서버에 보낸다 → 서버가 저장하고 모두에게 ReadUpdate 방송
//   ② 방송을 받으면 멤버별 "읽은 시각"(RoomMemberInfo.LastReadAt)을 갱신하고 RecomputeUnread가 모든 말풍선의 숫자를 다시 계산
//   ③ 방 말풍선의 숫자 = "그 메시지 시각보다 읽은 시각이 앞선 멤버 수" − (보낸 사람이 거기 포함되면 1)
//      · 각 멤버의 읽은 시각 = max(마지막 읽음, 방에 들어온 시각) → 들어오기 전 메시지는 그 사람을 "안 읽음"으로 세지 않는다
//      · 멤버 시각을 정렬해 두고 CountBefore(이진 탐색)로 센다
//   ④ 1:1은 단순: 내 메시지이고 상대가 읽은 시각보다 뒤이면 1, 아니면 0
//   ⑤ 기본 방(LobbyId)은 인원이 많아 읽음 표시를 하지 않는다
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 이진 탐색(하한, lower bound) ................... "binary search lower bound count less than"
//   - 상태 요약을 서버 방송으로 동기화 ................ "server broadcast state sync chat read receipt"
//   - 낙관적 갱신(내 화면은 바로, 서버 응답 기다리지 않음) .. "optimistic UI update"
//   - 지역 함수(local function) ..................... "C# local functions closure"
//   - Notify 이벤트로 "바뀐 부분만" 다시 그리기 ......... "invalidate only changed item UI"
//
// ■ 면접 예상 질문과 답
//   Q1. 카카오톡처럼 "안 읽은 사람 수"를 어떻게 계산하나요?
//       A. 서버는 사람별 "여기까지 읽음" 시각만 저장·방송하고, 숫자는 클라이언트가 계산합니다. 메시지마다 읽은 사람을 서버가 세어 보내면 메시지 수 × 인원만큼 데이터가 늘기 때문입니다. 각 멤버의 읽은 시각을 정렬해 두고, 메시지 시각보다 앞선 값의 개수를 이진 탐색으로 세면 메시지당 O(log 인원)입니다.
//   Q2. 들어오기 전에 보낸 메시지의 숫자는 어떻게 되나요?
//       A. 멤버의 읽은 시각을 max(마지막 읽음, 입장 시각)으로 잡아, 입장 전 메시지는 그 멤버가 이미 지나간 것으로 칩니다(숫자에 안 세짐). 보낸 사람도 읽은 것으로 보아 1을 뺍니다.
//   Q3. MarkRead가 같은 값을 계속 보내지 않게 한 방법은요?
//       A. Conversation.LastMarkedRead에 마지막으로 보낸 시각을 저장해 두고, 새 UpTo가 그보다 크지 않으면 보내지 않습니다. 재접속하면 이 값을 초기화해 끊기기 직전 읽음이 유실됐을 가능성에 대비합니다.
//   Q4. 내 화면의 숫자는 서버 방송을 기다리나요?
//       A. 내 읽음은 기다리지 않고 바로 반영합니다(SetMemberRead 후 계산). 방송을 기다리면 보는 순간 숫자가 늦게 줄어 어색합니다. 다른 사람이 읽은 것은 서버 방송을 받아야 반영됩니다(낙관적 갱신은 내 것만).
//   Q5. 삭제된 메시지에 달린 답장은 어떻게 처리하나요?
//       A. 지워진 내용이 답장의 인용 영역에 남아 보이면 안 되므로, 그 메시지를 인용한 답장들의 인용 텍스트를 "삭제된 메시지입니다."로 바꾸고, 지워진 것이 댓글이었다면 원래 메시지의 "↳ 댓글 N"이 줄도록 RecountReplies를 부릅니다. 서버도 소프트 삭제(삭제 표시만 해 두고, 지난 대화를 읽을 때 원문을 가려서 보냄)라서 재접속 후 불러와도 같은 모습입니다.
//   Q6. 수정된 메시지가 이미 삭제된 상태라면요?
//       A. HandleMessageEdited가 Deleted를 확인하고 무시합니다(삭제가 수정보다 우선). 수신 순서가 뒤바뀌는 상황(수정 방송이 삭제 방송 뒤에 처리)에서도 삭제된 내용이 되살아나지 않게 합니다.
//   Q7. 한계는요? (솔직하게 답하기)
//       A. (1) RecomputeUnread는 메시지가 올 때마다 대화 전체를 훑는다(O(메시지 × log 인원)) — 수백 개까지는 문제없지만 아주 긴 대화에서는 "새 메시지 하나만" 계산하는 증분 방식이 낫다. (2) 시각을 서버 기준(UTC)으로 비교하므로 시각이 같은 메시지끼리는 구분하지 못한다(밀리초 단위). (3) 아직 불러오지 않은 옛 메시지에 대한 공감/삭제 방송은 무시되고, 나중에 지난 대화를 불러올 때 최신 상태로 맞춘다(Conversation.InsertHistory).
//
// ■ 문제가 났을 때 어디를 볼까
//   - 읽었는데 숫자가 안 줄어든다 ............ MarkRead의 조건(연결, LatestMessageTime, LastMarkedRead), 서버 ReadUpdate 방송, room.Members가 비었는지
//   - 숫자가 한 명 많게/적게 보인다 .......... RecomputeUnread의 보낸 사람 보정(senderRead < item.Time), 멤버 JoinedAt
//   - 기본 방에 숫자가 없다 ................. 의도됨(LobbyId는 읽음 표시 안 함)
//   - 공감/삭제가 안 보인다 ................. FindConversation이 null인지(방 참여 여부), 해당 메시지가 불러온 범위에 있는지
//   - "입력 중"이 안 사라진다 ............... 서버가 보낸 IsTyping=false 수신, ChatWindow.SetTypingIndicator의 시간 만료
// ───────────────────────────────────────────────────────────────────────────

/// <summary>메인 창(partial) - 읽음 표시, 공감, 삭제, 수정, 입력 중 표시 같은 메시지 부가 기능 부분.</summary>
public sealed partial class MainForm
{
    // ───────────────────────── 읽음 표시 (카카오톡의 노란 숫자) ─────────────────────────

    /// <summary>
    /// 대화창을 보고 있음 → 서버에 "여기까지 읽음"을 알린다. 서버는 저장하고 방 사람들(1:1이면 상대)에게 ReadUpdate로 알린다.
    /// 기본 방은 인원이 많아 읽음 표시를 하지 않는다.
    /// </summary>
    private void MarkRead(Conversation conv)
    {
        // [읽는 법] 보내지 않는 경우를 한 줄에 모아 둔 것: ① 연결이 끊김 ② 읽음 기준이 될 메시지가 없음(LatestMessageTime이 null) ③ 이미 그 시각 이후를 보냈음. is not DateTime upTo는 "null이 아니면 값을 upTo에 꺼내 담는" 패턴.
        if (!_connected || conv.LatestMessageTime is not DateTime upTo || upTo <= conv.LastMarkedRead) return;
        ReadMark mark;
        if (TryGetRoomId(conv, out int roomId))
        {
            if (roomId == RoomInfo.LobbyId || !_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
            mark = new ReadMark { RoomId = roomId, UpTo = upTo };
            SetMemberRead(room, _me.EmployeeNo, upTo); // 내 화면의 숫자는 서버 응답을 기다리지 않고 바로 줄인다
        }
        else if (conv.Key.StartsWith("dm:"))
        {
            mark = new ReadMark { PeerNo = conv.Key["dm:".Length..], UpTo = upTo };
        }
        else return;

        conv.LastMarkedRead = upTo;
        _client.Send(Packet.Create(PacketType.ReadMark).WithData(mark));
        RecomputeUnread(conv);
    }

    /// <summary>누가 어디까지 읽었다 (방: 멤버 한 명, 1:1: 상대).</summary>
    private void HandleReadUpdate(Packet packet)
    {
        if (packet.GetData<ReadUpdate>() is not { } update) return;
        if (update.RoomId is int roomId)
        {
            if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
            SetMemberRead(room, update.EmployeeNo, update.LastReadAt);
            RecomputeUnread(GetRoomConversation(room));
        }
        else if (update.PeerNo is string peer && _conversations.TryGetValue(Conversation.DirectKey(peer), out Conversation? conv))
        {
            if (conv.PeerLastReadAt is null || update.LastReadAt > conv.PeerLastReadAt) conv.PeerLastReadAt = update.LastReadAt;
            RecomputeUnread(conv);
        }
    }

    private static void SetMemberRead(RoomInfo room, string employeeNo, DateTime upTo)
    {
        RoomMemberInfo? member = room.Members?.FirstOrDefault(m => m.EmployeeNo == employeeNo);
        if (member is not null && (member.LastReadAt is null || upTo > member.LastReadAt)) member.LastReadAt = upTo;
    }

    /// <summary>
    /// 말풍선마다 "안 읽은 사람 수"를 다시 계산한다.
    /// 방: 멤버마다 "여기까지 읽음" 시각 = max(마지막 읽음, 들어온 시각). 그 시각이 메시지보다 앞인 멤버 수 − (보낸 사람이 거기 들어 있으면 1).
    /// 1:1: 내 메시지인데 상대가 읽은 시각보다 뒤면 1.
    /// </summary>
    // [왜] 멤버 시각을 정렬해 두고 이진 탐색으로 센다. 메시지 수 × 멤버 수만큼 비교하면 큰 방에서 느려진다.
    private void RecomputeUnread(Conversation conv)
    {
        bool changed = false;
        void Set(ChatItem item, int count)
        {
            if (item.UnreadCount == count) return;
            item.UnreadCount = count;
            changed = true;
        }

        if (TryGetRoomId(conv, out int roomId))
        {
            if (roomId == RoomInfo.LobbyId || !_joinedRooms.TryGetValue(roomId, out RoomInfo? room) || room.Members is not { Count: > 0 } members)
                return;
            var readUpTo = new Dictionary<string, DateTime>();
            foreach (RoomMemberInfo m in members)
                // [읽는 법] 멤버별 "여기까지는 지나갔다" 시각. 읽은 기록이 있고 입장 시각보다 크면 그 값, 아니면 입장 시각. 입장 전에 올라온 메시지는 이 사람에게 해당하지 않으므로 숫자에 세지 않는다.
                readUpTo[m.EmployeeNo] = m.LastReadAt is DateTime r && r > m.JoinedAt ? r : m.JoinedAt;
            // [읽는 법] 멤버들의 시각을 오름차순으로 정렬한 배열. 이진 탐색(CountBefore)은 정렬되어 있어야 동작한다. 정렬은 한 번만 하고, 말풍선마다 재사용한다.
            DateTime[] sorted = readUpTo.Values.Order().ToArray();
            foreach (ChatItem item in conv.Items)
            {
                if (item.IsSystemLike) continue;
                int count = CountBefore(sorted, item.Time);
                // [읽는 법] 보낸 사람 본인은 자기 메시지를 읽은 것으로 쳐야 하는데, 위 계산은 "읽은 시각 < 메시지 시각"인 사람을 모두 센다. 보낸 사람의 읽은 시각이 자기 메시지보다 앞서 있다면(방금 보냈으니 보통 그렇다) 그 한 명이 섞여 있으므로 1을 뺀다.
                if (readUpTo.TryGetValue(item.SenderKey, out DateTime senderRead) && senderRead < item.Time) count--; // 보낸 사람은 읽은 것으로
                Set(item, Math.Max(0, count));
            }
        }
        else if (conv.Key.StartsWith("dm:"))
        {
            DateTime peerRead = conv.PeerLastReadAt ?? DateTime.MinValue;
            foreach (ChatItem item in conv.Items)
                if (!item.IsSystemLike) Set(item, item.Kind == ChatItemKind.Mine && peerRead < item.Time ? 1 : 0);
        }
        if (changed) conv.NotifyReadStateChanged();
    }

    /// <summary>정렬된 배열에서 time보다 앞(작은)인 값의 개수.</summary>
    private static int CountBefore(DateTime[] sorted, DateTime time)
    {
        int lo = 0, hi = sorted.Length;
        // [읽는 법] 하한(lower bound) 이진 탐색: [lo, hi) 구간의 가운데 값이 time보다 작으면 왼쪽을 버리고(lo = mid+1), 아니면 오른쪽을 버린다(hi = mid). 끝나면 lo는 "time보다 작은 값의 개수"와 같다. 예: [1,3,5], time=4 → 2.
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (sorted[mid] < time) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static bool TryGetRoomId(Conversation conv, out int roomId)
    {
        roomId = 0;
        return conv.Key.StartsWith("room:") && int.TryParse(conv.Key["room:".Length..], out roomId);
    }

    // ───────────────────────── 공감·삭제 ─────────────────────────

    /// <summary>방이면 방 대화, 1:1이면 그 상대와의 대화 (없으면 null).</summary>
    private Conversation? FindConversation(int? roomId, string? peerNo)
    {
        if (roomId is int id) return _joinedRooms.TryGetValue(id, out RoomInfo? room) ? GetRoomConversation(room) : null;
        return peerNo is not null && _conversations.TryGetValue(Conversation.DirectKey(peerNo), out Conversation? conv) ? conv : null;
    }

    private void HandleReactionUpdate(Packet packet)
    {
        if (packet.GetData<ReactionUpdate>() is not { } update) return;
        Conversation? conv = FindConversation(update.RoomId, update.PeerNo);
        if (conv?.FindMessage(update.MessageId) is not { } item) return; // 아직 불러오지 않은 옛 메시지
        item.Reactions = update.Reactions;
        conv.NotifyChanged(item);
    }

    private void HandleMessageDeleted(Packet packet)
    {
        if (packet.GetData<MessageRef>() is not { } deleted) return;
        Conversation? conv = FindConversation(deleted.RoomId, deleted.PeerNo);
        if (conv is null) return;
        if (conv.FindMessage(deleted.MessageId) is { } item)
        {
            item.Deleted = true;
            item.Reactions = [];
            conv.NotifyChanged(item);
        }
        // 이 메시지에 단 답장들의 인용도 바꾼다 (지워진 내용이 인용으로 남아 보이지 않게)
        foreach (ChatItem reply in conv.Items.Where(i => i.ReplyTo?.MessageId == deleted.MessageId))
        {
            // [읽는 법] ReplyTo!의 !는 "null이 아니라고 내가 보증한다"는 표시. 바로 위 Where에서 ReplyTo가 null이 아닌 것만 골랐으므로 안전하다.
            reply.ReplyTo!.Text = "삭제된 메시지입니다.";
            conv.NotifyChanged(reply);
        }
        conv.RecountReplies(); // 지워진 것이 댓글이었다면 원래 메시지의 "↳ 댓글 N"이 줄어든다
    }

    private void HandleMessageEdited(Packet packet)
    {
        if (packet.GetData<MessageRef>() is not { } edited) return;
        Conversation? conv = FindConversation(edited.RoomId, edited.PeerNo);
        if (conv?.FindMessage(edited.MessageId) is not { } item || item.Deleted) return;
        item.Text = packet.Body ?? "";
        item.Edited = true;
        conv.NotifyChanged(item);
    }

    private void HandleTypingUpdate(Packet packet)
    {
        if (packet.GetData<TypingInfo>() is not { } typing) return;
        Conversation? conv = FindConversation(typing.RoomId, typing.PeerNo);
        if (conv is null) return;
        ChatWindow? window = OpenWindows().FirstOrDefault(w => w.Conversation == conv);
        window?.SetTypingIndicator(typing.EmployeeNo, typing.DisplayName, typing.IsTyping);
    }
}
