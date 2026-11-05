using System.ComponentModel;
using System.Diagnostics;
using System.Media;
using System.Net.Sockets;
using System.Security.Authentication;
using ChatApp.Client.Chat;
using ChatApp.Client.Network;
using ChatApp.Client.Ui;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Security;
using ChatApp.Common.Transfer;

namespace ChatApp.Client.Forms;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] 동료·친구 목록과 채팅방 목록 화면을 만드는 코드(RefreshUserList, RefreshRoomList)와 안 읽은 수 조회.
// MainForm은 C#의 partial class(클래스를 여러 파일로 나누는 기능)로 쪼개 두었다.
// 이 파일도 MainForm.cs와 "같은 클래스"다. 필드(_client, _me 등)와 메서드를 파일 구분 없이 그대로 쓴다.
// 나눈 기준은 "기능별"이고, 동작은 나누기 전과 완전히 같다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   메모리에 있는 데이터(_users, _friends, _rooms, _conversations)를 "화면용 카드 목록(CardItem)"으로 바꿔 CardListView에 통째로 넘긴다. 데이터 → 화면 변환만 하는 순수한 "그리기 준비" 코드.
//
// ■ 동료 탭에 보이는 순서 (RefreshUserList)
//   ① 친구(접속 중이 위, 접속 안 한 친구는 회색) → ② 접속 중인 동료를 부서별로(친구는 중복해서 안 나옴) → ③ 접속하지 않았지만 대화 중인 사람
//   ※ _users는 "지금 접속 중인 사람"이다(서버 UserList). 친구는 접속 여부와 관계없이 _friends에서 온다.
//   접속 중 친구는 onlineByNo에서 최신 정보(live)를 가져와 표시하고, 없으면 친구 목록의 정보(friend)로 표시한다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - LINQ GroupBy/OrderBy/ToDictionary/ToHashSet ....... "LINQ GroupBy ToDictionary ToHashSet"
//   - 대소문자 무시 비교(StringComparer.OrdinalIgnoreCase) .. "StringComparer OrdinalIgnoreCase dictionary key"
//   - 화면용 모델(ViewModel) 분리: CardItem ............... "view model vs domain model WinForms"
//   - 전체 다시 만들기 vs 변경분만 갱신 ................... "rebuild list vs incremental update"
//   - 접기/펼치기 상태는 컨트롤이 기억(_collapsed) ......... "collapsible group header list state"
//
// ■ 면접 예상 질문과 답
//   Q1. 목록이 바뀔 때마다 통째로 다시 만드는데 비효율적이지 않나요?
//       A. 사내 규모(수백~수천 명)에서는 한 번 만드는 데 수 ms라 문제가 없고, 코드가 단순합니다("데이터가 진실, 화면은 그 결과"). 접속자 목록이 올 때마다, 안 읽은 수가 바뀔 때마다 호출되는데 사진 도착처럼 연달아 오는 경우는 150ms 디바운스 타이머가 한 번으로 묶습니다. 수만 명 규모라면 변경된 항목만 갱신하는 방식(가상화·증분 갱신)이 필요합니다.
//   Q2. 접기/펼치기 상태는 목록을 다시 만들어도 유지되나요?
//       A. 상태는 목록(CardItem)이 아니라 CardListView가 부서 키("dept:이름")로 기억합니다(_collapsed). 그래서 SetItems로 목록을 새로 넣어도 접힌 부서는 접힌 채로 남습니다. 키를 항목이 아닌 안정적인 문자열로 둔 덕분입니다.
//   Q3. 사원번호 비교에 OrdinalIgnoreCase를 쓰는 이유는요?
//       A. 서버·로그인·친구 목록 등 경로에 따라 대소문자가 달라질 수 있어("a0001" vs "A0001") 같은 사람이 둘로 갈라지는 것을 막기 위한 방어입니다. 사전과 집합을 만들 때 비교자를 같이 넘겨야 Contains/조회에도 적용됩니다.
//   Q4. 같은 사람이 친구이면서 접속 중이면 두 번 나오나요?
//       A. 아닙니다. 친구 구간에서 한 번만 보여 주고, 부서별 목록은 friendNos에 없는 사람만 보여 줍니다. 접속하지 않은 대화 상대 구간도 친구와 접속 중인 사람은 제외합니다. 세 구간이 서로 겹치지 않게 집합으로 걸러내는 것이 핵심입니다.
//   Q5. 한계는요? (솔직하게 답하기)
//       A. (1) 세 구간의 CardItem 만드는 코드가 거의 같아 반복이 많다(공통 함수로 뽑을 수 있음). (2) 호출 빈도가 높은 편이라(메시지 올 때마다 안 읽은 수 갱신) 목록이 아주 커지면 부담이다. (3) 사진은 PhotoKey로 CardListView가 AvatarCache에서 그리는 시점에 가져오는 구조라 여기서는 사진을 다루지 않는다.
//
// ■ 문제가 났을 때 어디를 볼까
//   - 친구가 목록에 안 보인다 .............. FriendList 수신(HandlePacket), _friends 채움, 사원번호 대소문자
//   - 접속 중인데 회색으로 나온다 ........... _users에 있는지(UserList 도착), onlineByNo 키 비교
//   - 안 읽은 숫자가 안 바뀐다 .............. RefreshUserList/RefreshRoomList 호출 위치(AddMessage, 읽음 처리)
//   - 부서가 접히지 않는다/풀린다 ........... CardListView._collapsed, 머리줄 Key("dept:부서")
//   - 탭 숫자가 이상하다 ................... "동료 N"은 접속 중인 사람(자기 제외) 수
// ───────────────────────────────────────────────────────────────────────────

