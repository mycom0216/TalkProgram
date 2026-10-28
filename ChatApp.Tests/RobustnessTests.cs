using System.Text;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Common.Utils;

namespace ChatApp.Tests;

// 서버 안정성 개선(1순위)과 관련된 테스트: 글자 자르기, 모르는 패킷 종류, 프로토콜 버전.
public class RobustnessTests
{
    [Fact]
    public void 짧은_글은_그대로_둔다() => Assert.Equal("안녕", TextUtil.Truncate("안녕", 500));

    [Fact]
    public void 이모지를_반쪽으로_자르지_않는다()
    {
        // 😀는 UTF-16으로 2칸(서로게이트 쌍). 4칸에서 자르면 "가나" + 😀 반쪽이 될 수 있다.
        string text = "가나😀다";
        string cut = TextUtil.Truncate(text, 3);
        Assert.Equal("가나", cut);                          // 반쪽 이모지 대신 이모지 전체를 뺀다
        Assert.Equal("가나😀", TextUtil.Truncate(text, 4)); // 이모지 전체가 들어가면 포함
    }

    [Fact]
    public void 긴_글은_최대_길이를_넘지_않는다()
    {
        string text = string.Concat(Enumerable.Repeat("😀가", 400)); // 1200칸
        string cut = TextUtil.Truncate(text, 500);
        Assert.True(cut.Length <= 500);
        Assert.False(char.IsHighSurrogate(cut[^1])); // 끝이 이모지 반쪽이 아니다
    }

    [Fact]
    public void 모르는_패킷_종류는_예외_없이_Unknown이_된다()
    {
        // 더 새 버전 서버가 보낸, 이 프로그램에 없는 패킷 종류
        byte[] json = Encoding.UTF8.GetBytes("""{"Type":"SomethingFromTheFuture","Body":"hi"}""");
        Packet packet = PacketSerializer.FromBody(json);
        Assert.Equal(PacketType.Unknown, packet.Type);
        Assert.Equal("hi", packet.Body); // 나머지 칸은 정상
    }

    [Fact]
    public void 아는_패킷_종류는_이름으로_읽는다()
    {
        Packet packet = PacketSerializer.FromBody(Encoding.UTF8.GetBytes("""{"Type":"ChatMsg"}"""));
        Assert.Equal(PacketType.ChatMsg, packet.Type);
    }

    [Fact]
    public void 버전_칸이_없는_옛_로그인_요청은_0으로_읽힌다()
    {
        // 기본값이 최신 버전이면, 버전을 모르는 옛 클라이언트가 최신으로 보이는 문제가 생긴다
        var packet = new Packet { Type = PacketType.LoginReq, Data = """{"EmployeeNo":"A0001","Password":"x"}""" };
        Assert.Equal(0, packet.GetData<LoginRequest>()!.ProtocolVersion);
        Assert.True(ProtocolInfo.MinSupported > 0);
    }
}
