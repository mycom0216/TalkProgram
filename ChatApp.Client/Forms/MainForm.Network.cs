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
// [파일 요약] 서버에서 온 패킷을 받는 길목: 수신 스레드 → UI 스레드 전환, 연결 끊김, 자동 재접속(지수 백오프), 패킷 종류별 분기(HandlePacket), 채팅·귓속말·시스템 메시지 처리.
// MainForm은 C#의 partial class(클래스를 여러 파일로 나누는 기능)로 쪼개 두었다.
// 이 파일도 MainForm.cs와 "같은 클래스"다. 필드(_client, _me 등)와 메서드를 파일 구분 없이 그대로 쓴다.
// 나눈 기준은 "기능별"이고, 동작은 나누기 전과 완전히 같다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   서버에서 온 모든 패킷의 "입구와 갈림길". 수신 스레드에서 UI 스레드로 넘기고(OnPacketReceived), 종류별로 처리 함수에 나눠 주며(HandlePacket), 끊기면 알아서 다시 붙는다(ReconnectLoopAsync).
//
// ■ 패킷이 지나가는 길
//   Receive 스레드 → ChatClient.PacketReceived → OnPacketReceived
//       ├ 파일 조각이면 _transfers.TryHandle이 수신 스레드에서 바로 파일에 씀(UI로 안 넘김)
//       └ 그 외 → UiHelper.RunOnUi(BeginInvoke) → UI 스레드 → HandlePacket(switch) → Handle○○ → Conversation 갱신 → 창·목록 다시 그리기
//   ※ 검색 결과·사람 목록·친구 검색·휴대폰 정보 응답은 여기서 break만 한다: 그 응답을 기다리는 창이 PacketReceived를 직접 구독해 받는다.
//
// ■ 읽는 순서
//   ① OnPacketReceived/OnDisconnected → ② ShowDisconnected(끊김 화면) → ③ ReconnectLoopAsync(재시도) → ④ HandleRelogin(재로그인 성공 후 복구)
//   → ⑤ HandlePacket(switch 전체 훑기) → ⑥ HandleChat/HandleWhisper/HandleSystem → ⑦ AddMessage(안 읽은 수·알림음·토스트 규칙)
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 지수 백오프(exponential backoff) .............. "exponential backoff reconnect jitter"
//   - async/await와 UI 동기화 컨텍스트 .............. "WinForms SynchronizationContext await UI thread"
//   - switch + 패턴 매칭(case ... when) ............. "C# switch case when guard pattern matching"
//   - fire-and-forget (_ = 작업) .................... "C# discard fire and forget Task"
//   - 재연결 뒤 상태 복구(읽음·방·상태 재전송) ........ "reconnect state resynchronization chat client"
//   - 핑퐁 방지(밀려난 클라이언트는 재접속 금지) ...... "single session kick reconnect loop prevent"
//
// ■ 면접 예상 질문과 답
//   Q1. 연결이 끊기면 어떻게 다시 붙나요? 간격은요?
//       A. ShowDisconnected가 UI를 "끊김" 상태로 바꾸고 ReconnectLoopAsync를 시작합니다. 2 → 4 → 8 → 16 → 30초(이후 계속 30초)로 간격을 늘리며, 연결되면 저장해 둔 토큰(없으면 비밀번호) 로그인 정보로 다시 로그인합니다. await Task.Delay라서 기다리는 동안에도 창을 움직이거나 닫을 수 있습니다.
//   Q2. 왜 간격을 점점 늘리나요?
//       A. 서버가 꺼져 있다가 켜질 때 모든 클라이언트가 1초마다 두드리면 켜지는 순간 접속이 몰려 다시 쓰러질 수 있습니다(thundering herd). 간격을 늘려 부하를 분산합니다. 한계: 무작위 흔들림(jitter)이 없어 같은 시각에 끊긴 클라이언트들은 같은 박자로 시도합니다. 보완하려면 간격에 0~1초 난수를 더하면 됩니다.
//   Q3. 다른 곳에서 로그인해 밀려났을 때는 왜 재접속하지 않나요?
//       A. 둘 다 재접속하면 두 PC가 서로를 계속 밀어내는 "핑퐁"이 생기기 때문입니다. 서버가 보낸 ForceLogout을 받으면 KickedOut이 켜지고, canReconnect가 false가 되어 안내만 합니다.
//   Q4. 재접속하고 로그인에 성공하면 무엇을 복구하나요?
//       A. (1) 서버가 준 최신 내 정보로 교체(관리자가 이름·권한을 바꿨을 수 있음) (2) 친구 목록 재요청 (3) 열린 창에 "다시 연결되었습니다" 알림과 입력 활성화 (4) "읽음" 전송 기록 초기화(끊기기 직전 읽음이 못 갔을 수 있음) (5) 방은 서버가 자동으로 다시 연결하되 3초 뒤에도 소식 없는 방만 직접 입장 요청 (6) 열어 본 1:1은 지난 대화를 다시 요청 — 같은 메시지는 번호로 걸러짐 (7) 내가 고른 상태(회의 중 등)를 재전송 (8) ReleaseHeld로 보관 패킷 풀기.
//   Q5. 인증서가 바뀐 서버로 재접속하면 어떻게 하나요?
//       A. UntrustedCertificateException이 나면 재시도를 멈추고 경고 창을 띄웁니다. 자동으로 믿거나 조용히 재시도하면 가짜 서버에 로그인 정보를 줄 수 있기 때문입니다. 관리자가 인증서를 바꿨는지 확인한 뒤 프로그램을 다시 실행해 지문을 다시 신뢰하게 합니다.
//   Q6. 파일 조각은 왜 UI 스레드로 넘기지 않나요?
//       A. 큰 파일은 조각이 수천 개라 하나씩 BeginInvoke하면 UI 스레드가 메시지 큐로 막혀 창이 멈춥니다. TryHandle이 수신 스레드에서 바로 파일에 쓰고, 진행률 같은 요약 이벤트만 UI로 보냅니다.
//   Q7. 알림은 어떤 규칙으로 울리나요?
//       A. 내가 보낸 메시지는 알리지 않고, 보고 있는 창(보이고 포커스)에 온 메시지는 바로 읽음 처리합니다. 안 보고 있으면 안 읽은 수를 올리고 작업 표시줄을 깜빡이며, 소리·토스트는 1:1과 "@나"로 불린 방 메시지만(방 메시지마다 알리면 시끄러움), 음소거한 방과 회의 중/방해 금지 상태에서는 소리를 끕니다.
//   Q8. 한계는요? (솔직하게 답하기)
//       A. (1) 재접속 간격에 jitter가 없다. (2) HandlePacket이 큰 switch라 패킷 종류가 늘수록 길어진다 — 종류→처리 함수 사전으로 바꿀 수 있다. (3) 재접속 중 보낸 메시지는 Send가 false를 돌려주는데 화면에서 재전송하지는 않는다(입력창을 비활성화해 막는 방식). (4) 재로그인이 실패하면(비밀번호 변경·계정 정지) 재시도 없이 종료 안내만 한다(의도됨).
//
// ■ 문제가 났을 때 어디를 볼까
//   - 재접속이 계속 실패한다 ............ 서버 상태, 인증서 지문 변경(경고 창 여부), LastLogin/LastTokenLogin이 비었는지
//   - 재접속 후 대화가 비어 보인다 ........ HandleRelogin의 ReleaseHeld, 서버의 RoomRes+History 전송, _rejoinRooms/_rejoinTimer
//   - 같은 메시지가 두 번 보인다 .......... 재접속 때 HistoryReq 응답과 실시간 메시지의 중복 제거(Conversation.DedupKey)
//   - 알림이 안 울린다/너무 울린다 ........ AddMessage의 조건(보고 있음, 방 음소거, @멘션, 내 상태)
//   - 화면이 갱신되지 않는다 .............. 새 PacketType을 HandlePacket에 추가했는지(switch에 없으면 무시됨)
// ───────────────────────────────────────────────────────────────────────────

