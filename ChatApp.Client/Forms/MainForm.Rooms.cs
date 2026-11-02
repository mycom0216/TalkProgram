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
// [파일 요약] 채팅방 기능: 입장·생성·초대·나가기·삭제·방장 넘기기·정원 변경·공지·멤버 보기, 방 대화창 열기.
// MainForm은 C#의 partial class(클래스를 여러 파일로 나누는 기능)로 쪼개 두었다.
// 이 파일도 MainForm.cs와 "같은 클래스"다. 필드(_client, _me 등)와 메서드를 파일 구분 없이 그대로 쓴다.
// 나눈 기준은 "기능별"이고, 동작은 나누기 전과 완전히 같다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   채팅방 기능의 클라이언트 쪽: "요청 보내기(Send) → 서버 응답/방송 받기(Handle○○) → 상태·화면 갱신" 구조. 방 상태의 진짜 주인은 서버이고, 여기서는 확인 창과 요청 전송, 응답 반영만 한다.
//
// ■ 두 방향으로 나누어 읽기
//   (A) 사용자가 한 일 → 서버로: EnterRoom, CreateRoom, InviteToRoom, ConfirmLeaveRoom, ConfirmDeleteRoom, ChangeRoomCapacity, EditRoomNotice, 방장 넘기기
//   (B) 서버가 알려 준 것 → 화면으로: HandleRoomResponse(내가 들어감/실패), HandleRoomUpdate(멤버·정보 바뀜), HandleRoomLeft(나감/방 삭제됨)
//   연결 고리: OpenRoomWindow가 ChatWindow를 만들고 "창 이벤트 → 메인 창 처리 함수"를 연결한다(창은 통신을 모른다).
//
// ■ 핵심 필드 (MainForm.cs)
//   _joinedRooms(내가 참여 중) / _rooms(공개 방 목록) / _roomWindows(방 번호 → 열린 창) / _conversations("room:번호" → 대화) / _openRoomOnJoin(내가 요청한 입장인지) / _rejoinRooms(재접속 후 확인 대기 방)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 요청-응답 + 서버 방송(서버가 상태의 주인) ........ "server authoritative state client request broadcast"
//   - 사용자 의도 플래그(_openRoomOnJoin)로 같은 응답을 다르게 처리 .. "flag to distinguish user initiated action"
//   - 컨텍스트 메뉴를 만들고 닫힐 때 Dispose (핸들 누수) .. "ContextMenuStrip dispose handle leak"
//   - 서버 제한에 맞춘 쪼개 보내기(Chunk 50명) .......... "batch requests chunk LINQ Chunk"
//   - 같은 연결의 패킷은 순서대로 처리된다는 가정 ....... "message ordering single TCP connection"
//
// ■ 면접 예상 질문과 답
//   Q1. 방에 들어가라는 응답(RoomRes)이 와도 창을 안 여는 경우가 있던데요?
//       A. 같은 RoomRes가 "내가 요청한 입장"과 "남이 나를 초대해 들어옴"과 "재접속 후 서버가 자동 복구"에서 모두 옵니다. 내가 방을 고르거나 만들 때만 _openRoomOnJoin을 켜 두었다가 응답이 오면 창을 엽니다. 초대받은 경우는 토스트로 알려 주고 누르면 열고, 로그인·재접속 때의 자동 복구는 조용히 상태만 맞춥니다.
//   Q2. 방장 권한(삭제, 정원, 공지)은 클라이언트에서 막으면 충분한가요?
//       A. 아닙니다. 버튼을 숨기거나 비활성화하는 것은 편의이고(방장·관리자일 때만 보이게), 서버가 같은 권한을 다시 검사해야 안전합니다. 수정된 클라이언트가 RoomDelete를 보내도 서버가 거부합니다. 클라이언트 검사는 "실수 방지"와 "화면 단순화" 용도입니다.
//   Q3. 방장이 나갈 때 어떻게 처리하나요?
//       A. 멤버가 더 있으면 먼저 방장을 넘길 사람을 고르게 합니다(RoomTransferOwner → RoomLeave 순서로 전송). "그냥 나가기"를 고르면 넘기지 않고 나가며, 서버가 가장 먼저 들어온 멤버를 새 방장으로 정하고 방에 알립니다. 같은 연결의 패킷은 보낸 순서대로 처리되므로 넘기기가 나가기보다 먼저 반영됩니다.
//   Q4. 정원을 넘겨서 초대하면 어떻게 되나요?
//       A. 방장·관리자만 "정원 늘리고 초대"가 되고(최대 1000명), 확인 후 RoomCapacity를 먼저 보낸 뒤 초대를 50명씩 나눠(서버가 한 번에 최대 50명) 보냅니다. 일반 멤버는 남은 자리까지만 고를 수 있습니다. 순서 보장 덕분에 정원이 먼저 늘어난 상태에서 초대가 처리됩니다.
//   Q5. 방을 나가면 대화 내용은 어떻게 되나요?
//       A. HandleRoomLeft가 창을 닫고 참여 목록과 Conversation을 지우며 그 대화의 파일 추적(_fileItems)도 정리합니다. 다시 들어오면 새 대화로 시작하고 서버가 입장 시각 이후의 지난 대화를 보내 줍니다(단체방은 들어오기 전 대화는 보이지 않음). 서버의 대화 기록은 남아 있습니다(삭제는 보관 기간 정책).
//   Q6. 컨텍스트 메뉴를 왜 닫힐 때 Dispose하나요? 왜 BeginInvoke로 한 박자 늦추나요?
//       A. 그때그때 만드는 ContextMenuStrip은 컨트롤 트리에 없어 자동 해제되지 않고 창 핸들이 쌓입니다. 다만 Closed 직후에는 클릭한 항목의 처리가 끝나기 전일 수 있어, Dispose를 메시지 큐 뒤로(BeginInvoke) 미뤄 클릭 처리가 끝난 뒤에 정리합니다.
//   Q7. 한계는요? (솔직하게 답하기)
//       A. (1) 서버 응답 없이 화면을 바꾸지는 않지만, 요청이 실패해도(예: 권한 변경으로) 메뉴가 잠깐 낡은 상태로 보일 수 있다 — 메뉴 열 때(Opening)마다 다시 계산해 줄이고 최종 판정은 서버가 한다. (2) 방 정보를 _joinedRooms와 _rooms 두 곳에 들고 있어 갱신 때 둘 다 바꿔야 한다(HandleRoomUpdate가 함께 처리). (3) 방장 판단(room.OwnerNo == _me.EmployeeNo || _me.IsAdmin)이 여러 곳에 반복된다 — 작은 함수로 뽑으면 좋다.
//   ※ 코드 정리 후보(동작 영향 없음): ShowRoomListMenu 위의 첫 번째 <summary>("더블클릭: …")는 EnterRoom 설명이 옮겨 붙은 것으로 보인다.
//
// ■ 문제가 났을 때 어디를 볼까
//   - 방을 만들었는데 창이 안 뜬다 .......... _openRoomOnJoin 플래그가 켜졌는지, HandleRoomResponse의 isNew/requested 분기
//   - 재접속 후 방 입력이 계속 막혀 있다 ..... _rejoinRooms/_rejoinTimer(3초), RoomRes 수신 시 SetInputEnabled(true)
//   - 방 인원/방장이 화면에서 안 바뀐다 ...... HandleRoomUpdate, UpdateRoomWindowHeader, 서버 RoomUpdate 방송
//   - 메뉴가 비활성/숨김 ................... BuildRoomMenu의 Opening 조건(방장·관리자, _connected, 멤버 수)
//   - 초대가 일부만 된다 ................... 정원(MaxUsers), 50명 나누기, 서버 응답 메시지
// ───────────────────────────────────────────────────────────────────────────

