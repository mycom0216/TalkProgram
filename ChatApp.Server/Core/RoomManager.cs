using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Server.Core;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 모든 채팅방(메모리)을 관리한다. 방 목록, 멤버, 접속 중인 연결.
// [핵심] 방과 멤버는 DB(rooms, room_members)에도 저장된다. 이 클래스는 "메모리 쪽"만 다루고,
//        DB 쓰기는 PacketHandler가 한다. 순서: ① 여기서 자리를 먼저 잡고(인원 확인) ② DB에 쓰고 ③ 실패하면 되돌린다.
//        [왜] DB 작업을 lock 안에서 하면, DB가 느릴 때 모든 방의 메시지 전송(SendToRoom도 같은 lock)이 같이 멈춘다.
// [스레드] 여러 사람의 수신 스레드가 동시에 부른다. lock 하나(_lock)로 모든 방 상태를 보호한다.
//          lock 안에서는 메모리 작업만 하고, 실제 전송(Send)은 lock 밖에서 한다(Send는 큐에 넣기만 해서 빠르지만 습관적으로).
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   모든 채팅방의 "메모리 장부". 방 목록(_rooms), 방마다 멤버(Members), 지금 접속 중인 연결(Online)을 들고 있다.
//
// ■ 꼭 구분할 두 가지
//   - Members = "자격". 방에 들어온 사람 명단. DB(room_members)에도 저장되고, 접속을 종료해도 그대로 남는다.
//   - Online  = "지금 접속 중". 그 방 소식을 실시간으로 받을 연결(ClientSession) 명단. 접속을 종료하면 빠진다.
//   양쪽 장부는 항상 같이 맞춘다: room.Online(방 → 접속자)과 session.JoinedRooms(접속자 → 방). 이 일을 Attach가 한다.
//
// ■ "3단계" 패턴 (입장·초대에서 반복되는 모양)
//   ① 여기서 메모리에 자리를 먼저 잡는다(TryJoin / TryInvite: 정원·비공개 확인)  ② 호출한 쪽(RoomService)이 DB에 저장한다
//   ③ DB 저장이 실패하면 RemoveMember로 되돌린다.  [왜] DB 작업을 lock 안에서 하면 DB가 느릴 때 모든 방의 전송이 멈추기 때문이다.
//
// ■ 면접 예상 질문과 답
//   Q1. lock을 하나만 쓴 이유는요? 단점은요?
//       A. 방 목록·멤버·접속자를 한꺼번에 바꾸는 작업(입장 + 장부 맞추기)이 많아서, lock 하나로 단순하고 틀리기 어렵게 만들었습니다.
//          방이 아주 많아져 경합이 늘면 방마다 lock을 두거나 ConcurrentDictionary를 쓰는 식으로 나눌 수 있습니다.
//   Q2. lock 안에서 DB 저장이나 전송을 하지 않는 이유는요?
//       A. lock을 잡고 있는 동안 다른 스레드가 모두 기다립니다. DB가 느리면 서버 전체의 채팅이 같이 느려지므로, lock 안에서는 메모리 작업만 합니다.
//   Q3. SendToRoom에서 ToArray()로 복사해서 lock 밖에서 보내는 이유는요?
//       A. 보내는 도중에 누가 들어오거나 나가면 컬렉션을 순회하다 예외가 납니다. 스냅샷을 떠 두면 안전하고, lock도 짧게 잡습니다.
//   Q4. 두 사람이 마지막 한 자리에 동시에 입장하면요?
//       A. "정원이 찼는지 확인(IsFull)"과 "멤버로 추가"가 lock 안에서 한 덩어리로 실행되므로, 한 명만 성공하고 다른 한 명은 Full을 받습니다.
//   Q5. 접속을 종료한 것과 방을 나간 것의 차이는요?
//       A. 접속 종료는 Detach(접속 연결만 뗌, 멤버로는 남음)이고, 나가기는 Leave(멤버에서도 빠짐)입니다. 그래서 다시 로그인하면 내 방들이 그대로 있습니다.
//   Q6. 방장이 나가면 누가 방장이 되나요?
//       A. PickNextOwner가 "가장 먼저 들어온 멤버"를 고릅니다(JoinedAt 순서). 마지막 멤버까지 나가면 방이 목록에서 사라집니다.
//   Q7. 같은 계정으로 다시 로그인하면 방 연결은 어떻게 되나요?
//       A. TransferMembership이 이전 연결의 방들을 새 연결로 옮깁니다. 이전 연결이 나중에 끊겨도 Detach가 아무 방에서도 빼지 않아서 "나갔습니다" 알림이 뜨지 않습니다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 정원이 안 맞는다 / 가득 찼다고 나온다 .... TryJoin의 IsFull, SetMaxUsers(지금 멤버 수보다 작게는 못 바꿈)
//   - 입장했는데 메시지가 안 온다 ................. Attach가 호출됐는지(Online에 들어갔는지)
//   - 퇴장·입장 알림이 중복된다 .................... Detach / TransferMembership
//   - 방 목록의 인원수가 다르다 ................... Room.ToInfo(MemberCount = 멤버 수, OnlineCount = 접속 수)
// ───────────────────────────────────────────────────────────────────────────

