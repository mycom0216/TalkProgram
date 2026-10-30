using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Server.Core;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;

namespace ChatApp.Server.Services;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 친구 목록·친구 찾기·친구 추가/삭제·내 휴대폰 번호 등록.
// [연결]     PacketHandler가 FriendListReq / FriendSearchReq / FriendAdd / FriendRemove / PhoneInfoReq / PhoneUpdate를 이리로 넘긴다.
// [규칙]     - 친구는 한쪽 방향(내 목록에만 추가). 상대 승인은 없다.
//            - 찾기는 아이디(사원번호)·휴대폰 번호가 "정확히" 같을 때만, 사용 중(ACTIVE) 계정만 찾는다.
//            - 휴대폰 번호는 본인 외에는 보여 주지 않는다(검색 결과에는 가린 번호만).
// [스레드]   요청한 사람의 수신 스레드에서 실행된다. 공유 상태가 없고(DB만 사용), 찾기 간격은 세션 필드를 Interlocked로 다룬다.
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   "내 사람들"에 관한 기능: 친구 목록 보기, 아이디·휴대폰 번호로 찾기, 추가·삭제, 프로필 카드, 내 휴대폰 번호 등록. 모두 DB만 쓰는 짧은 요청-응답이다.
//
// ■ 읽는 순서
//   ① SendList(목록 보내기, 다른 함수들이 마지막에 재사용) → ② HandleSearch(가장 규칙이 많음) → ③ HandleAdd/HandleRemove → ④ HandleProfile → ⑤ 휴대폰 번호 두 함수
//
// ■ 설계 포인트
//   - 친구는 "한쪽 방향": 내 목록에만 들어가고 상대 승인이 없다. 대신 상대는 내가 추가한 사실을 알 수 없고, 메시지는 친구 여부와 상관없이 보낼 수 있다.
//   - 휴대폰 번호는 개인정보라서 "정확히 같은 번호"로만 찾아지고, 결과에는 가린 번호(010-****-5678)만 나가며, 번호 전체는 본인에게만 보인다. 로그에도 번호는 남기지 않는다.
//   - 응답 방식이 한결같다: 추가·삭제를 하면 결과 문구와 함께 "최신 친구 목록 전체"를 다시 보낸다(클라이언트가 따로 계산하지 않아도 항상 서버 기준으로 맞는다).
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 정규화(Normalize): 입력 모양이 달라도 같은 값으로 .... "data normalization phone number E.164"
//   - 열거(enumeration) 공격과 요청 간격 제한 ............ "enumeration attack rate limiting"
//   - 개인정보 최소 노출(마스킹) .......................... "PII masking least privilege"
//   - 기본 생성자, 패턴 매칭 is { } online ................ "C# 12 primary constructor property pattern"
//   - Interlocked.Read / Exchange ......................... "Interlocked Exchange atomic"
//
// ■ 면접 예상 질문과 답
//   Q1. 휴대폰 번호로 친구를 찾는 기능에서 개인정보를 어떻게 지켰나요?
//       A. 부분 검색이 아니라 "정확히 같은 번호"일 때만 찾고, 사용 중 계정만 대상으로 하며, 결과에는 가운데를 가린 번호만 보냅니다. 번호를 바꿔 가며 계속 시도해 남의 번호를 알아내는 것은 1초에 한 번으로 제한해 느리게 만들었습니다. 저장된 전체 번호는 본인 요청에만 돌려줍니다.
//   Q2. 입력된 번호의 모양이 제각각(010-1234-5678, 01012345678, +82 10 ...)이어도 같은 번호로 찾아지게 한 방법은요?
//       A. 저장하기 전에 숫자만 남기고 국가번호(+82)는 0으로 바꾸는 정규화를 합니다. DB에는 정규화된 값만 저장하고 비교하므로 모양이 달라도 같은 번호면 같은 사람이 찾아집니다. 보여 줄 때만 하이픈을 넣습니다(Format).
//   Q3. 찾기 간격 제한이 세션 필드(LastFriendSearchTicks)에 있는 이유는요? 왜 Interlocked를 쓰나요?
//       A. 제한은 "사람(연결)마다" 걸어야 하므로 그 연결 객체에 마지막 시각을 둡니다. 읽기·쓰기가 서로 다른 스레드에서 일어날 수 있어 Interlocked로 안전하게 읽고 씁니다. 시각은 시계를 바꿔도 영향이 없는 TickCount64(부팅 후 경과 시간)를 씁니다.
//   Q4. 친구를 한쪽 방향으로 만든 이유는요?
//       A. 사내 메신저에서는 승인 절차가 오히려 번거롭고, 같은 회사 사람이라 대화 자체는 막을 이유가 없기 때문입니다. 대신 서버는 "사용 중인 계정"만 추가할 수 있게 하고, 최대 1,000명으로 제한해 한 사람이 DB를 부풀리지 못하게 했습니다.
//   Q5. 친구 목록의 접속 상태는 어디서 가져오나요?
//       A. 이름·부서 같은 고정 정보는 DB(ListFriends)에서, "지금 상태"(온라인·회의 중·상태 메시지)는 접속 중인 사람의 메모리 세션에서 덧씌웁니다. 상태는 자주 바뀌므로 DB에 매번 저장하지 않고 접속 중인 세션이 최신값을 갖습니다.
//   Q6. 한계는요? (솔직하게 답하기)
//       A. (1) 아이디(사원번호) 검색은 "사용 중인 계정인지"를 알려 주므로, 사원번호 형식을 아는 사람이 존재 여부를 확인할 수는 있다(사내 서비스라 사원번호가 비밀이 아니라는 전제). (2) 프로필 조회(HandleProfile)에는 별도의 요청 간격 제한이 없어 연달아 요청하면 DB 조회가 많아질 수 있다. (3) 간격 제한은 연결 단위라 여러 연결로 우회할 수 있는데, 서버의 IP별 연결 수 제한이 이를 어느 정도 막는다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - "친구 찾기는 잠시 후 다시" ................ 정상(1초 제한). 값은 FriendRules.SearchIntervalMs
//   - 번호로 찾는데 안 나온다 ................... 상대가 번호를 등록했는지, 정규화 결과가 DB 저장값과 같은지, 상대 계정이 ACTIVE인지
//   - 번호 등록이 "이미 등록된 번호" ............ users 테이블의 번호 UNIQUE 제약(SetPhone이 false)
//   - 친구 목록의 상태가 늦다 ................... 접속자 목록 방송 주기, SendList의 세션 덧씌우기
// ───────────────────────────────────────────────────────────────────────────
// [읽는 법] 기본 생성자로 받은 세 부품을 그대로 쓴다: sessions = 지금 접속 중인 사람 찾기, users = DB, log = 로그. 이 서비스 자체는 상태를 갖지 않는다(필드가 없다) — 그래서 lock이 필요 없다.
public sealed class FriendService(SessionManager sessions, UserRepository users, ServerLogger log)
{
    /// <summary>내 친구 목록 보내기. notice가 있으면 Body에 담아 화면 아래쪽에 보이게 한다.</summary>
    public void SendList(ClientSession session, string? notice = null)
    {
        // [읽는 법] DB에서 내 친구들의 기본 정보를 읽는다. 아래 for에서 "지금 접속 중인" 친구만 메모리의 최신 정보(상태·상태 메시지)로 바꿔 끼운다. is { } online 은 null이 아닐 때만 online 변수에 담는 패턴이다.
        List<UserInfo> friends = users.ListFriends(session.User!.EmployeeNo);
        // 접속 중인 친구는 지금 상태(온라인·회의 중 등)와 상태 메시지를 덧씌운다
        for (int i = 0; i < friends.Count; i++)
            if (sessions.FindByEmployeeNo(friends[i].EmployeeNo)?.User is { } online) friends[i] = online;
        session.Send(Packet.Create(PacketType.FriendList, notice).WithData(friends));
    }

