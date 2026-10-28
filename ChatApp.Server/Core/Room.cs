using ChatApp.Common.Models;

namespace ChatApp.Server.Core;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 채팅방 1개의 데이터(번호, 이름, 방장, 최대 인원, 현재 멤버).
//            스스로는 아무 동작도 하지 않는 "데이터 상자"이고, 입장/퇴장 같은 규칙은 RoomManager가 처리한다.
// [스레드]   여러 세션 스레드가 동시에 같은 방에 접근하므로, 바뀌는 값(Members)은
//            반드시 RoomManager의 lock 안에서만 만진다. 바뀌지 않는 값(Id, Name 등)은 아무 스레드나 읽어도 안전.
// [연결]     RoomManager가 만들고 보관 / ClientSession.JoinedRooms가 참조 / ToInfo()로 클라이언트에 보낼 RoomInfo 생성.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 채팅방 하나. 멤버 목록(Members)은 RoomManager의 lock 안에서만 읽고 쓴다.
/// </summary>
// [개념] 기본 생성자(primary constructor, C# 12): 클래스 이름 옆 괄호(id, name, ...)가 곧 생성자 매개변수다.
//        아래 속성 초기값(= id 등)에서 바로 쓸 수 있어, 생성자 본문을 따로 쓰지 않아도 된다.
public sealed class Room(int id, string name, UserInfo? owner, int maxUsers)
{
    // [왜] { get; }만 있는 속성 = 만든 뒤 바꿀 수 없음(불변). 불변 값은 여러 스레드가 동시에 읽어도 lock이 필요 없다.
    public int Id { get; } = id;
    public string Name { get; } = name;

    /// <summary>방장. 기본 방("전체 채팅")은 방장이 없어서 null.</summary>
    // [개념] UserInfo? 의 '?' = null이 될 수 있음을 표시(nullable 참조 형식). 컴파일러가 null 검사를 빠뜨리면 경고해 준다.
    public UserInfo? Owner { get; } = owner;

    /// <summary>0이면 제한 없음(기본 방).</summary>
    public int MaxUsers { get; } = maxUsers;

    /// <summary>현재 멤버. RoomManager._lock 안에서만 접근.</summary>
    // [개념] HashSet: 중복을 허용하지 않는 집합. "이미 들어있나?"(Contains) 확인과 추가/삭제가 평균 O(1)로 빠르다.
    // [주의] HashSet은 스레드 안전하지 않다. 두 스레드가 동시에 Add하면 내부 구조가 깨질 수 있어서 lock이 필수.
    // [개념] internal: 같은 프로젝트(ChatApp.Server) 안에서만 보인다. 외부에서 멤버 목록을 함부로 못 만지게 한다.
    // [개념] = [] : 컬렉션 식(C# 12). new HashSet<ClientSession>()과 같은 뜻.
    internal HashSet<ClientSession> Members { get; } = [];

    /// <summary>기본 방(1번, "전체 채팅")인지. 기본 방은 나갈 수 없고 삭제되지 않는다.</summary>
    public bool IsLobby => Id == RoomInfo.LobbyId;

    /// <summary>꽉 찼는지. Members.Count를 읽으므로 lock 안에서만 의미가 있다.</summary>
    internal bool IsFull => MaxUsers > 0 && Members.Count >= MaxUsers;

    /// <summary>RoomManager._lock 안에서 호출해야 인원수가 정확하다.</summary>
    // [왜] Room 객체를 그대로 보내지 않고 RoomInfo(전송용 데이터)로 바꿔 보낸다.
    //      Room에는 ClientSession(소켓 등) 같은 서버 내부 정보가 있어서 JSON으로 보낼 수도 없고 보내서도 안 된다.
    internal RoomInfo ToInfo() => new()
    {
        RoomId = Id,
        Name = Name,
        OwnerNo = Owner?.EmployeeNo,        // [개념] ?. : Owner가 null이면 null, 아니면 .EmployeeNo (null 조건 연산자)
        OwnerName = Owner?.DisplayName,
        MaxUsers = MaxUsers,
        MemberCount = Members.Count,
    };
}
