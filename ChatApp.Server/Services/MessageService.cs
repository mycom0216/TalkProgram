using System.Data.Common;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Utils;
using ChatApp.Server.Core;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;

namespace ChatApp.Server.Services;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 메시지 기능: 방 채팅·1:1 보내기(답장 포함), 삭제, 공감, 1:1 읽음 표시, 1:1 지난 대화, 검색, 회사 사람 목록.
// [핵심] 메시지 번호(MessageId): 서버가 받는 순간 번호를 붙여 모두에게 보낸다(MessageStore.NextId).
//        답장·삭제·공감은 이 번호로 메시지를 가리키고, 서버는 매번 "그 메시지를 볼 권한이 있는지" 확인한다.
// [스레드] 요청한 사람의 수신 스레드에서 실행된다. DB 쓰기는 MessageStore 큐(DbWriter 스레드)에 맡겨 순서를 지킨다.
// [연결]   PacketHandler가 ChatMsg/Whisper/MessageDelete/Reaction/ReadMark/HistoryReq/SearchReq/DirectoryReq를 이리로 넘긴다.
// ═══════════════════════════════════════════════════════════════════════════
public sealed class MessageService(
    SessionManager sessions,
    RoomManager rooms,
    RoomService roomService,
    MessageStore messages,
    MessageRepository repository,
    UserRepository users,
    AuditLog audit,
    ServerLogger log)
{
    public const int MaxMessageLength = 500;

    /// <summary>보낸 메시지를 지울 수 있는 시간. 관리자는 언제든(감사 로그에 남음).</summary>
    public static readonly TimeSpan DeleteWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan EditWindow = TimeSpan.FromMinutes(5);

    // ───────── 보내기 ─────────

    public void HandleChat(ClientSession session, Packet packet)
    {
        string? body = packet.Body?.Trim();
        if (string.IsNullOrEmpty(body)) return;
        body = TextUtil.Truncate(body, MaxMessageLength); // 이모지가 반쪽으로 잘리지 않게 글자 단위로 자른다

        // 어느 방에 보내는지는 Target(방 번호). 비어 있으면 기본 방.
        int roomId = int.TryParse(packet.Target, out int id) ? id : rooms.Lobby.Id;
        Room? room = rooms.GetJoinedRoom(session, roomId);
        if (room is null)
        {
            SendError(session, "참여하지 않은 방에는 메시지를 보낼 수 없습니다.");
            return;
        }

        // Sender는 클라이언트 값이 아니라 서버가 알고 있는 사용자 정보로 채운다(사칭 방지).
        UserInfo me = session.User!;
        var chat = Packet.Create(PacketType.ChatMsg, body, sender: me.DisplayName, target: room.Id.ToString());
        chat.SenderId = me.EmployeeNo;
        chat.Timestamp = RoomService.NowMs();
        chat.MessageId = messages.NextId();
        // 답장이면 원래 메시지가 "같은 방"의 메시지인지 확인한 뒤 요약을 붙인다 (다른 방 메시지 내용을 엿보는 것 방지)
        chat.ReplyTo = ResolveReply(packet.ReplyTo?.MessageId, m => m.RoomId == room.Id);

        // 저장을 "보내기 전에" 예약한다. 그래야 이 메시지를 실시간으로 못 받은 사람(막 입장하는 사람)도
        // 지난 대화 불러오기로 받을 수 있다 (MessageStore 큐는 순서대로 처리되므로).
        messages.Save(new StoredMessage(chat.MessageId, room.Id, me.EmployeeNo, null, body, "CHAT", chat.Timestamp, chat.ReplyTo?.MessageId));
        messages.Remember(new MessageMeta(chat.MessageId, room.Id, me.EmployeeNo, null, me.DisplayName, body, chat.Timestamp, false));
        rooms.SendToRoom(room, chat); // 같은 방 사람에게만
    }

    public void HandleWhisper(ClientSession session, Packet packet)
    {
        string? body = packet.Body?.Trim();
        string? targetNo = packet.Target?.Trim();
        if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(targetNo)) return;
        // [주의] 방 채팅뿐 아니라 1:1에도 길이 제한이 꼭 있어야 한다. DB 칸은 VARCHAR(500)이라 더 긴 글은 저장이 실패하고,
        //        같은 트랜잭션에 묶인 다른 사람들의 메시지까지 함께 저장되지 않는다(MessageStore 배치 저장).
        body = TextUtil.Truncate(body, MaxMessageLength);

        UserInfo me = session.User!;
        if (targetNo == me.EmployeeNo)
        {
            SendError(session, "자기 자신에게는 메시지를 보낼 수 없습니다.");
            return;
        }

        // 상대가 접속 중이 아니어도 보낼 수 있다 (카카오톡처럼). 저장해 두었다가 다음 로그인 때 "안 읽은 1:1"로 알려 준다.
        ClientSession? target = sessions.FindByEmployeeNo(targetNo);
        UserInfo? peerUser = target?.User ?? users.FindActiveUser(targetNo);
        if (peerUser is null)
        {
            SendError(session, $"사원번호 '{targetNo}'인 사용자를 찾을 수 없습니다. (정지·퇴사한 계정일 수 있습니다)");
            return;
        }

        string peer = peerUser.EmployeeNo;
        // Target = 받는 사람 사원번호, Data = 받는 사람 정보 (보낸 사람 화면에서 어느 1:1 창에 넣을지 알 수 있게)
        var whisper = Packet.Create(PacketType.Whisper, body, sender: me.DisplayName, target: peer).WithData(peerUser);
        whisper.SenderId = me.EmployeeNo;
        whisper.Timestamp = RoomService.NowMs();
        whisper.MessageId = messages.NextId();
        whisper.ReplyTo = ResolveReply(packet.ReplyTo?.MessageId, m => IsBetween(m, me.EmployeeNo, peer));

        messages.Save(new StoredMessage(whisper.MessageId, null, me.EmployeeNo, peer, body, "WHISPER", whisper.Timestamp, whisper.ReplyTo?.MessageId));
        messages.Remember(new MessageMeta(whisper.MessageId, null, me.EmployeeNo, peer, me.DisplayName, body, whisper.Timestamp, false));
        target?.Send(whisper);
        session.Send(whisper); // 보낸 사람 화면에도 표시
    }

    /// <summary>답장 대상 확인 → 요약. 권한이 없거나 없는 메시지면 null(답장 없이 보통 메시지로 보낸다).</summary>
    private ReplyInfo? ResolveReply(long? replyId, Func<MessageMeta, bool> sameConversation)
    {
        if (replyId is not long rid || rid <= 0) return null;
        MessageMeta? original = messages.Find(rid);
        if (original is null || !sameConversation(original)) return null;
        return new ReplyInfo
        {
            MessageId = original.Id,
            SenderName = original.SenderName,
            Text = original.Deleted ? "삭제된 메시지입니다." : MessageRepository.Preview(original.Content),
        };
    }

    private static bool IsBetween(MessageMeta m, string a, string b)
        => m.RoomId is null && ((m.SenderNo == a && m.ReceiverNo == b) || (m.SenderNo == b && m.ReceiverNo == a));

    // ───────── 삭제 ─────────

    public void HandleEdit(ClientSession session, Packet packet)
    {
        EditMessageRequest? request = packet.GetData<EditMessageRequest>();
        UserInfo me = session.User!;
        MessageMeta? meta = request is null ? null : messages.Find(request.MessageId);
        if (meta is null || !CanSee(session, meta) || meta.SenderNo != me.EmployeeNo || meta.Deleted)
        {
            SendError(session, "수정할 수 있는 내 메시지를 찾지 못했습니다.");
            return;
        }
        if (DateTime.UtcNow - meta.SentAt > EditWindow)
        {
            SendError(session, $"메시지는 보낸 뒤 {EditWindow.TotalMinutes:0}분 안에만 수정할 수 있습니다.");
            return;
        }
        string text = TextUtil.Truncate(request!.Text.Trim(), MaxMessageLength);
        if (text.Length == 0)
        {
            SendError(session, "메시지 내용을 입력하세요.");
            return;
        }

        messages.Enqueue("메시지 수정", () =>
        {
            if (!repository.MarkEdited(meta.Id, text)) return;
            messages.UpdateContent(meta.Id, text);
            audit.Record(AuditActions.MessageEdit, me.EmployeeNo, detail: $"#{meta.Id} {MessageRepository.Preview(text)}", ip: session.RemoteEndPoint);
            SendToConversation(meta, peer => Packet.Create(PacketType.MessageEdited, body: text)
                .WithData(new MessageRef { MessageId = meta.Id, RoomId = meta.RoomId, PeerNo = peer }));
        });
    }

    public void HandleTyping(ClientSession session, Packet packet)
    {
        if (packet.GetData<TypingRequest>() is not { } request) return;
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref session.LastTypingTicks);
        if (request.IsTyping && last != 0 && now - last < 1000) return;
        Interlocked.Exchange(ref session.LastTypingTicks, now);

        UserInfo me = session.User!;
        if (request.RoomId is int roomId)
        {
            Room? room = rooms.GetJoinedRoom(session, roomId);
            if (room is null) return;
            rooms.SendToRoom(room, Packet.Create(PacketType.TypingUpdate)
                .WithData(new TypingInfo { RoomId = roomId, EmployeeNo = me.EmployeeNo, DisplayName = me.DisplayName, IsTyping = request.IsTyping }), except: session);
            return;
        }
        string? peerNo = request.PeerNo?.Trim();
        if (string.IsNullOrEmpty(peerNo) || peerNo.Length > 20 || peerNo == me.EmployeeNo) return;
        sessions.FindByEmployeeNo(peerNo)?.Send(Packet.Create(PacketType.TypingUpdate)
            .WithData(new TypingInfo { PeerNo = me.EmployeeNo, EmployeeNo = me.EmployeeNo, DisplayName = me.DisplayName, IsTyping = request.IsTyping }));
    }

    public void HandleDelete(ClientSession session, Packet packet)
    {
        MessageRef? req = packet.GetData<MessageRef>();
        UserInfo me = session.User!;
        MessageMeta? meta = req is null ? null : messages.Find(req.MessageId);
        if (meta is null || !CanSee(session, meta))
        {
            SendError(session, "삭제할 메시지를 찾을 수 없습니다.");
            return;
        }
        if (meta.Deleted) return;

        bool mine = meta.SenderNo == me.EmployeeNo;
        // [왜] 남의 메시지는 관리자만 지울 수 있다(사내 규정 위반 글 정리). 내 메시지도 24시간이 지나면 못 지운다
        //      (오래된 대화가 몰래 바뀌는 것을 막는다). 내용은 DB에 남겨 두어 감사·분쟁에 대비한다.
        if (!mine && !me.IsAdmin)
        {
            SendError(session, "내가 보낸 메시지만 삭제할 수 있습니다.");
            return;
        }
        if (mine && !me.IsAdmin && DateTime.UtcNow - meta.SentAt > DeleteWindow)
        {
            SendError(session, $"보낸 지 {DeleteWindow.TotalHours:0}시간이 지난 메시지는 삭제할 수 없습니다.");
            return;
        }

        messages.Enqueue("메시지 삭제", () => repository.MarkDeleted(meta.Id, me.EmployeeNo));
        messages.ForgetContent(meta.Id);
        if (!mine)
        {
            audit.Record(AuditActions.MessageDelete, me.EmployeeNo, meta.SenderNo, $"#{meta.Id} {MessageRepository.Preview(meta.Content)}", session.RemoteEndPoint);
            log.Info($"관리자 메시지 삭제: #{meta.Id} (보낸 사람 {meta.SenderNo}) by {me}");
        }

        SendToConversation(meta, peer => Packet.Create(PacketType.MessageDeleted)
            .WithData(new MessageRef { MessageId = meta.Id, RoomId = meta.RoomId, PeerNo = peer }));
    }

    // ───────── 공감 ─────────

    public void HandleReaction(ClientSession session, Packet packet)
    {
        ReactionRequest? req = packet.GetData<ReactionRequest>();
        if (req is null || !ReactionRequest.Allowed.Contains(req.Emoji)) return;
        MessageMeta? meta = messages.Find(req.MessageId);
        if (meta is null || meta.Deleted || !CanSee(session, meta)) return;

        string me = session.User!.EmployeeNo;
        // DB 작업 스레드에서: 누르기/취소 → 바뀐 현황 읽기 → 대화의 사람들에게 보내기 (순서대로 처리되어 꼬이지 않는다)
        messages.Enqueue("공감", () =>
        {
            List<ReactionInfo> reactions = repository.ToggleReaction(meta.Id, me, req.Emoji);
            SendToConversation(meta, peer => Packet.Create(PacketType.ReactionUpdate)
                .WithData(new ReactionUpdate { MessageId = meta.Id, RoomId = meta.RoomId, PeerNo = peer, Reactions = reactions }));
        });
    }

    // ───────── 읽음 표시 ─────────

    public void HandleReadMark(ClientSession session, Packet packet)
    {
        ReadMark? mark = packet.GetData<ReadMark>();
        if (mark is null) return;
        // [주의] 미래 시각을 보내 "모든 메시지를 읽음"으로 만드는 것을 막는다(서버 시계 기준으로 자른다).
        DateTime upTo = mark.UpTo > DateTime.UtcNow ? RoomService.NowMs() : mark.UpTo;

        if (mark.RoomId is int roomId)
        {
            roomService.MarkRead(session, roomId, upTo);
            return;
        }

        string? peer = mark.PeerNo?.Trim();
        UserInfo me = session.User!;
        if (string.IsNullOrEmpty(peer) || peer.Length > 20 || peer == me.EmployeeNo) return;
        messages.Enqueue("1:1 읽음 저장", () => repository.UpsertDirectRead(me.EmployeeNo, peer, upTo));
        // 상대가 접속 중이면 "내가 읽었음"을 알려 준다 → 상대 화면에서 자기 메시지 옆 "1"이 사라진다
        sessions.FindByEmployeeNo(peer)?.Send(Packet.Create(PacketType.ReadUpdate)
            .WithData(new ReadUpdate { PeerNo = me.EmployeeNo, EmployeeNo = me.EmployeeNo, LastReadAt = upTo }));
    }

    /// <summary>로그인 직후: 접속하지 않은 동안 받은 1:1 메시지 요약을 보낸다. DB 작업 스레드에서 읽는다.</summary>
    public void OnLogin(ClientSession session)
    {
        string me = session.User!.EmployeeNo;
        messages.Enqueue("안 읽은 1:1", () =>
        {
            List<UnreadDirect> unread = repository.LoadUnreadDirect(me);
            if (unread.Count > 0) session.Send(Packet.Create(PacketType.UnreadDirect).WithData(unread));
        });
    }

    // ───────── 지난 대화 (1:1) ─────────

    /// <summary>1:1 대화창을 열 때 클라이언트가 요청한다 (Target = 상대 사원번호, 상대가 오프라인이어도 된다).</summary>
    public void HandleHistoryRequest(ClientSession session, Packet packet)
    {
        if (packet.GetData<HistoryRequest>() is { } request)
        {
            if (request.RoomId is int roomId)
            {
                Room? room = rooms.GetJoinedRoom(session, roomId);
                if (room is null) return;
                DateTime? joinedAt = room.IsLobby ? null : rooms.GetMember(room, session.User!.EmployeeNo)?.JoinedAt;
                if (request.BeforeKey is { Length: > 40 }) return;
                messages.RequestRoomHistory(session, roomId, joinedAt, request.Before, request.BeforeKey);
                return;
            }
            if (!string.IsNullOrWhiteSpace(request.PeerNo) && request.PeerNo.Length <= 20 && request.PeerNo != session.User!.EmployeeNo)
            {
                if (request.BeforeKey is { Length: > 40 }) return;
                messages.RequestDirectHistory(session, session.User.EmployeeNo, request.PeerNo, request.Before, request.BeforeKey);
            }
            return;
        }
        string peer = packet.Target?.Trim() ?? "";
        if (peer.Length is < 1 or > 20 || peer == session.User!.EmployeeNo) return;
        messages.RequestDirectHistory(session, session.User.EmployeeNo, peer);
    }

    // ───────── 검색 ─────────

    public void HandleSearch(ClientSession session, Packet packet)
    {
        string keyword = packet.GetData<SearchRequest>()?.Keyword.Trim() ?? "";
        if (keyword.Length < SearchRequest.MinKeywordLength || keyword.Length > 50)
        {
            SendError(session, $"검색어는 {SearchRequest.MinKeywordLength}~50자로 입력하세요.");
            return;
        }
        // [왜] 검색은 DB 전체를 훑는 무거운 작업이라, 한 사람이 연달아 누르지 못하게 2초 간격을 둔다.
        if (Environment.TickCount64 - Interlocked.Read(ref session.LastSearchTicks) < 2000)
        {
            SendError(session, "검색은 잠시 후 다시 해 주세요.");
            return;
        }
        Interlocked.Exchange(ref session.LastSearchTicks, Environment.TickCount64);

        UserInfo me = session.User!;
        List<int> roomIds = rooms.GetMemberRoomIds(me.EmployeeNo);
        // [스레드] 검색은 요청한 사람의 수신 스레드에서 바로 한다. DbWriter 큐에 넣으면 느린 검색 동안 모든 사람의 대화 저장이 밀린다.
        List<SearchHit> hits = repository.Search(me.EmployeeNo, roomIds, keyword, SearchRequest.MaxResults);
        foreach (SearchHit hit in hits.Where(h => h.RoomId is not null)) hit.RoomName = rooms.GetName(hit.RoomId!.Value);
        session.Send(Packet.Create(PacketType.SearchRes).WithData(new SearchResult { Keyword = keyword, Hits = hits }));
    }

    // ───────── 회사 사람 목록 ─────────

    private List<UserInfo>? _directory;
    private long _directoryTicks;

    /// <summary>초대할 사람을 고를 때 쓰는 "사용 중인 모든 사람" 목록 (30초 캐시).</summary>
    // [왜] 초대 창을 열 때마다 DB에서 수천 명을 읽지 않도록 잠깐 기억해 둔다. 새 직원이 30초 늦게 보이는 정도는 괜찮다.
    public void HandleDirectory(ClientSession session)
    {
        List<UserInfo>? list = Volatile.Read(ref _directory);
        if (list is null || Environment.TickCount64 - Interlocked.Read(ref _directoryTicks) > 30_000)
        {
            list = users.ListActiveUsers();
            Volatile.Write(ref _directory, list);
            Interlocked.Exchange(ref _directoryTicks, Environment.TickCount64);
        }
        session.Send(Packet.Create(PacketType.Directory).WithData(list));
    }

    // ───────── 공통 ─────────

    /// <summary>이 메시지를 볼 수 있는 사람인지: 방 메시지는 그 방 멤버, 1:1은 두 당사자.</summary>
    private bool CanSee(ClientSession session, MessageMeta meta)
    {
        string me = session.User!.EmployeeNo;
        return meta.RoomId is int roomId ? rooms.GetJoinedRoom(session, roomId) is not null
                                         : meta.SenderNo == me || meta.ReceiverNo == me;
    }

    /// <summary>
    /// 그 메시지가 있는 대화의 사람들에게 보낸다. 방이면 방 전체, 1:1이면 두 사람.
    /// makePacket(peer): 1:1이면 "받는 사람 입장에서의 상대" 사원번호를 넣어 패킷을 만든다(받는 쪽이 어느 창인지 알 수 있게).
    /// </summary>
    private void SendToConversation(MessageMeta meta, Func<string?, Packet> makePacket)
    {
        if (meta.RoomId is int roomId)
        {
            if (rooms.Find(roomId) is Room room) rooms.SendToRoom(room, makePacket(null));
            return;
        }
        string a = meta.SenderNo, b = meta.ReceiverNo ?? "";
        sessions.FindByEmployeeNo(a)?.Send(makePacket(b)); // 보낸 사람에게 상대는 받은 사람
        sessions.FindByEmployeeNo(b)?.Send(makePacket(a)); // 받은 사람에게 상대는 보낸 사람
    }

    private static void SendError(ClientSession session, string message)
        => session.Send(Packet.Create(PacketType.Error, message, success: false));
}
