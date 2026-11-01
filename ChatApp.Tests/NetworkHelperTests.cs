using ChatApp.Common.Protocol;
using ChatApp.Common.Utils;

namespace ChatApp.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 패킷 경계(framing) 테스트: [길이 4바이트][JSON] 형식으로 여러 패킷이 이어져 와도 정확히 하나씩 잘라 읽는지.
// [왜] TCP는 "바이트 흐름"이라 보낸 단위 그대로 오지 않는다. 패킷 2개가 한 번에 붙어 오거나,
//      패킷 하나가 여러 조각으로 쪼개져 올 수 있다. 실제 네트워크 없이 MemoryStream으로 그 상황을 흉내 낸다.
// ─────────────────────────────────────────────────────────────────────────────

public class NetworkHelperTests
{
// ===============
    // 이 테스트는 “연달아 쓴 패킷을 순서대로 하나씩 읽는다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 연달아_쓴_패킷을_순서대로_하나씩_읽는다()
    {
        using var stream = new MemoryStream();
        for (int i = 1; i <= 3; i++) NetworkHelper.WritePacket(stream, Packet.Create(PacketType.ChatMsg, $"메시지 {i}"));
        stream.Position = 0;

        Assert.Equal("메시지 1", NetworkHelper.ReadPacket(stream)?.Body);
        Assert.Equal("메시지 2", NetworkHelper.ReadPacket(stream)?.Body);
        Assert.Equal("메시지 3", NetworkHelper.ReadPacket(stream)?.Body);
        Assert.Null(NetworkHelper.ReadPacket(stream)); // 끝(상대가 정상 종료)이면 null
    }

// ===============
    // 이 테스트는 “바이트가 한 개씩 쪼개져 와도 패킷을 온전히 읽는다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 바이트가_한_개씩_쪼개져_와도_패킷을_온전히_읽는다()
    {
        using var buffer = new MemoryStream();
        NetworkHelper.WritePacket(buffer, Packet.Create(PacketType.ChatMsg, "쪼개진 메시지"));
        NetworkHelper.WritePacket(buffer, Packet.Create(PacketType.Ping));

        // 한 번에 1바이트씩만 주는 스트림 = 가장 심하게 쪼개진 TCP 수신
        using var trickle = new TrickleStream(buffer.ToArray());
        Assert.Equal("쪼개진 메시지", NetworkHelper.ReadPacket(trickle)?.Body);
        Assert.Equal(PacketType.Ping, NetworkHelper.ReadPacket(trickle)?.Type);
    }

// ===============
    // 이 테스트는 “비정상 길이면 연결을 끊도록 예외를 던진다” 상황이 제대로 처리되는지 확인합니다.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PacketSerializer.MaxBodySize + 1)]
    public void 비정상_길이면_연결을_끊도록_예외를_던진다(int length)
    {
        // [왜] 길이를 믿고 그만큼 메모리를 잡으면, 악의적인 상대가 "20억 바이트"라고 보내 서버 메모리를 고갈시킬 수 있다.
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        Assert.Throws<InvalidDataException>(() => NetworkHelper.ReadPacket(stream));
    }

// ===============
    // 이 테스트는 “본문 도중에 끊기면 예외를 던진다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 본문_도중에_끊기면_예외를_던진다()
    {
        byte[] wire = PacketSerializer.ToBytes(Packet.Create(PacketType.ChatMsg, "끝까지 못 온 메시지"));
        using var stream = new MemoryStream(wire, 0, wire.Length - 5); // 마지막 5바이트가 안 옴
        Assert.Throws<EndOfStreamException>(() => NetworkHelper.ReadPacket(stream));
    }

// ===============
    // 이 테스트는 “평문 서버는 TLS 인사말을 알아보고 바로 거절한다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 평문_서버는_TLS_인사말을_알아보고_바로_거절한다()
    {
        // TLS ClientHello의 앞부분: 0x16(핸드셰이크) 0x03 0x01(버전) + 길이 2바이트
        using var stream = new MemoryStream([0x16, 0x03, 0x01, 0x02, 0x00, 0x01, 0x00]);
        var ex = Assert.Throws<InvalidDataException>(() => NetworkHelper.ReadPacket(stream, checkTlsHello: true));
        Assert.Contains("TLS", ex.Message);
    }

    /// <summary>Read를 부를 때마다 최대 1바이트만 돌려주는 스트림.</summary>
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(1, buffer.Length)]);
    }
}
