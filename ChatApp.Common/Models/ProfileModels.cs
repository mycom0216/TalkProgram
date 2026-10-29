namespace ChatApp.Common.Models;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ProfileModels.cs - 프로필 사진 규칙 (서버·클라이언트 공통)
// - 클라이언트가 고른 사진을 정사각형 160×160 JPEG로 줄여서 보낸다(보통 10~30KB). 서버는 크기와 형식만 확인해 저장한다.
// - 누가 사진을 바꿨는지는 UserInfo.PhotoVersion(바꾼 시각 숫자)으로 알린다. 버전이 바뀌면 그때만 새로 받는다(캐시).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>프로필 사진 규칙.</summary>
public static class ProfileRules
{
    /// <summary>저장하는 사진 한 변의 픽셀 수 (정사각형).</summary>
    public const int PhotoSize = 160;

    /// <summary>사진 한 장의 최대 크기. 160×160 JPEG는 보통 30KB 안쪽이라 넉넉하다.</summary>
    public const int MaxPhotoBytes = 256 * 1024;

    /// <summary>JPEG 또는 PNG 파일인지 (파일 맨 앞 바이트로 확인). 그림이 아닌 파일을 사진이라고 올리는 것을 막는다.</summary>
    public static bool IsSupportedImage(byte[]? data)
    {
        if (data is null || data.Length < 8) return false;
        bool jpeg = data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
        bool png = data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 && data[4] == 0x0D && data[5] == 0x0A;
        return jpeg || png;
    }
}

/// <summary>ProfilePhoto 패킷의 Data: 사진 버전 (0 = 사진 없음). 사진 바이트는 Packet.Bin.</summary>
public sealed class PhotoInfo
{
    public long Version { get; set; }
}
