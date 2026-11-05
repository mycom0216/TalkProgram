using System.Collections.Concurrent;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Server.Core;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 서버에 접속해 있는 모든 사람(세션) 목록. "누가 접속 중인가", "사원번호로 세션 찾기",
//            "모두에게 보내기(브로드캐스트)"를 담당한다.
// [스레드]   Accept 스레드(추가), 각 세션 수신 스레드(로그인·검색·브로드캐스트), 종료 처리 스레드(삭제)가
//            동시에 접근한다 → ConcurrentDictionary로 lock 없이 스레드 안전하게 처리.
// [연결]     ChatServer가 1개 만든다. PacketHandler, FileService, Program.cs(/users)가 사용.
// ═══════════════════════════════════════════════════════════════════════════
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   "지금 누가 접속 중인가"를 들고 있는 명단. 장부가 두 개다: _all(번호 → 세션, 로그인 전 포함)과 _byEmployeeNo(사원번호 → 세션, 로그인한 사람만).
//
// ■ 왜 장부가 둘인가
//   접속하자마자(로그인 전) 이미 연결 하나가 존재한다. 그 연결도 하트비트·종료 처리를 해야 하므로 _all에 넣는다.
//   로그인에 성공하면 사원번호로 찾을 수 있어야 한다(귓속말, 1:1 파일, 중복 로그인 확인) → _byEmployeeNo에도 넣는다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - ConcurrentDictionary ..................... "ConcurrentDictionary TryAdd TryUpdate TryRemove"
//   - Compare-And-Swap(CAS) 낙관적 재시도 ........ "compare and swap optimistic concurrency loop"
//   - 스냅샷(ToArray)으로 안전하게 돌기 ........... "iterate concurrent collection snapshot"
//   - 대소문자 무시 비교(StringComparer) .......... "StringComparer.OrdinalIgnoreCase dictionary"
//
// ■ 면접 예상 질문과 답
//   Q1. 왜 Dictionary + lock이 아니라 ConcurrentDictionary를 썼나요?
//       A. 접속·로그인·종료가 여러 스레드에서 동시에 일어납니다. ConcurrentDictionary는 내부에서 잘게 나눈 lock을 써서 우리가 직접 lock을 걸지 않아도 안전하고, 전체를 잠그지 않아 병목이 작습니다.
//   Q2. ConcurrentDictionary면 항상 안전한가요?
//       A. 메서드 하나(TryAdd 등)는 원자적이지만, "꺼내 보고 → 판단하고 → 바꾸기"를 두 줄로 나누면 그 사이에 다른 스레드가 끼어들 수 있습니다(check-then-act 문제).
//          그래서 Login은 TryUpdate(키, 새 값, 내가 본 옛 값)로 "내가 본 값이 그대로일 때만" 바꾸고, 바뀌었으면 처음부터 다시 합니다(CAS 방식).
//   Q3. 같은 계정이 거의 동시에 두 번 로그인하면요?
//       A. Login의 루프에서 한쪽은 TryAdd/TryUpdate에 성공하고, 다른 쪽은 그 사이 값이 바뀌었으므로 실패해 다시 돕니다. 결국 나중에 성공한 쪽이 이기고, 밀려난 쪽 세션은 PacketHandler가 끊습니다.
//   Q4. Remove에서 키만이 아니라 (키, 세션) 쌍으로 지우는 이유는요?
//       A. 재접속 상황: 새 연결이 먼저 로그인해서 같은 사원번호를 차지하고, 이전 연결이 뒤늦게 끊기면서 Remove가 불립니다. 키만으로 지우면 이미 로그인한 새 세션을 지워 버립니다.
//          쌍이 맞을 때만 지우면 "내 자리"만 치우게 됩니다.
//   Q5. Broadcast에서 ToArray()로 복사하는 이유는요?
//       A. 보내는 도중에 누가 나가도 컬렉션이 바뀌어 생기는 오류나 누락을 피하려는 스냅샷입니다. 대신 복사 비용이 있어서, 접속자 목록 방송은 0.2초에 한 번으로 묶어 부담을 줄입니다.
//   Q6. 사원번호를 대소문자 구분 없이 비교하는 이유는요?
//       A. A0001과 a0001을 다른 사람으로 보면 한 사람이 두 번 접속하거나 귓속말이 안 가는 문제가 생깁니다. 사용자는 대소문자를 실수로 바꿔 입력할 수 있기 때문입니다.
//   Q7. 임시 비밀번호 상태인 사람은 왜 목록에서 빼나요?
//       A. 아직 비밀번호를 바꾸지 않아 로그인이 끝나지 않은 상태입니다. 다른 사람에게 보이거나 방송을 받지 않게(MustChangePassword) 걸러 냅니다.
//
// ■ 문제가 났을 때 어디를 볼까 (유지보수)
//   - 같은 계정이 두 곳에서 동시에 접속돼 있다 .... Login()의 TryAdd/TryUpdate 루프, PacketHandler의 ForceLogout 처리
//   - 끊겼는데 접속자 목록에 남는다 ............. Remove()가 불리는지(ClientSession.Close → PacketHandler.OnDisconnected), 하트비트
//   - 재로그인했는데 목록에서 사라진다 ........... Remove()의 (키, 값) 쌍 제거 확인
//   - 일부 사람에게만 공지가 안 간다 ............. Broadcast의 조건(MustChangePassword), 그 사람의 ClientSession 송신 큐
// ───────────────────────────────────────────────────────────────────────────

