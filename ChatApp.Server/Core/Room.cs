using ChatApp.Common.Models;

namespace ChatApp.Server.Core;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 채팅방 하나의 상태.
// [핵심] 멤버(Members)와 접속 중인 연결(Online)을 따로 관리한다.
//        - Members: 이 방의 멤버 (카카오톡 단체방처럼 접속을 끊어도 멤버로 남는다). DB room_members와 같다.
//        - Online : 지금 접속해 있는 멤버들의 연결. 실시간 메시지는 여기로 보낸다.
//        기본 방("전체 채팅")은 예외: 멤버 목록 없이 로그인한 모두가 Online에 들어간다.
// [스레드] 모든 필드는 RoomManager의 lock 안에서만 읽고 쓴다. (이 클래스 자체에는 lock이 없다)
// [연결]   RoomManager가 만들고 보관 / ClientSession.JoinedRooms가 참조 / ToInfo()로 클라이언트에 보낼 RoomInfo를 만든다.
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   방 한 개의 "메모리 상태" 상자. 멤버(Members, 접속과 무관), 접속 중인 연결(Online), 방장·공지·정원·공개 여부를 담고, 클라이언트에 보낼 정보(RoomInfo)로 바꿔 주는 일만 한다.
//
// ■ 핵심: 이 클래스에는 lock이 없다
//   모든 변경은 RoomManager가 lock을 잡은 상태에서만 일어난다. 그래서 필드·컬렉션을 internal로 제한해(같은 프로젝트의 RoomManager만 접근) 바깥에서 실수로 직접 고치지 못하게 했다.
//   "데이터는 Room, 동시성 규칙은 RoomManager" — 책임을 나눈 구조다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - internal / public 접근 수준 ............................. "C# access modifiers internal"
//   - 컬렉션 식 [] , HashSet vs Dictionary ..................... "HashSet Dictionary difference C#"
//   - { get; internal set; } 비대칭 접근 .................... "property different accessibility getter setter"
//   - 도메인 모델과 DTO(RoomInfo) 분리 ....................... "domain model DTO separation"
//
// ■ 면접 예상 질문과 답
//   Q1. 멤버(Members)와 접속자(Online)를 나눈 이유는요?
//       A. 단체방은 접속을 끊어도 멤버로 남아야 하고(DB와 동일), 실시간 메시지는 지금 접속한 연결에만 보내면 되기 때문입니다. 한 컬렉션에 섞으면 "오프라인 멤버에게 보내려다 오류" 같은 일이 생기므로 사원번호 기준의 Dictionary(멤버)와 세션 기준의 HashSet(접속자)으로 분리했습니다.
//   Q2. 이 클래스에 lock을 넣지 않은 이유는요?
//       A. 입장·나가기·방장 변경은 여러 필드를 함께 바꾸는 한 덩어리 작업이라, Room마다 lock을 두면 두 방을 동시에 건드리는 작업에서 교착(데드락) 위험이 생깁니다. RoomManager의 lock 하나로 모든 방의 변경을 묶고, Room의 변경용 멤버는 internal로 숨겼습니다.
//   Q3. Room을 그대로 클라이언트에 보내지 않고 RoomInfo로 바꾸는 이유는요?
//       A. Room에는 서버 내부 정보(접속 세션 목록 등)가 있어 JSON으로 보낼 수 없고 보내서도 안 됩니다. 화면에 필요한 값만 담은 전달용 객체(RoomInfo)로 복사해 보내고, 멤버 목록은 요청할 때만(withMembers) 담아 패킷 크기를 줄입니다.
//   Q4. 기본 방(전체 채팅)은 왜 예외로 다루나요?
//       A. 로그인한 모두가 들어가므로 멤버 목록을 따로 관리하면 사람 수만큼 DB 행이 생깁니다. 그래서 Members는 비워 두고 Online만 쓰며, 인원 수는 접속자 수로 보여 주고(IsLobby 분기) 읽음 표시 같은 기능도 끕니다.
//   Q5. 한계는요? (솔직하게 답하기)
//       A. 방마다 lock이 아니라 전역 lock 하나라서, 방이 매우 많고 동시 작업이 많아지면 lock 경합이 병목이 될 수 있다. 지금 규모(수천 명)에서는 작업이 아주 짧아 문제가 없지만, 커지면 방 단위 lock으로 쪼개되 두 방을 동시에 잠글 때의 순서 규칙을 정해야 한다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 방 인원수가 이상하다 ..................... ToInfo: 기본 방은 Online.Count, 단체방은 Members.Count
//   - 멤버인데 실시간 메시지가 안 온다 .......... Online에 연결이 올라갔는지(RoomManager.Attach), 재접속 때 AttachOnLogin
//   - 정원이 안 맞다 ........................... IsFull(Members.Count >= MaxUsers), DB max_users와 메모리 값 동기화
// ───────────────────────────────────────────────────────────────────────────

