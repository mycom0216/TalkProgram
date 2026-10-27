using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatApp.Common.Protocol;

/// <summary>
/// Packet &lt;-&gt; byte[] 변환.
/// 전송 형식: [본문 길이 int32, 리틀엔디언 4바이트] + [UTF-8 JSON 본문]
/// 길이 헤더가 있어야 TCP에서 패킷이 붙거나(병합) 잘려서(분할) 와도 경계를 정확히 복원할 수 있다.
/// </summary>
public static class PacketSerializer
{
    public const int HeaderSize = 4;
    public const int MaxBodySize = 1024 * 1024; // 1MB. 비정상 길이로 메모리를 잡아먹는 것을 막는다.

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }, // enum을 숫자 대신 "ChatMsg" 같은 문자열로
    };

    public static byte[] ToBytes(Packet packet)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(packet, Options);
        if (body.Length > MaxBodySize)
            throw new InvalidDataException($"패킷이 너무 큽니다: {body.Length} bytes");

        byte[] buffer = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, body.Length);
        body.CopyTo(buffer, HeaderSize);
        return buffer;
    }

    public static Packet FromBody(ReadOnlySpan<byte> body)
        => JsonSerializer.Deserialize<Packet>(body, Options)
           ?? throw new InvalidDataException("패킷 본문이 비어 있습니다.");
}
