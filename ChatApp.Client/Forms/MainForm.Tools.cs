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
// [파일 요약] 전체 대화 검색, 동료 찾기, 로그아웃, 새 버전 안내·설치 같은 기타 메뉴 기능.
// MainForm은 C#의 partial class(클래스를 여러 파일로 나누는 기능)로 쪼개 두었다.
// 이 파일도 MainForm.cs와 "같은 클래스"다. 필드(_client, _me 등)와 메서드를 파일 구분 없이 그대로 쓴다.
// 나눈 기준은 "기능별"이고, 동작은 나누기 전과 완전히 같다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//   ※ 위 [파일 요약]의 "로그아웃, 새 버전 안내·설치"는 지금은 MainForm.Profile.cs에 있다(옛 설명이 남은 것). 이 파일에는 검색과 동료 찾기 세 함수만 있다.
//
// ■ 한 줄 요약
//   메인 창에서 다른 창으로 이어 주는 "연결 함수" 세 개: 전체 대화 검색 창 열기(OpenSearch), 검색 결과 클릭 시 해당 대화·메시지로 이동(OpenSearchHit), 오프라인 포함 동료 찾기(FindPeople).
//
// ■ 검색 흐름
//   SearchForm이 SearchReq를 보내고 SearchRes를 직접 받아 목록에 표시(메인 창은 이 응답을 HandlePacket에서 일부러 무시) → 더블클릭 → HitActivated → OpenSearchHit
//   → 방이면 OpenRoomWindow, 1:1이면 OpenDirect → window.FocusMessage(메시지 번호)로 이동(불러온 범위 안에 있을 때)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 창 하나만 띄우기(싱글턴 폼) + 닫히면 null .......... "single instance modeless form pattern"
//   - 이벤트로 창 사이를 연결(결합도 낮추기) ............. "decouple forms with events WinForms"
//   - 검색 결과로 이동(deep link to message) .............. "jump to message search result chat"
//   - 서버 쪽 검색 제한(검색어 길이, 호출 간격) ........... "rate limit search endpoint minimum keyword length"
//
// ■ 면접 예상 질문과 답
//   Q1. SearchForm이 서버 응답을 직접 받는 이유는요? 메인 창이 받아서 전달하면 안 되나요?
//       A. 검색 창이 자기가 보낸 요청의 응답만 알면 되므로 PacketReceived를 직접 구독하는 편이 결합이 약합니다. 메인 창이 모든 응답을 받아 중계하면 메인 창이 모든 창의 사정을 알아야 합니다. 대신 규칙이 생깁니다: 메인 HandlePacket은 그 응답 종류에서 break만 하고(무시), 창은 닫힐 때 구독을 해제한다.
//   Q2. 검색 결과를 눌렀을 때 그 메시지 위치로 어떻게 이동하나요?
//       A. 결과에 방 번호(또는 상대 사원번호)와 메시지 번호가 있어, 해당 대화창을 열고(FocusMessage) 이미 불러온 대화 안에 그 번호의 말풍선이 있으면 스크롤해서 강조합니다. 한계: 아직 불러오지 않은 오래된 메시지는 이동하지 못합니다(창만 열림). 개선하려면 해당 메시지 주변을 서버에서 따로 불러오는 기능이 필요합니다.
//   Q3. 서버에서는 검색을 어떻게 제한하나요?
//       A. 검색어 길이(최소 MinKeywordLength ~ 최대 50자)와 호출 간격(세션당 2초), 결과 개수 상한(MaxResults)을 둡니다. LIKE 검색은 비용이 커서 남용을 막기 위한 장치입니다. 범위는 "내가 참여 중인 방 목록(roomIds)과 내 1:1"로 제한합니다.
//   Q4. 검색과 지난 대화(History)의 범위 규칙은 같은가요?
//       A. 확인한 바로는 다릅니다. 단체방의 지난 대화는 "들어온 시각(JoinedAt) 이후"만 보여 주는데, 검색 쿼리(MessageRepository.Search)에는 입장 시각 조건이 보이지 않습니다. 의도라면 문서에 남기고, 의도가 아니라면 같은 조건을 넣는 것이 일관됩니다(개선 후보로 기록).
//   Q5. 한계는요? (솔직하게 답하기)
//       A. (1) 검색은 서버 DB의 LIKE라서 데이터가 많아지면 느려진다(전문 검색 인덱스로 개선 가능). (2) 결과로 이동할 때 아직 불러오지 않은 옛 메시지는 못 찾아간다. (3) 참여하지 않은 방의 결과는 "지금은 참여하지 않은 방입니다"로 안내만 한다.
//
// ■ 문제가 났을 때 어디를 볼까
//   - 검색이 "잠시 후 다시"라고 나온다 ....... 서버 2초 간격 제한(정상 동작), 연타 여부
//   - 결과를 눌러도 이동하지 않는다 ........... 그 메시지가 불러온 범위 안에 있는지, FocusMessage(ChatWindow), 대화창 열림 여부
//   - 검색 창이 두 개 뜬다 ................... OpenSearch의 IsDisposed 검사, FormClosed에서 null로 되돌리는지
//   - 동료 찾기에 사람이 안 나온다 ........... PeoplePickerForm의 Directory 요청(서버 30초 캐시), 제외 목록([_me.EmployeeNo])
// ───────────────────────────────────────────────────────────────────────────

