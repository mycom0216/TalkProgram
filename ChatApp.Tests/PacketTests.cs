using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// 패킷 직렬화(JSON) 테스트: 보낸 값이 받는 쪽에서 "그대로" 복원되는지.
// [개념] 단위 테스트(unit test): 프로그램의 작은 조각(메서드 하나)을 따로 떼어 "이 입력이면 이 결과"를 자동으로 확인하는 코드.
//        나중에 코드를 고쳤을 때 예전 기능이 깨졌는지(회귀, regression) 사람이 일일이 눌러 보지 않아도 바로 알 수 있다.
// [개념] [Fact] = 입력이 고정된 테스트 하나. [Theory] + [InlineData] = 같은 테스트를 여러 입력으로 반복.
// [개념] Arrange(준비) → Act(실행) → Assert(확인) 순서로 쓰면 읽기 쉽다.
// ─────────────────────────────────────────────────────────────────────────────

public class PacketTests
{
    // 이 테스트는 “기본 칸들이 직렬화 후 그대로 복원된다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 기본_칸들이_직렬화_후_그대로_복원된다()
    {
        // Arrange
        var original = Packet.Create(PacketType.ChatMsg, "안녕하세요 😀", sender: "홍길동 대리", target: "1", success: false);
        original.SenderId = "A0001";
        original.Timestamp = new DateTime(2026, 10, 28, 12, 34, 56, 789);

        // Act: 헤더(4바이트)를 뺀 본문만 다시 읽는다
        byte[] wire = PacketSerializer.ToBytes(original);
        Packet copy = PacketSerializer.FromBody(wire.AsSpan(PacketSerializer.HeaderSize));

        // Assert
        Assert.Equal(PacketType.ChatMsg, copy.Type);
        Assert.Equal("안녕하세요 😀", copy.Body);
        Assert.Equal("홍길동 대리", copy.Sender);
        Assert.Equal("A0001", copy.SenderId);
        Assert.Equal("1", copy.Target);
        Assert.False(copy.Success);
        Assert.Equal(original.Timestamp, copy.Timestamp);
    }

    // 이 테스트는 “헤더의 길이값이 본문 길이와 같다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 헤더의_길이값이_본문_길이와_같다()
    {
        byte[] wire = PacketSerializer.ToBytes(Packet.Create(PacketType.Ping));
        int length = BitConverter.ToInt32(wire, 0); // 리틀 엔디언 (x86/x64 기본)
        Assert.Equal(wire.Length - PacketSerializer.HeaderSize, length);
    }

    // 이 테스트는 “패킷 종류는 숫자가 아니라 이름으로 보낸다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 패킷_종류는_숫자가_아니라_이름으로_보낸다()
    {
        // [왜] 이름으로 보내야 enum 중간에 새 값을 끼워 넣어도 옛 클라이언트와 번호가 어긋나지 않는다(PacketType.cs 주의 참고).
        byte[] wire = PacketSerializer.ToBytes(Packet.Create(PacketType.AdminActionReq));
        string json = System.Text.Encoding.UTF8.GetString(wire, PacketSerializer.HeaderSize, wire.Length - PacketSerializer.HeaderSize);
        Assert.Contains("\"AdminActionReq\"", json);
    }

    // 이 테스트는 “Data에 담은 객체를 GetData로 꺼낼 수 있다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void Data에_담은_객체를_GetData로_꺼낼_수_있다()
    {
        var user = new UserInfo { EmployeeNo = "A0001", Name = "홍길동", Position = "대리", Role = "ADMIN", MustChangePassword = true };
        Packet packet = Roundtrip(Packet.Create(PacketType.LoginRes).WithData(user));

        UserInfo? copy = packet.GetData<UserInfo>();
        Assert.NotNull(copy);
        Assert.Equal("홍길동 대리", copy!.DisplayName);
        Assert.True(copy.IsAdmin);
        Assert.True(copy.MustChangePassword);
    }

    // 이 테스트는 “파일 조각 바이트가 그대로 복원된다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 파일_조각_바이트가_그대로_복원된다()
    {
        byte[] chunk = new byte[1000];
        new Random(42).NextBytes(chunk);
        Packet copy = Roundtrip(new Packet { Type = PacketType.FileChunk, Bin = chunk });
        Assert.Equal(chunk, copy.Bin);
    }

    // 이 테스트는 “같은 패킷을 여러 명에게 보낼 때 바이트를 한 번만 만든다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 같은_패킷을_여러_명에게_보낼_때_바이트를_한_번만_만든다()
    {
        // 300명 방에 메시지 1개를 보낼 때 JSON 변환을 300번 하지 않게 하는 최적화(부하 테스트로 찾은 병목)
        Packet packet = Packet.Create(PacketType.ChatMsg, "공지");
        Assert.Same(packet.ToWireBytes(), packet.ToWireBytes());
    }

    // 이 테스트는 “깨진 Data는 예외 없이 null이 된다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 깨진_Data는_예외_없이_null이_된다()
    {
        var packet = new Packet { Type = PacketType.LoginRes, Data = "{이건 JSON이 아님" };
        Assert.Null(packet.GetData<UserInfo>());
    }

    private static Packet Roundtrip(Packet packet)
    {
        byte[] wire = PacketSerializer.ToBytes(packet);
        return PacketSerializer.FromBody(wire.AsSpan(PacketSerializer.HeaderSize));
    }
}
