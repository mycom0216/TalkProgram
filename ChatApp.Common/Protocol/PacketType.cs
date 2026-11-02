namespace ChatApp.Common.Protocol;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 역할] 패킷 "종류" 목록(enum). 통신 규약(프로토콜)의 목차라고 보면 된다.
//   각 값 옆 주석에 그 패킷이 어느 방향(클라이언트 → 서버 / 서버 → 클라이언트)으로 가고,
//   Packet의 어느 칸(Target, Body, Data, Bin)을 쓰는지 적어 두었다.
// [연결 관계] 서버 PacketHandler.Handle의 switch, 클라이언트 MainForm.HandlePacket의 switch가
//   이 값을 보고 처리할 함수를 고른다.
// [개념] enum(열거형): 서로 관련된 이름 붙은 상수들의 묶음. 숫자 대신 이름을 써서 코드가 읽기 쉽고 오타를 컴파일러가 잡아 준다.
// [주의] JSON에는 숫자가 아니라 이름 문자열("ChatMsg")로 보낸다(PacketSerializer의 JsonStringEnumConverter).
//   그래서 중간에 새 값을 끼워 넣어도 되지만, 이미 있는 값의 "이름을 바꾸면" 옛 버전과 통신이 깨진다.
// 이름 규칙: ~Req = 요청, ~Res = 응답(Success로 성공/실패 표시)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 서버와 클라이언트가 주고받는 패킷 종류.
/// 새 기능을 추가할 때는 여기에 타입을 추가하고, 서버 PacketHandler에 case를 하나 추가하면 된다.
/// </summary>
public enum PacketType
{
    /// <summary>이 프로그램이 모르는 패킷 종류(더 새 버전이 보낸 것). 무시하면 된다.</summary>
    // 이 프로그램이 모르는 종류(더 새 버전이 보낸 패킷). PacketTypeConverter가 모르는 이름을 이 값으로 바꾼다.
    // [왜] 모르는 이름 때문에 JSON 해석이 실패하면 수신 스레드가 "잘못된 데이터"로 보고 연결을 끊는다.
    //      그러면 새 서버 + 옛 클라이언트 조합에서 접속 → 끊김 → 재접속이 끝없이 반복된다. 모르는 패킷은 그냥 무시하면 된다.
    Unknown,

    // 서버 안내 (로그인 전에도 가능)
    /// <summary>클라이언트 → 서버: 서버 설정을 알려 달라</summary>
    ServerInfoReq,
    /// <summary>Data = ServerInfo (가입 가능한 이메일 도메인 등)</summary>
    ServerInfo,

    // 회원 (사내 메신저: 사원번호 + 비밀번호, MariaDB)
    /// <summary>Data = RegisterRequest</summary>
    RegisterReq,
    /// <summary>Success, Body = 안내 메시지</summary>
    RegisterRes,
    /// <summary>Data = LoginRequest</summary>
    LoginReq,
    /// <summary>Success, Body = 안내 메시지, Data = UserInfo(성공 시)</summary>
    LoginRes,
    /// <summary>Data = TokenLoginRequest. TLS 전용 자동 로그인</summary>
    TokenLoginReq,
    /// <summary>클라이언트 → 서버: 로그아웃(서버는 연결을 닫는다)</summary>
    Logout,

    // 채팅
    /// <summary>전체(이후 단계에서는 방) 채팅</summary>
    ChatMsg,
    /// <summary>귓속말 (Target = 상대 사원번호)</summary>
    Whisper,
    /// <summary>입장/퇴장/공지 등 서버 알림</summary>
    SystemMsg,
    /// <summary>접속자 목록 (Data = List&lt;UserInfo&gt;)</summary>
    UserList,

    // 연결 유지 / 오류
    /// <summary>"살아 있니?" (하트비트용)</summary>
    Ping,
    /// <summary>Ping에 대한 대답</summary>
    Pong,
    /// <summary>서버 → 클라이언트: 요청을 처리할 수 없음 (Body = 이유)</summary>
    Error,