/// <summary>방 입장 시도 결과 (성공, 방 없음, 정원 초과, 이미 입장, 비공개 방).</summary>
public enum JoinResult { Ok, NotFound, Full, AlreadyIn, Private }

/// <summary>방 퇴장 시도 결과 (성공, 방 없음, 멤버 아님, 기본 방은 나갈 수 없음).</summary>
public enum LeaveResult { Ok, NotFound, NotMember, Lobby }

/// <summary>모든 채팅방의 목록과 입장·퇴장·접속 연결을 하나의 lock으로 관리하는 클래스 (스레드 안전).</summary>
public sealed class RoomManager
{
    private readonly object _lock = new();
    private readonly Dictionary<int, Room> _rooms = [];

    /// <summary>기본 방 "전체 채팅". 로그인한 모두가 들어간다.</summary>
    public Room Lobby { get; }

    public RoomManager()
    {
        Lobby = new Room(RoomInfo.LobbyId, "전체 채팅", null, null, maxUsers: 0, isPrivate: false);
        _rooms[Lobby.Id] = Lobby;
    }

    /// <summary>서버 시작 시 DB에서 읽은 방들을 넣는다 (기본 방 제외).</summary>
    public void Load(IEnumerable<Room> rooms)
    {
        lock (_lock)
            foreach (Room room in rooms.Where(r => !r.IsLobby))
                _rooms[room.Id] = room;
    }

    /// <summary>현재 방 개수 (기본 방 포함).</summary>
    public int RoomCount { get { lock (_lock) return _rooms.Count; } }

    // ───────── 조회 ─────────

    /// <summary>서버 콘솔 /rooms 용: 모든 방.</summary>
    public List<RoomInfo> GetRoomInfos()
    {
        lock (_lock) return _rooms.Values.OrderBy(r => r.Id).Select(r => r.ToInfo()).ToList();
    }

    /// <summary>공개 방 목록 (모두에게 같은 내용).</summary>
    public List<RoomInfo> GetPublicRoomInfos()
    {
        lock (_lock) return _rooms.Values.Where(r => !r.IsPrivate).OrderBy(r => r.Id).Select(r => r.ToInfo()).ToList();
    }

    /// <summary>이 사람이 멤버인 비공개 방 목록 (그 사람에게만 보인다).</summary>
    public List<RoomInfo> GetPrivateRoomInfos(string employeeNo)
    {
        lock (_lock)
            return _rooms.Values.Where(r => r.IsPrivate && r.Members.ContainsKey(employeeNo))
                         .OrderBy(r => r.Id).Select(r => r.ToInfo()).ToList();
    }

