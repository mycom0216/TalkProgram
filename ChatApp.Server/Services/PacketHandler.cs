using System.Data.Common;
using System.Net;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Server.Core;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;

namespace ChatApp.Server.Services;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 받은 패킷을 종류(PacketType)별로 알맞은 곳에 넘기는 "교통정리" + 회원(가입·로그인·비밀번호)·관리자·상태 처리.
//            방 기능은 RoomService, 메시지 기능은 MessageService, 파일은 FileService가 맡는다(파일이 너무 커지지 않게 나눔).
// [스레드]   각 세션의 수신 스레드에서 동시에 호출된다(병렬 처리).
//            공유 상태는 SessionManager·RoomManager(스레드 안전)와 DB를 통해서만 건드린다.
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   서버로 들어온 모든 패킷이 거치는 "접수 창구". 종류(PacketType)를 보고 알맞은 담당(서비스)에게 넘기고, 회원·관리자 처리는 이 파일이 직접 한다.
//
// ■ 담당 나누기 (switch가 하는 일)
//   회원(가입·로그인·비밀번호·상태) = 이 파일 / 방 = RoomService / 메시지 = MessageService / 친구·휴대폰 = FriendService
//   프로필 사진 = ProfileService / 파일 = FileService / 관리자 = AdminService(이 파일의 HandleAdmin이 권한 확인 후 호출)
//
// ■ Handle()의 순서
//   ① 로그인 전이면 허용 목록(안내·가입·로그인·핑) 밖의 패킷을 거절  ② 임시 비밀번호 상태면 비밀번호 변경·핑·로그아웃만 허용
//   ③ switch로 담당에게 전달  ④ DB 오류(DbException)는 여기서 잡아 "DB 오류" 응답을 보내고, 서버와 다른 사용자는 계속 동작
//
// ■ 로그인 흐름 (면접 단골 · HandleLogin)
//   이미 로그인? → 요청 해석 → 프로토콜 버전 확인 → (LDAP 계정은 TLS 필요) → IP 속도 제한 → AuthService.Login(비밀번호 확인, 연속 5회 실패 시 10분 잠금: 설정 MaxLoginAttempts·LockMinutes)
//   → SessionManager.Login(같은 계정이 이미 있으면 새 로그인이 이기고 이전 연결을 끊음) → 계정 활성 재확인 → (임시 비밀번호면 변경 대기)
//   → (TLS + 로컬 계정이면 자동 로그인 토큰 발급) → LoginRes 전송 → CompleteLogin(내 방 복원, 안 읽은 1:1 알림, 접속자·방 목록 방송)
//
// ■ 새 기능(패킷)을 추가할 때 손댈 곳 (유지보수 순서)
//   ① Common의 PacketType에 이름 추가 → ② 클라이언트가 그 패킷을 보냄 → ③ 이 파일 switch에 case 한 줄 → ④ 담당 서비스에 Handle 메서드
//   → ⑤ 응답 패킷을 보냄 → ⑥ 클라이언트 MainForm.HandlePacket에 case 추가.  case를 빠뜨리면 "지원하지 않는 요청" 오류가 난다.
//
// ■ 면접 예상 질문과 답
//   Q1. 로그인 전에 허용하는 패킷을 왜 제한하나요?
//       A. 클라이언트 화면에서만 막으면 수정한 클라이언트로 우회할 수 있습니다. 서버가 "로그인 안 한 사람은 가입·로그인·핑만"이라고 직접 막아야 진짜 보안입니다.
//   Q2. 관리자 기능은 어떻게 권한을 지키나요?
//       A. 클라이언트가 메뉴를 숨기는 건 편의일 뿐입니다. 관리 요청이 올 때마다 서버가 session.User.IsAdmin을 다시 확인하고, 아니면 거절과 함께 감사 로그(Denied)를 남깁니다.
//   Q3. 접속자 목록을 왜 바로 안 보내고 0.2초 타이머로 묶나요?
//       A. 한 명이 들어올 때마다 전체에게 목록을 보내면 데이터가 접속자 수의 세제곱에 비례해 늘어납니다(300명 부하 테스트에서 로그인 27초). "바뀌었다"는 표시만 해 두고 0.2초마다 한 번만 방송하도록 바꿨습니다.
//   Q4. 같은 계정으로 두 번 로그인하면요?
//       A. 새 로그인이 이깁니다. 이전 연결에는 ForceLogout을 보내고 닫으며, 방 연결은 TransferMembership으로 새 연결에 옮겨서 "나갔습니다" 알림이 뜨지 않게 했습니다.
//   Q5. DB가 꺼지면 서버는 어떻게 되나요?
//       A. DbException을 Handle()에서 잡아 해당 요청에만 실패 응답을 보냅니다. 서버와 다른 사용자의 채팅은 계속 됩니다.
//   Q6. 로그인 속도 제한과 계정 잠금은 뭐가 다른가요?
//       A. 계정 잠금은 "사원번호 하나"를 연속 5회 실패(기본값, 10분 잠금)로 보호하고, 속도 제한은 "한 IP가 여러 사원번호를 돌아가며 찔러 보는 것"을 늦춥니다. 서로 다른 공격을 막습니다.
//   Q7. 이 클래스는 스레드에 안전한가요?
//       A. Handle()은 여러 사람의 수신 스레드에서 동시에 호출됩니다. 그래서 이 클래스는 자기 상태를 거의 갖지 않고, 공유 데이터는 SessionManager·RoomManager(lock)와 DB를 통해서만 바꿉니다.
//   Q8. 자동 로그인(토큰)은 안전한가요?
//       A. TLS 연결에서만 발급·사용하고, 기기별 deviceId와 30일 만료를 둡니다. 비밀번호를 바꾸거나 재설정하면 서버가 기존 토큰을 모두 지웁니다(DB에는 원문이 아닌 해시 token_hash만 저장). LDAP 계정에는 토큰을 주지 않고 매번 디렉터리 인증을 합니다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 로그인이 안 된다 ............... HandleLogin의 순서대로(버전 → 속도 제한 → AuthService.Login). 서버 로그의 "로그인 실패 ... - 사유"
//   - "지원하지 않는 요청" 오류 ...... Handle()의 switch에 case가 빠졌는지
//   - 목록이 0.2초쯤 늦게 갱신된다 ... 정상(FlushLists의 묶음 방송)
//   - 관리자 기능이 거절된다 .......... HandleAdmin의 IsAdmin 확인, 감사 로그의 Denied 기록
// ───────────────────────────────────────────────────────────────────────────
public sealed class PacketHandler
{
    private readonly SessionManager _sessions;
    private readonly RoomManager _rooms;
    private readonly AuthService _auth;
    private readonly UserRepository _users;
    private readonly FileService _files;
    private readonly AdminService _admin;
    private readonly AuditLog _audit;
    private readonly RoomService _roomService;
    private readonly MessageService _messageService;
    private readonly ServerConfig _config;

