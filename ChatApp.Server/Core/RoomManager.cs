using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Server.Core;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 서버에 있는 모든 채팅방과 "누가 어느 방에 있는지"를 관리한다. (요구사항 RM-01~03)
// [스레드]   여러 사람의 수신 스레드가 동시에 입장/퇴장/채팅을 하므로 모든 공개 메서드가 스레드 안전해야 한다.
//            → lock 하나(_lock)로 방 목록·멤버 목록·각 세션의 참여 방 목록을 함께 보호한다.
// [연결]     ChatServer가 1개 만든다. PacketHandler(방 요청 처리), FileService(파일 권한 확인),
//            Program.cs(/rooms 명령)가 사용한다.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>입장 결과. 실패 이유마다 다른 안내 문구를 보여 주기 위해 bool 대신 enum을 쓴다.</summary>
public enum JoinResult { Ok, NotFound, Full, AlreadyIn }

/// <summary>퇴장 결과.</summary>
public enum LeaveResult { Ok, NotFound, NotMember, Lobby }

/// <summary>
/// 채팅방 목록과 멤버 관리 (RM-01~03).
/// 한 사람이 여러 방에 동시에 참여할 수 있다(메신저처럼 방마다 대화창이 따로 뜬다).
/// 기본 방("전체 채팅")은 로그인하면 자동으로 들어가고 나갈 수 없다.
/// 방은 마지막 멤버가 "나가기"하거나 접속을 끊으면 삭제된다.
///
/// 동시성 설계
/// - 멤버 목록(Room.Members)과 각 사용자의 참여 방 목록(ClientSession.JoinedRooms)은 항상 함께 바뀌어야 하므로
///   lock 하나(_lock)로 묶는다. 인원 확인과 입장도 같은 lock 안이라 마지막 한 자리에 두 명이 동시에 와도 한 명만 성공한다.
/// - 메시지 전송(네트워크 I/O)은 lock 밖에서 한다. lock 안에서는 멤버 "스냅샷"만 뜬다.
/// </summary>
public sealed class RoomManager
{
    // [개념] lock (_lock) { ... }: 중괄호 안은 한 번에 한 스레드만 들어갈 수 있다(상호 배제, 내부적으로 Monitor 사용).
    //        다른 스레드는 앞 스레드가 나올 때까지 입구에서 기다린다.
    // [왜] lock 대상으로 전용 object를 따로 만든다. this나 외부에 공개된 객체를 lock하면,
    //      바깥 코드가 같은 객체를 lock해서 의도치 않게 서로 기다리는(교착) 일이 생길 수 있다.
    private readonly object _lock = new();

    // [개념] Dictionary<방번호, 방>: 번호로 방을 빠르게(평균 O(1)) 찾는다. 스레드 안전하지 않으므로 _lock 안에서만 사용.
    // [왜] ConcurrentDictionary를 쓰지 않은 이유: "방 목록 + 멤버 목록 + 세션의 참여 목록" 여러 개를
    //      한꺼번에 일관되게 바꿔야 해서, 각각이 따로 스레드 안전한 것으로는 부족하다. 하나의 lock으로 묶어야 한다.
    private readonly Dictionary<int, Room> _rooms = [];

    /// <summary>기본 방(1번, "전체 채팅"). 서버가 켜져 있는 동안 항상 존재한다.</summary>
    public Room Lobby { get; }

    public RoomManager()
    {
        // [개념] owner: null, maxUsers: 0 처럼 "이름: 값"으로 넘기는 것을 명명된 인수라고 한다. 무슨 값인지 읽기 쉽다.
        Lobby = new Room(RoomInfo.LobbyId, "전체 채팅", owner: null, maxUsers: 0);
        _rooms[Lobby.Id] = Lobby;
    }

