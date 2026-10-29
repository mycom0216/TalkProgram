using ChatApp.Common.Models;

namespace ChatApp.Tests;

// 프로필 사진 형식 확인 (서버가 그림이 아닌 파일을 사진으로 저장하지 않게)
public class ProfileRulesTests
{
    [Fact]
    public void JPEG와_PNG만_사진으로_본다()
    {
        Assert.True(ProfileRules.IsSupportedImage([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46]));          // JPEG
        Assert.True(ProfileRules.IsSupportedImage([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));     // PNG
        Assert.False(ProfileRules.IsSupportedImage("<script>alert(1)</script>"u8.ToArray()));               // 글자
        Assert.False(ProfileRules.IsSupportedImage([0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0]));                     // 실행 파일(MZ)
        Assert.False(ProfileRules.IsSupportedImage([0xFF, 0xD8]));                                           // 너무 짧음
        Assert.False(ProfileRules.IsSupportedImage(null));
    }

    [Fact]
    public void 사진_크기_제한은_패킷_한도_안에_들어간다()
    {
        // 사진은 Base64로 바뀌어 약 4/3배가 된다. 패킷 한도(1MB)를 넘으면 연결이 끊기므로 여유 있게 작아야 한다.
        Assert.True(ProfileRules.MaxPhotoBytes * 4 / 3 + 4096 < ChatApp.Common.Protocol.PacketSerializer.MaxBodySize);
        Assert.True(ProfileRules.MaxBackgroundBytes * 4 / 3 + 4096 < ChatApp.Common.Protocol.PacketSerializer.MaxBodySize);
        Assert.Equal(ProfileRules.MaxBackgroundBytes, ProfileRules.MaxBytes(PhotoKind.Background));
        Assert.Equal(ProfileRules.MaxPhotoBytes, ProfileRules.MaxBytes(PhotoKind.Profile));
    }
}
