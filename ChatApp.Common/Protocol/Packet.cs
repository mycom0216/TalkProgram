namespace ChatApp.Common.Protocol;

/// <summary>
/// 모든 통신에 쓰는 공통 패킷. JSON으로 직렬화되어 [길이 4바이트][JSON] 형태로 전송된다.
/// </summary>
public sealed class Packet
{
    public PacketType Type { get; set; }

    /// <summary>보낸 사람 닉네임. 서버가 직접 채워 넣는다(클라이언트가 보낸 값은 믿지 않음).</summary>
    public string? Sender { get; set; }

    /// <summary>받는 대상(귓속말 상대, 이후 단계에서는 방 ID 등).</summary>
    public string? Target { get; set; }

    /// <summary>메시지 본문 또는 부가 데이터.</summary>
    public string? Body { get; set; }

    /// <summary>요청 결과(LoginRes 등 응답 패킷에서 사용).</summary>
    public bool Success { get; set; } = true;

    public DateTime Timestamp { get; set; } = DateTime.Now;

    public static Packet Create(PacketType type, string? body = null, string? sender = null, string? target = null, bool success = true)
        => new() { Type = type, Body = body, Sender = sender, Target = target, Success = success };

    public override string ToString() => $"{Type} from={Sender} to={Target} body={Body}";
}