    public void HandleSearch(ClientSession session, Packet packet)
    {
        UserInfo me = session.User!;
        FriendSearchRequest? req = packet.GetData<FriendSearchRequest>();
        string query = req?.Query.Trim() ?? "";
        if (req is null || query.Length == 0 || query.Length > 30)
        {
            SendError(session, "찾을 아이디나 휴대폰 번호를 입력하세요.");
            return;
        }
        // [왜] 번호를 하나씩 바꿔 가며 계속 찾으면 다른 사람의 번호를 알아낼 수 있다 → 1초에 한 번만
        long now = Environment.TickCount64;
        // [읽는 법] 지금 시각 − 마지막 찾기 시각 < 1초면 거절한다. 통과하면 바로 아래에서 마지막 시각을 지금으로 바꿔 둔다(Exchange = 값을 바꾸고 이전 값을 돌려주는 원자 연산). 거절된 요청도 DB를 건드리지 않아 비용이 거의 없다.
        if (now - Interlocked.Read(ref session.LastFriendSearchTicks) < FriendRules.SearchIntervalMs)
        {
            SendError(session, "친구 찾기는 잠시 후 다시 해 주세요.");
            return;
        }
        Interlocked.Exchange(ref session.LastFriendSearchTicks, now);

        var result = new FriendSearchResult { By = req.By, Query = query };
        if (req.By == FriendSearchBy.Phone)
        {
            // [읽는 법] 입력을 숫자만 남긴 표준 모양("01012345678")으로 바꾼다. 휴대폰 번호 형식(01X로 시작, 10~11자리)이 아니면 null이라 "형식이 올바르지 않습니다"로 끝난다. 표준 모양끼리 비교하므로 입력 모양이 달라도 같은 번호면 찾아진다.
            string? phone = PhoneRules.Normalize(query);
            if (phone is null)
            {
                SendError(session, "휴대폰 번호 형식이 올바르지 않습니다. (예: 010-1234-5678)");
                return;
            }
            // [읽는 법] "사용 중(ACTIVE)인 계정"만 찾는다 — 정지·퇴사·승인 대기 계정은 번호가 같아도 나오지 않는다. 찾았을 때만 가린 번호(MaskedPhone)를 결과에 넣는다.
            result.User = users.FindActiveByPhone(phone);
            if (result.User is not null) result.MaskedPhone = PhoneRules.Mask(phone);
        }
        else
        {
            result.User = users.FindActiveUser(query); // 사원번호는 대소문자 구분 없이 (DB 기본 정렬 규칙)
        }

        if (result.User is not null)
        {
            result.IsMe = string.Equals(result.User.EmployeeNo, me.EmployeeNo, StringComparison.OrdinalIgnoreCase);
            // [읽는 법] 결과 카드의 버튼(추가/이미 친구/나)을 서버가 판단해서 준다. 자신이면 IsMe, 이미 친구면 AlreadyFriend. "&&"는 앞이 거짓이면 뒤(DB 조회)를 실행하지 않는 단축 평가라 불필요한 조회를 아낀다.
            result.AlreadyFriend = !result.IsMe && users.IsFriend(me.EmployeeNo, result.User.EmployeeNo);
            if (sessions.FindByEmployeeNo(result.User.EmployeeNo)?.User is { } online) result.User = online; // 지금 상태
        }
        session.Send(Packet.Create(PacketType.FriendSearchRes).WithData(result));
    }

