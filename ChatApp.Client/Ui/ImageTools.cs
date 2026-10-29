using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ChatApp.Client.Ui;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ImageTools.cs - 사진 파일을 서버에 올릴 크기로 줄이기 (프로필 사진·배경 사진)
// - 휴대폰 사진의 회전 정보(EXIF)를 반영하고, 원하는 비율로 가운데를 잘라 지정한 크기의 JPEG로 만든다.
// - [왜] 원본(수 MB)을 그대로 보내면 서버 저장 공간과 모두의 네트워크를 낭비한다. 화면에 필요한 크기만 보낸다.
// ─────────────────────────────────────────────────────────────────────────────
internal static class ImageTools
{
    /// <summary>
    /// 사진 파일 → width×height JPEG. 비율이 다르면 가운데를 잘라 맞춘다(화면을 가득 채우는 "커버" 방식).
    /// maxBytes 안에 들어가게 화질을 낮춰 가며 시도하고, 그래도 크면 null.
    /// </summary>
    /// <exception cref="OutOfMemoryException">그림이 아닌 파일 (GDI+는 형식 오류를 이 예외로 알린다)</exception>
    public static byte[]? MakeJpeg(string path, int width, int height, int maxBytes)
    {
        using Image source = Image.FromFile(path);
        ApplyExifRotation(source);

        // 원하는 비율(width:height)로 가운데 잘라 내기
        double target = (double)width / height;
        int cropW = source.Width, cropH = source.Height;
        if ((double)source.Width / source.Height > target) cropW = (int)Math.Round(source.Height * target); // 옆으로 넓음 → 좌우를 자름
        else cropH = (int)Math.Round(source.Width / target);                                                  // 위아래로 긺 → 위아래를 자름
        var crop = new Rectangle((source.Width - cropW) / 2, (source.Height - cropH) / 2, cropW, cropH);

        using var bitmap = new Bitmap(width, height);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White); // 투명 PNG도 JPEG로 바꿀 때 검게 되지 않게
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, width, height), crop, GraphicsUnit.Pixel);
        }

        ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        foreach (long quality in new long[] { 88, 78, 65, 50 })
        {
            using var ms = new MemoryStream();
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(ms, jpeg, parameters);
            if (ms.Length <= maxBytes) return ms.ToArray();
        }
        return null;
    }

    /// <summary>휴대폰 사진은 픽셀은 눕혀 두고 "회전해서 보라"는 표시(EXIF 0x0112)만 붙어 있는 경우가 많다 → 실제로 돌려 둔다.</summary>
    private static void ApplyExifRotation(Image image)
    {
        const int OrientationId = 0x0112;
        if (!image.PropertyIdList.Contains(OrientationId) || image.GetPropertyItem(OrientationId)?.Value is not { Length: > 0 } value) return;
        RotateFlipType rotate = value[0] switch
        {
            3 => RotateFlipType.Rotate180FlipNone,
            6 => RotateFlipType.Rotate90FlipNone,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (rotate != RotateFlipType.RotateNoneFlipNone) image.RotateFlip(rotate);
    }

    /// <summary>그림을 rect에 "커버" 방식으로 그린다 (비율 유지, 넘치는 부분은 잘림).</summary>
    public static void DrawCover(Graphics g, Image image, Rectangle rect)
    {
        double scale = Math.Max((double)rect.Width / image.Width, (double)rect.Height / image.Height);
        int w = (int)Math.Ceiling(image.Width * scale), h = (int)Math.Ceiling(image.Height * scale);
        GraphicsState state = g.Save();
        g.SetClip(rect);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, new Rectangle(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h));
        g.Restore(state);
    }
}
