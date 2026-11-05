namespace ChatApp.Common.Models;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ProfileModels.cs - 프로필 사진·배경 사진 규칙 (서버·클라이언트 공통)
// - 프로필 사진: 정사각형 160×160 JPEG (보통 10~30KB)
// - 배경 사진: 세로 3:4, 720×960 JPEG (보통 80~200KB) — 프로필 카드 뒤에 깔린다
// - 클라이언트가 줄여서 보내고, 서버는 크기와 형식(JPEG·PNG)만 확인해 저장한다.
// - 누가 사진을 바꿨는지는 UserInfo.PhotoVersion / BackgroundVersion(바꾼 시각 숫자)으로 알린다. 버전이 바뀔 때만 새로 받는다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>어떤 사진인지.</summary>
public enum PhotoKind
{
    /// <summary>프로필 사진 (동그란 아바타)</summary>
    Profile,
    /// <summary>배경 사진 (프로필 카드 뒤)</summary>
    Background,
}

/// <summary>사진 규칙.</summary>
public static class ProfileRules
{
    /// <summary>프로필 사진 한 변의 픽셀 수 (정사각형).</summary>
    public const int PhotoSize = 160;

    /// <summary>프로필 사진 최대 크기. 160×160 JPEG는 보통 30KB 안쪽이라 넉넉하다.</summary>
    public const int MaxPhotoBytes = 256 * 1024;

    /// <summary>배경 사진 크기 (세로 3:4, 프로필 카드 비율).</summary>
    public const int BackgroundWidth = 720, BackgroundHeight = 960;

    /// <summary>배경 사진 최대 크기. Base64로 보내면 약 4/3배가 되어도 패킷 한도(1MB) 안에 들어간다.</summary>
    public const int MaxBackgroundBytes = 600 * 1024;

    /// <summary>사진 종류별 최대 크기(바이트)를 돌려준다(배경 600KB, 프로필 256KB).</summary>
    public static int MaxBytes(PhotoKind kind) => kind == PhotoKind.Background ? MaxBackgroundBytes : MaxPhotoBytes;

    /// <summary>JPEG 또는 PNG 파일인지 (파일 맨 앞 바이트로 확인). 그림이 아닌 파일을 사진이라고 올리는 것을 막는다.</summary>
    public static bool IsSupportedImage(byte[]? data)
    {
        if (data is null || data.Length < 8) return false;
        bool jpeg = data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
        bool png = data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 && data[4] == 0x0D && data[5] == 0x0A;
        return jpeg || png;
    }
}

/// <summary>
/// ProfilePhotoUpdate·ProfilePhotoReq·ProfilePhoto 패킷의 Data. 사진 바이트는 Packet.Bin.
/// 요청할 때는 Kind만, 응답에는 Kind와 Version(0 = 사진 없음)이 들어 있다.
/// </summary>
public sealed class PhotoInfo
{
    /// <summary>어떤 사진인지(프로필 / 배경).</summary>
    public PhotoKind Kind { get; set; }
    /// <summary>사진 버전(바꾼 시각 숫자, 0 = 사진 없음). 요청할 때는 쓰지 않는다.</summary>
    public long Version { get; set; }
}