/// <summary>메인 창(partial) - 채팅방 입장·생성·초대·나가기·삭제·공지 등 방 기능과 방 대화창 열기 부분.</summary>
public sealed partial class MainForm
{
    // ───────────────────────── 채팅방 ─────────────────────────

    private void HandleRoomResponse(Packet packet)
    {
        if (!packet.Success)
        {
            // 재접속 후 다시 들어가려던 방이 사라짐(서버 재시작 등) → 팝업 대신 그 방 대화창에 알린다
            if (int.TryParse(packet.Target, out int failedId) && _rejoinRooms.Remove(failedId))
            {
                if (_joinedRooms.Remove(failedId, out RoomInfo? gone))
                {
                    GetRoomConversation(gone).Add(new ChatItem { Kind = ChatItemKind.Error, Text = $"'{gone.Name}' 방에 다시 들어가지 못했습니다: {packet.Body}" });
                    if (_roomWindows.TryGetValue(failedId, out ChatWindow? goneWindow))
                    {
                        goneWindow.ShowLeaveButton = false;
                        goneWindow.ShowInviteButton = false;
                        goneWindow.SetInputEnabled(false); // 참여하지 않은 방이 되었으므로 보낼 수 없다
                    }
                }
                RefreshRoomList();
                return;
            }
            _openRoomOnJoin = false;
            MessageBox.Show(this, packet.Body ?? "요청을 처리하지 못했습니다.", "채팅방", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        RoomInfo? room = packet.GetData<RoomInfo>();
        if (room is null) return;
        _rejoinRooms.Remove(room.RoomId);
        bool isNew = !_joinedRooms.ContainsKey(room.RoomId);
        bool requested = _openRoomOnJoin;

        // 이미 열려 있던 창이면(재접속) 입력을 다시 연다
        if (_roomWindows.TryGetValue(room.RoomId, out ChatWindow? openWindow))
        {
            openWindow.SetInputEnabled(true);
            UpdateRoomWindowHeader(room);
        }

        // 다른 방에서는 나가지 않는다 (여러 방 동시 참여)
        _joinedRooms[room.RoomId] = room;
        Conversation conv = GetRoomConversation(room);
        conv.Title = room.Name;
        if (!_rooms.Any(r => r.RoomId == room.RoomId)) _rooms.Add(room); // 비공개 방은 방 목록이 늦게 올 수 있다
        if (_openRoomOnJoin)
        {
            _openRoomOnJoin = false;
            OpenRoomWindow(room);
        }
        else if (isNew && !requested && !room.IsLobby && !string.IsNullOrEmpty(packet.Body))
        {
            // 누가 나를 초대함 → 알려 주고, 누르면 그 방을 연다
            SetStatus($"● {packet.Body}", Theme.Notice);
            ShowToast("채팅방 초대", packet.Body, $"room:{room.RoomId}", () => OpenRoomWindow(room));
        }
        RecomputeUnread(conv);
        RefreshRoomList();
    }

    /// <summary>방 멤버가 바뀜(입장·초대·퇴장) → 머리의 인원, 읽음 숫자 계산에 쓰는 명단을 바꾼다.</summary>
    private void HandleRoomUpdate(Packet packet)
    {
        if (packet.GetData<RoomInfo>() is not { } room || !_joinedRooms.ContainsKey(room.RoomId)) return;
        _joinedRooms[room.RoomId] = room;
        int index = _rooms.FindIndex(r => r.RoomId == room.RoomId);
        if (index >= 0) _rooms[index] = room;
        UpdateRoomWindowHeader(room);
        UpdateRoomMentionCandidates(room);
        RecomputeUnread(GetRoomConversation(room));
        RefreshRoomList();
    }

    /// <summary>[나가기]가 처리됨 → 창을 닫고 참여 목록에서 뺀다.</summary>
    private void HandleRoomLeft(Packet packet)
    {
        if (!int.TryParse(packet.Target, out int roomId)) return;
        _joinedRooms.Remove(roomId);
        if (_roomWindows.TryGetValue(roomId, out ChatWindow? window)) window.CloseByProgram();
        if (_conversations.Remove(Conversation.RoomKey(roomId), out Conversation? removed))
        {
            // 다시 들어가면 새 대화로 시작. 그 대화의 파일 말풍선도 더 이상 추적하지 않는다
            foreach (string id in _fileItems.Where(kv => kv.Value.Conv == removed).Select(kv => kv.Key).ToList())
                _fileItems.Remove(id);
        }
        SetStatus(packet.Body ?? "방에서 나왔습니다.", Theme.SubText);
        RefreshRoomList();
    }

    /// <summary>방 목록에서 더블클릭: 참여 중이면 창만 열고, 아니면 서버에 입장 요청.</summary>
    /// <summary>방 목록에서 오른쪽 클릭: 열기 / 들어가기, 나가기, 방 삭제(방장·관리자).</summary>
    private void ShowRoomListMenu(RoomInfo room, Point location)
    {
        bool joined = _joinedRooms.ContainsKey(room.RoomId);
        RoomInfo current = _joinedRooms.GetValueOrDefault(room.RoomId) ?? room;
        bool canDelete = !room.IsLobby && (current.OwnerNo == _me.EmployeeNo || _me.IsAdmin);

        var menu = new ContextMenuStrip();
        menu.Items.Add(joined ? "열기" : "들어가기", null, (_, _) => EnterRoom(room));
        if (!room.IsLobby && joined)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("방 나가기", null, (_, _) => ConfirmLeaveRoom(room.RoomId, this)).Enabled = _connected;
        }
        if (canDelete)
        {
            if (!joined) menu.Items.Add(new ToolStripSeparator());
            ToolStripItem delete = menu.Items.Add("방 삭제", null, (_, _) => ConfirmDeleteRoom(room.RoomId, this));
            delete.ForeColor = Theme.Danger;
            delete.Enabled = _connected;
        }
        // [주의] 메뉴는 닫힐 때 바로 해제한다(그때그때 만드는 메뉴라 남겨 두면 창 핸들이 쌓인다).
        //        Closed 직후에는 아직 클릭 처리가 끝나지 않았을 수 있어, 한 박자 뒤(BeginInvoke)에 해제한다.
        // [읽는 법] 닫힐 때 바로 Dispose하지 않고 메시지 큐 맨 뒤로 미룬다(BeginInvoke). 이미 큐에 있던 "항목 클릭" 처리가 끝난 뒤에 정리되므로, 클릭한 동작이 사라지는 일이 없다.
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(_roomList, location);
    }

    /// <summary>방 나가기 확인. 방장이면 먼저 방장을 넘길 사람을 고른다. 창 [나가기]·방 메뉴·방 목록 메뉴가 함께 쓴다.</summary>
    private void ConfirmLeaveRoom(int roomId, IWin32Window owner)
    {
        if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? current) || current.IsLobby) return;
        // 방장이 나가려면 먼저 방장을 넘길 사람을 고른다 (고르지 않으면 서버가 가장 오래된 멤버에게 자동으로 넘긴다)
        if (current.OwnerNo == _me.EmployeeNo && (current.Members?.Count ?? 0) > 1)
        {
            // [읽는 법] PickNewOwner의 반환값이 세 가지다: null = 취소(나가지 않음), "" = "그냥 나가기"(자동 승계), 그 외 = 사원번호(그 사람에게 넘김). null과 빈 문자열을 구분하는 점에 주의.
            string? next = PickNewOwner(current, owner, "나가기 전에 방장을 넘길 사람을 고르세요",
                                        skipText: "그냥 나가기 (자동으로 넘기기)");
            if (next is null) return; // 취소 = 나가지 않음
            // "그냥 나가기"("")면 넘기지 않고 나간다 → 서버가 가장 먼저 들어온 멤버를 새 방장으로 정하고 방에 알린다
            if (next.Length > 0)
                _client.Send(Packet.Create(PacketType.RoomTransferOwner, body: next, target: roomId.ToString()));
            _client.Send(Packet.Create(PacketType.RoomLeave, target: roomId.ToString())); // 같은 연결이라 순서대로 처리된다
            return;
        }
        string note = "\n다른 멤버가 남아 있으면 방은 그대로 남습니다. (방을 없애려면 방장이 [방 삭제])";
        if (current.IsPrivate) note += "\n비공개 방이라 다시 들어오려면 멤버의 초대가 필요합니다.";
        if (MessageBox.Show(owner, $"'{current.Name}' 방에서 나갈까요?{note}", "채팅방 나가기",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            _client.Send(Packet.Create(PacketType.RoomLeave, target: roomId.ToString()));
        // 창은 서버가 RoomLeft로 확인해 주면 닫힌다
    }

    /// <summary>방 삭제 확인 (방장·관리자). 참여하지 않은 방도 관리자는 목록에서 바로 지울 수 있다.</summary>
    private void ConfirmDeleteRoom(int roomId, IWin32Window owner)
    {
        RoomInfo? current = _joinedRooms.GetValueOrDefault(roomId) ?? _rooms.FirstOrDefault(r => r.RoomId == roomId);
        if (current is null || current.IsLobby) return;
        if (MessageBox.Show(owner,
                $"'{current.Name}' 방을 삭제할까요?\n\n멤버 {current.MemberCount}명 모두가 방에서 빠지고, 방 목록에서 사라집니다.\n(대화 기록은 서버에 남습니다. 되돌릴 수 없습니다)",
                "방 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            _client.Send(Packet.Create(PacketType.RoomDelete, target: roomId.ToString()));
        // 결과: 접속 중인 멤버 모두에게 RoomLeft → 창이 닫히고 목록에서 빠진다 (서버가 방 목록도 새로 보낸다)
    }

    private void EnterRoom(RoomInfo room)
    {
        if (_joinedRooms.TryGetValue(room.RoomId, out RoomInfo? joined))
        {
            OpenRoomWindow(joined);
            return;
        }
        // [읽는 법] "내가 방을 골랐다"는 표시. 서버 응답(RoomRes)이 오면 이 플래그를 보고 창을 열고 끈다. 전송에 실패하면(연결 끊김) 응답이 영영 안 오므로 바로 아래에서 플래그를 되돌린다.
        _openRoomOnJoin = true;
        if (!_client.Send(Packet.Create(PacketType.RoomJoin, target: room.RoomId.ToString())))
            _openRoomOnJoin = false;
    }

    private void CreateRoom()
    {
        using var form = new CreateRoomForm();
        if (form.ShowDialog(this) != DialogResult.OK) return;

        var request = new CreateRoomRequest { Name = form.RoomName, MaxUsers = form.MaxUsers, IsPrivate = form.IsPrivate };
        if (form.IsPrivate)
        {
            // 비공개 방은 목록에 안 보이므로 함께할 사람을 바로 고르게 한다 (건너뛰어도 나중에 [초대]로 부를 수 있다)
            // 만들면서 초대는 한 번에 최대 50명(서버 제한)이고, 나를 뺀 방 인원까지만 고를 수 있다. 더 부르려면 방에서 [초대]
            using var picker = new PeoplePickerForm(_client, $"'{form.RoomName}' 방에 초대할 사람 (건너뛰려면 취소)", multiSelect: true, [_me.EmployeeNo],
                                                    maxSelect: Math.Min(RoomInviteRequest.MaxPerRequest, Math.Max(1, form.MaxUsers - 1)));
            if (picker.ShowDialog(this) == DialogResult.OK)
                request.InviteNos = picker.Selected.Select(u => u.EmployeeNo).Take(Math.Max(0, form.MaxUsers - 1)).ToList();
        }

        _openRoomOnJoin = true;
        if (!_client.Send(Packet.Create(PacketType.RoomCreate).WithData(request)))
            _openRoomOnJoin = false;
    }

    /// <summary>방 창 머리를 눌렀을 때의 메뉴: 멤버 보기, 방장 넘기기(방장·관리자만).</summary>
    private ContextMenuStrip BuildRoomMenu(int roomId, ChatWindow window)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("멤버 보기", null, (_, _) => ShowMembers(roomId, window));
        menu.Items.Add("방 파일 모아보기", null, (_, _) => OpenRoomFiles(window));
        var mute = new ToolStripMenuItem("방 알림 끄기") { CheckOnClick = true, Checked = _settings.MutedRoomIds.Contains(roomId) };
        mute.CheckedChanged += (_, _) =>
        {
            if (mute.Checked)
            {
                if (!_settings.MutedRoomIds.Contains(roomId)) _settings.MutedRoomIds.Add(roomId);
            }
            else _settings.MutedRoomIds.Remove(roomId);
            _settings.Save();
        };
        menu.Items.Add(mute);
        var notice = menu.Items.Add("공지 고정·수정...", null, (_, _) => EditRoomNotice(roomId, window));
        var transfer = menu.Items.Add("방장 넘기기...", null, (_, _) =>
        {
            if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
            string? next = PickNewOwner(room, window, "새 방장을 고르세요");
            if (next is not null)
                _client.Send(Packet.Create(PacketType.RoomTransferOwner, body: next, target: roomId.ToString()));
        });
        var capacity = menu.Items.Add("방 정원 바꾸기...", null, (_, _) => ChangeRoomCapacity(roomId, window));
        menu.Items.Add(new ToolStripSeparator());
        var leave = menu.Items.Add("방 나가기", null, (_, _) => ConfirmLeaveRoom(roomId, window));
        var delete = menu.Items.Add("방 삭제", null, (_, _) => ConfirmDeleteRoom(roomId, window));
        delete.ForeColor = Theme.Danger;
        menu.Opening += (_, _) =>
        {
            RoomInfo? room = _joinedRooms.GetValueOrDefault(roomId);
            leave.Enabled = room is not null && _connected;
            delete.Visible = room is not null && (room.OwnerNo == _me.EmployeeNo || _me.IsAdmin);
            delete.Enabled = _connected;
            notice.Enabled = room is not null && _connected && (room.OwnerNo == _me.EmployeeNo || _me.IsAdmin);
            capacity.Enabled = notice.Enabled;
            transfer.Enabled = room is not null && _connected && (room.OwnerNo == _me.EmployeeNo || _me.IsAdmin) && (room.Members?.Count ?? 0) > 1;
        };
        return menu;
    }

    /// <summary>방 멤버 중 새 방장 고르기 (나 제외). 고르지 않으면 null.</summary>
    /// <remarks>skipText를 주면 "고르지 않고 진행" 버튼이 생기고, 누르면 빈 문자열("")을 돌려준다.</remarks>
    private string? PickNewOwner(RoomInfo room, IWin32Window owner, string title, string? skipText = null)
    {
        var members = (room.Members ?? [])
            .Where(m => m.EmployeeNo != _me.EmployeeNo && m.EmployeeNo != room.OwnerNo)
            .Select(m => new UserInfo { EmployeeNo = m.EmployeeNo, Name = m.DisplayName, Department = _users.Any(u => u.EmployeeNo == m.EmployeeNo) ? "접속 중" : "" })
            .ToList();
        if (members.Count == 0)
        {
            MessageBox.Show(owner, "방장을 넘길 다른 멤버가 없습니다.", "방장 넘기기", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        using var picker = new PeoplePickerForm(_client, $"'{room.Name}' - {title}", multiSelect: false, [],
            people: members, okText: "방장 넘기기", hint: "더블클릭하거나 고른 뒤 [방장 넘기기]", skipText: skipText);
        DialogResult result = picker.ShowDialog(owner);
        if (result == DialogResult.Ignore) return "";
        return result == DialogResult.OK && picker.Selected.Count > 0 ? picker.Selected[0].EmployeeNo : null;
    }

    /// <summary>방 멤버 목록 (방장, 접속 중 표시).</summary>
    private void ShowMembers(int roomId, IWin32Window owner)
    {
        if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room) || room.Members is null) return;
        var online = _users.Select(u => u.EmployeeNo).ToHashSet();
        online.Add(_me.EmployeeNo);
        IEnumerable<string> lines = room.Members
            .OrderByDescending(m => m.EmployeeNo == room.OwnerNo).ThenByDescending(m => online.Contains(m.EmployeeNo)).ThenBy(m => m.DisplayName)
            .Select(m => $"{(online.Contains(m.EmployeeNo) ? "●" : "○")} {m.DisplayName} ({m.EmployeeNo}){(m.EmployeeNo == room.OwnerNo ? "  [방장]" : "")}");
        MessageBox.Show(owner, $"멤버 {room.Members.Count}명 (● 접속 중)\n\n{string.Join("\n", lines)}", $"'{room.Name}' 멤버",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>[초대] → 사람 고르기 → 서버에 초대 요청 (이미 멤버인 사람은 목록에서 뺀다).</summary>
    private void InviteToRoom(int roomId, IWin32Window owner)
    {
        if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
        var members = room.Members?.Select(m => m.EmployeeNo) ?? [];
        int roomLeft = room.MaxUsers > 0 ? Math.Max(0, room.MaxUsers - room.MemberCount) : int.MaxValue;
        bool canManage = room.OwnerNo == _me.EmployeeNo || _me.IsAdmin;
        if (roomLeft == 0 && !canManage)
        {
            MessageBox.Show(owner, $"방이 가득 찼습니다 (최대 {room.MaxUsers}명).\n방장에게 정원을 늘려 달라고 요청하세요.", "초대",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var picker = new PeoplePickerForm(_client, $"'{room.Name}' 방에 초대", multiSelect: true, members.Append(_me.EmployeeNo),
                                                maxSelect: roomLeft);
        // [방장·관리자] 남은 자리보다 많이 고르면 "정원 늘리고 초대"로 바뀐다 → 확인 후 정원부터 늘린다
        if (canManage)
        {
            picker.ConfirmOverLimit = count =>
            {
                int need = room.MemberCount + count;
                if (need > CreateRoomRequest.MaxUsersLimit)
                {
                    MessageBox.Show(picker, $"방 정원은 최대 {CreateRoomRequest.MaxUsersLimit}명입니다. {need - CreateRoomRequest.MaxUsersLimit}명을 빼 주세요.",
                        "초대", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                return MessageBox.Show(picker, $"방 정원을 {room.MaxUsers}명 → {need}명으로 늘리고 {count}명을 초대할까요?", "정원 늘리고 초대",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            };
        }
        if (picker.ShowDialog(owner) != DialogResult.OK) return;
        if (room.MaxUsers > 0 && picker.Selected.Count > roomLeft)
        {
            // [순서] 서버는 한 연결의 패킷을 받은 순서대로 처리하므로, 정원 변경이 초대보다 먼저 반영된다
            _client.Send(Packet.Create(PacketType.RoomCapacity, body: (room.MemberCount + picker.Selected.Count).ToString(), target: roomId.ToString()));
        }
        // 서버는 요청 하나에 최대 50명까지 받는다 → [전체 선택]으로 많이 골랐으면 50명씩 나눠 보낸다
        // [읽는 법] Chunk(50)은 목록을 50개씩 잘라 string[] 조각들로 돌려준다. 서버가 한 요청에 최대 50명만 받으므로 "전체 선택" 같은 많은 초대를 여러 번에 나눠 보낸다.
        foreach (string[] chunk in picker.Selected.Select(u => u.EmployeeNo).Chunk(RoomInviteRequest.MaxPerRequest))
        {
            _client.Send(Packet.Create(PacketType.RoomInvite).WithData(new RoomInviteRequest
            {
                RoomId = roomId,
                EmployeeNos = chunk.ToList(),
            }));
        }
        // 결과: 초대된 사람에게 RoomRes, 방 사람들에게 SystemMsg("…님을 초대했습니다") + RoomUpdate
    }

    /// <summary>
    /// 방 대화창 열기. X로 닫으면 창만 닫히고 방에는 남는다(다시 더블클릭하면 이전 대화와 함께 열림).
    /// 방에서 나가려면 창 위쪽 [나가기].
    /// </summary>
    private void OpenRoomWindow(RoomInfo room)
    {
        if (_roomWindows.TryGetValue(room.RoomId, out ChatWindow? existing) && !existing.IsDisposed)
        {
            BringToFront(existing);
            return;
        }

        int roomId = room.RoomId;
        Conversation conv = GetRoomConversation(room);
        var window = new ChatWindow(ChatWindowKind.Room, conv, $"room:{roomId}");
        UpdateRoomMentionCandidates(room);
        window.ShowLeaveButton = !room.IsLobby;
        window.ShowInviteButton = !room.IsLobby;
        // [읽는 법] 창 ↔ 메인 창 연결 방식: 창은 "보내 달라"는 이벤트만 올리고, 패킷 만들기와 전송은 메인 창이 한다. roomId를 지역 변수로 복사해 람다가 잡아 두므로(클로저) 이 창은 항상 자기 방으로 보낸다.
        window.SendRequested += (_, text, replyId) => SendText(PacketType.ChatMsg, text, roomId.ToString(), replyId);
        window.InviteRequested += _ => InviteToRoom(roomId, window);
        window.ShowDeleteButton = !room.IsLobby && (room.OwnerNo == _me.EmployeeNo || _me.IsAdmin);
        if (!room.IsLobby) window.HeaderMenu = BuildRoomMenu(roomId, window);
        window.DeleteRoomRequested += _ => ConfirmDeleteRoom(roomId, window);
        window.LeaveRequested += _ => ConfirmLeaveRoom(roomId, window);
        window.FilesRequested += (_, paths) => SendFiles(conv, paths, roomId, null);
        WireWindow(window);
        // [읽는 법] 창이 닫히면 _roomWindows에서 뺀다. 다시 열 때를 대비한 안전 검사(w == window)는, 닫히는 도중 같은 방의 새 창이 이미 등록된 경우 새 창을 지우지 않기 위한 것이다.
        window.FormClosed += (_, _) =>
        {
            if (_roomWindows.TryGetValue(roomId, out ChatWindow? w) && w == window) _roomWindows.Remove(roomId);
        };

        _roomWindows[roomId] = window;
        UpdateRoomWindowHeader(room);
        UpdateRoomMentionCandidates(room);
        window.SetInputEnabled(_connected);
        window.Show();
    }

    private void UpdateRoomWindowHeader(RoomInfo room)
    {
        if (!_roomWindows.TryGetValue(room.RoomId, out ChatWindow? window)) return;
        if (room.IsLobby)
        {
            window.SetPinnedNotice(null);
            window.SetHeader(room.Name, $"{room.MemberCount}명 접속 중 · 모두가 함께하는 기본 방");
            return;
        }
        // 멤버(접속하지 않은 사람 포함)와 지금 접속한 사람을 따로 보여 준다
        window.ShowDeleteButton = room.OwnerNo == _me.EmployeeNo || _me.IsAdmin; // 방장이 바뀌면 버튼도 따라간다
        window.SetPinnedNotice(room.PinnedNotice);
        string max = room.MaxUsers > 0 ? $"/{room.MaxUsers}" : "";
        string kind = room.IsPrivate ? "비공개 · " : "";
        window.SetHeader(room.Name, $"{kind}멤버 {room.MemberCount}{max}명 · 접속 {room.OnlineCount}명 · 방장 {room.OwnerName}");
    }

    /// <summary>방장·관리자: 방 정원(최대 인원) 바꾸기.</summary>
    private void ChangeRoomCapacity(int roomId, IWin32Window owner)
    {
        if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
        using var form = new RoomCapacityForm(room.Name, room.MaxUsers, room.MemberCount);
        if (form.ShowDialog(owner) != DialogResult.OK || form.Capacity == room.MaxUsers) return;
        _client.Send(Packet.Create(PacketType.RoomCapacity, body: form.Capacity.ToString(), target: roomId.ToString()));
    }

    private void EditRoomNotice(int roomId, IWin32Window owner)
    {
        if (!_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
        using var form = new RoomNoticeForm(room.Name, room.PinnedNotice);
        if (form.ShowDialog(owner) != DialogResult.OK) return;
        _client.Send(Packet.Create(PacketType.RoomNotice).WithData(new RoomNoticeRequest { RoomId = roomId, Text = form.NoticeText }));
    }

    private void UpdateRoomMentionCandidates(RoomInfo room)
    {
        if (!_roomWindows.TryGetValue(room.RoomId, out ChatWindow? window)) return;
        IEnumerable<(string EmployeeNo, string DisplayName)> candidates = room.IsLobby
            ? _users.Select(u => (u.EmployeeNo, u.DisplayName))
            : (room.Members ?? []).Select(m => (m.EmployeeNo, m.DisplayName));
        window.SetMentionCandidates(candidates, _me.EmployeeNo);
    }

    private Conversation GetRoomConversation(RoomInfo room)
        => GetConversation(Conversation.RoomKey(room.RoomId), room.Name);
}