/// <summary>
/// 접속자 목록. 여러 세션 스레드가 동시에 추가/삭제/조회하므로 ConcurrentDictionary를 쓴다(NF-03).
/// </summary>
public sealed class SessionManager
{
    // [개념] ConcurrentDictionary: 여러 스레드가 동시에 읽고 써도 안전한 Dictionary.
    //        내부에서 잘게 나눈 lock을 쓰기 때문에, 우리가 직접 lock을 걸지 않아도 된다.
    //        TryAdd / TryRemove / TryGetValue 같은 "Try" 메서드는 한 번의 원자적 동작으로 처리된다.

    // 접속한 모든 세션(로그인 전 포함)
    private readonly ConcurrentDictionary<int, ClientSession> _all = new();

    // 로그인한 세션(사원번호 → 세션). 같은 사원번호 중복 접속 방지.
    // [개념] StringComparer.OrdinalIgnoreCase: 키를 비교할 때 대소문자를 무시한다("a0001" == "A0001").
    private readonly ConcurrentDictionary<string, ClientSession> _byEmployeeNo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>접속 수(로그인 전 포함).</summary>
    public int ConnectedCount => _all.Count;

    /// <summary>[스레드] Accept 스레드에서 새 접속마다 호출.</summary>
    public void Add(ClientSession session) => _all[session.Id] = session;

    /// <summary>
    /// 로그인 세션 등록. 같은 사원번호가 이미 접속 중이면 "새 로그인이 이긴다": 목록을 새 세션으로 바꾸고 이전 세션을 돌려준다.
    /// (호출한 쪽이 이전 세션에 ForceLogout을 보내고 끊는다)
    ///
    /// [왜] 예전에는 이미 접속 중이면 거절했다. 그런데 노트북 절전·와이파이 끊김 뒤 자동 재접속을 하면,
    ///      서버는 아직 이전 연결이 끊긴 줄 모르므로(하트비트로 정리될 때까지 최대 45초) 재접속이 거절된다.
    ///      카카오톡 PC처럼 새 로그인을 받아들이고 이전 연결을 정리하는 쪽이 사용자에게 자연스럽다.
    /// </summary>
    // [주의] "꺼내 보기 → 바꾸기"를 두 줄로 나누면, 그 사이에 다른 스레드가 같은 사원번호로 로그인할 수 있다.
    //        그래서 TryAdd / TryUpdate(키, 새 값, "내가 본 옛 값")로 원자적으로 바꾸고, 그사이 바뀌었으면 처음부터 다시 한다.
    //        TryUpdate는 "지금 값이 내가 본 옛 값과 같을 때만" 바꾼다 (Compare-And-Swap 방식).
    public ClientSession? Login(ClientSession session, UserInfo user)
    {
        // [읽는 법] 세션을 명단에 올리기 "전에" 로그인 정보부터 채운다. 명단에 올라가는 순간 다른 스레드가 이 세션의 User를 읽을 수 있으므로, 비어 있는 채로 보이면 안 된다.
        session.User = user;
        // [읽는 법] "성공할 때까지 다시 시도하는" 낙관적 재시도 루프. 빠져나오는 길은 두 가지다: ① TryAdd 성공 = 처음 로그인(null 반환)
        //           ② TryUpdate 성공 = 이전 세션을 밀어냄(old 반환). 둘 다 실패하는 건 다른 스레드가 그 사이 값을 바꿨다는 뜻이라, 다시 읽고 시도한다.
        while (true)
        {
            if (_byEmployeeNo.TryAdd(user.EmployeeNo, session)) return null; // 처음 로그인
            if (_byEmployeeNo.TryGetValue(user.EmployeeNo, out ClientSession? old) &&
                _byEmployeeNo.TryUpdate(user.EmployeeNo, session, old))
                return old; // 이전 연결을 밀어냄
            // 그사이 이전 연결이 빠졌거나 다른 스레드가 바꿨다 → 다시 시도
        }
    }