    // 채팅방
    /// <summary>방 목록 요청</summary>
    RoomListReq,
    /// <summary>방 목록 (Data = List&lt;RoomInfo&gt;), 방이 바뀔 때마다 모두에게 전송</summary>
    RoomList,
    /// <summary>방 만들기 (Data = CreateRoomRequest) → 만든 사람은 바로 입장</summary>
    RoomCreate,
    /// <summary>방 입장 (Target = 방 번호). 여러 방에 동시에 참여할 수 있다</summary>
    RoomJoin,
    /// <summary>방 나가기 (Target = 방 번호). 기본 방("전체 채팅")은 나갈 수 없다</summary>
    RoomLeave,
    /// <summary>방 요청 결과 (Success, Body = 안내, Data = 들어간 방 RoomInfo)</summary>
    RoomRes,
    /// <summary>방에서 나갔음 (Target = 방 번호, Body = 안내)</summary>
    RoomLeft,

    // 참고: ChatMsg는 Target = 방 번호, 방 안의 입장·퇴장 SystemMsg도 Target = 방 번호 (공지는 Target 없음)

    // 파일·이미지·동영상 (256KB 조각으로 나눠 보내 채팅이 멈추지 않게 한다. 모든 패킷의 Target = 파일 ID)
    // [왜] 3GB 파일을 패킷 하나로 보내면 그동안 다른 패킷(채팅)이 전혀 못 지나간다.
    //   작은 조각으로 나누면 조각 사이사이에 채팅 패킷이 끼어 갈 수 있다.
    // 흐름(올리기):   FileUploadReq → FileUploadRes(허락) → FileChunk × N → FileUploadEnd → (서버) FileMsg
    // 흐름(내려받기): FileDownloadReq → FileDownloadRes(시작) → FileChunk × N → FileDownloadEnd
    /// <summary>올리기 시작 요청 (Data = FileOffer)</summary>
    FileUploadReq,
    /// <summary>올리기 허락/실패 (Target = 파일 ID, Success, Body = 실패 이유). 처리 도중 실패해도 이 패킷으로 알린다</summary>
    FileUploadRes,
    /// <summary>파일 조각 (Bin = 바이트). 올리기·내려받기 모두 사용</summary>
    FileChunk,
    /// <summary>다 보냈음 → 서버가 저장을 마치고 FileMsg를 보낸다</summary>
    FileUploadEnd,
    /// <summary>파일 메시지 (Data = SharedFile). 방이면 Target = 방 번호, 1:1이면 Target = 받는 사람 사원번호</summary>
    FileMsg,
    /// <summary>내려받기 요청</summary>
    FileDownloadReq,
    /// <summary>내려받기 시작(Success, Data = SharedFile) 또는 실패(Body = 이유)</summary>
    FileDownloadRes,
    /// <summary>내려받기 끝</summary>
    FileDownloadEnd,
    /// <summary>올리기 취소 (클라이언트 → 서버, Target = 파일 ID). 서버는 임시 파일과 DB 행을 지운다</summary>
    FileUploadCancel,
    /// <summary>내려받기 취소 (클라이언트 → 서버, Target = 파일 ID). 서버의 내려받기 스레드가 멈춘다</summary>
    FileDownloadCancel,
    /// <summary>방 파일 보관함의 과거 페이지 요청 (Data = RoomFilesRequest)</summary>
    RoomFilesReq,
    /// <summary>파일 목록 페이지 (Data = RoomFilesBatch)</summary>
    RoomFilesRes,
    // 참고: 내려받기 관련 패킷(FileDownloadReq/Res/Chunk/End)의 Body = 요청 ID.
    //       취소 직후 같은 파일을 다시 받을 때, 앞 요청의 늦게 도착한 조각이 새 파일에 섞이지 않게 구분하는 용도다.

    // 대화 기록 (DB에 저장된 지난 메시지)
    /// <summary>1:1 지난 대화 요청 (Target = 상대 사원번호). 방은 입장할 때 서버가 알아서 보낸다</summary>
    HistoryReq,
    /// <summary>지난 대화 묶음 (Data = HistoryBatch). Last = true가 올 때까지 여러 개 올 수 있다</summary>
    History,
    // 상태 표시
    /// <summary>내 상태·상태 메시지 바꾸기 (Data = StatusUpdate). 서버는 바뀐 접속자 목록(UserList)을 모두에게 보낸다</summary>
    StatusUpdate,