/// <summary>메인 창(partial) - 전체 대화 검색과 동료 찾기 부분.</summary>
public sealed partial class MainForm
{
    // ───────────────────────── 전체 검색·동료 찾기·로그아웃·업데이트 ─────────────────────────

    private void OpenSearch()
    {
        if (_searchForm is null || _searchForm.IsDisposed)
        {
            _searchForm = new SearchForm(_client);
            // [읽는 법] 검색 창은 "이 결과를 열어 달라"는 이벤트만 올리고, 대화창을 여는 일은 메인 창이 한다(창이 대화·창 관리를 모르게 하는 구조). 아래 FormClosed에서 null로 되돌려, 닫힌 창을 다시 쓰지 않게 한다.
            _searchForm.HitActivated += OpenSearchHit;
            _searchForm.FormClosed += (_, _) => _searchForm = null;
            _searchForm.Show();
        }
        else
        {
            BringToFront(_searchForm);
        }
    }

    /// <summary>검색 결과를 더블클릭 → 그 대화창을 열고, 불러온 대화 안에 있으면 그 메시지로 이동한다.</summary>
    private void OpenSearchHit(SearchHit hit)
    {
        ChatWindow? window = null;
        if (hit.RoomId is int roomId)
        {
            if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room))
            {
                SetStatus("● 지금은 참여하지 않은 방입니다.", Theme.SubText);
                return;
            }
            OpenRoomWindow(room);
            window = _roomWindows.GetValueOrDefault(roomId);
        }
        else if (hit.PeerNo is string peer)
        {
            window = OpenDirect(KnownPeer(peer, hit.PeerName), activate: true);
        }
        // [읽는 법] window?.는 "창이 있을 때만 호출"(참여하지 않은 방이면 위에서 이미 return). FocusMessage는 대화 안에서 그 번호의 말풍선을 찾아 스크롤·강조한다. 불러오지 않은 옛 메시지면 찾지 못하고 창만 열린 상태가 된다.
        window?.FocusMessage(hit.MessageId);
    }

    /// <summary>접속하지 않은 사람까지 회사 전체에서 골라 1:1 대화 열기.</summary>
    private void FindPeople()
    {
        // [읽는 법] 사람 고르기 창을 재사용한다: 한 명만 고르기(multiSelect: false), 마지막 인자는 목록에서 뺄 사원번호들(나 자신). using var라서 이 함수가 끝나면 창이 자동으로 Dispose된다. 접속하지 않은 사람까지 서버의 Directory(회사 전체) 응답으로 보여 준다.
        using var picker = new PeoplePickerForm(_client, "동료 찾기 (접속하지 않은 사람 포함)", multiSelect: false, [_me.EmployeeNo]);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected.Count == 0) return;
        UserInfo user = picker.Selected[0];
        _knownPeers[user.EmployeeNo] = user;
        OpenDirect(user, activate: true);
    }
}
