using System.Buffers.Binary;
using ChatApp.Common.Protocol;

namespace ChatApp.Common.Utils;

/// <summary>
/// 스트림에서 패킷 1개를 읽고 쓰는 도우미. 서버와 클라이언트가 똑같이 사용한다.
/// </summary>
public static class NetworkHelper
{
    /// <summary>
    /// 패킷 1개를 읽을 때까지 블로킹한다. 상대가 연결을 정상 종료하면 null을 반환한다.
    /// </summary>
    public static Packet? ReadPacket(Stream stream)
    {
        byte[] header = new byte[PacketSerializer.HeaderSize];
        try
        {
            // ReadExactly: 요청한 바이트 수가 다 찰 때까지 읽는다(TCP 분할 수신 대응).
            stream.ReadExactly(header);
        }
        catch (EndOfStreamException)
        {
            return null; // 상대가 연결을 닫음
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > PacketSerializer.MaxBodySize)
            throw new InvalidDataException($"잘못된 패킷 길이: {length}");

        byte[] body = new byte[length];
        stream.ReadExactly(body);
        return PacketSerializer.FromBody(body);
    }

    /// <summary>
    /// 패킷 1개를 쓴다. 스레드 안전하지 않으므로 호출하는 쪽에서 lock을 걸어야 한다.
    /// </summary>
    public static void WritePacket(Stream stream, Packet packet)
    {
        byte[] data = PacketSerializer.ToBytes(packet);
        stream.Write(data);
        stream.Flush();
    }
}