    /// <summary>서버 → 클라이언트: 강제 로그아웃 (Body = 이유). 받은 클라이언트는 자동으로 다시 접속하지 않는다.</summary>
    // 강제 로그아웃 (Body = 이유). 같은 사원번호로 다른 곳에서 로그인했을 때 이전 연결에 보낸다.
    // 받은 클라이언트는 자동 재접속을 하지 않는다(두 PC가 서로를 계속 밀어내는 일을 막기 위해).
    ForceLogout,

    /// <summary>클라이언트 → 서버: 비밀번호 변경 요청 (Data = ChangePasswordRequest).</summary>
    // 비밀번호 변경 (Data = ChangePasswordRequest → ChangePasswordRes: Success, Body = 안내)
    // 관리자가 비밀번호를 초기화한 계정은 로그인 직후 이것만 할 수 있다(UserInfo.MustChangePassword).
    ChangePasswordReq,
    /// <summary>서버 → 클라이언트: 비밀번호 변경 결과 (Success, Body = 안내).</summary>
    ChangePasswordRes,

    // 관리자 (Role = ADMIN만. 서버가 매번 권한을 다시 확인한다)
    /// <summary>회원 목록 요청 (Body = 상태 필터: PENDING/ACTIVE/SUSPENDED/RESIGNED, 비우면 전체)</summary>
    AdminUserListReq,
    /// <summary>회원 목록 (Data = List&lt;AdminUserInfo&gt;)</summary>
    AdminUserList,
    /// <summary>관리 동작 (Data = AdminAction: 승인·정지·퇴사·잠금 해제·비밀번호 초기화 등)</summary>
    AdminActionReq,
    /// <summary>결과 (Success, Body = 안내, Data = AdminActionResult: 임시 비밀번호 등)</summary>
    AdminActionRes,
    /// <summary>감사 로그 조회 (Data = AuditQuery)</summary>
    AdminAuditReq,
    /// <summary>감사 로그 (Data = List&lt;AuditEntry&gt;)</summary>
    AdminAudit,
    /// <summary>서버 → 접속 중인 관리자: 알림 (Body = 내용. 예: 새 가입 신청). 관리자 창이 열려 있으면 목록을 새로 고친다</summary>
    AdminNotify,
    /// <summary>관리자 통계 요청</summary>
    AdminStatsReq,
    /// <summary>AdminStats 데이터</summary>
    AdminStats,

    // 채팅방 멤버 (방은 서버를 재시작해도 남고, 멤버는 접속을 끊어도 멤버로 남는다)
    /// <summary>방에 초대 (Data = RoomInviteRequest). 비공개 방은 초대로만 들어갈 수 있다</summary>
    RoomInvite,
    /// <summary>방장 넘기기 (Target = 방 번호, Body = 새 방장 사원번호). 방장 또는 관리자만</summary>
    RoomTransferOwner,
    /// <summary>방 상단 공지 고정/수정/삭제 (Data = RoomNoticeRequest). 방장 또는 관리자만</summary>
    RoomNotice,
    /// <summary>방 삭제 (Target = 방 번호). 방장 또는 관리자만. 모든 멤버가 빠지고 방이 닫힌다(대화 기록은 남음)</summary>
    RoomDelete,
    /// <summary>방 정보·멤버가 바뀜 (Data = RoomInfo + Members). 방에 있는 사람에게 보낸다</summary>
    RoomUpdate,
    /// <summary>회사 사람 전체 목록 요청 (초대할 사람 고르기)</summary>
    DirectoryReq,
    /// <summary>사람 목록 (Data = List&lt;UserInfo&gt;, 접속하지 않은 사람 포함)</summary>
    Directory,