    /// <summary>클라이언트에 보낼 방 목록 (번호순). 인원수를 정확히 세기 위해 lock 안에서 만든다.</summary>
    public List<RoomInfo> GetRoomInfos()
    {
        lock (_lock)
            // [개념] LINQ: OrderBy(정렬) → Select(변환) → ToList(새 리스트로 복사).
            //        ToList()로 "복사본"을 만들어 돌려주므로, lock을 나간 뒤 다른 스레드가 방을 바꿔도 안전하다.
            return _rooms.Values.OrderBy(r => r.Id).Select(r => r.ToInfo()).ToList();
    }

    /// <summary>방 하나의 정보 (입장 응답용).</summary>
    public RoomInfo GetInfo(Room room)
    {
        lock (_lock) return room.ToInfo();
    }

    /// <summary>session이 참여 중인 방이면 그 방, 아니면 null (채팅 보낼 때 확인용).</summary>
    // [왜] 클라이언트가 보낸 방 번호를 그대로 믿지 않는다. 참여하지 않은 방에 메시지를 보내거나
    //      파일을 받아 가는 것을 막는 "권한 확인" 역할을 한다.
    public Room? GetJoinedRoom(ClientSession session, int roomId)
    {
        lock (_lock)
            // [개념] out Room? room: 메서드가 두 번째 결과(찾은 방)를 이 변수에 넣어 준다. TryGetValue 패턴.
            // [개념] 조건 ? A : B (삼항 연산자): 조건이 참이면 A, 거짓이면 B.
            return _rooms.TryGetValue(roomId, out Room? room) && session.JoinedRooms.Contains(room) ? room : null;
    }

    /// <summary>
    /// 새 방 등록 + 만든 사람 입장을 한 번에(lock 하나로) 처리한다.
    /// 나눠서 하면, 빈 방이 잠깐 보이는 사이 다른 사람이 들어왔다 나가며 방이 삭제될 수 있다.
    /// </summary>
    // [주의] 이런 문제를 "확인과 실행 사이의 틈(check-then-act) 경쟁 상태"라고 한다.
    //        여러 단계를 하나의 lock 안에서 처리하면 다른 스레드가 중간 상태를 볼 수 없다.
    /// <summary>
    /// 같은 사람이 다시 로그인했을 때(자동 재접속, 다른 PC) 이전 연결의 방 참여를 새 연결로 옮긴다.
    /// [왜] lock 하나 안에서 "빼고 넣기"를 한 번에 해야, 그 사이에 방이 0명이 되어 삭제되는 순간이 생기지 않는다.
    /// </summary>
    public void TransferMembership(ClientSession from, ClientSession to)
    {
        lock (_lock)
        {
            foreach (Room room in from.JoinedRooms)
            {
                room.Members.Remove(from);
                room.Members.Add(to);
                to.JoinedRooms.Add(room);
            }
            from.JoinedRooms.Clear(); // 이전 연결이 끊길 때 LeaveAll이 아무 방에서도 빼지 않게
        }
    }

    public JoinResult AddAndJoin(Room room, ClientSession creator)
    {
        lock (_lock)
        {
            _rooms[room.Id] = room;
            // [개념] out _ : 돌려받는 값이 필요 없을 때 버리는 표시(discard).
            return JoinLocked(creator, room.Id, out _);
        }
    }

    /// <summary>방에 들어간다. 다른 방에서는 나가지 않는다.</summary>
    public JoinResult Join(ClientSession session, int roomId, out Room? room)
    {
        lock (_lock) return JoinLocked(session, roomId, out room);
    }

