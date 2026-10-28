using System.Text.Json.Serialization;

namespace ChatApp.Common.Models;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 역할] 채팅방 관련 데이터 모양(DTO).
//   - RoomInfo: 방 목록·입장 응답으로 서버 → 클라이언트에 보내는 방 정보(스냅샷)
//   - CreateRoomRequest: 방 만들기 요청(클라이언트 → 서버) + 이름·인원 규칙 상수
// [연결 관계] 서버 Room(실제 방 객체, 멤버 목록 보유).ToInfo()가 RoomInfo를 만든다.
//   클라이언트 MainForm은 RoomInfo로 방 목록 카드와 대화창 머리를 그린다.
// [왜] 서버의 Room을 그대로 보내지 않는 이유: Room에는 접속 세션 목록(소켓) 같은 서버 내부 객체가 들어 있다.
//   보낼 값만 골라 담은 RoomInfo를 따로 두면 안전하고 패킷도 작다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>채팅방 정보 (방 목록, 입장 응답에 사용).</summary>
public sealed class RoomInfo
{
    /// <summary>로그인하면 자동으로 들어가는 기본 방("전체 채팅")의 번호. 삭제되지 않는다.</summary>
    // [개념] const: 컴파일할 때 값이 박히는 상수. 서버와 클라이언트가 같은 값(1)을 쓰도록 Common에 둔다.
    public const int LobbyId = 1;

    public int RoomId { get; set; }          // DB(rooms 테이블)의 AUTO_INCREMENT 번호
    public string Name { get; set; } = "";
    public string? OwnerNo { get; set; }     // 방장 사원번호 (기본 방은 null)
    public string? OwnerName { get; set; }   // 방장 표시 이름
    public string PinnedNotice { get; set; } = "";

    /// <summary>최대 인원. 0이면 제한 없음(기본 방).</summary>
    public int MaxUsers { get; set; }
    public int MemberCount { get; set; }     // 멤버 수 (접속하지 않은 멤버 포함). 기본 방은 지금 접속한 사람 수
    public int OnlineCount { get; set; }     // 지금 접속 중인 멤버 수

    /// <summary>true = 비공개 방. 목록에는 멤버에게만 보이고, 초대로만 들어갈 수 있다.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>멤버 목록과 읽음 위치. 입장 결과(RoomRes)·방 변경(RoomUpdate)에만 담고, 방 목록 방송에는 넣지 않는다(크기 절약).</summary>
    public List<RoomMemberInfo>? Members { get; set; }

    // [개념] [JsonIgnore] + 계산 속성: RoomId만 있으면 알 수 있는 값이라 JSON으로 보내지 않는다.
    [JsonIgnore]
    public bool IsLobby => RoomId == LobbyId;

    /// <summary>"3/10" 또는 제한이 없으면 "3명"</summary>
    // [개념] 삼항 연산자(조건 ? A : B)로 제한 없음(0)일 때와 있을 때 표시를 다르게 만든다.
    [JsonIgnore]
    public string CapacityText => MaxUsers == 0 ? $"{MemberCount}명" : $"{MemberCount}/{MaxUsers}";
}

/// <summary>채팅방 상단에 고정할 공지. 빈 글이면 기존 공지를 지운다.</summary>
public sealed class RoomNoticeRequest
{
    public int RoomId { get; set; }
    public string Text { get; set; } = "";
    public const int MaxLength = 200;
}

/// <summary>방 만들기 요청 (클라이언트 → 서버, Packet.Data).</summary>
// [왜] 규칙 상수를 Common에 두면 클라이언트 입력창(CreateRoomForm)과 서버 검사(PacketHandler)가
//   항상 같은 기준을 쓴다. 한 곳만 고치면 양쪽이 같이 바뀐다.
public sealed class CreateRoomRequest
{
    public const int MinUsers = 2;
    // [주의] 1000명이라 DB 컬럼 rooms.max_users를 SMALLINT(최대 65535)로 바꿨다. TINYINT(최대 255)면
    //   "Out of range value" DB 오류가 났었다.
    public const int MaxUsersLimit = 1000;
    public const int MaxNameLength = 30;

    public string Name { get; set; } = "";
    public int MaxUsers { get; set; } = 10;

    /// <summary>true = 비공개 방 (초대한 사람만 들어올 수 있음).</summary>
    public bool IsPrivate { get; set; }

    /// <summary>만들면서 바로 초대할 사람들 (선택).</summary>
    public List<string> InviteNos { get; set; } = [];
}

/// <summary>방 멤버 한 명 (RoomRes·RoomUpdate의 RoomInfo.Members). 읽음 표시 계산에 쓴다.</summary>
public sealed class RoomMemberInfo
{
    public string EmployeeNo { get; set; } = "";
    public string DisplayName { get; set; } = "";   // "홍길동 대리"

    /// <summary>이 사람이 이 방에서 마지막으로 읽은 메시지 시각. 아직 읽은 적 없으면 null.</summary>
    public DateTime? LastReadAt { get; set; }

    /// <summary>방에 들어온 시각. 들어오기 전 메시지는 이 사람의 "안 읽음"에 세지 않는다.</summary>
    public DateTime JoinedAt { get; set; }
}

/// <summary>방 초대 (RoomInvite의 Data). 방 멤버만 초대할 수 있다.</summary>
public sealed class RoomInviteRequest
{
    public const int MaxPerRequest = 50;

    public int RoomId { get; set; }
    public List<string> EmployeeNos { get; set; } = [];
}
