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
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   채팅방의 "생애"를 맡는 파일: 만들기 → 입장/초대 → 방장 위임·정원·공지 → 나가기/삭제, 그리고 로그인할 때 내 방 복원과 방 읽음 위치.
//   방 목록·인원 같은 "메모리 상태"는 RoomManager가, "DB 저장"은 RoomRepository가 하고, 이 파일은 둘을 올바른 순서로 엮는 지휘자다.
//
// ■ 읽는 순서 (코드가 길 때 이렇게 읽으세요)
//   ① OnLogin / OnDisconnected : 접속과 방의 연결·해제  ② HandleCreate → HandleJoin → Invite : 사람이 방에 "들어가는" 세 가지 길
//   ③ HandleLeave : 나가기(방장이면 위임, 마지막이면 방 닫힘)  ④ HandleTransferOwner / HandleCapacity / HandleNotice : 방장 권한 기능
//   ⑤ HandleDelete : 방 통째로 삭제  ⑥ MarkRead : 읽음 위치  ⑦ 맨 아래 공통 함수(BroadcastRoomUpdate, RoomSystem, SendFail/SendError, NowMs)
//
// ■ 모든 Handle~ 함수에 공통인 5단계 (한 번만 익히면 파일 전체가 읽힌다)
//   ① 패킷에서 요청을 꺼낸다(GetData)  ② 값이 올바른지 검사(이름 길이, 인원 범위, 방 번호 숫자인지)
//   ③ 권한 검사(방장/관리자/멤버인지) — "서버가" 한다. 클라이언트 버튼을 숨기는 것은 보안이 아니라 편의일 뿐이다.
//   ④ 상태 변경(RoomManager, lock 안) + DB 저장  ⑤ 결과 알림(요청자에게 응답 + 방 사람들에게 방송) + 방 목록 갱신 알림(roomListChanged)
//
// ■ 가장 중요한 설계: "메모리 먼저, DB 나중, 실패하면 되돌리기" (HandleJoin/Invite)
//   정원 확인과 자리 잡기는 반드시 lock 안에서 한 번에 해야 한다. 안 그러면 마지막 한 자리에 두 사람이 동시에 들어온다.
//   그런데 DB 저장은 느려서 lock 안에서 할 수 없다. 그래서 ① 메모리에서 자리를 먼저 확보(빠름, lock) → ② DB 저장(느림, lock 밖)
//   → ③ DB가 실패하면 메모리 자리를 반납(되돌리기) 순서로 한다. 반대로 방 삭제(HandleDelete)는 되돌릴 일이 없게 "DB 먼저"다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 기본 생성자(primary constructor): class RoomService(a, b, c) ... "C# 12 primary constructor"
//   - 패턴 매칭 is < 1 or > 20, is not X, is { } ............ "C# pattern matching relational logical patterns"
//   - 컬렉션 식 [owner] ...................................... "C# 12 collection expressions"
//   - throw; 와 throw ex; 의 차이(호출 스택 보존) ............ "C# throw vs throw ex stack trace"
//   - 보상 동작(되돌리기), 트랜잭션 없는 두 저장소 맞추기 ..... "compensating action saga pattern"
//   - 콜백 Action 주입(roomListChanged) ...................... "C# Action delegate callback dependency injection"
//   - 멱등성(이미 멤버면 다시 연결만) ........................ "idempotent operation"
//
// ■ 면접 예상 질문과 답
//   Q1. 방 멤버를 접속 여부가 아니라 DB에 저장하는 이유는요?
//       A. 카카오톡 단체방처럼 접속을 끊어도 멤버로 남아야 하고, 서버를 재시작해도 방이 그대로여야 하기 때문입니다. 접속 목록(Online)과 멤버 목록(Members)을 분리했고, 로그인하면 OnLogin이 DB의 멤버 정보로 방에 다시 연결합니다.
//   Q2. 입장 때 왜 메모리를 먼저 바꾸고 DB를 나중에 저장하나요?
//       A. 정원 검사와 자리 확보를 lock 안에서 원자적으로 해야 동시 입장으로 정원이 넘치지 않습니다. DB는 느려서 lock 안에 넣을 수 없으니, 메모리 선점 → DB 저장 → 실패 시 반납 순서로 합니다.
//   Q3. 비공개 방은 어떻게 지키나요? 목록에서 숨기면 충분한가요?
//       A. 아니요. 방 번호를 직접 보내 입장을 시도할 수 있으니, TryJoin이 서버에서 "비공개면 거절"을 판정합니다. 비공개 방에는 멤버의 초대로만 들어올 수 있고, 목록에도 멤버에게만 보입니다.
//   Q4. 방장이 위임 없이 나가면 방은 어떻게 되나요?
//       A. 가장 먼저 들어온 멤버(JoinedAt이 가장 이른 사람, 같으면 사원번호 순)가 자동으로 새 방장이 됩니다. 방장 없는 방이 남지 않게 하려는 것입니다. 마지막 멤버가 나가면 방은 닫힙니다.
//   Q5. [나가기]와 [삭제]는 무엇이 다른가요?
//       A. 나가기는 나만 빠지고 방은 남습니다. 삭제는 방장이나 관리자가 모든 멤버를 한 번에 빼고 방을 목록에서 없애는 것입니다. 삭제해도 대화 기록은 DB에 남깁니다.
//   Q6. 방 목록이 바뀔 때마다 바로 모든 사람에게 보내지 않고 roomListChanged 콜백을 부르는 이유는요?
//       A. 입장·초대·나가기가 한꺼번에 몰리면 목록을 그때마다 전체에게 보내는 비용이 큽니다. 이 서비스는 "바뀌었다"고만 알리고, 서버가 모아서(짧은 타이머로) 한 번에 보내게 했습니다.
//   Q7. 초대할 때 어떤 입력을 걸러내나요?
//       A. 앞뒤 공백, 빈 값, 나 자신, 20자 초과, 중복, 한 번에 허용 개수 초과를 걸러내고, DB에서 "사용 중인 계정"만 인정합니다(정지·퇴사자는 초대 불가). 정원이 차면 남은 자리까지만 들어갑니다.
//   Q8. 이미 멤버인 사람이 [입장]을 또 누르면요?
//       A. 오류가 아니라 "다시 연결"로 처리해 방 정보와 지난 대화만 다시 보냅니다. 같은 요청을 여러 번 해도 결과가 같은 "멱등성"을 지킨 것이고, 재접속·창 복구에도 도움이 됩니다.
//   Q9. 입장 알림에서 보낸 사람을 빼는(except: session) 이유는요?
//       A. 본인은 이미 별도의 "입장 성공" 응답을 받았으니 같은 알림을 두 번 보지 않게 하려는 것입니다. 같은 이유로 방 갱신 방송도 본인을 제외하는 경우가 있습니다.
//   Q10. 이 설계의 약점은 무엇인가요? (솔직하게 답하기)
//       A. (1) 입장·초대는 DB 실패 때 메모리를 되돌리지만, 나가기는 메모리를 먼저 바꾼 뒤 DB를 지우므로 DB가 실패하면 메모리와 DB가 어긋날 수 있습니다(재시작하면 DB 기준으로 복원). (2) 방 생성은 방 행을 만든 뒤 멤버를 넣는 두 번의 DB 호출이라, 그 사이 실패하면 멤버 없는 방이 남을 수 있습니다.
//          해결은 한 트랜잭션으로 묶거나, 실패 시 보상 동작을 넣는 것입니다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - "방이 가득 찼습니다"인데 실제로는 자리가 있다 .... RoomManager.TryJoin의 IsFull, DB의 max_users와 메모리 MaxUsers가 같은지
//   - 재접속하면 내 방이 사라진다 ...................... OnLogin → AttachOnLogin, DB room_members 행이 있는지
//   - 초대했는데 상대 화면에 안 뜬다 .................... Invite 뒤쪽 foreach: 상대가 접속 중이어야 즉시 뜬다(오프라인이면 다음 로그인 때 OnLogin이 복원)
//   - 방 목록 인원수가 늦게 바뀐다 ...................... roomListChanged가 모아서 보내는 것(정상). 타이머 주기를 확인
//   - 방장이 없는 방 / 방장 표시가 틀리다 ............... ChangeOwner, PickNextOwner, DB rooms.owner_no
//   - 공지가 저장 안 된다 ............................... HandleNotice의 권한 검사와 길이 제한(RoomNoticeRequest.MaxLength)
//   - 시각이 1ms씩 어긋난다 ............................. NowMs: DB DATETIME(3) 정밀도에 맞춰 마이크로초를 자른 값
// ───────────────────────────────────────────────────────────────────────────
/// <summary>방 만들기·입장·초대·나가기·삭제와 방 공지, 인원 설정, 읽음 처리를 담당하는 서비스.</summary>
// [개념] 클래스 이름 바로 옆의 (sessions, rooms, ...)는 C# 12의 "기본 생성자"다. 이 값들이 클래스 안에서 그대로 필드처럼 쓰인다.
//        DI(의존성 주입) 방식: 이 서비스는 필요한 부품(세션 관리자, 방 관리자, DB 저장소 ...)을 스스로 만들지 않고 밖에서 받는다.
//        그래서 시험할 때 가짜 부품을 끼우기 쉽다. 마지막 Action roomListChanged는 "방 목록이 바뀌었다"고 알리는 신호 함수다.
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
        // [왜] 자동 재접속이면 이전 연결에서 방 연결을 이미 넘겨받은 상태다. 이때 "님이 들어왔습니다"를 또 보내면 새로고침할 때마다 입장 알림이 반복되므로 미리 확인해 둔다.
        bool alreadyInLobby = session.JoinedRooms.Contains(rooms.Lobby);
        // [읽는 법] AttachOnLogin은 lock 안에서 "기본 방 + 내가 멤버인 모든 방"의 접속자 목록(Online)에 이 연결을 올린다. 돌려받은 joined는 아래에서 방별 정보를 보낼 대상이다.
        List<Room> joined = rooms.AttachOnLogin(session, me.EmployeeNo);

        // 기본 방: 예전처럼 입장 알림을 보낸다 (기본 방은 "지금 접속한 사람"이 멤버)
        // [읽는 법] 패킷 만들기 = Packet.Create(종류, 메시지, success) + .WithData(내용). 종류(RoomRes)가 "방에 관한 응답"이고, WithData는 방 정보를 JSON으로 실어 보낸다.
        //           session.Send는 바로 소켓에 쓰는 게 아니라 이 연결의 송신 큐에 넣는 것이라 즉시 돌아온다.
        session.Send(Packet.Create(PacketType.RoomRes, $"'{rooms.Lobby.Name}' 방에 들어왔습니다.", success: true)
                           .WithData(rooms.GetInfo(rooms.Lobby)));
        if (!alreadyInLobby)
            // [읽는 법] except: session = "이 사람을 뺀 나머지 접속자에게". 본인은 위의 응답으로 이미 입장을 알았으니 중복 알림을 막는다.
            rooms.SendToRoom(rooms.Lobby, RoomSystem(rooms.Lobby, $"{me.DisplayName}님이 들어왔습니다."), except: session);
        // [읽는 법] 지난 대화는 DbWriter 큐에 "요청"으로 들어가고, 저장이 모두 끝난 뒤 순서대로 처리된다(저장→조회 순서 보장).
        //           기본 방은 입장 시각 인자가 없다 = 제한 없이 최근 대화를 준다. 단체방은 아래처럼 JoinedAt 이후만 준다.
        messages.RequestRoomHistory(session, rooms.Lobby.Id);

        // 내 단체방: 조용히 다시 연결 (다른 멤버에게 알림 없음). 지난 대화는 내가 들어온 시각 이후만.
        // [읽는 법] 내 단체방들을 하나씩 "조용히" 다시 연결한다. 다른 멤버에게는 아무 알림도 가지 않는다(내가 접속했다고 방마다 떠들지 않게).
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
        // [읽는 법] Detach = 이 연결을 각 방의 접속자 목록(Online)에서만 뺀다. 멤버 목록(Members)과 DB는 건드리지 않으므로, 다음에 로그인하면 방이 그대로 복원된다.
        rooms.Detach(session);
        if (wasInLobby && session.User is UserInfo user)
            rooms.SendToRoom(rooms.Lobby, RoomSystem(rooms.Lobby, $"{user.DisplayName}님이 나갔습니다. (접속 종료)"));
    }

    // ───────── 만들기 ─────────

    /// <summary>방 만들기 요청 처리: 이름·최대 인원을 검사하고 DB와 메모리에 방을 만든 뒤, 요청에 초대 대상이 있으면 바로 초대한다.</summary>
    public void HandleCreate(ClientSession session, Packet packet)
    {
        CreateRoomRequest? req = packet.GetData<CreateRoomRequest>();
        // [읽는 법] req?.Name : req가 null이면 null, 아니면 Name. ?? "" : null이면 빈 문자열. 잘못된 패킷이 와도 NullReferenceException 없이 아래 검사에서 "1~N자로 입력하세요"로 걸러진다.
        string name = req?.Name.Trim() ?? "";
        int max = req?.MaxUsers ?? 0;

        // [개념] "is < 1 or > 상한"은 패턴 매칭으로 쓴 범위 검사다. (name.Length < 1 || name.Length > 상한)과 같은 뜻이며, 변수를 한 번만 쓰면 되어 읽기 쉽다.
        //        이 검사는 클라이언트에도 있지만 서버에서 반드시 다시 한다(수정된 클라이언트는 아무 값이나 보낼 수 있다).
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
        // [왜] 방 번호를 DB가 발급한다(AUTO_INCREMENT). 서버가 재시작해도 번호가 겹치지 않고, 이 번호가 메시지·파일·멤버 기록의 연결 고리가 된다.
        // [주의] 아래 AddMembers와 별개의 두 번 DB 호출이다. 그 사이에 실패하면 멤버 없는 방 행이 남을 수 있다(노트의 약점 참고).
        int id = repository.Create(name, me.EmployeeNo, max, isPrivate); // DB에서 방 번호 발급
        // [읽는 법] RoomMember(사원번호, 이름, 들어온 시각, 마지막 읽은 시각). 방장도 멤버이므로 첫 멤버로 넣고, 읽은 기록이 아직 없어 마지막 인자는 null이다.
        //           들어온 시각(JoinedAt)은 나중에 "들어오기 전 대화는 안 보이게" 하는 기준이 되고, 방장이 나갔을 때 다음 방장을 고르는 기준도 된다.
        var owner = new RoomMember(me.EmployeeNo, me.DisplayName, NowMs(), null);
        // [개념] [owner]는 C# 12의 컬렉션 식으로, 원소 1개짜리 리스트를 만드는 간단한 표기다. 여러 명을 한 번에 넣는 함수이므로 한 명도 리스트로 감싸 준다.
        repository.AddMembers(id, [owner]);

        var room = new Room(id, name, me.EmployeeNo, me.DisplayName, max, isPrivate);
        // [읽는 법] DB에 저장한 뒤 메모리(RoomManager)에 방을 등록하고, 만든 사람의 연결을 이 방의 접속자로 올린다. 이후 이 방으로 오는 방송을 받게 된다.
        rooms.AddRoom(room, session, owner);
        log.Info($"방 생성: #{id} '{name}' ({(isPrivate ? "비공개" : "공개")}, 최대 {max}명) by {me}");
        audit.Record(AuditActions.RoomCreate, me.EmployeeNo, detail: $"#{id} {name}{(isPrivate ? " (비공개)" : "")}", ip: session.RemoteEndPoint);

        session.Send(Packet.Create(PacketType.RoomRes, $"'{name}' 방을 만들었습니다.", success: true).WithData(rooms.GetInfo(room, withMembers: true)));
        messages.RequestRoomHistory(session, room.Id, owner.JoinedAt); // 빈 기록이지만 "불러오기 끝"을 알려 준다

        // 만들면서 바로 초대
        // [읽는 법] 방을 만들면서 고른 사람들을 바로 초대한다. 별도 패킷을 또 보낼 필요가 없어 왕복이 줄고, 초대 규칙(걸러내기·정원·활성 계정)은 아래 Invite 하나로 통일된다.
        if (req.InviteNos.Count > 0) Invite(session, room, req.InviteNos);
        // [읽는 법] 방 목록이 바뀌었다는 신호만 보낸다. 실제 목록 전송은 서버가 모아서(짧은 타이머) 한 번에 한다. 입장·초대·나가기가 몰려도 전송이 폭주하지 않는다.
        roomListChanged();
    }

    // ───────── 입장 (공개 방) ─────────

    /// <summary>공개 방 입장 요청 처리 (Target = 방 번호): 없는 방·비공개·정원 초과는 거절하고, 이미 멤버이면 다시 연결만 해 준다.</summary>
    public void HandleJoin(ClientSession session, Packet packet)
    {
        if (!int.TryParse(packet.Target, out int roomId))
        {
            SendFail(session, "방 번호가 올바르지 않습니다.");
            return;
        }

        UserInfo me = session.User!;
        var member = new RoomMember(me.EmployeeNo, me.DisplayName, NowMs(), null);
        // [읽는 법] TryJoin은 lock 안에서 순서대로 검사한다: 없는 방 → 이미 멤버 → 비공개 → 정원 초과 → (모두 통과하면) 멤버로 추가 + 연결.
        //           결과(JoinResult)가 Ok일 때만 switch의 어느 case에도 걸리지 않고 아래로 내려간다. 실패 case는 모두 return하므로 아래 코드는 "입장 성공"만 처리한다.
        //           out Room? room : 함수가 결과 값 말고 방 객체도 돌려주는 방식(여러 값을 반환할 때 쓰는 out 매개변수).
        switch (rooms.TryJoin(session, roomId, member, out Room? room))
        {
            // Target = 방 번호: 재접속한 클라이언트가 "어느 방에 다시 못 들어갔는지" 알 수 있게
            case JoinResult.NotFound: SendFail(session, "존재하지 않거나 이미 사라진 방입니다.", roomId); return;
            case JoinResult.Full: SendFail(session, "방이 가득 찼습니다.", roomId); return;
            case JoinResult.Private: SendFail(session, "비공개 방입니다. 방 멤버에게 초대를 받아야 들어갈 수 있습니다.", roomId); return;
            // [개념] 멱등성: 이미 멤버인데 또 [입장]을 눌러도 오류가 아니라 "다시 연결"로 처리한다. 창을 닫았다 열거나 재접속한 뒤 놓친 대화를 다시 채울 때 이 경로를 쓴다.
            case JoinResult.AlreadyIn:
                // 이미 멤버 → 창을 되살리고 놓친 대화를 채울 수 있게 정보와 지난 대화만 다시 보낸다
                session.Send(Packet.Create(PacketType.RoomRes, $"'{room!.Name}' 방에 다시 연결되었습니다.", success: true)
                                   .WithData(rooms.GetInfo(room, withMembers: true)));
                messages.RequestRoomHistory(session, room.Id, room.IsLobby ? null : rooms.GetMember(room, me.EmployeeNo)?.JoinedAt);
                return;
        }

        try
        {
            // [순서] ① 메모리 자리 확보(TryJoin, lock) → ② 여기서 DB 저장(느림, lock 밖). ②가 실패하면 아래 catch에서 ①을 되돌린다.
            repository.AddMembers(room!.Id, [member]);
        }
        catch (Exception)
        {
            // [개념] 보상 동작(되돌리기): DB와 메모리는 한 트랜잭션으로 묶을 수 없으므로, 실패하면 직접 반대로 돌려 둘을 일치시킨다. 이게 없으면 "메모리엔 멤버, DB엔 없음"이 되어 재시작 후 방에서 사라진다.
            rooms.RemoveMember(room!, me.EmployeeNo, session); // ③ DB 실패 → 메모리도 되돌린다
            // [개념] throw; 는 잡은 예외를 "원래 호출 스택 그대로" 다시 던진다. throw ex; 라고 쓰면 스택이 여기서부터로 덮어써져 원인 추적이 어려워진다.
            //        되돌리기까지 마친 뒤 다시 던져, 위의 PacketHandler가 사용자에게 "DB 오류"로 응답하고 서버 로그에 남기게 한다.
            throw; // PacketHandler가 "DB 오류"로 답한다
        }

        session.Send(Packet.Create(PacketType.RoomRes, $"'{room.Name}' 방에 들어왔습니다.", success: true)
                           .WithData(rooms.GetInfo(room, withMembers: true)));
        rooms.SendToRoom(room, RoomSystem(room, $"{me.DisplayName}님이 들어왔습니다."), except: session);
        messages.RequestRoomHistory(session, room.Id, member.JoinedAt);
        // [읽는 법] 방 안의 다른 사람들 화면에서 멤버 목록·인원수가 새로 그려지도록 최신 방 정보를 보낸다. 본인은 위 응답에 이미 최신 정보가 들어 있어 제외한다.
        BroadcastRoomUpdate(room, except: session);
        roomListChanged();
    }

    // ───────── 초대 ─────────

    /// <summary>초대 요청 처리: 그 방의 멤버만 (기본 방 제외) 사용 중인 계정을 초대할 수 있다.</summary>
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
        // [읽는 법] 걸러내기 파이프라인: 공백 제거 → (빈 값 · 20자 초과 · 나 자신) 제외 → 중복 제거(Distinct) → 한 번에 허용하는 수까지만(Take) → 리스트.
        //           입력이 수천 명이어도, 같은 사람이 여러 번 들어 있어도 서버 작업량이 일정 수준으로 제한된다.
        List<string> wanted = employeeNos.Select(n => n.Trim()).Where(n => n.Length is > 0 and <= 20 && n != me.EmployeeNo)
                                         .Distinct().Take(RoomInviteRequest.MaxPerRequest).ToList();
        if (wanted.Count == 0) return;

        // 실제로 있는, 사용 중인 사람만 (정지·퇴사한 사람은 초대할 수 없다)
        // [왜] 사용자 입력(사원번호)을 믿지 않고 DB에서 "실제로 있고 사용 중인" 계정만 골라 이름까지 한 번에 가져온다. 없는 번호, 정지·퇴사자는 결과에서 빠진다.
        Dictionary<string, string> names = users.GetActiveDisplayNames(wanted);
        DateTime now = NowMs();
        // [읽는 법] lock 안에서 후보를 앞에서부터 넣다가 정원이 차면 멈춘다(이미 멤버인 사람은 건너뜀). 돌려받는 added는 "실제로 새로 들어간 사람들"이라 알림·DB 저장은 이 목록만 대상으로 한다.
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
            // [읽는 법] 입장과 같은 보상 동작. DB 저장이 실패하면 방금 메모리에 넣은 사람들을 모두 되돌린다(세 번째 인자 null = 연결 정리 대상 세션이 없다는 뜻).
            foreach (RoomMember m in added) rooms.RemoveMember(room, m.EmployeeNo, null);
            throw;
        }

        string who = string.Join(", ", added.Select(m => m.DisplayName));
        log.Info($"초대: #{room.Id} '{room.Name}' ← {who} by {me}");
        rooms.SendToRoom(room, RoomSystem(room, $"{me.DisplayName}님이 {who}님을 초대했습니다."));

        // 초대받은 사람이 지금 접속 중이면 바로 방에 연결하고 알려 준다
        foreach (RoomMember m in added)
        {
            // [읽는 법] 초대받은 사람이 지금 접속 중인지 찾는다. 접속 중이 아니거나(null) 이미 끊어지는 중이면 건너뛴다 — 그 사람은 다음 로그인 때 OnLogin이 DB 멤버 정보로 방을 복원해 준다.
            // [개념] "is not ClientSession target"은 null이 아닌지 확인하면서 동시에 변수 target에 담는 패턴 매칭이다.
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

    /// <summary>방 나가기 요청 처리 (Target = 방 번호): 멤버에서 빠지며, 방장이 나가면 가장 오래된 멤버가 방장이 되고 마지막 멤버가 나가면 방이 닫힌다.</summary>
    public void HandleLeave(ClientSession session, Packet packet)
    {
        if (!int.TryParse(packet.Target, out int roomId))
        {
            SendFail(session, "나갈 방 번호가 없습니다.");
            return;
        }

        UserInfo me = session.User!;
        // [읽는 법] Leave는 lock 안에서 "멤버에서 빼고 연결도 뗀다". 마지막 멤버가 나가 방이 비면 closed = true로 알려 준다. 기본 방·없는 방·멤버 아님은 case로 걸러 return한다.
        switch (rooms.Leave(session, roomId, out Room? room, out bool closed))
        {
            case LeaveResult.Lobby: SendFail(session, "기본 방(전체 채팅)은 나갈 수 없습니다."); return;
            case LeaveResult.NotFound: SendFail(session, "존재하지 않거나 이미 사라진 방입니다."); return;
            case LeaveResult.NotMember: SendFail(session, "참여하지 않은 방입니다."); return;
        }

        // [주의] 여기서는 메모리를 이미 바꾼 뒤 DB를 지운다. DB가 실패하면 되돌리는 코드가 없어서 둘이 어긋날 수 있다(서버를 재시작하면 DB 기준으로 복원됨). 개선 여지가 있는 부분이다.
        repository.RemoveMember(room!.Id, me.EmployeeNo);
        // [읽는 법] 마지막 멤버가 나간 방은 DB에서도 닫힘 처리한다(목록에서 사라지고, 대화 기록은 남는다).
        if (closed) repository.Close(room.Id);

        session.Send(Packet.Create(PacketType.RoomLeft, $"'{room.Name}' 방에서 나왔습니다.", target: room.Id.ToString()));
        if (!closed)
        {
            rooms.SendToRoom(room, RoomSystem(room, $"{me.DisplayName}님이 나갔습니다."));
            // 방장이 위임하지 않고 나감 → 가장 오래된 멤버가 자동으로 방장이 된다 (방장 없는 방이 남지 않게)
            // [읽는 법] 나간 사람이 방장이었다면, 남은 멤버 중 가장 먼저 들어온 사람을 새 방장으로 자동 지정한다. 남은 멤버가 없으면 null이라 이 조건은 거짓이 된다.
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
        // [개념] 권한 검사 공식: "방장도 아니고 관리자도 아니면 거절". 방장 위임·정원 변경·삭제가 모두 같은 공식을 쓴다. 클라이언트의 버튼 숨김과 상관없이 서버가 최종 판단한다.
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

    /// <summary>방장 또는 관리자가 방 정원(최대 인원)을 바꾼다. 지금 멤버 수보다 작게는 못 줄인다.</summary>
    public void HandleCapacity(ClientSession session, Packet packet)
    {
        UserInfo me = session.User!;
        Room? room = int.TryParse(packet.Target, out int roomId) ? rooms.Find(roomId) : null;
        if (room is null || room.IsLobby)
        {
            SendError(session, "정원을 바꿀 수 없는 방입니다.");
            return;
        }
        if (room.OwnerNo != me.EmployeeNo && !me.IsAdmin)
        {
            SendError(session, "방장 또는 관리자만 방 정원을 바꿀 수 있습니다.");
            return;
        }
        if (!int.TryParse(packet.Body, out int max) || max < CreateRoomRequest.MinUsers || max > CreateRoomRequest.MaxUsersLimit)
        {
            SendError(session, $"방 정원은 {CreateRoomRequest.MinUsers}~{CreateRoomRequest.MaxUsersLimit}명으로 정할 수 있습니다.");
            return;
        }
        int before = room.MaxUsers;
        if (max == before) return;
        // [읽는 법] 정원 줄이기 검사는 lock 안에서 한다: 지금 멤버 수보다 작게는 못 줄인다(이미 들어온 사람을 쫓아내지 않는다). 성공하면 메모리가 바뀐 것이니 바로 아래에서 DB에 반영한다.
        if (!rooms.SetMaxUsers(room, max))
        {
            SendError(session, "지금 멤버 수보다 작게 줄일 수 없습니다.");
            return;
        }
        repository.SetMaxUsers(room.Id, max);
        log.Info($"방 정원 변경: #{room.Id} '{room.Name}' {before} → {max} by {me}");
        audit.Record(AuditActions.RoomCapacity, me.EmployeeNo, null, $"#{room.Id} {room.Name} {before}→{max}");
        rooms.SendToRoom(room, RoomSystem(room, $"{me.DisplayName}님이 방 정원을 {max}명으로 바꿨습니다."));
        BroadcastRoomUpdate(room);
        roomListChanged(); // 방 목록의 "3/10"도 바뀐다
    }

    /// <summary>방장 바꾸기 (메모리 → DB → 알림·감사). 멤버가 아니면 false.</summary>
    private bool ChangeOwner(Room room, string employeeNo, UserInfo actor, string? reason)
    {
        string? before = room.OwnerNo;
        // [순서] 메모리(멤버인지 확인하며 방장 변경) → DB → 로그·감사 기록 → 방 안 알림. 멤버가 아닌 사람을 방장으로 지정하려 하면 SetOwner가 null을 돌려줘 여기서 false로 끝난다.
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
        // [읽는 법] 공지 권한: ① 방이 없거나 ② 방장도 관리자도 아니거나 ③ (관리자가 아니면서) 멤버도 아니면 거절하고, 시도 자체를 감사 로그(Denied)에 남긴다. 거절 기록은 나중에 무단 시도를 추적하는 단서가 된다.
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

        // [읽는 법] 빈 문자열 text는 "공지 삭제"를 뜻한다. DB를 먼저 저장하고 메모리를 바꾸는데, 그 사이 방이 삭제됐다면 메모리 갱신이 false로 끝나 "다시 시도" 안내가 나간다.
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

        // [왜 DB 먼저] 삭제는 되돌리기가 어렵다. DB가 실패하면 예외로 빠져나가 메모리는 그대로이므로 "반쯤 삭제된 방"이 생기지 않는다. 입장·초대와 순서가 반대인 이유가 이것이다.
        repository.Delete(room.Id); // DB 먼저 (실패하면 PacketHandler가 "DB 오류"로 답하고 메모리는 그대로)
        // [읽는 법] DeleteRoom은 lock 안에서 모든 멤버·연결을 비우고 방을 없앤 뒤, "그때 접속 중이던 연결 목록"을 돌려준다. 그 사람들에게 "방이 삭제됨"을 알리려는 것이다. ?? [] 는 null이면 빈 목록(안전장치).
        //           out _ 는 "돌려주는 값이 있지만 쓰지 않겠다"는 표시다.
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
        // [읽는 법] 이미 더 뒤까지 읽은 기록이 있으면 false → 아무것도 하지 않고 끝낸다. 스크롤할 때마다 오는 읽음 신호가 쌓여도 DB 저장과 방송이 "앞으로 나아갈 때만" 일어나게 하는 장치다.
        if (!rooms.MarkRead(room, me.EmployeeNo, upTo)) return; // 이미 더 뒤까지 읽음 → 할 일 없음

        // [읽는 법] 읽음 저장은 급하지 않은 DB 쓰기라 DbWriter 큐에 맡기고 바로 방송으로 넘어간다. 화면의 숫자 감소는 DB 저장을 기다리지 않는다.
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
    // [읽는 법] Ticks(100나노초 단위) 중 1ms(=10,000틱) 미만을 잘라 낸다. DB의 DATETIME(3)은 밀리초까지만 저장하므로, 메모리의 시각과 DB에서 읽은 시각이 같은 값이 되어 "읽음 위치 비교" 같은 계산이 어긋나지 않는다.
    public static DateTime NowMs()
    {
        DateTime now = DateTime.UtcNow;
        return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, now.Kind);
    }
}
