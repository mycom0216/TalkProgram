using System.Data.Common;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Server.Core;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;

namespace ChatApp.Server.Services;

/// <summary>
/// PacketType별 처리. 각 세션의 수신 스레드에서 동시에 호출된다(병렬 처리).
/// 공유 상태는 SessionManager·RoomManager(스레드 안전)와 DB를 통해서만 건드린다.
/// </summary>
public sealed class PacketHandler(
    SessionManager sessions,
    RoomManager rooms,
    AuthService auth,
    RoomRepository roomRepository,
    FileService files,
    MessageStore messages,
    AdminService admin,
    AuditLog audit,
    ServerLogger log)
{
    private const int MaxMessageLength = 500;

    public ServerLogger Log { get; } = log;

    public void Handle(ClientSession session, Packet packet)
    {
        // 로그인 전에는 서버 안내/회원가입/로그인/핑만 허용
        if (!session.IsLoggedIn && packet.Type is not (PacketType.ServerInfoReq or PacketType.RegisterReq or PacketType.LoginReq or PacketType.Ping))
        {
            SendError(session, "먼저 로그인하세요.");
            return;
        }

        // 임시 비밀번호로 로그인한 사람은 비밀번호를 바꾸기 전까지 다른 기능을 쓸 수 없다
        // [왜] 화면(클라이언트)에서만 막으면, 수정한 클라이언트로 임시 비밀번호인 채 계속 쓸 수 있다. 서버가 막아야 진짜로 막힌다.
        if (session.MustChangePassword && packet.Type is not (PacketType.ChangePasswordReq or PacketType.Ping or PacketType.Logout))
        {
            SendError(session, "임시 비밀번호입니다. 먼저 새 비밀번호로 바꾸세요.");
            return;
        }

        try
        {
            switch (packet.Type)
            {
                case PacketType.ServerInfoReq: session.Send(Packet.Create(PacketType.ServerInfo).WithData(auth.GetServerInfo())); break;
                case PacketType.RegisterReq: HandleRegister(session, packet); break;
                case PacketType.LoginReq: HandleLogin(session, packet); break;
                case PacketType.ChatMsg: HandleChat(session, packet); break;
                case PacketType.Whisper: HandleWhisper(session, packet); break;
                case PacketType.RoomListReq: session.Send(CreateRoomListPacket()); break;
                case PacketType.RoomCreate: HandleRoomCreate(session, packet); break;
                case PacketType.RoomJoin: HandleRoomJoin(session, packet); break;
                case PacketType.RoomLeave: HandleRoomLeave(session, packet); break;
                case PacketType.FileUploadReq: files.HandleUploadRequest(session, packet); break;
                case PacketType.FileChunk: files.HandleChunk(session, packet); break;
                case PacketType.FileUploadEnd: files.HandleUploadEnd(session, packet); break;
                case PacketType.FileDownloadReq: files.HandleDownloadRequest(session, packet); break;
                case PacketType.StatusUpdate: HandleStatusUpdate(session, packet); break;
                case PacketType.FileUploadCancel: files.HandleUploadCancel(session, packet); break;
                case PacketType.FileDownloadCancel: files.HandleDownloadCancel(session, packet); break;
                case PacketType.HistoryReq: HandleHistoryRequest(session, packet); break;
                case PacketType.ChangePasswordReq: HandleChangePassword(session, packet); break;
                case PacketType.AdminUserListReq:
                case PacketType.AdminActionReq:
                case PacketType.AdminAuditReq: HandleAdmin(session, packet); break;
                case PacketType.Logout:
                    audit.Record(AuditActions.Logout, session.User?.EmployeeNo, ip: session.RemoteEndPoint);
                    session.Close();
                    break;
                case PacketType.Ping: session.Send(Packet.Create(PacketType.Pong)); break;
                default: SendError(session, $"지원하지 않는 요청: {packet.Type}"); break;
            }
        }
        catch (DbException ex)
        {
            // DB가 꺼져 있거나 오류가 나도 서버와 다른 사용자는 계속 동작한다
            Log.Error($"[{session.Id}] DB 오류 ({packet.Type}): {ex.Message}");
            Packet reply = packet.Type switch
            {
                PacketType.RegisterReq => Packet.Create(PacketType.RegisterRes, "서버 DB 오류로 처리하지 못했습니다.", success: false),
                PacketType.LoginReq => Packet.Create(PacketType.LoginRes, "서버 DB 오류로 처리하지 못했습니다.", success: false),
                PacketType.RoomCreate => Packet.Create(PacketType.RoomRes, "서버 DB 오류로 방을 만들지 못했습니다.", success: false),
                PacketType.ChangePasswordReq => Packet.Create(PacketType.ChangePasswordRes, "서버 DB 오류로 처리하지 못했습니다.", success: false),
                PacketType.AdminActionReq => Packet.Create(PacketType.AdminActionRes, "서버 DB 오류로 처리하지 못했습니다.", success: false),
                _ => Packet.Create(PacketType.Error, "서버 DB 오류가 발생했습니다.", success: false),
            };
            session.Send(reply);
        }
    }

    // ───────────────────────── 회원 ─────────────────────────

    private void HandleRegister(ClientSession session, Packet packet)
    {
        if (session.IsLoggedIn)
        {
            SendError(session, "로그인 상태에서는 회원가입할 수 없습니다.");
            return;
        }

        RegisterRequest? req = packet.GetData<RegisterRequest>();
        if (req is null)
        {
            session.Send(Packet.Create(PacketType.RegisterRes, "잘못된 요청입니다.", success: false));
            return;
        }

        var (success, message) = auth.Register(req);
        session.Send(Packet.Create(PacketType.RegisterRes, message, success: success));
        if (!success) return;

        string no = req.EmployeeNo.Trim();
        Log.Info($"회원가입: {no} ({session.RemoteEndPoint})");
        bool pending = !auth.GetServerInfo().AutoApprove;
        audit.Record(AuditActions.Register, no, no, $"{req.Name.Trim()} / {req.Department.Trim()}{(pending ? " (승인 대기)" : "")}", session.RemoteEndPoint);
        if (pending) admin.NotifyAdmins($"새 가입 신청: {req.Name.Trim()} {req.Position.Trim()} ({req.Department.Trim()}, {no})");
    }

    private void HandleLogin(ClientSession session, Packet packet)
    {
        if (session.IsLoggedIn)
        {
            SendError(session, "이미 로그인했습니다.");
            return;
        }

        LoginRequest? req = packet.GetData<LoginRequest>();
        if (req is null)
        {
            session.Send(Packet.Create(PacketType.LoginRes, "잘못된 요청입니다.", success: false));
            return;
        }

        var (user, message) = auth.Login(req);
        if (user is null)
        {
            session.Send(Packet.Create(PacketType.LoginRes, message, success: false));
            Log.Info($"로그인 실패: {req.EmployeeNo.Trim()} ({session.RemoteEndPoint}) - {message}");
            // [왜] 실패도 기록한다: 같은 사원번호로 짧은 시간에 실패가 몰리면 비밀번호 대입 공격의 흔적이다.
            //      없는 사원번호도 그대로 남겨야 "어떤 아이디들을 찔러 봤는지" 알 수 있다(최대 20자로 잘라 저장).
            audit.Record(AuditActions.LoginFail, null, req.EmployeeNo.Trim(), message, session.RemoteEndPoint);
            return;
        }

        // 같은 사원번호의 이전 연결이 있으면 새 로그인이 이긴다 (자동 재접속, 다른 PC에서 로그인)
        if (sessions.Login(session, user) is ClientSession old)
        {
            Log.Info($"중복 로그인: 이전 연결 {old} 을(를) 끊고 새 연결 {session} 로 바꿉니다.");
            // 이전 연결이 들어가 있던 방들을 새 연결로 옮긴다. 이렇게 하면 이전 연결이 끊길 때
            // "○○님이 나갔습니다" 알림이 뜨지 않고, 혼자 있던 방이 "빈 방"으로 삭제되지도 않는다.
            rooms.TransferMembership(old, session);
            old.SendAndClose(Packet.Create(PacketType.ForceLogout, "다른 곳에서 같은 사원번호로 로그인하여 연결을 끊었습니다."));
        }

        auth.RecordLoginSuccess(user.EmployeeNo);
        audit.Record(AuditActions.LoginOk, user.EmployeeNo, ip: session.RemoteEndPoint);
        if (user.MustChangePassword)
        {
            // 임시 비밀번호: 로그인은 됐지만 비밀번호를 바꿀 때까지 방 입장·목록 방송을 미룬다 (CompleteLogin은 변경 후에)
            session.MustChangePassword = true;
            session.Send(Packet.Create(PacketType.LoginRes, "임시 비밀번호로 로그인했습니다. 새 비밀번호로 바꿔 주세요.", success: true).WithData(user));
            Log.Info($"로그인(임시 비밀번호, 변경 대기): {session}");
            return;
        }

        session.Send(Packet.Create(PacketType.LoginRes, message, success: true).WithData(user));
        Log.Info($"로그인: {session}");
        CompleteLogin(session);
    }

    /// <summary>로그인 마무리: 기본 방 입장, 접속자·방 목록 방송, 관리자면 승인 대기 알림.</summary>
    private void CompleteLogin(ClientSession session)
    {
        JoinRoom(session, rooms.Lobby.Id); // 로그인하면 기본 방에 자동으로 들어간다
        BroadcastUserList();
        BroadcastRoomList();

        if (session.User is { IsAdmin: true } && admin.ListUsers("PENDING").Count is > 0 and int pending)
            session.Send(Packet.Create(PacketType.AdminNotify, $"승인을 기다리는 가입 신청이 {pending}건 있습니다."));
    }

    /// <summary>본인 비밀번호 변경. 임시 비밀번호 상태였다면 변경 후 로그인을 마무리한다.</summary>
    private void HandleChangePassword(ClientSession session, Packet packet)
    {
        ChangePasswordRequest? req = packet.GetData<ChangePasswordRequest>();
        if (req is null)
        {
            session.Send(Packet.Create(PacketType.ChangePasswordRes, "잘못된 요청입니다.", success: false));
            return;
        }

        UserInfo me = session.User!;
        var (success, message) = auth.ChangePassword(me.EmployeeNo, req);
        session.Send(Packet.Create(PacketType.ChangePasswordRes, message, success: success));
        if (!success) return;

        audit.Record(AuditActions.PasswordChange, me.EmployeeNo, me.EmployeeNo, session.MustChangePassword ? "임시 비밀번호 변경" : null, session.RemoteEndPoint);
        Log.Info($"비밀번호 변경: {session}");
        if (session.MustChangePassword)
        {
            session.MustChangePassword = false;
            CompleteLogin(session);
        }
    }

    // ───────────────────────── 관리자 ─────────────────────────

    /// <summary>관리자 요청. 매번 서버가 권한을 확인한다 (클라이언트가 메뉴를 숨기는 것은 편의일 뿐 보안이 아니다).</summary>
    private void HandleAdmin(ClientSession session, Packet packet)
    {
        UserInfo me = session.User!;
        if (!me.IsAdmin)
        {
            // 관리자가 아닌데 관리 패킷을 보냈다 = 클라이언트를 고쳐서 시도했을 가능성 → 기록해 둔다
            audit.Record(AuditActions.Denied, me.EmployeeNo, detail: packet.Type.ToString(), ip: session.RemoteEndPoint);
            SendError(session, "관리자 권한이 없습니다.");
            return;
        }

        switch (packet.Type)
        {
            case PacketType.AdminUserListReq:
                session.Send(Packet.Create(PacketType.AdminUserList, target: packet.Body).WithData(admin.ListUsers(packet.Body)));
                break;

            case PacketType.AdminActionReq:
                AdminAction? action = packet.GetData<AdminAction>();
                if (action is null) { session.Send(Packet.Create(PacketType.AdminActionRes, "잘못된 요청입니다.", success: false)); return; }
                AdminOutcome result = admin.Execute(action, me.EmployeeNo, session.RemoteEndPoint);
                session.Send(Packet.Create(PacketType.AdminActionRes, result.Message, success: result.Success)
                    .WithData(new AdminActionResult { Type = action.Type, EmployeeNo = action.EmployeeNo.Trim(), TempPassword = result.TempPassword }));
                break;

            case PacketType.AdminAuditReq:
                AuditQuery query = packet.GetData<AuditQuery>() ?? new AuditQuery();
                session.Send(Packet.Create(PacketType.AdminAudit).WithData(admin.QueryAudit(query)));
                break;
        }
    }

    // ───────────────────────── 채팅 ─────────────────────────

    private void HandleChat(ClientSession session, Packet packet)
    {
        string? body = packet.Body?.Trim();
        if (string.IsNullOrEmpty(body)) return;
        if (body.Length > MaxMessageLength) body = body[..MaxMessageLength];

        // 어느 방에 보내는지는 Target(방 번호). 예전 클라이언트처럼 비어 있으면 기본 방.
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
        chat.Timestamp = NowMs();

        // 저장을 "보내기 전에" 예약한다. 그래야 이 메시지를 실시간으로 못 받은 사람(막 입장하는 사람)도
        // 지난 대화 불러오기로 받을 수 있다 (MessageStore 큐는 순서대로 처리되므로).
        messages.Save(new StoredMessage(room.Id, me.EmployeeNo, null, body, "CHAT", chat.Timestamp));
        rooms.SendToRoom(room, chat); // 같은 방 사람에게만
    }

    private void HandleWhisper(ClientSession session, Packet packet)
    {
        string? body = packet.Body?.Trim();
        string? targetNo = packet.Target?.Trim();
        if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(targetNo)) return;

        ClientSession? target = sessions.FindByEmployeeNo(targetNo);
        if (target?.User is null)
        {
            SendError(session, $"사원번호 '{targetNo}'인 사용자는 접속 중이 아닙니다.");
            return;
        }
        if (target == session)
        {
            SendError(session, "자기 자신에게는 귓속말을 보낼 수 없습니다.");
            return;
        }

        UserInfo me = session.User!;
        // Target = 받는 사람 사원번호, Data = 받는 사람 정보 (보낸 사람 화면에서 어느 1:1 창에 넣을지 알 수 있게)
        var whisper = Packet.Create(PacketType.Whisper, body, sender: me.DisplayName, target: target.User.EmployeeNo)
                            .WithData(target.User);
        whisper.SenderId = me.EmployeeNo;
        whisper.Timestamp = NowMs();
        messages.Save(new StoredMessage(null, me.EmployeeNo, target.User.EmployeeNo, body, "WHISPER", whisper.Timestamp));
        target.Send(whisper);
        session.Send(whisper); // 보낸 사람 화면에도 표시
    }

    // ───────────────────────── 채팅방 ─────────────────────────

    private void HandleRoomCreate(ClientSession session, Packet packet)
    {
        CreateRoomRequest? req = packet.GetData<CreateRoomRequest>();
        string name = req?.Name.Trim() ?? "";
        int max = req?.MaxUsers ?? 0;

        if (name.Length is < 1 or > CreateRoomRequest.MaxNameLength)
        {
            SendRoomFail(session, $"방 이름은 1~{CreateRoomRequest.MaxNameLength}자로 입력하세요.");
            return;
        }
        if (max is < CreateRoomRequest.MinUsers or > CreateRoomRequest.MaxUsersLimit)
        {
            SendRoomFail(session, $"최대 인원은 {CreateRoomRequest.MinUsers}~{CreateRoomRequest.MaxUsersLimit}명으로 정하세요.");
            return;
        }

        UserInfo me = session.User!;
        int id = roomRepository.Create(name, me.EmployeeNo, max); // DB에서 방 번호 발급
        Log.Info($"방 생성: #{id} '{name}' (최대 {max}명) by {me}");
        audit.Record(AuditActions.RoomCreate, me.EmployeeNo, detail: $"#{id} {name}", ip: session.RemoteEndPoint);

        var room = new Room(id, name, me, max);
        if (rooms.AddAndJoin(room, session) == JoinResult.Ok)
        {
            AnnounceJoin(session, room);
            BroadcastRoomList();
        }
    }

    private void HandleRoomJoin(ClientSession session, Packet packet)
    {
        if (!int.TryParse(packet.Target, out int roomId))
        {
            SendRoomFail(session, "방 번호가 올바르지 않습니다.");
            return;
        }
        if (JoinRoom(session, roomId)) BroadcastRoomList();
    }

    private void HandleRoomLeave(ClientSession session, Packet packet)
    {
        if (!int.TryParse(packet.Target, out int roomId))
        {
            SendRoomFail(session, "나갈 방 번호가 없습니다.");
            return;
        }

        switch (rooms.Leave(session, roomId, out Room? room, out bool removed))
        {
            case LeaveResult.Lobby: SendRoomFail(session, "기본 방(전체 채팅)은 나갈 수 없습니다."); return;
            case LeaveResult.NotFound: SendRoomFail(session, "존재하지 않거나 이미 사라진 방입니다."); return;
            case LeaveResult.NotMember: SendRoomFail(session, "참여하지 않은 방입니다."); return;
        }

        session.Send(Packet.Create(PacketType.RoomLeft, $"'{room!.Name}' 방에서 나왔습니다.", target: room.Id.ToString()));
        if (!removed)
            rooms.SendToRoom(room, RoomSystem(room, $"{session.User!.DisplayName}님이 나갔습니다."));
        else
            Log.Info($"방 삭제: #{room.Id} '{room.Name}' (마지막 멤버가 나감)");
        BroadcastRoomList();
    }

    /// <summary>방 입장 처리 + 알림. 다른 방에서는 나가지 않는다.</summary>
    private bool JoinRoom(ClientSession session, int roomId)
    {
        switch (rooms.Join(session, roomId, out Room? room))
        {
            // Target = 방 번호: 재접속한 클라이언트가 "어느 방에 다시 못 들어갔는지" 알 수 있게
            case JoinResult.NotFound: SendRoomFail(session, "존재하지 않거나 이미 사라진 방입니다.", roomId); return false;
            case JoinResult.Full: SendRoomFail(session, "방이 가득 찼습니다.", roomId); return false;
            case JoinResult.AlreadyIn:
                // 이미 참여 중이면 실패 대신 "입장 결과 + 지난 대화"만 다시 보낸다 (입장 알림은 없음).
                // [왜] 자동 재접속한 클라이언트는 원래 있던 방에 다시 입장을 요청한다. 서버가 이전 연결의 참여 정보를
                //      새 연결로 옮겨 두었다면(TransferMembership) 이미 참여 중이므로, 이때도 창을 되살리고 놓친 대화를 채울 수 있게 한다.
                session.Send(Packet.Create(PacketType.RoomRes, $"'{room!.Name}' 방에 다시 연결되었습니다.", success: true)
                                   .WithData(rooms.GetInfo(room)));
                messages.RequestRoomHistory(session, room.Id);
                return false;
        }
        AnnounceJoin(session, room!);
        return true;
    }

    /// <summary>
    /// 순서: ① 나에게 입장 결과(RoomRes) ② 방 사람들에게 입장 알림.
    /// RoomRes를 먼저 보내야 클라이언트가 방 대화창을 만든 뒤 알림을 그 창에 넣을 수 있다.
    /// </summary>
    private void AnnounceJoin(ClientSession session, Room room)
    {
        session.Send(Packet.Create(PacketType.RoomRes, $"'{room.Name}' 방에 들어왔습니다.", success: true)
                           .WithData(rooms.GetInfo(room)));
        rooms.SendToRoom(room, RoomSystem(room, $"{session.User!.DisplayName}님이 들어왔습니다."));
        messages.RequestRoomHistory(session, room.Id); // 지난 대화는 DB 작업 스레드가 읽어서 보내 준다
    }

    // ───────────────────────── 상태 표시 ─────────────────────────

    /// <summary>
    /// 내 상태(온라인·자리 비움·회의 중·방해 금지)와 상태 메시지 바꾸기 → 모두에게 바뀐 접속자 목록을 보낸다.
    /// [스레드] session.User를 고치지 않고 새 UserInfo로 통째로 바꾼다(WithStatus). 다른 스레드가 접속자 목록을
    ///          JSON으로 만드는 중이어도 옛 객체 아니면 새 객체 중 하나를 온전히 보게 된다.
    /// </summary>
    private void HandleStatusUpdate(ClientSession session, Packet packet)
    {
        StatusUpdate? req = packet.GetData<StatusUpdate>();
        if (req is null || !Enum.IsDefined(req.Presence)) return;

        UserInfo me = session.User!;
        string message = me.StatusMessage;
        if (req.StatusMessage is not null)
        {
            message = req.StatusMessage.Trim();
            if (message.Length > StatusUpdate.MaxMessageLength) message = message[..StatusUpdate.MaxMessageLength];
            if (message != me.StatusMessage && !auth.SaveStatusMessage(me.EmployeeNo, message))
                Log.Info("users.status_message 칸이 없어 상태 메시지를 저장하지 못했습니다 (migrate_003 필요). 이번 접속 동안만 유지됩니다.");
        }
        if (req.Presence == me.Presence && message == me.StatusMessage) return; // 바뀐 게 없음

        session.User = me.WithStatus(req.Presence, message);
        BroadcastUserList();
    }

    // ───────────────────────── 대화 기록 ─────────────────────────

    /// <summary>1:1 대화창을 열 때 클라이언트가 요청한다 (Target = 상대 사원번호, 상대가 오프라인이어도 된다).</summary>
    private void HandleHistoryRequest(ClientSession session, Packet packet)
    {
        string peer = packet.Target?.Trim() ?? "";
        if (peer.Length is < 1 or > 20 || peer == session.User!.EmployeeNo) return;
        messages.RequestDirectHistory(session, session.User.EmployeeNo, peer);
    }

    /// <summary>
    /// 지금 시각을 밀리초까지만 남긴다. DB(DATETIME(3))도 밀리초까지만 저장하므로,
    /// 실시간으로 받은 메시지와 기록에서 불러온 같은 메시지의 시각이 똑같아져 클라이언트가 중복을 걸러낼 수 있다.
    /// </summary>
    private static DateTime NowMs()
    {
        DateTime now = DateTime.Now;
        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, now.Kind);
    }

    /// <summary>방 안의 알림(입장·퇴장). Target = 방 번호라 클라이언트가 해당 대화창에 넣는다.</summary>
    private static Packet RoomSystem(Room room, string message)
        => Packet.Create(PacketType.SystemMsg, message, target: room.Id.ToString());

    // ───────────────────────── 접속 종료 ─────────────────────────

    /// <summary>ClientSession.Close()에서 호출된다(어느 스레드에서든 한 번만).</summary>
    public void OnDisconnected(ClientSession session)
    {
        sessions.Remove(session);
        files.OnDisconnected(session); // 올리던 파일 정리
        var left = rooms.LeaveAll(session);
        Log.Info($"접속 해제: {session} (현재 {sessions.ConnectedCount}명)");

        if (session.User is not null)
        {
            foreach (var (room, removed) in left)
            {
                if (!removed) rooms.SendToRoom(room, RoomSystem(room, $"{session.User.DisplayName}님이 나갔습니다. (접속 종료)"));
                else if (!room.IsLobby) Log.Info($"방 삭제: #{room.Id} '{room.Name}' (마지막 멤버가 접속 종료)");
            }
            BroadcastUserList();
            BroadcastRoomList();
        }
    }

    // ───────────────────────── 공통 전송 ─────────────────────────

    /// <summary>서버 공지 등 모든 로그인 사용자에게 (방과 무관).</summary>
    public void BroadcastSystem(string message)
        => sessions.Broadcast(Packet.Create(PacketType.SystemMsg, message));

    // ── 접속자·방 목록 방송 묶기 ──
    // [왜] 예전에는 누가 로그인할 때마다 "전체 접속자 목록"을 모두에게 바로 보냈다. N명이 한꺼번에 로그인하면
    //      목록 크기(N) × 받는 사람(N) × 로그인 수(N) = N³에 비례해 데이터가 커진다(부하 테스트 300명에서 로그인 27초).
    //      그래서 "목록이 바뀌었다"는 표시만 해 두고, 타이머가 200ms마다 한 번만 보낸다. 200ms 안에 100명이 들어와도 방송은 1번.
    // [스레드] 표시(dirty)는 여러 수신 스레드가 동시에 세우므로 Interlocked로, 타이머 콜백은 스레드 풀에서 실행된다.
    private const int ListBroadcastMs = 200;
    private int _userListDirty, _roomListDirty, _flushing;
    private Timer? _listTimer;

    /// <summary>목록 방송 타이머 시작 (ChatServer.Start에서 1번).</summary>
    public void StartListBroadcaster() => _listTimer = new Timer(_ => FlushLists(), null, ListBroadcastMs, ListBroadcastMs);

    public void StopListBroadcaster() => _listTimer?.Dispose();

    private void FlushLists()
    {
        // 앞 콜백이 아직 보내는 중이면 건너뛴다 (타이머는 이전 콜백이 안 끝나도 다음 콜백을 다른 스레드에서 부를 수 있음)
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            if (Interlocked.Exchange(ref _userListDirty, 0) == 1)
                sessions.Broadcast(Packet.Create(PacketType.UserList).WithData(sessions.GetUsers()));
            if (Interlocked.Exchange(ref _roomListDirty, 0) == 1)
                sessions.Broadcast(CreateRoomListPacket());
        }
        finally
        {
            Volatile.Write(ref _flushing, 0);
        }
    }

    private void BroadcastUserList() => Interlocked.Exchange(ref _userListDirty, 1);

    private void BroadcastRoomList() => Interlocked.Exchange(ref _roomListDirty, 1);

    private Packet CreateRoomListPacket() => Packet.Create(PacketType.RoomList).WithData(rooms.GetRoomInfos());

    private static void SendRoomFail(ClientSession session, string message, int? roomId = null)
        => session.Send(Packet.Create(PacketType.RoomRes, message, target: roomId?.ToString(), success: false));

    private static void SendError(ClientSession session, string message)
        => session.Send(Packet.Create(PacketType.Error, message, success: false));
}