    /// <summary>방 하나의 전달용 <see cref="RoomInfo"/>를 lock 안에서 만들어 돌려준다 (withMembers가 true면 멤버 목록 포함).</summary>
    public RoomInfo GetInfo(Room room, bool withMembers = false)
    {
        lock (_lock) return room.ToInfo(withMembers);
    }

    /// <summary>이 세션이 연결된(= 멤버인) 방. 아니면 null. 메시지·파일 권한 확인에 쓴다.</summary>
    public Room? GetJoinedRoom(ClientSession session, int roomId)
    {
        lock (_lock)
            return _rooms.TryGetValue(roomId, out Room? room) && session.JoinedRooms.Contains(room) ? room : null;
    }

    /// <summary>방 번호로 방을 찾는다. 없으면 null.</summary>
    public Room? Find(int roomId)
    {
        lock (_lock) return _rooms.GetValueOrDefault(roomId);
    }

    /// <summary>방의 고정 공지를 바꾼다. 그 방이 이미 삭제되었거나 다른 객체로 바뀌었으면 false.</summary>
    public bool SetPinnedNotice(Room room, string text)
    {
        lock (_lock)
        {
            if (!_rooms.TryGetValue(room.Id, out Room? current) || !ReferenceEquals(current, room)) return false;
            room.PinnedNotice = text;
            return true;
        }
    }

    /// <summary>이 사람이 그 방의 멤버인지 (접속 여부와 관계없이).</summary>
    public bool IsMember(int roomId, string employeeNo)
    {
        lock (_lock) return _rooms.TryGetValue(roomId, out Room? room) && room.IsMember(employeeNo);
    }

    /// <summary>이 사람이 멤버인 방 번호들 (기본 방 포함). 검색 범위 계산용.</summary>
    public List<int> GetMemberRoomIds(string employeeNo)
    {
        lock (_lock) return _rooms.Values.Where(r => r.IsMember(employeeNo)).Select(r => r.Id).ToList();
    }

    /// <summary>방 이름 (검색 결과 표시용).</summary>
    public string? GetName(int roomId)
    {
        lock (_lock) return _rooms.GetValueOrDefault(roomId)?.Name;
    }

    // ───────── 로그인·접속 종료 ─────────

    /// <summary>
    /// 로그인한 세션을 기본 방과 "멤버인 모든 방"에 연결한다. 연결한 방(기본 방 제외)을 돌려준다.
    /// [왜] 카카오톡처럼 다시 로그인하면 내 단체방들이 그대로 있어야 한다.
    /// </summary>
    public List<Room> AttachOnLogin(ClientSession session, string employeeNo)
    {
        lock (_lock)
        {
            Attach(Lobby, session);
            var joined = new List<Room>();
            foreach (Room room in _rooms.Values.Where(r => !r.IsLobby && r.Members.ContainsKey(employeeNo)))
            {
                Attach(room, session);
                joined.Add(room);
            }
            return joined;
        }
    }

    /// <summary>접속 종료: 모든 방에서 연결만 뗀다 (멤버 자격은 그대로).</summary>
    public void Detach(ClientSession session)
    {
        lock (_lock)
        {
            foreach (Room room in session.JoinedRooms) room.Online.Remove(session);
            session.JoinedRooms.Clear();
        }
    }

    /// <summary>같은 계정이 새로 로그인함 → 이전 연결의 방 연결을 새 연결로 옮긴다.</summary>
    public void TransferMembership(ClientSession from, ClientSession to)
    {
        lock (_lock)
        {
            foreach (Room room in from.JoinedRooms)
            {
                room.Online.Remove(from);
                Attach(room, to);
            }
            from.JoinedRooms.Clear(); // 이전 연결이 끊길 때 Detach가 아무 방에서도 빼지 않게
        }
    }

    // ───────── 멤버 추가·삭제 ─────────

