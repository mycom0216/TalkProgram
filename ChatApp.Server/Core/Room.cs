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

/// <summary>방 멤버 한 명의 상태 (읽음 위치 포함).</summary>
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
    public string? OwnerNo { get; internal set; } = ownerNo;
    public string? OwnerName { get; internal set; } = ownerName;
    public string PinnedNotice { get; internal set; } = "";

    /// <summary>최대 인원 (0 = 제한 없음). 멤버 수 기준. 방장·관리자가 바꿀 수 있다(RoomManager의 lock 안에서만).</summary>
    public int MaxUsers { get; internal set; } = maxUsers;

    /// <summary>비공개 방: 목록에는 멤버에게만 보이고, 초대로만 들어올 수 있다.</summary>
    public bool IsPrivate { get; } = isPrivate;

    /// <summary>멤버 (사원번호 → 상태). 기본 방은 비어 있다.</summary>
    internal Dictionary<string, RoomMember> Members { get; } = [];

    /// <summary>지금 접속해서 이 방에 연결된 세션들.</summary>
    internal HashSet<ClientSession> Online { get; } = [];

    public bool IsLobby => Id == RoomInfo.LobbyId;

    internal bool IsFull => !IsLobby && MaxUsers > 0 && Members.Count >= MaxUsers;

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
        MemberCount = IsLobby ? Online.Count : Members.Count,
        OnlineCount = Online.Count,
        Members = withMembers && !IsLobby ? Members.Values.OrderBy(m => m.DisplayName).Select(m => m.ToInfo()).ToList() : null,
    };
}