    // ── 속도 제한 (IP 단위) ──
    // 로그인: 한 IP에서 연달아 10번까지, 그 뒤로는 1분에 10번 꼴. 회원가입: 연달아 3번, 그 뒤로는 10분에 3번 꼴.
    private readonly RateLimiter _loginLimiter = new(capacity: 10, refillPerMinute: 10);
    private readonly RateLimiter _registerLimiter = new(capacity: 3, refillPerMinute: 0.3);

    public ServerLogger Log { get; }
    private readonly FriendService _friendService;
    private readonly ProfileService _profileService;

    public PacketHandler(SessionManager sessions, RoomManager rooms, AuthService auth, RoomRepository roomRepository,
                         MessageRepository messageRepository, UserRepository users, FileService files, MessageStore messages,
                         AdminService admin, AuditLog audit, ServerConfig config, ServerLogger log)
    {
        _sessions = sessions;
        _rooms = rooms;
        _auth = auth;
        _users = users;
        _files = files;
        _admin = admin;
        _audit = audit;
        _config = config;
        Log = log;
        _roomService = new RoomService(sessions, rooms, roomRepository, users, messages, audit, log, BroadcastRoomList);
        _messageService = new MessageService(sessions, rooms, _roomService, messages, messageRepository, users, audit, log);
        _friendService = new FriendService(sessions, users, log);
        _profileService = new ProfileService(users, config.UploadRoot, log, BroadcastUserList);
    }