    /// <summary>새 방을 등록하고 만든 사람을 첫 멤버로 연결한다.</summary>
    public void AddRoom(Room room, ClientSession creator, RoomMember owner)
    {
        lock (_lock)
        {
            _rooms[room.Id] = room;
            room.Members[owner.EmployeeNo] = owner;
            Attach(room, creator);
        }
    }

    /// <summary>
    /// 공개 방 입장 ① 단계: 확인하고 메모리에 멤버로 먼저 넣는다(자리 잡기). DB 저장이 실패하면 RemoveMember로 되돌린다.
    /// </summary>
    public JoinResult TryJoin(ClientSession session, int roomId, RoomMember member, out Room? room)
    {
        lock (_lock)
        {
            if (!_rooms.TryGetValue(roomId, out room)) return JoinResult.NotFound;
            if (room.IsMember(member.EmployeeNo))
            {
                Attach(room, session); // 멤버인데 연결이 빠져 있던 경우(드묾)도 맞춰 둔다
                return JoinResult.AlreadyIn;
            }
            if (room.IsPrivate) return JoinResult.Private;
            if (room.IsFull) return JoinResult.Full;

            room.Members[member.EmployeeNo] = member;
            Attach(room, session);
            return JoinResult.Ok;
        }
    }

    /// <summary>
    /// 초대 ① 단계: 멤버가 아니고 자리가 있는 사람만 메모리에 넣는다. 실제로 넣은 사람들을 돌려준다.
    /// </summary>
    public List<RoomMember> TryInvite(Room room, IEnumerable<RoomMember> candidates)
    {
        lock (_lock)
        {
            var added = new List<RoomMember>();
            foreach (RoomMember m in candidates)
            {
                if (room.IsFull) break;
                if (room.Members.ContainsKey(m.EmployeeNo)) continue;
                room.Members[m.EmployeeNo] = m;
                added.Add(m);
            }
            return added;
        }
    }

    /// <summary>이미 멤버인 사람의 접속 중인 세션을 방에 연결한다 (초대받은 사람이 접속 중일 때).</summary>
    public void AttachIfMember(Room room, ClientSession session)
    {
        lock (_lock)
        {
            if (session.User is not null && room.Members.ContainsKey(session.User.EmployeeNo)) Attach(room, session);
        }
    }

    /// <summary>나가기: 멤버에서 빼고 연결도 뗀다. 마지막 멤버가 나가면 방을 목록에서 없앤다(closed = true).</summary>
    public LeaveResult Leave(ClientSession session, int roomId, out Room? room, out bool closed)
    {
        closed = false;
        string? no = session.User?.EmployeeNo;
        lock (_lock)
        {
            if (!_rooms.TryGetValue(roomId, out room)) return LeaveResult.NotFound;
            if (room.IsLobby) return LeaveResult.Lobby;
            if (no is null || !room.Members.ContainsKey(no)) return LeaveResult.NotMember;
            // [읽는 법] 이름 끝의 Locked = "이미 _lock을 잡은 상태에서만 부르는 내부 함수"라는 표시. lock 안에서 lock을 또 부르는 실수를 이름으로 막는다.
            closed = RemoveMemberLocked(room, no, session);
            return LeaveResult.Ok;
        }
    }

    /// <summary>방장을 그 멤버로 바꾼다. 멤버가 아니면 null, 바꿨으면 새 방장 정보.</summary>
    public RoomMember? SetOwner(Room room, string employeeNo)
    {
        lock (_lock)
        {
            if (!room.Members.TryGetValue(employeeNo, out RoomMember? member)) return null;
            room.OwnerNo = member.EmployeeNo;
            room.OwnerName = member.DisplayName;
            return member;
        }
    }

    /// <summary>방 정원 바꾸기. 지금 멤버 수보다 작으면 false (이미 들어온 사람을 내보내지 않는다).</summary>
    public bool SetMaxUsers(Room room, int maxUsers)
    {
        lock (_lock)
        {
            if (room.IsLobby || maxUsers < room.Members.Count) return false;
            room.MaxUsers = maxUsers;
            return true;
        }
    }

