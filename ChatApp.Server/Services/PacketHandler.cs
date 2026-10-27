using ChatApp.Common.Protocol;
using ChatApp.Server.Core;
using ChatApp.Server.Logging;

namespace ChatApp.Server.Services;

/// <summary>
/// PacketType별 처리. 각 세션의 수신 스레드에서 동시에 호출되므로,
/// 공유 상태는 SessionManager(스레드 안전)를 통해서만 건드린다.
/// </summary>
public sealed class PacketHandler(SessionManager sessions, ServerLogger log)
{
    private const int MaxNicknameLength = 12;
    private const int MaxMessageLength = 500;

    public ServerLogger Log { get; } = log;

    public void Handle(ClientSession session, Packet packet)
    {
        // 로그인 전에는 로그인/핑만 허용
        if (!session.IsLoggedIn && packet.Type is not (PacketType.LoginReq or PacketType.Ping))
        {
            SendError(session, "먼저 로그인하세요.");
            return;
        }

        switch (packet.Type)
        {
            case PacketType.LoginReq: HandleLogin(session, packet); break;
            case PacketType.ChatMsg: HandleChat(session, packet); break;
            case PacketType.Whisper: HandleWhisper(session, packet); break;
            case PacketType.Logout: session.Close(); break;
            case PacketType.Ping: session.Send(Packet.Create(PacketType.Pong)); break;
            default: SendError(session, $"지원하지 않는 요청: {packet.Type}"); break;
        }
    }

    private void HandleLogin(ClientSession session, Packet packet)
    {
        if (session.IsLoggedIn)
        {
            SendError(session, "이미 로그인했습니다.");
            return;
        }

        string nickname = packet.Body?.Trim() ?? "";
        string? reason = ValidateNickname(nickname);

        if (reason is null && !sessions.TryLogin(session, nickname))
            reason = "이미 사용 중인 닉네임입니다.";

        if (reason is not null)
        {
            session.Send(Packet.Create(PacketType.LoginRes, body: reason, success: false));
            return;
        }

        session.Send(Packet.Create(PacketType.LoginRes, body: $"{nickname}님, 환영합니다!", success: true));
        Log.Info($"로그인: {session}");

        BroadcastSystem($"{nickname}님이 입장하셨습니다.");
        BroadcastUserList();
    }

    private static string? ValidateNickname(string nickname)
    {
        if (nickname.Length == 0) return "닉네임을 입력하세요.";
        if (nickname.Length > MaxNicknameLength) return $"닉네임은 {MaxNicknameLength}자 이하로 입력하세요.";
        if (nickname.Any(char.IsWhiteSpace) || nickname.Contains(',')) return "닉네임에 공백이나 쉼표는 쓸 수 없습니다.";
        return null;
    }

    private void HandleChat(ClientSession session, Packet packet)
    {
        string? body = packet.Body?.Trim();
        if (string.IsNullOrEmpty(body)) return;
        if (body.Length > MaxMessageLength) body = body[..MaxMessageLength];

        // Sender는 클라이언트 값이 아니라 서버가 알고 있는 닉네임으로 채운다(사칭 방지).
        sessions.Broadcast(Packet.Create(PacketType.ChatMsg, body, sender: session.Nickname));
    }

    private void HandleWhisper(ClientSession session, Packet packet)
    {
        string? body = packet.Body?.Trim();
        string? targetName = packet.Target?.Trim();
        if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(targetName)) return;

        ClientSession? target = sessions.FindByNickname(targetName);
        if (target is null)
        {
            SendError(session, $"'{targetName}'님은 접속 중이 아닙니다.");
            return;
        }
        if (target == session)
        {
            SendError(session, "자기 자신에게는 귓속말을 보낼 수 없습니다.");
            return;
        }

        var whisper = Packet.Create(PacketType.Whisper, body, sender: session.Nickname, target: target.Nickname);
        target.Send(whisper);
        session.Send(whisper); // 보낸 사람 화면에도 표시
    }

    /// <summary>ClientSession.Close()에서 호출된다(어느 스레드에서든 한 번만).</summary>
    public void OnDisconnected(ClientSession session)
    {
        sessions.Remove(session);
        Log.Info($"접속 해제: {session} (현재 {sessions.ConnectedCount}명)");

        if (session.Nickname is not null)
        {
            BroadcastSystem($"{session.Nickname}님이 퇴장하셨습니다.");
            BroadcastUserList();
        }
    }

    public void BroadcastSystem(string message)
        => sessions.Broadcast(Packet.Create(PacketType.SystemMsg, message));

    private void BroadcastUserList()
        => sessions.Broadcast(Packet.Create(PacketType.UserList, string.Join(",", sessions.GetNicknames())));

    private static void SendError(ClientSession session, string message)
        => session.Send(Packet.Create(PacketType.Error, message, success: false));
}