/// <summary>방 멤버 한 명의 상태 (읽음 위치 포함).</summary>
// [읽는 법] 멤버 한 명의 상태. EmployeeNo와 JoinedAt은 get만 있어 만든 뒤 바뀌지 않는다(사원번호는 키, 입장 시각은 기록 열람 기준). DisplayName과 LastReadAt은 set이 있어 이름 변경·읽음 위치 갱신 때 바뀐다. DateTime?의 ?는 "아직 읽은 적 없으면 null".
public sealed class RoomMember(string employeeNo, string displayName, DateTime joinedAt, DateTime? lastReadAt)
{
    public string EmployeeNo { get; } = employeeNo;
    public string DisplayName { get; set; } = displayName;
    public DateTime JoinedAt { get; } = joinedAt;
    public DateTime? LastReadAt { get; set; } = lastReadAt;

    public RoomMemberInfo ToInfo() => new()
    {
        EmployeeNo = EmployeeNo, DisplayName = DisplayName, JoinedAt = JoinedAt, LastReadAt = LastReadAt,
    };
}

public sealed class Room(int id, string name, string? ownerNo, string? ownerName, int maxUsers, bool isPrivate)
{
    public int Id { get; } = id;
    public string Name { get; } = name;

    /// <summary>방장 (처음엔 만든 사람, 위임하면 바뀐다. 기본 방은 null). RoomManager의 lock 안에서만 바꾼다.</summary>
    // [개념] { get; internal set; } = 읽기는 누구나(public), 쓰기는 같은 프로젝트 안에서만(internal). 방장 변경은 RoomManager(lock 안)만 하게 해, 바깥에서 lock 없이 바꾸는 실수를 컴파일러가 막아 준다.
    public string? OwnerNo { get; internal set; } = ownerNo;
    public string? OwnerName { get; internal set; } = ownerName;
    public string PinnedNotice { get; internal set; } = "";

    /// <summary>최대 인원 (0 = 제한 없음). 멤버 수 기준. 방장·관리자가 바꿀 수 있다(RoomManager의 lock 안에서만).</summary>
    public int MaxUsers { get; internal set; } = maxUsers;

    /// <summary>비공개 방: 목록에는 멤버에게만 보이고, 초대로만 들어올 수 있다.</summary>
    public bool IsPrivate { get; } = isPrivate;

    /// <summary>멤버 (사원번호 → 상태). 기본 방은 비어 있다.</summary>
    // [읽는 법] 사원번호 → 멤버 상태. 키로 찾기가 빠르고 중복 입장이 불가능하다. = []는 빈 컬렉션 식으로 처음부터 비어 있는 Dictionary를 만든다. 기본 방은 이 목록이 항상 비어 있다.
    internal Dictionary<string, RoomMember> Members { get; } = [];

    /// <summary>지금 접속해서 이 방에 연결된 세션들.</summary>
    // [읽는 법] 지금 접속해서 이 방에 연결된 세션들의 집합. HashSet은 같은 세션이 두 번 들어가지 않고(중복 무시) 넣고 빼기가 빠르다. 실시간 방송은 이 집합을 순회해 보낸다(순회 전에 복사본을 떠서 보낸다).
    internal HashSet<ClientSession> Online { get; } = [];

    public bool IsLobby => Id == RoomInfo.LobbyId;

    // [읽는 법] 정원이 찼는지: 기본 방이 아니고, 정원이 0보다 크고(0 = 제한 없음), 멤버 수가 정원 이상이면 true. "멤버 수" 기준이라 접속을 끊은 멤버도 자리를 차지한다.
    internal bool IsFull => !IsLobby && MaxUsers > 0 && Members.Count >= MaxUsers;

    // [읽는 법] 기본 방은 로그인한 모두가 멤버로 취급되어 항상 true, 단체방은 Members에 있어야 true. 권한 확인(메시지 보내기·파일 올리기·내려받기)이 모두 이 규칙을 쓴다.
    internal bool IsMember(string employeeNo) => IsLobby || Members.ContainsKey(employeeNo);

    /// <param name="withMembers">true면 멤버 목록·읽음 위치를 함께 담는다 (입장 결과·방 변경 알림용).</param>
    internal RoomInfo ToInfo(bool withMembers = false) => new()
    {
        RoomId = Id,
        Name = Name,
        OwnerNo = OwnerNo,
        OwnerName = OwnerName,
        PinnedNotice = PinnedNotice,
        MaxUsers = MaxUsers,
        IsPrivate = IsPrivate,
        // 기본 방은 멤버 목록이 없으므로 "지금 접속한 사람 수"를 인원으로 보여 준다
        // [읽는 법] 화면의 "인원" 숫자. 기본 방은 멤버 목록이 없으므로 접속자 수로 대신 보여 준다. 아래 OnlineCount는 두 경우 모두 접속자 수다.
        MemberCount = IsLobby ? Online.Count : Members.Count,
        OnlineCount = Online.Count,
        // [읽는 법] 멤버 목록은 요청한 경우(withMembers)이고 기본 방이 아닐 때만 담는다. 이름순으로 정렬한 뒤 각 멤버를 전달용 객체로 바꿔 리스트로 만든다(OrderBy → Select → ToList는 LINQ 파이프라인). 목록 방송처럼 자주 보내는 패킷에는 null을 담아 크기를 줄인다.
        Members = withMembers && !IsLobby ? Members.Values.OrderBy(m => m.DisplayName).Select(m => m.ToInfo()).ToList() : null,
    };
}
