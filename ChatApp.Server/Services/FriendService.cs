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
public sealed class FriendService(SessionManager sessions, UserRepository users, ServerLogger log)
{
    /// <summary>내 친구 목록 보내기. notice가 있으면 Body에 담아 화면 아래쪽에 보이게 한다.</summary>
    public void SendList(ClientSession session, string? notice = null)
    {
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
        if (now - Interlocked.Read(ref session.LastFriendSearchTicks) < FriendRules.SearchIntervalMs)
        {
            SendError(session, "친구 찾기는 잠시 후 다시 해 주세요.");
            return;
        }
        Interlocked.Exchange(ref session.LastFriendSearchTicks, now);

        var result = new FriendSearchResult { By = req.By, Query = query };
        if (req.By == FriendSearchBy.Phone)
        {
            string? phone = PhoneRules.Normalize(query);
            if (phone is null)
            {
                SendError(session, "휴대폰 번호 형식이 올바르지 않습니다. (예: 010-1234-5678)");
                return;
            }
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