    public void Handle(ClientSession session, Packet packet)
    {
        // 로그인 전에는 서버 안내/회원가입/로그인/핑만 허용
        if (!session.IsLoggedIn && packet.Type is not (PacketType.ServerInfoReq or PacketType.RegisterReq or PacketType.LoginReq or PacketType.TokenLoginReq or PacketType.Ping))
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
            // [읽는 법] 이 switch는 "이 종류의 패킷은 이 담당에게"라는 안내표다. 각 줄은 담당 서비스의 메서드를 부르기만 하고,
            //           실제 일은 그 서비스에서 한다. 새 기능을 만들면 여기에 case를 한 줄 더하면 된다.
            switch (packet.Type)
            {
                // 회원
                case PacketType.ServerInfoReq: session.Send(Packet.Create(PacketType.ServerInfo).WithData(_auth.GetServerInfo())); break;
                case PacketType.RegisterReq: HandleRegister(session, packet); break;
                case PacketType.LoginReq: HandleLogin(session, packet); break;
                case PacketType.TokenLoginReq: HandleTokenLogin(session, packet); break;
                case PacketType.ChangePasswordReq: HandleChangePassword(session, packet); break;
                case PacketType.StatusUpdate: HandleStatusUpdate(session, packet); break;
                case PacketType.Logout:
                    _audit.Record(AuditActions.Logout, session.User?.EmployeeNo, ip: session.RemoteEndPoint);
                    session.Close();
                    break;

                // 메시지
                case PacketType.ChatMsg: _messageService.HandleChat(session, packet); break;
                case PacketType.Whisper: _messageService.HandleWhisper(session, packet); break;
                case PacketType.MessageDelete: _messageService.HandleDelete(session, packet); break;
                case PacketType.MessageEditReq: _messageService.HandleEdit(session, packet); break;
                case PacketType.Typing: _messageService.HandleTyping(session, packet); break;
                case PacketType.Reaction: _messageService.HandleReaction(session, packet); break;
                case PacketType.ReadMark: _messageService.HandleReadMark(session, packet); break;
                case PacketType.HistoryReq: _messageService.HandleHistoryRequest(session, packet); break;
                case PacketType.SearchReq: _messageService.HandleSearch(session, packet); break;
                case PacketType.DirectoryReq: _messageService.HandleDirectory(session); break;

                // 방
                case PacketType.RoomListReq: session.Send(CreateRoomListPacket(session.User!.EmployeeNo)); break;
                case PacketType.RoomCreate: _roomService.HandleCreate(session, packet); break;
                case PacketType.RoomJoin: _roomService.HandleJoin(session, packet); break;
                case PacketType.RoomInvite: _roomService.HandleInvite(session, packet); break;
                case PacketType.RoomLeave: _roomService.HandleLeave(session, packet); break;
                case PacketType.RoomDelete: _roomService.HandleDelete(session, packet); break;
                case PacketType.RoomTransferOwner: _roomService.HandleTransferOwner(session, packet); break;
                case PacketType.RoomNotice: _roomService.HandleNotice(session, packet); break;
                case PacketType.RoomCapacity: _roomService.HandleCapacity(session, packet); break;

                // 친구·휴대폰 번호
                case PacketType.FriendListReq: _friendService.SendList(session); break;
                case PacketType.FriendSearchReq: _friendService.HandleSearch(session, packet); break;
                case PacketType.FriendAdd: _friendService.HandleAdd(session, packet); break;
                case PacketType.FriendRemove: _friendService.HandleRemove(session, packet); break;
                case PacketType.PhoneInfoReq: _friendService.HandlePhoneInfo(session); break;
                case PacketType.PhoneUpdate: _friendService.HandlePhoneUpdate(session, packet); break;

                // 프로필 사진
                case PacketType.ProfilePhotoUpdate: _profileService.HandleUpdate(session, packet); break;
                case PacketType.ProfilePhotoReq: _profileService.HandleRequest(session, packet); break;
                case PacketType.ProfileReq: _friendService.HandleProfile(session, packet); break;

                // 파일
                case PacketType.FileUploadReq: _files.HandleUploadRequest(session, packet); break;
                case PacketType.FileChunk: _files.HandleChunk(session, packet); break;
                case PacketType.FileUploadEnd: _files.HandleUploadEnd(session, packet); break;
                case PacketType.FileDownloadReq: _files.HandleDownloadRequest(session, packet); break;
                case PacketType.FileUploadCancel: _files.HandleUploadCancel(session, packet); break;
                case PacketType.FileDownloadCancel: _files.HandleDownloadCancel(session, packet); break;
                case PacketType.RoomFilesReq: _files.HandleRoomFilesRequest(session, packet); break;

                // 관리자
                case PacketType.AdminUserListReq:
                case PacketType.AdminActionReq:
                case PacketType.AdminAuditReq: HandleAdmin(session, packet); break;
                case PacketType.AdminStatsReq: HandleAdmin(session, packet); break;

                case PacketType.Ping: session.Send(Packet.Create(PacketType.Pong)); break;
                case PacketType.Unknown: break; // 더 새 버전 클라이언트의 모르는 요청 → 조용히 무시 (연결은 유지)
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
                PacketType.TokenLoginReq => Packet.Create(PacketType.LoginRes, "서버 DB 오류로 처리하지 못했습니다.", success: false),
                PacketType.RoomCreate or PacketType.RoomJoin => Packet.Create(PacketType.RoomRes, "서버 DB 오류로 처리하지 못했습니다.", success: false),
                PacketType.ChangePasswordReq => Packet.Create(PacketType.ChangePasswordRes, "서버 DB 오류로 처리하지 못했습니다.", success: false),
                PacketType.AdminActionReq => Packet.Create(PacketType.AdminActionRes, "서버 DB 오류로 처리하지 못했습니다.", success: false),
                _ => Packet.Create(PacketType.Error, "서버 DB 오류가 발생했습니다.", success: false),
            };
            session.Send(reply);
        }
    }

    // ───────────────────────── 회원 ─────────────────────────

    /// <summary>속도 제한에 쓸 대상 이름. 같은 PC(127.0.0.1)는 설정에 따라 제외한다(부하 테스트·개발).</summary>
    private string? RateKey(ClientSession session)
    {
        IPAddress? ip = session.RemoteAddress;
        if (ip is null) return null;
        if (!_config.RateLimitLoopback && IPAddress.IsLoopback(ip)) return null;
        return ip.ToString();
    }

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

        if (req.ProtocolVersion < ProtocolInfo.MinSupported)
        {
            session.Send(Packet.Create(PacketType.RegisterRes, OldClientMessage(req.ProtocolVersion), success: false));
            return;
        }

        if (RateKey(session) is string key && !_registerLimiter.TryAcquire(key))
        {
            session.Send(Packet.Create(PacketType.RegisterRes, "가입 시도가 너무 잦습니다. 몇 분 뒤 다시 시도하세요.", success: false));
            Log.Info($"회원가입 속도 제한: {session.RemoteEndPoint}");
            return;
        }

        var (success, message) = _auth.Register(req);
        session.Send(Packet.Create(PacketType.RegisterRes, message, success: success));
        if (!success) return;

        string no = req.EmployeeNo.Trim();
        Log.Info($"회원가입: {no} ({session.RemoteEndPoint})");
        bool pending = !_auth.GetServerInfo().AutoApprove;
        _audit.Record(AuditActions.Register, no, no, $"{req.Name.Trim()} / {req.Department.Trim()}{(pending ? " (승인 대기)" : "")}", session.RemoteEndPoint);
        if (pending) _admin.NotifyAdmins($"새 가입 신청: {req.Name.Trim()} {req.Position.Trim()} ({req.Department.Trim()}, {no})");
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

        // 옛 클라이언트는 새 서버의 패킷을 이해하지 못한다 → 로그인 전에 분명히 알려 준다
        if (req.ProtocolVersion < ProtocolInfo.MinSupported)
        {
            session.Send(Packet.Create(PacketType.LoginRes, OldClientMessage(req.ProtocolVersion), success: false));
            Log.Info($"옛 버전 클라이언트 거절: {req.EmployeeNo.Trim()} (프로토콜 v{req.ProtocolVersion}) {session.RemoteEndPoint}");
            return;
        }

        // [왜] LDAP 계정은 사내 디렉터리의 비밀번호를 그대로 쓴다. 암호화되지 않은 연결로 받으면 그 비밀번호가 노출되므로 TLS일 때만 허용한다.
        if (_users.FindByEmployeeNo(req.EmployeeNo.Trim()) is { AuthSource: "LDAP" } && !session.IsTls)
        {
            session.Send(Packet.Create(PacketType.LoginRes, "LDAP 계정 로그인은 클라이언트와 서버 사이 TLS 연결이 필요합니다.", success: false));
            return;
        }

        // [왜] 계정 잠금(5회 실패)은 "사원번호 하나"를 지키고, 속도 제한은 "한 IP가 여러 사원번호를 돌아가며 찔러 보는 것"을 늦춘다.
        if (RateKey(session) is string key && !_loginLimiter.TryAcquire(key))
        {
            session.Send(Packet.Create(PacketType.LoginRes, "로그인 시도가 너무 잦습니다. 1분 뒤 다시 시도하세요.", success: false));
            _audit.Record(AuditActions.LoginFail, null, req.EmployeeNo.Trim(), "속도 제한", session.RemoteEndPoint);
            return;
        }

        // [흐름] 비밀번호 확인은 AuthService가 한다(해시 비교, 5회 실패 잠금, 정지·승인 대기 확인). 이 파일은 그 결과(user가 null인지)로 분기만 한다.
        var (user, message) = _auth.Login(req);
        if (user is null)
        {
            session.Send(Packet.Create(PacketType.LoginRes, message, success: false));
            Log.Info($"로그인 실패: {req.EmployeeNo.Trim()} ({session.RemoteEndPoint}) - {message}");
            // [왜] 실패도 기록한다: 같은 사원번호로 짧은 시간에 실패가 몰리면 비밀번호 대입 공격의 흔적이다.
            //      없는 사원번호도 그대로 남겨야 "어떤 아이디들을 찔러 봤는지" 알 수 있다(최대 20자로 잘라 저장).
            _audit.Record(AuditActions.LoginFail, null, req.EmployeeNo.Trim(), message, session.RemoteEndPoint);
            return;
        }

        // 같은 사원번호의 이전 연결이 있으면 새 로그인이 이긴다 (자동 재접속, 다른 PC에서 로그인)
        // [개념] SessionManager.Login은 "사원번호 → 세션" 장부에 등록하고, 같은 사원번호의 이전 세션이 있으면 돌려준다(old).
        //        old가 있다 = 같은 계정이 이미 접속 중 = 자동 재접속 또는 다른 PC에서 로그인한 경우.
        if (_sessions.Login(session, user) is ClientSession old)
        {
            Log.Info($"중복 로그인: 이전 연결 {old} 을(를) 끊고 새 연결 {session} 로 바꿉니다.");
            // 이전 연결의 방 연결을 새 연결로 옮긴다 → 이전 연결이 끊길 때 "나갔습니다" 알림이 뜨지 않는다
            _rooms.TransferMembership(old, session);
            old.SendAndClose(Packet.Create(PacketType.ForceLogout, "다른 곳에서 같은 사원번호로 로그인하여 연결을 끊었습니다."));
        }

        // [주의] 비밀번호 확인(auth.Login)과 세션 등록 사이에 관리자가 정지했을 수 있다.
        //        그 순간 정지 처리는 "접속 중인 세션"을 찾지 못해 끊지 못했으므로, 등록한 뒤 한 번 더 확인한다.
        if (!_users.IsActive(user.EmployeeNo))
        {
            session.SendAndClose(Packet.Create(PacketType.LoginRes, "사용할 수 없는 계정입니다. 관리자에게 문의하세요.", success: false));
            return;
        }

        _auth.RecordLoginSuccess(user.EmployeeNo);
        _audit.Record(AuditActions.LoginOk, user.EmployeeNo, ip: session.RemoteEndPoint);
        if (user.MustChangePassword)
        {
            // 임시 비밀번호: 로그인은 됐지만 비밀번호를 바꿀 때까지 방 입장·목록 방송을 미룬다 (CompleteLogin은 변경 후에)
            session.MustChangePassword = true;
            session.Send(Packet.Create(PacketType.LoginRes, "임시 비밀번호로 로그인했습니다. 새 비밀번호로 바꿔 주세요.", success: true).WithData(user));
            Log.Info($"로그인(임시 비밀번호, 변경 대기): {session}");
            return;
        }

        // Keep LDAP authentication authoritative. A bearer token could otherwise
        // outlive a password change or account disablement in the directory.
        string deviceId = NormalizeDeviceId(req.DeviceId);
        string? token = session.IsTls && _users.FindByEmployeeNo(user.EmployeeNo)?.AuthSource == "LOCAL"
            ? IssueLoginToken(user.EmployeeNo, deviceId) : null;
        session.Send(Packet.Create(PacketType.LoginRes, message, target: token, success: true).WithData(user));
        Log.Info($"로그인: {session}");
        CompleteLogin(session);
    }

    private string IssueLoginToken(string employeeNo, string deviceId)
    {
        // [개념] 자동 로그인 토큰 = 암호학적으로 안전한 난수 32바이트(256비트)를 URL에 써도 되는 문자열로 바꾼 것. 추측할 수 없어야 하므로
        //        Random이 아니라 RandomNumberGenerator를 쓴다. (+ / = 문자를 바꾸고 지우는 이유는 Base64 특수문자를 피하려는 것)
        string token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _users.IssueLoginToken(employeeNo, deviceId, token, DateTime.UtcNow.AddDays(30));
        return token;
    }

    private static string NormalizeDeviceId(string? deviceId)
        => deviceId is { Length: 32 } && deviceId.All(Uri.IsHexDigit) ? deviceId.ToLowerInvariant() : "legacy";

    private void HandleTokenLogin(ClientSession session, Packet packet)
    {
        if (session.IsLoggedIn) { SendError(session, "이미 로그인했습니다."); return; }
        TokenLoginRequest? req = packet.GetData<TokenLoginRequest>();
        if (req is null || req.EmployeeNo.Length == 0 || req.Token.Length < 40 || req.ProtocolVersion < ProtocolInfo.MinSupported)
        {
            session.Send(Packet.Create(PacketType.LoginRes, "자동 로그인 정보가 올바르지 않습니다.", success: false));
            return;
        }
        if (!session.IsTls)
        {
            session.Send(Packet.Create(PacketType.LoginRes, "자동 로그인은 TLS 보안 연결에서만 사용할 수 있습니다.", success: false));
            return;
        }
        if (RateKey(session) is string rateKey && !_loginLimiter.TryAcquire(rateKey))
        {
            session.Send(Packet.Create(PacketType.LoginRes, "로그인 시도가 너무 잦습니다. 1분 뒤 다시 시도하세요.", success: false));
            return;
        }
        string deviceId = NormalizeDeviceId(req.DeviceId);
        if (!_users.ValidateLoginToken(req.EmployeeNo.Trim(), deviceId, req.Token) || !_users.IsActive(req.EmployeeNo.Trim()))
        {
            session.Send(Packet.Create(PacketType.LoginRes, "자동 로그인 정보가 만료되었습니다. 비밀번호로 다시 로그인하세요.", success: false));
            return;
        }
        UserInfo? user = _users.FindActiveUser(req.EmployeeNo.Trim());
        if (user is null) { session.Send(Packet.Create(PacketType.LoginRes, "사용할 수 없는 계정입니다.", success: false)); return; }
        if (_sessions.Login(session, user) is ClientSession old)
        {
            _rooms.TransferMembership(old, session);
            old.SendAndClose(Packet.Create(PacketType.ForceLogout, "다른 곳에서 같은 사원번호로 로그인하여 연결을 끊었습니다."));
        }
        _auth.RecordLoginSuccess(user.EmployeeNo);
        _audit.Record(AuditActions.LoginOk, user.EmployeeNo, detail: "자동 로그인", ip: session.RemoteEndPoint);
        session.Send(Packet.Create(PacketType.LoginRes, $"{user.DisplayName}님, 환영합니다!", target: req.Token, success: true).WithData(user));
        CompleteLogin(session);
    }

    private static string OldClientMessage(int version) =>
        $"사내톡 프로그램이 오래된 버전입니다(v{version}). 관리자에게 최신 버전을 받아 설치하세요. (서버는 v{ProtocolInfo.MinSupported} 이상 필요)";

    /// <summary>로그인 마무리: 내 방들 복원, 안 읽은 1:1 알림, 접속자·방 목록 방송, 관리자면 승인 대기 알림.</summary>
    private void CompleteLogin(ClientSession session)
    {
        _roomService.OnLogin(session);
        _messageService.OnLogin(session);
        BroadcastUserList();
        BroadcastRoomList();

        if (session.User is { IsAdmin: true } && _users.CountPending() is > 0 and int pending)
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
        var (success, message) = _auth.ChangePassword(me.EmployeeNo, req);
        session.Send(Packet.Create(PacketType.ChangePasswordRes, message, success: success));
        if (!success) return;

        _audit.Record(AuditActions.PasswordChange, me.EmployeeNo, me.EmployeeNo, session.MustChangePassword ? "임시 비밀번호 변경" : null, session.RemoteEndPoint);
        Log.Info($"비밀번호 변경: {session}");
        if (session.MustChangePassword)
        {
            session.MustChangePassword = false;
            CompleteLogin(session);
        }
    }

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
            if (message != me.StatusMessage && !_auth.SaveStatusMessage(me.EmployeeNo, message))
                Log.Info("users.status_message 칸이 없어 상태 메시지를 저장하지 못했습니다 (migrate_003 필요). 이번 접속 동안만 유지됩니다.");
        }
        if (req.Presence == me.Presence && message == me.StatusMessage) return; // 바뀐 게 없음

        // [스레드] UpdateUser: 관리자가 동시에 권한을 바꾸더라도 두 변경이 모두 남는다 (ClientSession.UpdateUser 설명)
        session.UpdateUser(u => u.WithStatus(req.Presence, message));
        BroadcastUserList();
    }

    // ───────────────────────── 관리자 ─────────────────────────

    /// <summary>관리자 요청. 매번 서버가 권한을 확인한다 (클라이언트가 메뉴를 숨기는 것은 편의일 뿐 보안이 아니다).</summary>
    private void HandleAdmin(ClientSession session, Packet packet)
    {
        UserInfo me = session.User!;
        if (!me.IsAdmin)
        {
            // 관리자가 아닌데 관리 패킷을 보냈다 = 클라이언트를 고쳐서 시도했을 가능성 → 기록해 둔다
            _audit.Record(AuditActions.Denied, me.EmployeeNo, detail: packet.Type.ToString(), ip: session.RemoteEndPoint);
            SendError(session, "관리자 권한이 없습니다.");
            return;
        }

        switch (packet.Type)
        {
            case PacketType.AdminUserListReq:
                session.Send(Packet.Create(PacketType.AdminUserList, target: packet.Body).WithData(_admin.ListUsers(packet.Body)));
                break;

            case PacketType.AdminActionReq:
                AdminAction? action = packet.GetData<AdminAction>();
                if (action is null) { session.Send(Packet.Create(PacketType.AdminActionRes, "잘못된 요청입니다.", success: false)); return; }
                AdminOutcome result = _admin.Execute(action, me.EmployeeNo, session.RemoteEndPoint);
                session.Send(Packet.Create(PacketType.AdminActionRes, result.Message, success: result.Success)
                    .WithData(new AdminActionResult { Type = action.Type, EmployeeNo = action.EmployeeNo.Trim(), TempPassword = result.TempPassword }));
                break;

            case PacketType.AdminAuditReq:
                AuditQuery query = packet.GetData<AuditQuery>() ?? new AuditQuery();
                session.Send(Packet.Create(PacketType.AdminAudit).WithData(_admin.QueryAudit(query)));
                break;

            case PacketType.AdminStatsReq:
                session.Send(Packet.Create(PacketType.AdminStats).WithData(_admin.GetStats()));
                break;
        }
    }

    /// <summary>주기 정리 (하트비트 스레드가 1분마다): 오래 쓰지 않은 속도 제한 기록을 지운다.</summary>
    public void Maintenance()
    {
        _loginLimiter.Cleanup();
        _registerLimiter.Cleanup();
    }

    // ───────────────────────── 접속 종료 ─────────────────────────

    /// <summary>ClientSession.Close()에서 호출된다(어느 스레드에서든 한 번만).</summary>
    public void OnDisconnected(ClientSession session)
    {
        _sessions.Remove(session);
        _files.OnDisconnected(session); // 올리던 파일 정리
        _roomService.OnDisconnected(session); // 방 연결만 뗀다 (멤버로는 남는다)
        Log.Info($"접속 해제: {session} (현재 {_sessions.ConnectedCount}명)");

        if (session.User is not null)
        {
            BroadcastUserList();
            BroadcastRoomList(); // 접속 인원이 바뀌었으므로
        }
    }

    // ───────────────────────── 공통 전송 ─────────────────────────

    /// <summary>서버 공지 등 모든 로그인 사용자에게 (방과 무관).</summary>
    public void BroadcastSystem(string message)
        => _sessions.Broadcast(Packet.Create(PacketType.SystemMsg, message));

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
        // [주의] 타이머 콜백(스레드 풀)에서 잡히지 않은 예외가 나면 서버 프로세스 전체가 종료된다. 반드시 여기서 잡는다.
        try
        {
            if (Interlocked.Exchange(ref _userListDirty, 0) == 1)
                _sessions.Broadcast(Packet.Create(PacketType.UserList).WithData(_sessions.GetUsers()));
            if (Interlocked.Exchange(ref _roomListDirty, 0) == 1)
                SendRoomLists();
        }
        catch (Exception ex)
        {
            Log.Error($"목록 방송 중 오류: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _flushing, 0);
        }
    }

    /// <summary>
    /// 방 목록 보내기. 공개 방 목록은 모두 같으므로 패킷 하나를 같이 쓰고(직렬화 1번),
    /// 비공개 방 멤버에게만 "공개 + 내 비공개 방" 목록을 따로 만들어 보낸다.
    /// </summary>
    private void SendRoomLists()
    {
        List<RoomInfo> publicRooms = _rooms.GetPublicRoomInfos();
        Packet shared = Packet.Create(PacketType.RoomList).WithData(publicRooms);
        foreach (ClientSession session in _sessions.GetLoggedInSessions())
        {
            List<RoomInfo> mine = _rooms.GetPrivateRoomInfos(session.User!.EmployeeNo);
            session.Send(mine.Count == 0 ? shared : Packet.Create(PacketType.RoomList).WithData(Merge(publicRooms, mine)));
        }
    }

    private Packet CreateRoomListPacket(string employeeNo)
        => Packet.Create(PacketType.RoomList).WithData(Merge(_rooms.GetPublicRoomInfos(), _rooms.GetPrivateRoomInfos(employeeNo)));

    private static List<RoomInfo> Merge(List<RoomInfo> a, List<RoomInfo> b) => a.Concat(b).OrderBy(r => r.RoomId).ToList();

    private void BroadcastUserList() => Interlocked.Exchange(ref _userListDirty, 1);

    private void BroadcastRoomList() => Interlocked.Exchange(ref _roomListDirty, 1);

    private static void SendError(ClientSession session, string message)
        => session.Send(Packet.Create(PacketType.Error, message, success: false));
}