    /// <summary>
    /// 실제 입장 처리. 이름 끝의 "Locked"는 "이미 _lock을 잡은 상태에서만 부르라"는 약속(관례)이다.
    /// </summary>
    // [주의] "꽉 찼나?" 확인(IsFull)과 "추가"(Members.Add)가 같은 lock 안에 있어야 한다.
    //        lock 밖에서 확인하면, 마지막 한 자리에 두 명이 동시에 "자리 있음"을 보고 둘 다 들어와 정원을 넘는다.
    //        (실제로 9명이 동시에 남은 2자리에 들어오는 테스트에서 정확히 2명만 성공하는 것을 확인했다)
    private JoinResult JoinLocked(ClientSession session, int roomId, out Room? room)
    {
        if (!_rooms.TryGetValue(roomId, out room)) return JoinResult.NotFound;
        if (session.JoinedRooms.Contains(room)) return JoinResult.AlreadyIn;
        if (room.IsFull) return JoinResult.Full;

        // [왜] 방 쪽(Members)과 사람 쪽(JoinedRooms) 양쪽에 같이 기록한다(양방향 연결).
        //      방 → 멤버(메시지 보낼 때), 사람 → 방(접속 끊길 때 모든 방에서 빼기)을 둘 다 빠르게 찾기 위해서.
        room.Members.Add(session);
        session.JoinedRooms.Add(room);
        return JoinResult.Ok;
    }

    /// <summary>방에서 나간다. 기본 방은 나갈 수 없다. 마지막 멤버가 나가면 방이 삭제된다(removed = true).</summary>
    public LeaveResult Leave(ClientSession session, int roomId, out Room? room, out bool removed)
    {
        removed = false; // [개념] out 매개변수는 메서드가 끝나기 전에 반드시 값을 넣어야 한다.
        lock (_lock)
        {
            if (!_rooms.TryGetValue(roomId, out room)) return LeaveResult.NotFound;
            if (room.IsLobby) return LeaveResult.Lobby;
            if (!session.JoinedRooms.Contains(room)) return LeaveResult.NotMember;

            removed = RemoveMemberLocked(room, session);
            return LeaveResult.Ok;
        }
    }

    /// <summary>접속 종료 시 모든 방에서 뺀다. (방, 삭제됐는지) 목록을 돌려준다.</summary>
    // [개념] List<(Room Room, bool Removed)>: 튜플(값 여러 개를 묶은 것)의 리스트. 작은 결과 묶음을 클래스 없이 돌려줄 때 편하다.
    public List<(Room Room, bool Removed)> LeaveAll(ClientSession session)
    {
        lock (_lock)
        {
            var result = new List<(Room, bool)>();
            // [주의] JoinedRooms를 돌면서 동시에 그 안에서 지우면(Remove) "컬렉션이 수정됨" 예외가 난다.
            //        그래서 ToList()로 복사본을 만들어 그 복사본을 돈다.
            foreach (Room room in session.JoinedRooms.ToList())
                result.Add((room, RemoveMemberLocked(room, session)));
            return result;
        }
    }

    /// <summary>멤버를 빼고, 기본 방이 아닌데 아무도 없으면 방을 삭제한다(RM-03). _lock 안에서만 호출.</summary>
    private bool RemoveMemberLocked(Room room, ClientSession session)
    {
        room.Members.Remove(session);
        session.JoinedRooms.Remove(room);
        if (room.IsLobby || room.Members.Count > 0) return false;
        _rooms.Remove(room.Id);
        return true;
    }

    /// <summary>방 멤버 전원에게 전송. 스냅샷을 떠서 lock 밖에서 보낸다.</summary>
    // [왜] lock 안에서 Send까지 하면, 멤버가 많을 때 그동안 다른 모든 방 작업(입장·퇴장·채팅)이 lock 앞에서 기다린다.
    //      lock 안에서는 멤버 배열을 복사(스냅샷)만 하고 바로 나온 뒤, 복사본으로 보낸다.
    // [주의] 스냅샷을 뜬 직후 누가 나가면 그 사람에게도 보내질 수 있지만, 닫힌 세션의 Send는 그냥 무시되므로 문제없다.
    public void SendToRoom(Room room, Packet packet, ClientSession? except = null)
    {
        ClientSession[] members;
        lock (_lock) members = room.Members.ToArray();

        foreach (ClientSession member in members)
        {
            if (member != except) member.Send(packet); // Send는 큐에 넣기만 하므로 빠르다
        }
    }
}