    /// <summary>방장이 나갔을 때 다음 방장: 가장 먼저 들어온 멤버 (없으면 null).</summary>
    public RoomMember? PickNextOwner(Room room)
    {
        lock (_lock) return room.Members.Values.OrderBy(m => m.JoinedAt).ThenBy(m => m.EmployeeNo).FirstOrDefault();
    }

    /// <summary>
    /// 방 삭제: 모든 멤버를 빼고 방을 없앤다. 지금 접속해 있던 연결들을 돌려준다(그들에게 "방이 삭제됨"을 알리려고).
    /// 없는 방이거나 기본 방이면 null.
    /// </summary>
    public List<ClientSession>? DeleteRoom(int roomId, out Room? room)
    {
        lock (_lock)
        {
            if (!_rooms.TryGetValue(roomId, out room) || room.IsLobby) return null;
            List<ClientSession> online = room.Online.ToList();
            foreach (ClientSession s in online) s.JoinedRooms.Remove(room);
            room.Online.Clear();
            room.Members.Clear();
            _rooms.Remove(roomId);
            return online;
        }
    }

    /// <summary>멤버 빼기 (DB 저장 실패 시 되돌리기용). 방이 비면 닫는다.</summary>
    public bool RemoveMember(Room room, string employeeNo, ClientSession? session)
    {
        lock (_lock) return RemoveMemberLocked(room, employeeNo, session);
    }

    private bool RemoveMemberLocked(Room room, string employeeNo, ClientSession? session)
    {
        room.Members.Remove(employeeNo);
        if (session is not null)
        {
            room.Online.Remove(session);
            session.JoinedRooms.Remove(room);
        }
        // [규칙] 기본 방(전체 채팅)은 비어도 없애지 않는다. 다른 방은 마지막 멤버가 나가면 목록에서 지우고 true를 돌려,
        //        호출한 쪽에 "방이 닫혔다"고 알린다.
        if (room.IsLobby || room.Members.Count > 0) return false;
        _rooms.Remove(room.Id);
        return true;
    }

    /// <summary>"여기까지 읽음" 기록. 더 뒤로 갈 때만 바꾼다(되돌아가지 않음). 바뀌었으면 true.</summary>
    public bool MarkRead(Room room, string employeeNo, DateTime upTo)
    {
        lock (_lock)
        {
            if (!room.Members.TryGetValue(employeeNo, out RoomMember? member)) return false;
            if (member.LastReadAt is DateTime old && old >= upTo) return false;
            member.LastReadAt = upTo;
            return true;
        }
    }

    /// <summary>멤버의 표시 이름 갱신 (방 멤버 목록의 이름이 최신이 되게).</summary>
    public RoomMember? GetMember(Room room, string employeeNo)
    {
        lock (_lock) return room.Members.GetValueOrDefault(employeeNo);
    }

    // [읽는 법] 양쪽 장부를 같이 맞춘다: 방 쪽(room.Online)과 사람 쪽(session.JoinedRooms). 한쪽만 바꾸면 "방에는 있는데 사람은 모르는" 상태가 된다.
    //           lock이 없는 이유: 이 함수는 항상 _lock을 잡은 메서드 안에서만 부른다(그래서 private static).
    private static void Attach(Room room, ClientSession session)
    {
        room.Online.Add(session);
        session.JoinedRooms.Add(room);
    }

    // ───────── 전송 ─────────

    /// <summary>방에 접속해 있는 모든 사람에게 보낸다.</summary>
    public void SendToRoom(Room room, Packet packet, ClientSession? except = null)
    {
        ClientSession[] online;
        // [왜] 스냅샷(ToArray)을 떠서 lock 밖에서 보낸다. 보내는 동안 누가 나가거나 들어와도 안전하다.
        lock (_lock) online = room.Online.ToArray();

        foreach (ClientSession member in online)
        {
            if (member != except) member.Send(packet); // Send는 큐에 넣기만 하므로 빠르다
        }
    }
}
