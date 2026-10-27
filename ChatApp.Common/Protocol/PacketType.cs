namespace ChatApp.Common.Protocol;

/// <summary>
/// 서버와 클라이언트가 주고받는 패킷 종류.
/// 새 기능을 추가할 때는 여기에 타입을 추가하고, 서버 PacketHandler에 case를 하나 추가하면 된다.
/// </summary>
public enum PacketType
{
    // 접속 / 인증 (1단계: 닉네임만으로 로그인, 4단계에서 DB 회원가입·로그인으로 교체)
    LoginReq,
    LoginRes,
    Logout,

    // 채팅
    ChatMsg,      // 전체(이후 단계에서는 방) 채팅
    Whisper,      // 귓속말
    SystemMsg,    // 입장/퇴장/공지 등 서버 알림
    UserList,     // 접속자 목록 (Body = "닉1,닉2,...")

    // 연결 유지 / 오류
    Ping,
    Pong,
    Error,

    // TODO(4단계): RegisterReq, RegisterRes, RoomListReq, RoomList, RoomCreate, RoomJoin, RoomLeave, RoomRes
    // TODO(5단계): FileOffer, FileAccept, FileReject, FileChunk, FileEnd
}
