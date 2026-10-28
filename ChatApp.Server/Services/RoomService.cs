using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Server.Core;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;

namespace ChatApp.Server.Services;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 채팅방 기능: 만들기(공개/비공개), 입장, 초대, 나가기, 로그인 시 내 방 복원, 방 읽음 위치.
// [핵심] 카카오톡 단체방처럼 "멤버"는 접속을 끊어도 남는다(DB room_members). 서버를 다시 켜도 방이 그대로다.
//        - 공개 방: 목록에 보이고 누구나 들어올 수 있다.
//        - 비공개 방: 멤버에게만 목록에 보이고, 멤버가 초대해야 들어올 수 있다(다른 부서 대화·파일 보호).
//        - 나중에 들어온 멤버는 들어오기 전 대화를 보지 못한다(joined_at 이후만).
// [순서] 멤버 추가: ① RoomManager에 자리 잡기(인원 확인) → ② DB 저장 → ③ 실패하면 ①을 되돌린다.
// [스레드] 요청한 사람의 수신 스레드에서 실행된다. 공유 상태는 RoomManager(lock)로만 바꾼다.
// [연결]   PacketHandler가 RoomCreate/RoomJoin/RoomInvite/RoomLeave/ReadMark(방)를 이리로 넘긴다.
// ═══════════════════════════════════════════════════════════════════════════
public sealed class RoomService(
    SessionManager sessions,
    RoomManager rooms,
    RoomRepository repository,
    UserRepository users,
    MessageStore messages,
    AuditLog audit,
    ServerLogger log,
    Action roomListChanged)
{
    /// <summary>로그인 마무리: 기본 방 + 내가 멤버인 방들에 연결하고, 각 방의 정보·지난 대화를 보낸다.</summary>
    public void OnLogin(ClientSession session)
    {
        UserInfo me = session.User!;
        // 자동 재접속처럼 이전 연결의 방 연결을 이미 넘겨받았다면(TransferMembership) 입장 알림을 다시 보내지 않는다
        bool alreadyInLobby = session.JoinedRooms.Contains(rooms.Lobby);
        List<Room> joined = rooms.AttachOnLogin(session, me.EmployeeNo);

        // 기본 방: 예전처럼 입장 알림을 보낸다 (기본 방은 "지금 접속한 사람"이 멤버)
        session.Send(Packet.Create(PacketType.RoomRes, $"'{rooms.Lobby.Name}' 방에 들어왔습니다.", success: true)
                           .WithData(rooms.GetInfo(rooms.Lobby)));
        if (!alreadyInLobby)
            rooms.SendToRoom(rooms.Lobby, RoomSystem(rooms.Lobby, $"{me.DisplayName}님이 들어왔습니다."), except: session);
        messages.RequestRoomHistory(session, rooms.Lobby.Id);

        // 내 단체방: 조용히 다시 연결 (다른 멤버에게 알림 없음). 지난 대화는 내가 들어온 시각 이후만.
        foreach (Room room in joined)
        {
            session.Send(Packet.Create(PacketType.RoomRes, success: true).WithData(rooms.GetInfo(room, withMembers: true)));
            messages.RequestRoomHistory(session, room.Id, rooms.GetMember(room, me.EmployeeNo)?.JoinedAt);
        }
    }

    /// <summary>접속 종료: 방 연결만 뗀다(멤버로는 남음). 기본 방에는 퇴장 알림.</summary>
    public void OnDisconnected(ClientSession session)
    {
        bool wasInLobby = session.JoinedRooms.Contains(rooms.Lobby);
        rooms.Detach(session);
        if (wasInLobby && session.User is UserInfo user)
            rooms.SendToRoom(rooms.Lobby, RoomSystem(rooms.Lobby, $"{user.DisplayName}님이 나갔습니다. (접속 종료)"));
    }

    // ───────── 만들기 ─────────

    public void HandleCreate(ClientSession session, Packet packet)
    {
        CreateRoomRequest? req = packet.GetData<CreateRoomRequest>();
        string name = req?.Name.Trim() ?? "";
        int max = req?.MaxUsers ?? 0;

        if (name.Length is < 1 or > CreateRoomRequest.MaxNameLength)
        {
            SendFail(session, $"방 이름은 1~{CreateRoomRequest.MaxNameLength}자로 입력하세요.");
            return;
        }
        if (max is < CreateRoomRequest.MinUsers or > CreateRoomRequest.MaxUsersLimit)
        {
            SendFail(session, $"최대 인원은 {CreateRoomRequest.MinUsers}~{CreateRoomRequest.MaxUsersLimit}명으로 정하세요.");
            return;
        }

        UserInfo me = session.User!;
        bool isPrivate = req!.IsPrivate;
        int id = repository.Create(name, me.EmployeeNo, max, isPrivate); // DB에서 방 번호 발급
        var owner = new RoomMember(me.EmployeeNo, me.DisplayName, NowMs(), null);
        repository.AddMembers(id, [owner]);

        var room = new Room(id, name, me.EmployeeNo, me.DisplayName, max, isPrivate);
        rooms.AddRoom(room, session, owner);
        log.Info($"방 생성: #{id} '{name}' ({(isPrivate ? "비공개" : "공개")}, 최대 {max}명) by {me}");
        audit.Record(AuditActions.RoomCreate, me.EmployeeNo, detail: $"#{id} {name}{(isPrivate ? " (비공개)" : "")}", ip: session.RemoteEndPoint);

        session.Send(Packet.Create(PacketType.RoomRes, $"'{name}' 방을 만들었습니다.", success: true).WithData(rooms.GetInfo(room, withMembers: true)));
        messages.RequestRoomHistory(session, room.Id, owner.JoinedAt); // 빈 기록이지만 "불러오기 끝"을 알려 준다

        // 만들면서 바로 초대
        if (req.InviteNos.Count > 0) Invite(session, room, req.InviteNos);
        roomListChanged();
    }

    // ───────── 입장 (공개 방) ─────────

    public void HandleJoin(ClientSession session, Packet packet)
    {
        if (!int.TryParse(packet.Target, out int roomId))
        {
            SendFail(session, "방 번호가 올바르지 않습니다.");
            return;
        }

        UserInfo me = session.User!;
        var member = new RoomMember(me.EmployeeNo, me.DisplayName, NowMs(), null);
        switch (rooms.TryJoin(session, roomId, member, out Room? room))
        {
            // Target = 방 번호: 재접속한 클라이언트가 "어느 방에 다시 못 들어갔는지" 알 수 있게
            case JoinResult.NotFound: SendFail(session, "존재하지 않거나 이미 사라진 방입니다.", roomId); return;
            case JoinResult.Full: SendFail(session, "방이 가득 찼습니다.", roomId); return;
            case JoinResult.Private: SendFail(session, "비공개 방입니다. 방 멤버에게 초대를 받아야 들어갈 수 있습니다.", roomId); return;
            case JoinResult.AlreadyIn:
                // 이미 멤버 → 창을 되살리고 놓친 대화를 채울 수 있게 정보와 지난 대화만 다시 보낸다
                session.Send(Packet.Create(PacketType.RoomRes, $"'{room!.Name}' 방에 다시 연결되었습니다.", success: true)
                                   .WithData(rooms.GetInfo(room, withMembers: true)));
                messages.RequestRoomHistory(session, room.Id, room.IsLobby ? null : rooms.GetMember(room, me.EmployeeNo)?.JoinedAt);
                return;
        }

        try
        {
            repository.AddMembers(room!.Id, [member]);
        }
        catch (Exception)
        {
            rooms.RemoveMember(room!, me.EmployeeNo, session); // ③ DB 실패 → 메모리도 되돌린다
            throw; // PacketHandler가 "DB 오류"로 답한다
        }

        session.Send(Packet.Create(PacketType.RoomRes, $"'{room.Name}' 방에 들어왔습니다.", success: true)
                           .WithData(rooms.GetInfo(room, withMembers: true)));
        rooms.SendToRoom(room, RoomSystem(room, $"{me.DisplayName}님이 들어왔습니다."), except: session);
        messages.RequestRoomHistory(session, room.Id, member.JoinedAt);
        BroadcastRoomUpdate(room, except: session);
        roomListChanged();
    }

    // ───────── 초대 ─────────

    public void HandleInvite(ClientSession session, Packet packet)
    {
        RoomInviteRequest? req = packet.GetData<RoomInviteRequest>();
        Room? room = req is null ? null : rooms.GetJoinedRoom(session, req.RoomId);
        if (req is null || room is null || room.IsLobby)
        {
            SendError(session, "초대할 수 없는 방입니다. (방 멤버만 초대할 수 있습니다)");
            return;
        }
        Invite(session, room, req.EmployeeNos);
    }

    private void Invite(ClientSession inviter, Room room, IEnumerable<string> employeeNos)
    {
        UserInfo me = inviter.User!;
        // 같은 사람 여러 번, 나 자신, 너무 많은 요청 걸러내기
        List<string> wanted = employeeNos.Select(n => n.Trim()).Where(n => n.Length is > 0 and <= 20 && n != me.EmployeeNo)
                                         .Distinct().Take(RoomInviteRequest.MaxPerRequest).ToList();
        if (wanted.Count == 0) return;

        // 실제로 있는, 사용 중인 사람만 (정지·퇴사한 사람은 초대할 수 없다)
        Dictionary<string, string> names = users.GetActiveDisplayNames(wanted);
        DateTime now = NowMs();
        List<RoomMember> added = rooms.TryInvite(room, names.Select(kv => new RoomMember(kv.Key, kv.Value, now, null)));
        if (added.Count == 0)
        {
            SendError(inviter, room.IsFull ? "방이 가득 차서 초대할 수 없습니다." : "초대할 사람이 없습니다. (이미 멤버이거나 사용할 수 없는 계정)");
            return;
        }

        try
        {
            repository.AddMembers(room.Id, added);
        }
        catch (Exception)
        {
            foreach (RoomMember m in added) rooms.RemoveMember(room, m.EmployeeNo, null);
            throw;
        }

        string who = string.Join(", ", added.Select(m => m.DisplayName));
        log.Info($"초대: #{room.Id} '{room.Name}' ← {who} by {me}");
        rooms.SendToRoom(room, RoomSystem(room, $"{me.DisplayName}님이 {who}님을 초대했습니다."));

        // 초대받은 사람이 지금 접속 중이면 바로 방에 연결하고 알려 준다
        foreach (RoomMember m in added)
        {
            if (sessions.FindByEmployeeNo(m.EmployeeNo) is not ClientSession target || target.IsClosed) continue;
            rooms.AttachIfMember(room, target);
            target.Send(Packet.Create(PacketType.RoomRes, $"{me.DisplayName}님이 '{room.Name}' 방에 초대했습니다.", success: true)
                              .WithData(rooms.GetInfo(room, withMembers: true)));
            messages.RequestRoomHistory(target, room.Id, m.JoinedAt);
        }
        BroadcastRoomUpdate(room);
        roomListChanged();
    }

    // ───────── 나가기 ─────────

    public void HandleLeave(ClientSession session, Packet packet)
    {
        if (!int.TryParse(packet.Target, out int roomId))
        {
            SendFail(session, "나갈 방 번호가 없습니다.");
            return;
        }

        UserInfo me = session.User!;
        switch (rooms.Leave(session, roomId, out Room? room, out bool closed))
        {
            case LeaveResult.Lobby: SendFail(session, "기본 방(전체 채팅)은 나갈 수 없습니다."); return;
            case LeaveResult.NotFound: SendFail(session, "존재하지 않거나 이미 사라진 방입니다."); return;
            case LeaveResult.NotMember: SendFail(session, "참여하지 않은 방입니다."); return;
        }

        repository.RemoveMember(room!.Id, me.EmployeeNo);
        if (closed) repository.Close(room.Id);

        session.Send(Packet.Create(PacketType.RoomLeft, $"'{room.Name}' 방에서 나왔습니다.", target: room.Id.ToString()));
        if (!closed)
        {
            rooms.SendToRoom(room, RoomSystem(room, $"{me.DisplayName}님이 나갔습니다."));
            // 방장이 위임하지 않고 나감 → 가장 오래된 멤버가 자동으로 방장이 된다 (방장 없는 방이 남지 않게)
            if (room.OwnerNo == me.EmployeeNo && rooms.PickNextOwner(room) is RoomMember next)
                ChangeOwner(room, next.EmployeeNo, actor: me, reason: "방장이 나가서");
            BroadcastRoomUpdate(room);
        }
        else
        {
            log.Info($"방 닫힘: #{room.Id} '{room.Name}' (마지막 멤버가 나감)");
        }
        roomListChanged();
    }

    // ───────── 방장 넘기기 ─────────

    /// <summary>방장 위임: 방장 또는 관리자가 다른 멤버에게 방장을 넘긴다 (Target = 방 번호, Body = 새 방장).</summary>
    public void HandleTransferOwner(ClientSession session, Packet packet)
    {
        UserInfo me = session.User!;
        Room? room = int.TryParse(packet.Target, out int roomId) ? rooms.Find(roomId) : null;
        string target = packet.Body?.Trim() ?? "";
        if (room is null || room.IsLobby)
        {
            SendError(session, "방장을 넘길 수 없는 방입니다.");
            return;
        }
        if (room.OwnerNo != me.EmployeeNo && !me.IsAdmin)
        {
            SendError(session, "방장 또는 관리자만 방장을 넘길 수 있습니다.");
            return;
        }
        if (target == room.OwnerNo) return; // 이미 방장
        if (!ChangeOwner(room, target, actor: me, reason: null))
            SendError(session, "그 사람은 이 방의 멤버가 아닙니다.");
        else
            BroadcastRoomUpdate(room);
    }

    /// <summary>방장 바꾸기 (메모리 → DB → 알림·감사). 멤버가 아니면 false.</summary>
    private bool ChangeOwner(Room room, string employeeNo, UserInfo actor, string? reason)
    {
        string? before = room.OwnerNo;
        RoomMember? owner = rooms.SetOwner(room, employeeNo);
        if (owner is null) return false;
        repository.SetOwner(room.Id, owner.EmployeeNo);
        log.Info($"방장 변경: #{room.Id} '{room.Name}' {before} → {owner.EmployeeNo}{(reason is null ? "" : $" ({reason})")} by {actor}");
        audit.Record(AuditActions.RoomOwner, actor.EmployeeNo, owner.EmployeeNo, $"#{room.Id} {room.Name}{(reason is null ? "" : $" ({reason})")}");
        rooms.SendToRoom(room, RoomSystem(room, reason is null
            ? $"{actor.DisplayName}님이 {owner.DisplayName}님에게 방장을 넘겼습니다."
            : $"{reason} {owner.DisplayName}님이 새 방장이 되었습니다."));
        roomListChanged(); // 방 목록의 "방장 ○○"도 바뀐다
        return true;
    }

    /// <summary>방장 또는 관리자가 방 상단 공지를 고정·수정·삭제한다.</summary>
    public void HandleNotice(ClientSession session, Packet packet)
    {
        if (packet.GetData<RoomNoticeRequest>() is not { } request || request.RoomId <= RoomInfo.LobbyId)
        {
            SendError(session, "공지할 방을 찾을 수 없습니다.");
            return;
        }
        UserInfo me = session.User!;
        Room? room = rooms.Find(request.RoomId);
        if (room is null || (room.OwnerNo != me.EmployeeNo && !me.IsAdmin) || (!me.IsAdmin && !rooms.IsMember(room.Id, me.EmployeeNo)))
        {
            SendError(session, "방장 또는 관리자만 공지를 고정할 수 있습니다.");
            audit.Record(AuditActions.Denied, me.EmployeeNo, detail: $"방 공지 권한 없음 #{request.RoomId}", ip: session.RemoteEndPoint);
            return;
        }
        string text = request.Text.Trim();
        if (text.Length > RoomNoticeRequest.MaxLength)
        {
            SendError(session, $"공지 내용은 {RoomNoticeRequest.MaxLength}자 이내로 입력하세요.");
            return;
        }

        repository.SetPinnedNotice(room.Id, text, me.EmployeeNo);
        if (!rooms.SetPinnedNotice(room, text))
        {
            SendError(session, "방 정보가 바뀌었습니다. 다시 시도해 주세요.");
            return;
        }
        audit.Record(AuditActions.Notice, me.EmployeeNo, detail: $"#{room.Id} {(text.Length == 0 ? "공지 삭제" : "공지 설정: " + text)}", ip: session.RemoteEndPoint);
        log.Info($"방 공지 {(text.Length == 0 ? "삭제" : "설정")}: #{room.Id} by {me}");
        BroadcastRoomUpdate(room);
    }

    // ───────── 삭제 (방장·관리자) ─────────

    /// <summary>
    /// 방 삭제: 방장이나 관리자만. 모든 멤버가 빠지고 방이 목록에서 사라진다. 대화 기록은 DB에 남는다.
    /// [왜] [나가기]는 "나만" 빠진다. 다른 멤버가 남아 있으면 방이 계속 남으므로, 방을 만든 사람이 정리할 방법이 필요하다.
    /// </summary>
    public void HandleDelete(ClientSession session, Packet packet)
    {
        UserInfo me = session.User!;
        Room? room = int.TryParse(packet.Target, out int roomId) ? rooms.Find(roomId) : null;
        if (room is null || room.IsLobby)
        {
            SendError(session, room is null ? "존재하지 않거나 이미 사라진 방입니다." : "기본 방(전체 채팅)은 삭제할 수 없습니다.");
            return;
        }
        if (room.OwnerNo != me.EmployeeNo && !me.IsAdmin)
        {
            SendError(session, "방장 또는 관리자만 방을 삭제할 수 있습니다.");
            return;
        }

        repository.Delete(room.Id); // DB 먼저 (실패하면 PacketHandler가 "DB 오류"로 답하고 메모리는 그대로)
        List<ClientSession> online = rooms.DeleteRoom(room.Id, out _) ?? [];
        log.Info($"방 삭제: #{room.Id} '{room.Name}' by {me}");
        audit.Record(AuditActions.RoomDelete, me.EmployeeNo, room.OwnerNo, $"#{room.Id} {room.Name}", session.RemoteEndPoint);

        // 접속 중인 멤버들의 창을 닫는다 (RoomLeft를 받으면 클라이언트가 창을 닫고 목록에서 뺀다)
        foreach (ClientSession s in online)
            s.Send(Packet.Create(PacketType.RoomLeft, $"'{room.Name}' 방이 삭제되었습니다. ({me.DisplayName})", target: room.Id.ToString()));
        if (!online.Contains(session)) // 멤버가 아닌 관리자가 지운 경우에도 결과를 알려 준다
            session.Send(Packet.Create(PacketType.SystemMsg, $"'{room.Name}' 방을 삭제했습니다."));
        roomListChanged();
    }

    // ───────── 읽음 위치 (방) ─────────

    /// <summary>"이 방을 여기까지 읽음" → 메모리·DB 저장 후 방 사람들에게 알린다 (메시지 옆 숫자가 줄어든다).</summary>
    public void MarkRead(ClientSession session, int roomId, DateTime upTo)
    {
        Room? room = rooms.GetJoinedRoom(session, roomId);
        if (room is null || room.IsLobby) return; // 기본 방은 인원이 많아 읽음 표시를 하지 않는다
        UserInfo me = session.User!;
        if (!rooms.MarkRead(room, me.EmployeeNo, upTo)) return; // 이미 더 뒤까지 읽음 → 할 일 없음

        messages.Enqueue("방 읽음 저장", () => repository.UpdateRead(room.Id, me.EmployeeNo, upTo));
        rooms.SendToRoom(room, Packet.Create(PacketType.ReadUpdate)
            .WithData(new ReadUpdate { RoomId = room.Id, EmployeeNo = me.EmployeeNo, LastReadAt = upTo }));
    }

    // ───────── 공통 ─────────

    /// <summary>멤버가 바뀌었음을 방 사람들에게 알린다 (멤버 목록·읽음 위치 포함).</summary>
    private void BroadcastRoomUpdate(Room room, ClientSession? except = null)
        => rooms.SendToRoom(room, Packet.Create(PacketType.RoomUpdate).WithData(rooms.GetInfo(room, withMembers: true)), except);

    /// <summary>방 안의 알림(입장·퇴장·초대). Target = 방 번호라 클라이언트가 해당 대화창에 넣는다.</summary>
    public static Packet RoomSystem(Room room, string message)
        => Packet.Create(PacketType.SystemMsg, message, target: room.Id.ToString());

    private static void SendFail(ClientSession session, string message, int? roomId = null)
        => session.Send(Packet.Create(PacketType.RoomRes, message, target: roomId?.ToString(), success: false));

    private static void SendError(ClientSession session, string message)
        => session.Send(Packet.Create(PacketType.Error, message, success: false));

    /// <summary>밀리초까지만 남긴 지금 시각 (DB DATETIME(3)과 같은 정밀도).</summary>
    public static DateTime NowMs()
    {
        DateTime now = DateTime.UtcNow;
        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, now.Kind);
    }
}