    /// <summary>접속 종료 시 목록에서 뺀다. (ClientSession.Close → PacketHandler.OnDisconnected에서 호출)</summary>
    public void Remove(ClientSession session)
    {
        _all.TryRemove(session.Id, out _);
        if (session.User is not null)
            // [왜] 키(사원번호)만으로 지우지 않고 (키, 값=이 세션) "짝"이 맞을 때만 지운다.
            //      만약 이 사원번호로 이미 "다른" 세션이 등록돼 있다면 그 세션을 실수로 지우면 안 되기 때문이다.
            _byEmployeeNo.TryRemove(new KeyValuePair<string, ClientSession>(session.User.EmployeeNo, session));
    }

    /// <summary>사원번호로 접속 중인 세션 찾기 (귓속말, 1:1 파일). 없으면 null.</summary>
    // [개념] out var s: 찾은 값을 s라는 새 변수로 받는다(타입은 컴파일러가 추론).
    public ClientSession? FindByEmployeeNo(string employeeNo)
        => _byEmployeeNo.TryGetValue(employeeNo, out var s) ? s : null;

    /// <summary>접속 중인 사용자 목록 (부서 → 이름 순, CH-03).</summary>
    public List<UserInfo> GetUsers()
        => _byEmployeeNo.Values
            .Where(s => !s.MustChangePassword) // 임시 비밀번호를 아직 안 바꾼 사람은 목록에 보이지 않는다
            .Select(s => s.User)
            // [개념] OfType<UserInfo>(): null이 아닌 UserInfo만 남긴다(null 걸러내기 + 타입을 non-null로).
            .OfType<UserInfo>()
            .OrderBy(u => u.Department)
            .ThenBy(u => u.Name)
            .ToList();

    /// <summary>
    /// 로그인한 모든 사용자에게 전송.
    /// ToArray()로 스냅샷을 떠서 돌리므로, 도는 중에 누가 나가도 예외가 나지 않는다.
    /// </summary>
    public void Broadcast(Packet packet, ClientSession? except = null)
    {
        foreach (ClientSession session in _byEmployeeNo.Values.ToArray())
        {
            if (session != except && !session.MustChangePassword) session.Send(packet);
        }
    }

    /// <summary>로그인을 마친 세션들의 스냅샷 (임시 비밀번호 변경 대기 중인 세션 제외).</summary>
    public ClientSession[] GetLoggedInSessions() => _byEmployeeNo.Values.Where(s => !s.MustChangePassword).ToArray();

    /// <summary>접속한 모든 세션(로그인 전 포함)의 스냅샷. 하트비트 감시 스레드가 무응답 연결을 찾을 때 쓴다.</summary>
    public ClientSession[] GetAllSessions() => _all.Values.ToArray();

    /// <summary>서버 종료 시 모든 연결을 끊는다.</summary>
    public void CloseAll()
    {
        foreach (ClientSession session in _all.Values.ToArray())
            session.Close();
    }
}