    public void HandleAdd(ClientSession session, Packet packet)
    {
        UserInfo me = session.User!;
        string no = packet.Body?.Trim() ?? "";
        UserInfo? target = no.Length == 0 ? null : users.FindActiveUser(no);
        if (target is null)
        {
            SendError(session, "사용 중인 계정이 아니라 친구로 추가할 수 없습니다.");
            return;
        }
        if (string.Equals(target.EmployeeNo, me.EmployeeNo, StringComparison.OrdinalIgnoreCase))
        {
            SendError(session, "나 자신은 친구로 추가할 수 없습니다.");
            return;
        }
        // [읽는 법] 순서: 대상이 사용 중인 계정인지 → 나 자신이 아닌지 → 친구 수 한도 → 추가. AddFriend가 false를 돌려주면 "이미 친구"라는 뜻이라 오류가 아니라 안내 문구로 처리한다(같은 요청을 반복해도 결과가 같은 멱등성).
        if (users.CountFriends(me.EmployeeNo) >= FriendRules.MaxFriends)
        {
            SendError(session, $"친구는 최대 {FriendRules.MaxFriends}명까지 추가할 수 있습니다.");
            return;
        }
        bool added = users.AddFriend(me.EmployeeNo, target.EmployeeNo);
        if (added) log.Info($"친구 추가: {me.EmployeeNo} → {target.EmployeeNo}");
        SendList(session, added ? $"{target.DisplayName}님을 친구로 추가했습니다." : $"{target.DisplayName}님은 이미 친구입니다.");
    }

