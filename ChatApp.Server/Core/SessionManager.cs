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
        session.User = user;
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