    // 메시지 기능 (ChatMsg·Whisper 패킷의 MessageId로 어느 메시지인지 가리킨다)
    /// <summary>내 메시지 삭제 요청 (Data = MessageRef)</summary>
    MessageDelete,
    /// <summary>삭제됨 알림 (Data = MessageRef) → 받은 사람 화면에서 "삭제된 메시지입니다"로 바꾼다</summary>
    MessageDeleted,
    /// <summary>공감 누르기/취소 (Data = ReactionRequest) - 같은 공감을 다시 누르면 취소</summary>
    Reaction,
    /// <summary>공감 현황 (Data = ReactionUpdate)</summary>
    ReactionUpdate,
    /// <summary>여기까지 읽었음 (Data = ReadMark). 대화창을 보고 있을 때 클라이언트가 보낸다</summary>
    ReadMark,
    /// <summary>누가 어디까지 읽었는지 (Data = ReadUpdate) → 메시지 옆 "안 읽은 사람 수"가 줄어든다</summary>
    ReadUpdate,
    /// <summary>서버에 저장된 대화 검색 (Data = SearchRequest)</summary>
    SearchReq,
    /// <summary>검색 결과 (Data = SearchResult)</summary>
    SearchRes,
    /// <summary>로그인 직후: 접속하지 않은 동안 받은 1:1 메시지 요약 (Data = List&lt;UnreadDirect&gt;)</summary>
    UnreadDirect,
    /// <summary>메시지 수정 요청 (Data = EditMessageRequest). 본인 메시지만 5분 내 수정</summary>
    MessageEditReq,
    /// <summary>수정된 메시지 전파 (Body = 새 내용, Data = MessageRef)</summary>
    MessageEdited,
    /// <summary>입력 중 상태 (Data = TypingRequest)</summary>
    Typing,
    /// <summary>입력 중 상태 전달 (Data = TypingInfo)</summary>
    TypingUpdate,
    /// <summary>방 정원 바꾸기 (Target = 방 번호, Body = 새 정원). 방장 또는 관리자만. 결과는 RoomUpdate</summary>
    RoomCapacity,

    // 친구 (한쪽 방향: 내 목록에만 추가)
    /// <summary>내 친구 목록 요청</summary>
    FriendListReq,
    /// <summary>내 친구 목록 (Data = List&lt;UserInfo&gt;, Body = 안내 문구: 추가·삭제 결과)</summary>
    FriendList,
    /// <summary>아이디(사원번호)·휴대폰 번호로 찾기 (Data = FriendSearchRequest). 정확히 일치할 때만</summary>
    FriendSearchReq,
    /// <summary>찾기 결과 (Data = FriendSearchResult)</summary>
    FriendSearchRes,
    /// <summary>친구 추가 (Body = 사원번호) → FriendList</summary>
    FriendAdd,
    /// <summary>친구 삭제 (Body = 사원번호) → FriendList</summary>
    FriendRemove,
    /// <summary>내 휴대폰 번호 요청 (본인만)</summary>
    PhoneInfoReq,
    /// <summary>내 휴대폰 번호 등록·변경 (Body = 번호, 빈 문자열 = 지우기) → PhoneInfo</summary>
    PhoneUpdate,
    /// <summary>내 휴대폰 번호 (Data = PhoneInfo, 실패하면 Success=false + Body)</summary>
    PhoneInfo,

    // 프로필 사진
    /// <summary>내 사진 바꾸기 (Data = PhotoInfo{Kind}: 프로필 160×160 / 배경 720×960, Bin 비어 있으면 지우기)</summary>
    ProfilePhotoUpdate,
    /// <summary>다른 사람 사진 받기 (Body = 사원번호, Data = PhotoInfo{Kind})</summary>
    ProfilePhotoReq,
    /// <summary>사진 (Target = 사원번호, Data = PhotoInfo{Kind, Version}, Bin = 사진 / 실패하면 Success=false + Body)</summary>
    ProfilePhoto,
    /// <summary>한 사람의 프로필 정보 요청 (Body = 사원번호) — 접속하지 않은 사람의 프로필 카드를 열 때</summary>
    ProfileReq,
    /// <summary>프로필 정보 (Target = 사원번호, Data = UserInfo / 없는 계정이면 Success=false + Body)</summary>
    ProfileRes,
}