    public void HandleRemove(ClientSession session, Packet packet)
    {
        UserInfo me = session.User!;
        string no = packet.Body?.Trim() ?? "";
        if (no.Length == 0) return;
        if (users.RemoveFriend(me.EmployeeNo, no)) log.Info($"친구 삭제: {me.EmployeeNo} → {no}");
        SendList(session, "친구 목록에서 뺐습니다.");
    }

    /// <summary>
    /// 한 사람의 프로필 (프로필 카드용). 접속 중이면 지금 상태 그대로, 아니면 DB에서 읽고 상태 메시지를 붙인다.
    /// [주의] 휴대폰 번호 같은 개인정보는 넣지 않는다 (UserInfo에는 원래 없다).
    /// </summary>
    public void HandleProfile(ClientSession session, Packet packet)
    {
        string no = packet.Body?.Trim() ?? "";
        if (no.Length is 0 or > 20) return;
        UserInfo? user = sessions.FindByEmployeeNo(no)?.User;
        if (user is null && users.FindActiveUser(no) is { } found)
        {
            // [읽는 법] 접속 중이 아닌 사람의 프로필: DB에서 읽은 기본 정보에 마지막 상태 메시지를 붙인다. 접속 중이면 위의 메모리 값을 그대로 쓴다. 휴대폰 번호는 UserInfo에 원래 들어 있지 않아 실수로 새어 나갈 길이 없다.
            found.StatusMessage = users.TryLoadStatusMessage(found.EmployeeNo) ?? "";
            user = found;
        }
        session.Send(user is null
            ? Packet.Create(PacketType.ProfileRes, "사용하지 않는 계정이라 프로필을 볼 수 없습니다.", target: no, success: false)
            : Packet.Create(PacketType.ProfileRes, target: no).WithData(user));
    }

    /// <summary>내 휴대폰 번호 알려 주기 (본인에게만).</summary>
    public void HandlePhoneInfo(ClientSession session)
    {
        string? phone = users.GetPhone(session.User!.EmployeeNo);
        session.Send(Packet.Create(PacketType.PhoneInfo).WithData(new PhoneInfo { Phone = phone is null ? "" : PhoneRules.Format(phone) }));
    }

    /// <summary>내 휴대폰 번호 등록·변경 (Body가 비어 있으면 지우기).</summary>
    public void HandlePhoneUpdate(ClientSession session, Packet packet)
    {
        UserInfo me = session.User!;
        string input = packet.Body?.Trim() ?? "";
        string? phone = null;
        if (input.Length > 0)
        {
            phone = PhoneRules.Normalize(input);
            if (phone is null)
            {
                session.Send(Packet.Create(PacketType.PhoneInfo, "휴대폰 번호 형식이 올바르지 않습니다. (예: 010-1234-5678)", success: false));
                return;
            }
        }
        // [읽는 법] 번호가 다른 계정에 이미 있으면 DB의 UNIQUE 제약이 막고 false가 돌아온다. "먼저 조회 후 저장" 방식이 아니라 DB가 중복을 거절하게 하는 것이라 두 사람이 동시에 같은 번호를 등록해도 한 명만 성공한다(AuthService 가입과 같은 원리). phone이 null이면 "지우기"다.
        if (!users.SetPhone(me.EmployeeNo, phone))
        {
            session.Send(Packet.Create(PacketType.PhoneInfo, "다른 계정에 이미 등록된 번호입니다.", success: false));
            return;
        }
        log.Info($"휴대폰 번호 {(phone is null ? "삭제" : "등록")}: {me.EmployeeNo}"); // [주의] 번호 자체는 로그에 남기지 않는다
        session.Send(Packet.Create(PacketType.PhoneInfo, phone is null ? "휴대폰 번호를 지웠습니다." : "휴대폰 번호를 저장했습니다.")
            .WithData(new PhoneInfo { Phone = phone is null ? "" : PhoneRules.Format(phone) }));
    }

    private static void SendError(ClientSession session, string message)
        => session.Send(Packet.Create(PacketType.Error, message, success: false));
}
