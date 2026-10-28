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

    /// <summary>최대 인원. 0이면 제한 없음(기본 방).</summary>
    public int MaxUsers { get; set; }
    public int MemberCount { get; set; }     // 보낼 당시의 현재 인원

    // [개념] [JsonIgnore] + 계산 속성: RoomId만 있으면 알 수 있는 값이라 JSON으로 보내지 않는다.
    [JsonIgnore]
    public bool IsLobby => RoomId == LobbyId;

    /// <summary>"3/10" 또는 제한이 없으면 "3명"</summary>
    // [개념] 삼항 연산자(조건 ? A : B)로 제한 없음(0)일 때와 있을 때 표시를 다르게 만든다.
    [JsonIgnore]
    public string CapacityText => MaxUsers == 0 ? $"{MemberCount}명" : $"{MemberCount}/{MaxUsers}";
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
}
