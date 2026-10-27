using System.Collections.Concurrent;
using ChatApp.Common.Protocol;

namespace ChatApp.Server.Core;

/// <summary>
/// 접속자 목록. 여러 세션 스레드가 동시에 추가/삭제/조회하므로 ConcurrentDictionary를 쓴다(NF-03).
/// </summary>
public sealed class SessionManager
{
    // 접속한 모든 세션(로그인 전 포함)
    private readonly ConcurrentDictionary<int, ClientSession> _all = new();

    // 로그인한 세션(닉네임 → 세션). 대소문자 구분 없이 중복 방지.
    private readonly ConcurrentDictionary<string, ClientSession> _byNickname = new(StringComparer.OrdinalIgnoreCase);

    public int ConnectedCount => _all.Count;

    public void Add(ClientSession session) => _all[session.Id] = session;

    /// <summary>닉네임 등록. TryAdd는 원자적이라 두 명이 동시에 같은 닉네임을 써도 한 명만 성공한다.</summary>
    public bool TryLogin(ClientSession session, string nickname)
    {
        if (!_byNickname.TryAdd(nickname, session)) return false;
        session.Nickname = nickname;
        return true;
    }

    public void Remove(ClientSession session)
    {
        _all.TryRemove(session.Id, out _);
        if (session.Nickname is not null)
            _byNickname.TryRemove(new KeyValuePair<string, ClientSession>(session.Nickname, session));
    }

    public ClientSession? FindByNickname(string nickname)
        => _byNickname.TryGetValue(nickname, out var s) ? s : null;

    public List<string> GetNicknames()
        => _byNickname.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// 로그인한 모든 사용자에게 전송.
    /// ToArray()로 스냅샷을 떠서 돌리므로, 도는 중에 누가 나가도 예외가 나지 않는다.
    /// </summary>
    public void Broadcast(Packet packet, ClientSession? except = null)
    {
        foreach (ClientSession session in _byNickname.Values.ToArray())
        {
            if (session != except) session.Send(packet);
        }
    }

    public void CloseAll()
    {
        foreach (ClientSession session in _all.Values.ToArray())
            session.Close();
    }
}