/// <summary>메인 창(partial) - 서버 패킷 수신, 연결 끊김·자동 재접속, 패킷 종류별 처리 부분.</summary>
public sealed partial class MainForm
{
    // ───────────────────────── 수신 (수신 스레드 → UI 스레드) ─────────────────────────

    private void OnPacketReceived(Packet packet)
    {
        // 파일 조각 등은 수신 스레드에서 바로 파일에 쓴다(수천 개의 조각을 UI 스레드로 넘기지 않는다)
        // [읽는 법] TryHandle은 "이 패킷이 파일 전송용(업로드/다운로드 응답, 파일 조각)이면 직접 처리하고 true"를 돌려준다. true면 여기서 끝, 아니면 아래에서 UI 스레드로 넘긴다. 이 한 줄이 수신 스레드에서 처리할 것과 UI에서 처리할 것을 가르는 경계다.
        if (_transfers.TryHandle(packet)) return;
        UiHelper.RunOnUi(this, () => HandlePacket(packet));
    }

    private void OnDisconnected(string reason)
    {
        _transfers.CancelAll(reason); // 진행 중인 파일 전송 실패 처리 (이벤트는 UI 스레드로 넘어간다)
        UiHelper.RunOnUi(this, () => ShowDisconnected(reason));
    }

    /// <summary>
    /// 연결이 끊겼을 때 (UI 스레드). 다른 곳에서 로그인해서 밀려난 경우가 아니면 자동 재접속을 시작한다.
    /// [왜] 예전에는 팝업을 띄우고 "프로그램을 다시 실행하세요"로 끝났다. 와이파이가 잠깐 끊기거나 서버를 재시작할 때마다
    ///      사용자가 직접 다시 켜야 했으므로, 메신저답게 알아서 다시 붙도록 바꿨다.
    /// </summary>
    private void ShowDisconnected(string reason)
    {
        _connected = false;
        _header.Invalidate();
        SetStatus($"● {reason}", Theme.Danger);
        // 받다 만 지난 대화 묶음은 버린다 (다시 로그인하면 서버가 처음부터 다시 보낸다 → 섞이면 같은 말풍선이 두 번 보임)
        foreach (Conversation c in _conversations.Values) c.PendingHistory.Clear();
        _createRoomButton.Enabled = false;

        // 밀려났거나(KickedOut) 로그인 정보가 없으면 재접속하지 않는다.
        // [주의] 밀려났는데도 재접속하면, 두 PC가 서로를 계속 밀어내는 "핑퐁"이 생긴다.
        bool canReconnect = !_client.KickedOut && _client.LastLogin is not null;
        string tail = canReconnect ? "자동으로 다시 연결합니다." : "프로그램을 다시 실행해 접속하세요.";
        foreach (ChatWindow window in OpenWindows())
        {
            // 말풍선마다 화면 배치 정보를 갖고 있으므로 창마다 새로 만든다(같은 객체를 공유하면 위치가 꼬인다)
            window.Conversation.Add(new ChatItem { Kind = ChatItemKind.Error, Text = $"{reason} {tail}" });
            window.SetInputEnabled(false);
        }

        if (!canReconnect)
        {
            MessageBox.Show(this, $"{reason}\n프로그램을 다시 실행해 접속하세요.", Theme.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _ = ReconnectLoopAsync(); // [개념] _ = : 기다리지 않고 시작만 한다(결과를 버린다는 표시). 루프는 UI 스레드에서 await로 돈다.
    }

    /// <summary>
    /// 자동 재접속 루프. 2 → 4 → 8 → 16 → 30초(이후 계속 30초) 간격으로 다시 연결을 시도하고,
    /// 연결되면 저장해 둔 로그인 정보로 다시 로그인한다. 결과는 LoginRes(HandleRelogin)에서 이어서 처리한다.
    ///
    /// [개념] async/await: await Task.Delay(1000)은 1초 동안 "UI 스레드를 놓아 주고" 기다린다.
    ///   Thread.Sleep처럼 창을 멈추지 않으므로 기다리는 동안에도 창을 옮기거나 닫을 수 있다.
    ///   await가 끝나면 다시 UI 스레드에서 이어서 실행되므로(WinForms의 동기화 컨텍스트) 화면을 바로 바꿔도 된다.
    /// [왜] 간격을 점점 늘린다(지수 백오프): 서버가 꺼져 있을 때 수백 명이 1초마다 접속을 시도하면,
    ///   서버가 다시 켜지는 순간 접속이 한꺼번에 몰려 또 쓰러질 수 있다.
    /// </summary>
    private async Task ReconnectLoopAsync()
    {
        if (_reconnecting) return;
        _reconnecting = true;
        try
        {
            while (!IsDisposed && !_connected)
            {
                int attempt = ++_reconnectAttempt;
                // [읽는 법] 1 << n은 2의 n제곱(비트 이동). 시도 1→2초, 2→4, 3→8, 4→16, 5 이상→32가 되지만 Math.Min(30)으로 30초에서 멈춘다. 안쪽 Math.Min(attempt, 5)는 attempt가 아주 커져도 이동 횟수가 넘치지 않게 막는 안전장치.
                int delay = Math.Min(30, 1 << Math.Min(attempt, 5)); // 2, 4, 8, 16, 30, 30, ...
                for (int left = delay; left > 0; left--)
                {
                    SetStatus($"● 연결 끊김 · {left}초 후 다시 연결 ({attempt}번째 시도)", Theme.Danger);
                    await Task.Delay(1000);
                    if (IsDisposed) return; // 기다리는 동안 창을 닫음
                }

                SetStatus("● 다시 연결하는 중...", Theme.Notice);
                try
                {
                    await _client.ConnectAsync(_client.Host, _client.Port);
                }
                catch (UntrustedCertificateException ex)
                {
                    // [주의] 재접속했더니 서버 인증서가 바뀌었다 = 서버 인증서를 새로 만들었거나, 누군가 가짜 서버로 가로채는 중.
                    //        조용히 다시 시도하거나 자동으로 믿으면 안 된다. 멈추고 사용자에게 알린다.
                    SetStatus("● 서버 인증서가 바뀌어 연결을 멈췄습니다", Theme.Danger);
                    MessageBox.Show(this,
                        "서버의 보안 인증서가 이전과 다릅니다.\n누군가 중간에서 연결을 가로채고 있을 수 있어 연결하지 않았습니다.\n\n" +
                        $"새 지문(SHA-256):\n{TlsHelper.Format(ex.Fingerprint)}\n\n" +
                        "관리자에게 서버 인증서를 바꿨는지 확인한 뒤, 프로그램을 다시 실행해 로그인하세요.",
                        Theme.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                catch (Exception ex) when (ex is SocketException or TimeoutException or IOException or AuthenticationException)
                {
                    continue; // 서버가 아직 꺼져 있음(또는 아직 준비 중) → 다음 시도
                }

                if (IsDisposed) { _client.Disconnect(); return; } // 연결하는 사이 창을 닫음

                // 연결됨 → 다시 로그인. 이후는 LoginRes가 오면 HandleRelogin에서 처리한다.
                // (그 전에 또 끊기면 Disconnected → ShowDisconnected → 새 루프가 다시 시작된다)
                if (_client.LastTokenLogin is { } tokenLogin)
                    _client.Send(Packet.Create(PacketType.TokenLoginReq).WithData(tokenLogin));
                else if (_client.LastLogin is { } lastLogin)
                    _client.Send(Packet.Create(PacketType.LoginReq).WithData(lastLogin));
                else
                {
                    SetStatus("● 다시 로그인해야 합니다", Theme.Danger);
                    _client.Disconnect();
                }
                return;
            }
        }
        finally
        {
            _reconnecting = false;
        }
    }

    /// <summary>
    /// 재접속 후 다시 로그인한 결과 (UI 스레드).
    /// 성공하면: 입력을 다시 열고, 참여하던 방에 다시 입장을 요청하고(서버가 놓친 대화를 지난 대화로 보내 줌), 보관된 패킷을 받는다.
    /// </summary>
    private void HandleRelogin(Packet packet)
    {
        if (!packet.Success)
        {
            // 비밀번호가 바뀌었거나 계정이 정지된 경우 → 더 시도해도 소용없다
            SetStatus($"● 다시 로그인하지 못했습니다: {packet.Body}", Theme.Danger);
            _client.Disconnect();
            MessageBox.Show(this, $"다시 로그인하지 못했습니다.\n{packet.Body}\n프로그램을 다시 실행해 로그인하세요.", Theme.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _connected = true;
        _reconnectAttempt = 0;
        // 끊긴 사이 관리자가 이름·직급·권한을 바꿨을 수 있다 → 서버가 준 최신 정보로
        if (packet.GetData<UserInfo>() is { } latest && latest.EmployeeNo == _me.EmployeeNo)
        {
            _me = latest;
            _client.Me = latest;
            _avatars.Note(latest);
            Text = $"{Theme.AppName} - {_me.DisplayName}";
        }
        _header.Invalidate();
        SetStatus(ConnectedStatusText(), Theme.SubText);
        _createRoomButton.Enabled = true;
        _client.Send(Packet.Create(PacketType.FriendListReq)); // 끊긴 사이 바뀌었을 수 있다

        foreach (ChatWindow window in OpenWindows())
        {
            window.Conversation.Add(new ChatItem { Kind = ChatItemKind.System, Text = "다시 연결되었습니다." });
            // 방 창은 서버가 그 방을 다시 연결해 준 뒤(RoomRes)에 입력을 연다. 기본 방과 1:1은 바로.
            bool waitRoom = window.Kind == ChatWindowKind.Room && _roomWindows.FirstOrDefault(kv => kv.Value == window).Key is int rid && rid != RoomInfo.LobbyId;
            window.SetInputEnabled(!waitRoom);
        }
        // 끊기기 직전에 보낸 "읽음"이 서버에 닿지 못했을 수 있다 → 다시 보낼 수 있게 초기화하고, 보고 있는 창은 바로 다시 알린다
        // [읽는 법] default는 DateTime의 "기본값"(0001-01-01). "마지막으로 읽음을 보낸 시각"을 아주 옛날로 되돌려, 다음 읽음 판단 때 서버에 다시 보내도록 만든다.
        foreach (Conversation c in _conversations.Values) c.LastMarkedRead = default;
        _suppressUnreadToast = true; // 재접속 때 다시 오는 "안 읽은 1:1" 요약으로 알림 창을 또 띄우지 않는다

        // 기본 방과 내가 멤버인 방은 서버가 로그인 때 자동으로 다시 연결하고 놓친 대화도 보내 준다(RoomRes + History).
        // 3초 뒤에도 소식이 없는 방(서버가 다시 시작되며 닫혔거나, 내보내진 방)만 직접 입장을 요청해 결과를 확인한다.
        _rejoinRooms.Clear();
        foreach (int roomId in _joinedRooms.Keys.Where(id => id != RoomInfo.LobbyId)) _rejoinRooms.Add(roomId);
        _rejoinTimer.Stop();
        if (_rejoinRooms.Count > 0) _rejoinTimer.Start();

        // 1:1은 서버가 자동으로 보내 주지 않으므로, 열어 본 적 있는 1:1의 지난 대화를 다시 요청한다
        // (끊겨 있던 동안 온 메시지가 채워지고, 같은 메시지는 번호로 걸러진다)
        foreach (Conversation conv in _conversations.Values.Where(c => c.HistoryRequested && c.Key.StartsWith("dm:")))
            _client.Send(Packet.Create(PacketType.HistoryReq, target: conv.Key["dm:".Length..]));

        // 서버는 로그인할 때마다 "온라인"으로 시작하므로, 내가 고른 상태(회의 중 등)를 다시 알린다
        if (_presence != Presence.Online)
            _client.Send(Packet.Create(PacketType.StatusUpdate).WithData(new StatusUpdate { Presence = _presence }));

        // [주의] ChatClient는 LoginRes 성공 직후부터 패킷을 보관한다. 여기서 풀어 주지 않으면 이후 패킷이 영영 오지 않는다.
        _client.ReleaseHeld();
        if (_lastActiveWindow is { IsDisposed: false, ContainsFocus: true } focused) MarkRead(focused.Conversation);
    }

    /// <summary>다시 로그인한 뒤 3초가 지나도 서버가 다시 연결해 주지 않은 방 → 직접 입장을 요청해 본다.</summary>
    private void RejoinMissingRooms()
    {
        _rejoinTimer.Stop();
        if (!_connected) return;
        foreach (int roomId in _rejoinRooms.ToList())
            _client.Send(Packet.Create(PacketType.RoomJoin, target: roomId.ToString()));
        // 결과는 HandleRoomResponse: 성공이면 _rejoinRooms에서 빠지고, 실패면 그 방 대화창에 알린다
    }

    // [읽는 법] 모든 서버 패킷의 갈림길. 새 기능에서 서버가 새 종류의 패킷을 보내기 시작하면 여기에 case를 추가해야 화면이 반응한다. 여기서 break만 하는 종류(SearchRes, Directory 등)는 그 응답을 기다리는 창이 직접 구독해 받으므로 의도적으로 비워 둔 것이다.
    private void HandlePacket(Packet packet)
    {
        switch (packet.Type)
        {
            case PacketType.ChatMsg: HandleChat(packet); break;
            case PacketType.Whisper: HandleWhisper(packet); break;
            case PacketType.LoginRes: HandleRelogin(packet); break; // 자동 재접속 후 다시 로그인한 결과
            case PacketType.FileMsg: HandleFileMessage(packet); break;
            case PacketType.History: HandleHistory(packet); break;
            case PacketType.RoomFilesRes: HandleRoomFilesResponse(packet); break;
            case PacketType.ServerInfo:
                if (packet.GetData<ServerInfo>() is { } info)
                {
                    if (info.MaxFileSize > 0) _transfers.MaxFileSize = info.MaxFileSize;
                    CheckClientVersion(info);
                }
                break;
            case PacketType.RoomUpdate: HandleRoomUpdate(packet); break;
            case PacketType.ReadUpdate: HandleReadUpdate(packet); break;
            case PacketType.ReactionUpdate: HandleReactionUpdate(packet); break;
            case PacketType.MessageDeleted: HandleMessageDeleted(packet); break;
            case PacketType.MessageEdited: HandleMessageEdited(packet); break;
            case PacketType.TypingUpdate: HandleTypingUpdate(packet); break;
            case PacketType.UnreadDirect: HandleUnreadDirect(packet); break;
            case PacketType.SearchRes:
            case PacketType.Directory:
                break; // 검색 창·사람 고르기 창이 직접 받는다
            case PacketType.SystemMsg: HandleSystem(packet); break;
            case PacketType.RoomRes: HandleRoomResponse(packet); break;
            case PacketType.RoomLeft: HandleRoomLeft(packet); break;

            case PacketType.RoomList:
                _rooms = packet.GetData<List<RoomInfo>>() ?? [];
                foreach (RoomInfo room in _rooms.Where(r => _joinedRooms.ContainsKey(r.RoomId)))
                {
                    // [주의] 방 목록에는 멤버 명단이 없다(가볍게 보내려고). 읽음 표시에 쓰는 명단은 이전 것을 이어받는다.
                    room.Members ??= _joinedRooms[room.RoomId].Members;
                    _joinedRooms[room.RoomId] = room; // 인원수 등 최신 정보로
                    UpdateRoomWindowHeader(room);
                }
                RefreshRoomList();
                break;

            case PacketType.UserList:
                _users = packet.GetData<List<UserInfo>>() ?? [];
                foreach (UserInfo u in _users)
                {
                    _knownPeers[u.EmployeeNo] = u;
                    _avatars.Note(u); // 사진 버전이 바뀐 사람만 새로 받아 온다
                }
                foreach (var (no, window) in _directWindows)
                {
                    UserInfo? user = _users.FirstOrDefault(u => u.EmployeeNo == no);
                    window.SetOnline(user is not null && _connected);
                    if (user is not null) window.SetHeader(user.DisplayName, UserSubtitle(user)); // 상대 상태(회의 중 등)도 보이게
                }
                foreach (RoomInfo room in _joinedRooms.Values) UpdateRoomMentionCandidates(room);
                RefreshUserList();
                RefreshProfileCard();
                break;


            case PacketType.AdminNotify:
                // 새 가입 신청 등. 관리자 창이 열려 있으면 그 창이 직접 새로 고친다(AdminForm도 이 패킷을 구독).
                SetStatus($"● {packet.Body}", Theme.Notice);
                if (_me.IsAdmin)
                    ShowToast("관리자 알림", packet.Body ?? "", Theme.AppName, () => OpenAdmin(showPending: true));
                break;

            case PacketType.FriendList:
                _friends = packet.GetData<List<UserInfo>>() ?? [];
                foreach (UserInfo f in _friends)
                {
                    _knownPeers.TryAdd(f.EmployeeNo, f);
                    _avatars.Note(f);
                }
                if (!string.IsNullOrEmpty(packet.Body)) SetStatus($"● {packet.Body}", Theme.SubText);
                RefreshUserList();
                RefreshProfileCard();
                break;

            case PacketType.ProfilePhoto:
                if (!packet.Success)
                {
                    SetStatus($"● {packet.Body}", Theme.Danger);
                    break;
                }
                if (string.Equals(packet.Target, _me.EmployeeNo, StringComparison.OrdinalIgnoreCase) && packet.GetData<PhotoInfo>() is { } mine)
                {
                    _me = mine.Kind == PhotoKind.Background ? _me.WithBackground(mine.Version) : _me.WithPhoto(mine.Version);
                    _client.Me = _me;
                }
                _avatars.Handle(packet);
                RefreshProfileCard();
                if (!string.IsNullOrEmpty(packet.Body)) SetStatus($"● {packet.Body}", Theme.SubText);
                break;

            case PacketType.ProfileRes:
                if (!packet.Success)
                {
                    SetStatus($"● {packet.Body}", Theme.SubText);
                    break;
                }
                if (packet.GetData<UserInfo>() is { } profile)
                {
                    _knownPeers[profile.EmployeeNo] = profile;
                    _avatars.Note(profile);
                    RefreshProfileCard();
                }
                break;

            case PacketType.FriendSearchRes:
            case PacketType.PhoneInfo:
                break; // 친구 추가 창·휴대폰 번호 창이 직접 받는다

            // [읽는 법] case ... when 조건: Error 패킷이면서 "친구 추가 창이 응답을 기다리는 중"일 때만 이 줄에 걸린다. 그 오류는 그 창이 직접 보여 주므로 여기서는 무시. 조건에 안 맞는 일반 오류는 바로 아래 case로 내려가 현재 활성 창에 오류 말풍선으로 표시된다. (case 순서가 중요: 조건 있는 것이 먼저.)
            case PacketType.Error when _addFriendForm is { IsDisposed: false, IsWaiting: true }:
                break; // 친구 추가 창이 기다리던 오류 → 그 창이 보여 준다

            case PacketType.Error:
                var error = new ChatItem { Kind = ChatItemKind.Error, Text = packet.Body ?? "오류가 발생했습니다." };
                if (_lastActiveWindow is { IsDisposed: false }) _lastActiveWindow.Conversation.Add(error);
                else SetStatus($"● {error.Text}", Theme.Danger);
                break;
        }
    }

    private void HandleChat(Packet packet)
    {
        // 어느 방의 메시지인지는 Target(방 번호)
        if (!int.TryParse(packet.Target, out int roomId) || !_joinedRooms.TryGetValue(roomId, out RoomInfo? room)) return;
        bool mine = packet.SenderId == _me.EmployeeNo;
        Conversation conv = GetRoomConversation(room);
        AddMessage(conv, _roomWindows.GetValueOrDefault(roomId), new ChatItem
        {
            Kind = mine ? ChatItemKind.Mine : ChatItemKind.Other,
            Sender = packet.Sender ?? "",
            SenderKey = packet.SenderId ?? "",
            Text = packet.Body ?? "",
            Time = packet.Timestamp,
            Mentioned = !mine && MentionsMe(packet.Body),
            MessageId = packet.MessageId,
            ReplyTo = packet.ReplyTo,
        });
        RecomputeUnread(conv);
    }

    private void HandleWhisper(Packet packet)
    {
        bool mine = packet.SenderId == _me.EmployeeNo;

        // 상대방: 내가 보낸 귓속말이면 받는 사람(Data), 받은 귓속말이면 보낸 사람
        UserInfo? other = mine
            ? packet.GetData<UserInfo>()
            : _users.FirstOrDefault(u => u.EmployeeNo == packet.SenderId)
              ?? new UserInfo { EmployeeNo = packet.SenderId ?? "", Name = packet.Sender ?? "", Position = "" };
        if (other is null || other.EmployeeNo.Length == 0) return;
        _knownPeers.TryAdd(other.EmployeeNo, other);

        // 받은 메시지인데 창이 없으면 자동으로 띄운다(포커스는 빼앗지 않음)
        ChatWindow window = OpenDirect(other, activate: false);
        AddMessage(window.Conversation, window, new ChatItem
        {
            Kind = mine ? ChatItemKind.Mine : ChatItemKind.Other,
            Sender = packet.Sender ?? "",
            SenderKey = packet.SenderId ?? "",
            Text = packet.Body ?? "",
            Time = packet.Timestamp,
            MessageId = packet.MessageId,
            ReplyTo = packet.ReplyTo,
        });
        RecomputeUnread(window.Conversation);
    }

    private void HandleSystem(Packet packet)
    {
        string text = packet.Body ?? "";
        bool notice = text.StartsWith("[공지]");
        if (notice) SetStatus(text, Theme.Notice);

        if (int.TryParse(packet.Target, out int roomId))
        {
            // 방 안의 입장·퇴장 알림 → 그 방 대화에
            if (_joinedRooms.TryGetValue(roomId, out RoomInfo? room))
                GetRoomConversation(room).Add(new ChatItem { Kind = ChatItemKind.System, Text = text, Time = packet.Timestamp });
        }
        else
        {
            // 서버 공지 → 참여 중인 모든 방 대화에 (말풍선마다 배치 정보가 있어 방마다 새로 만든다)
            foreach (RoomInfo room in _joinedRooms.Values)
                GetRoomConversation(room).Add(new ChatItem { Kind = notice ? ChatItemKind.Notice : ChatItemKind.System, Text = text, Time = packet.Timestamp });
        }
    }

    /// <summary>대화에 메시지를 넣고, 창이 안 보이면 안 읽은 수를 올리고, 받은 메시지면 깜빡임·알림음.</summary>
    private void AddMessage(Conversation conv, ChatWindow? window, ChatItem item)
    {
        conv.Add(item);
        if (item.Kind == ChatItemKind.Mine) return;

        // [읽는 법] "지금 이 대화를 실제로 보고 있는가": 창이 있고, 화면에 보이고, 키보드 포커스가 그 창 안에 있을 때만 true. 최소화되었거나 다른 창을 쓰는 중이면 false라서 안 읽은 수가 올라간다.
        bool reading = window is { Visible: true, ContainsFocus: true };
        if (!reading)
        {
            conv.Unread++;
            RefreshUserList();
            RefreshRoomList();
        }
        if (window is not null && !reading) NativeMethods.Flash(window);
        if (reading)
        {
            MarkRead(conv); // 보고 있는 창에 온 메시지는 바로 읽음 → 보낸 사람 화면의 숫자가 줄어든다
            return;
        }

        // 알림 창 + 소리: 1:1 메시지와, 방에서 나를 부른(@이름) 메시지만. (방 메시지마다 알리면 너무 시끄럽다)
        bool direct = conv.Key.StartsWith("dm:");
        if (!direct && int.TryParse(conv.Key["room:".Length..], out int roomId) && _settings.MutedRoomIds.Contains(roomId)) return;
        // [읽는 법] 소리·토스트 알림 규칙의 핵심: 방 메시지는 "@나"일 때만 알린다(안 읽은 수와 깜빡임은 이미 위에서 처리됨). 1:1은 항상 알린다. 위쪽 줄은 음소거한 방을 먼저 걸러낸다.
        if (!direct && !item.Mentioned) return;
        if (_presence is not (Presence.Busy or Presence.DoNotDisturb)) SystemSounds.Asterisk.Play(); // 회의 중·방해 금지면 소리 끔
        string title = direct ? item.Sender : $"{conv.Title} · {item.Sender}";
        string text = item.Mentioned ? $"[나를 불렀어요] {item.Text}" : item.Text;
        ShowToast(title, text, item.SenderKey, () => OpenConversation(conv));
    }

    /// <summary>"@이름" 또는 "@사원번호"가 들어 있으면 나를 부른 것.</summary>
    private bool MentionsMe(string? text)
        => text is not null &&
           (text.Contains("@" + _me.Name, StringComparison.OrdinalIgnoreCase) || text.Contains("@" + _me.EmployeeNo, StringComparison.OrdinalIgnoreCase));

    /// <summary>알림 창을 눌렀을 때: 그 대화의 창을 연다.</summary>
    private void OpenConversation(Conversation conv)
    {
        if (conv.Key.StartsWith("room:") && int.TryParse(conv.Key["room:".Length..], out int roomId) && _joinedRooms.TryGetValue(roomId, out RoomInfo? room))
            OpenRoomWindow(room);
        else if (conv.Key.StartsWith("dm:") && _directWindows.TryGetValue(conv.Key["dm:".Length..], out ChatWindow? window))
            BringToFront(window);
    }
}