/// <summary>메인 창(partial) - 동료·친구 목록과 채팅방 목록 화면을 만드는 부분.</summary>
public sealed partial class MainForm
{
    private int UnreadOf(string key) => _conversations.TryGetValue(key, out Conversation? c) ? c.Unread : 0;

    private void RefreshUserList()
    {
        var others = _users.Where(u => u.EmployeeNo != _me.EmployeeNo).ToList();
        var items = new List<CardItem>();

        // ① 친구 (접속 중인 친구가 위, 접속하지 않은 친구는 회색). 친구는 아래 부서 목록에 다시 나오지 않는다.
        // [읽는 법] "사원번호 → 접속 중인 사람" 사전. 같은 사원번호가 혹시 두 번 오더라도 ToDictionary가 예외를 내지 않도록 GroupBy로 묶어 첫 번째만 쓴다(g.First()). 대소문자를 무시하는 비교자를 사전에 같이 넘긴다.
        var onlineByNo = others.GroupBy(u => u.EmployeeNo, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var friendNos = _friends.Select(f => f.EmployeeNo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_friends.Count > 0)
        {
            int onlineFriends = _friends.Count(f => onlineByNo.ContainsKey(f.EmployeeNo));
            items.Add(new CardItem { IsHeader = true, Key = "dept:~friends", Title = $"친구 ({onlineFriends}/{_friends.Count})" });
            // [읽는 법] 정렬: 접속 중(true)이 먼저(OrderByDescending), 같은 그룹 안에서는 이름순(ThenBy). bool은 true가 false보다 크므로 내림차순이면 true가 앞에 온다.
            foreach (UserInfo friend in _friends.OrderByDescending(f => onlineByNo.ContainsKey(f.EmployeeNo)).ThenBy(f => f.Name))
            {
                bool online = onlineByNo.TryGetValue(friend.EmployeeNo, out UserInfo? live);
                UserInfo shown = live ?? friend;
                int unread = UnreadOf(Conversation.DirectKey(friend.EmployeeNo));
                items.Add(new CardItem
                {
                    Key = $"user:{friend.EmployeeNo}",
                    Title = shown.DisplayName,
                    Subtitle = online ? UserSubtitle(shown)
                        : shown.StatusMessage.Length > 0 ? $"접속하지 않음 · {shown.StatusMessage}" : $"접속하지 않음 · {shown.Department}",
                    StatusColor = online ? Theme.PresenceColor(shown.Presence) : Theme.Offline,
                    AvatarText = shown.Name,
                    PhotoKey = friend.EmployeeNo,
                    AvatarColor = Theme.AvatarColor(friend.EmployeeNo),
                    ShowStatus = true,
                    Online = online,
                    Badge = unread > 0 ? unread.ToString() : null,
                    BadgeAlert = unread > 0,
                    Tag = shown,
                });
            }
        }

        // ② 접속 중인 동료 (부서별)
        foreach (var dept in others.Where(u => !friendNos.Contains(u.EmployeeNo)).GroupBy(u => u.Department).OrderBy(g => g.Key))

        {
            items.Add(new CardItem { IsHeader = true, Key = $"dept:{dept.Key}", Title = $"{dept.Key} ({dept.Count()})" });
            foreach (UserInfo user in dept.OrderBy(u => u.Name))
            {
                int unread = UnreadOf(Conversation.DirectKey(user.EmployeeNo));
                items.Add(new CardItem
                {
                    Key = $"user:{user.EmployeeNo}",
                    Title = user.DisplayName,
                    Subtitle = UserSubtitle(user),
                    StatusColor = Theme.PresenceColor(user.Presence),
                    AvatarText = user.Name,
                    PhotoKey = user.EmployeeNo,
                    AvatarColor = Theme.AvatarColor(user.EmployeeNo),
                    ShowStatus = true,
                    Online = true,
                    Badge = unread > 0 ? unread.ToString() : null,
                    BadgeAlert = unread > 0,
                    Tag = user,
                });
            }
        }
        // 접속하지 않은 사람과의 1:1 (안 읽은 메시지가 있거나 이번에 대화한 사람) → 목록 아래에 따로
        var onlineNos = others.Select(u => u.EmployeeNo).ToHashSet();
        // [읽는 법] "접속하지 않았지만 화면에 둘 사람" = 1:1 대화(dm:)가 있고, 지금 접속 중도 아니고 친구도 아니며, 안 읽은 메시지가 있거나 대화 내용이 있는 사람. 안 읽은 것이 많은 사람이 위로.
        var offline = _conversations.Values
            .Where(c => c.Key.StartsWith("dm:") && !onlineNos.Contains(c.Key["dm:".Length..]) && !friendNos.Contains(c.Key["dm:".Length..])
                        && (c.Unread > 0 || c.Items.Count > 0))
            .Select(c => (Conv: c, User: KnownPeer(c.Key["dm:".Length..], c.Title)))
            .OrderByDescending(x => x.Conv.Unread).ThenBy(x => x.User.Name).ToList();
        if (offline.Count > 0)
        {
            items.Add(new CardItem { IsHeader = true, Key = "dept:~offline", Title = $"접속하지 않음 · 대화 중 ({offline.Count})" });
            foreach (var (conv, user) in offline)
            {
                items.Add(new CardItem
                {
                    Key = $"user:{user.EmployeeNo}",
                    Title = user.DisplayName,
                    Subtitle = conv.Unread > 0 ? "안 읽은 메시지가 있어요" : "접속하지 않음 · 메시지는 다음 접속 때 전달",
                    StatusColor = Theme.Offline,
                    AvatarText = user.Name,
                    PhotoKey = user.EmployeeNo,
                    AvatarColor = Theme.AvatarColor(user.EmployeeNo),
                    ShowStatus = true,
                    Online = false,
                    Badge = conv.Unread > 0 ? conv.Unread.ToString() : null,
                    BadgeAlert = conv.Unread > 0,
                    Tag = user,
                });
            }
        }
        // [읽는 법] 목록 전체를 한 번에 교체한다. CardListView가 접힘 상태를 키로 기억하고 있어 교체해도 유지된다. 이어서 탭 글자와 트레이 툴팁(UpdateTrayText)의 숫자도 같은 데이터로 맞춘다.
        _userList.SetItems(items);
        _usersTab.Text = $"동료 {others.Count}";
        UpdateTrayText();
    }

    private void RefreshRoomList()
    {
        var items = _rooms.Select(room =>
        {
            bool current = _joinedRooms.ContainsKey(room.RoomId); // 참여 중인 방
            int unread = current ? UnreadOf(Conversation.RoomKey(room.RoomId)) : 0;
            string sub = room.IsLobby ? "모두가 함께하는 기본 방" : $"방장 {room.OwnerName}";
            return new CardItem
            {
                Key = $"room:{room.RoomId}",
                Title = room.IsLobby ? $"{room.Name} (기본)" : room.Name,
                Subtitle = current ? $"● 참여 중 · {sub}" : sub,
                AvatarText = "#",
                AvatarColor = Theme.AvatarColor($"room:{room.RoomId}"),
                // [읽는 법] 방 카드 오른쪽 배지: 안 읽은 메시지가 있으면 그 수(빨간색 BadgeAlert), 없으면 인원 표시(예: "12/50"). 같은 자리를 두 가지 정보가 번갈아 쓴다.
                Badge = unread > 0 ? unread.ToString() : room.CapacityText,
                BadgeAlert = unread > 0,
                Highlight = current,
                Tag = room,
            };
        });
        _roomList.SetItems(items);
        _roomsTab.Text = $"채팅방 {_rooms.Count}";
        UpdateTrayText();
    }
}
