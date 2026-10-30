using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ChatApp.Client.Ui;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ImageTools.cs - 사진 파일을 서버에 올릴 크기로 줄이기 (프로필 사진·배경 사진)
// - 휴대폰 사진의 회전 정보(EXIF)를 반영하고, 원하는 비율로 가운데를 잘라 지정한 크기의 JPEG로 만든다.
// - [왜] 원본(수 MB)을 그대로 보내면 서버 저장 공간과 모두의 네트워크를 낭비한다. 화면에 필요한 크기만 보낸다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   사진 파일을 서버에 올리기 전에 "정해진 크기·비율의 작은 JPEG"로 줄이는 도구(MakeJpeg)와, 그림을 비율 유지로 영역에 가득 채워 그리는 도구(DrawCover). 크기·용량 한도(프로필 160×160/256KB, 배경 720×960/600KB)는 호출하는 MainForm.Profile이 정해 넘긴다.
//
// ■ MakeJpeg 순서 (한 번에 읽기)
//   ① Image.FromFile로 열기 → ② EXIF 회전 표시를 실제 회전으로 반영(휴대폰 사진은 눕혀 저장되고 "돌려 보라"는 표시만 있는 경우가 많음)
//   ③ 목표 비율(width:height)로 가운데를 잘라낼 사각형(crop) 계산 → ④ 목표 크기의 새 Bitmap에 흰 배경 + 고품질 보간으로 그리기(투명 PNG가 검게 변하지 않게)
//   ⑤ 화질을 88 → 78 → 65 → 50으로 낮추며 JPEG로 저장해 보고, maxBytes 이하가 되는 첫 결과를 돌려준다. 끝까지 크면 null(호출한 쪽이 "너무 큽니다" 안내).
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 이미지 크기 줄이기와 커버(cover) 방식 ........... "image resize cover crop center aspect ratio"
//   - EXIF Orientation(0x0112) ....................... "EXIF orientation 0x0112 RotateFlip"
//   - JPEG 품질 낮추며 용량 맞추기 ..................... "JPEG quality encoder parameter size limit loop"
//   - GDI+ 객체 using 해제 ........................... "GDI+ Dispose Bitmap Graphics using"
//   - GDI+의 OutOfMemoryException = 형식 오류 ........... "GDI+ OutOfMemoryException invalid image format"
//   - 클립(SetClip) 후 그리기 ........................ "Graphics SetClip Save Restore"
//
// ■ 면접 예상 질문과 답
//   Q1. 사진을 왜 클라이언트에서 줄여서 올리나요?
//       A. 원본(수 MB)을 그대로 올리면 서버 저장 공간과 모든 사용자의 네트워크(사진을 받는 쪽도)가 낭비됩니다. 화면에 필요한 크기만 올리면 서버 부담이 줄고 표시도 빠릅니다. 서버도 용량 한도(바이트)와 형식(JPEG/PNG 여부)을 따로 검사합니다(코드로 확인함, 클라이언트 값은 신뢰하지 않음).
//   Q2. 휴대폰으로 찍은 사진이 옆으로 누워 보이는 문제는 어떻게 해결했나요?
//       A. 사진 파일의 EXIF Orientation 값(3, 6, 8)을 읽어 RotateFlip으로 실제 픽셀을 돌린 뒤 자릅니다. 이 값을 무시하면 다른 프로그램에서는 바로 보이는데 우리 앱에서만 누워 보입니다.
//   Q3. 용량 제한을 맞추는 방법은요?
//       A. 화질을 단계적으로 낮춰 가며 저장해 보고 한도 이하가 되는 첫 결과를 씁니다. 크기가 이미 작게 정해져 있어 대부분 첫 시도에서 끝납니다. 끝까지 한도를 넘으면 null을 돌려 호출한 쪽이 안내하게 합니다.
//   Q4. GDI+ 코드에서 메모리 누수를 어떻게 막았나요?
//       A. Image, Bitmap, Graphics, MemoryStream, EncoderParameters를 모두 using으로 열어 함수를 벗어날 때 해제합니다. 반환하는 것은 byte[] 복사본이라 원본 객체가 밖으로 새지 않습니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - EXIF 회전은 3·6·8(회전만)만 처리하고, 좌우 반전이 섞인 값(2, 4, 5, 7)은 처리하지 않는다. 일반 카메라 사진에서는 거의 나오지 않는다.
//     같은 일을 하는 ImageHelper.ApplyExifRotation은 1~8을 모두 처리한다(코드로 확인함) → 두 곳이 어긋나 있으니 합칠 후보.
//   - 그림이 아닌 파일은 GDI+가 OutOfMemoryException으로 알린다(이름과 달리 형식 오류). 호출하는 MainForm.Profile이 이 예외를 잡아 안내한다(코드로 확인함).
//   - 투명 PNG는 흰 배경으로 합쳐진다(JPEG가 투명을 지원하지 않기 때문).
// ───────────────────────────────────────────────────────────────────────────
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
        // [읽는 법] 원본 Image 객체를 직접 회전시킨다(파일은 그대로, 메모리 속 그림만). 그래서 아래 source.Width/Height는 회전 후의 값이다.
        ApplyExifRotation(source);

        // 원하는 비율(width:height)로 가운데 잘라 내기
        double target = (double)width / height;
        int cropW = source.Width, cropH = source.Height;
        // [읽는 법] 원본이 목표보다 "더 옆으로 넓으면" 좌우를 잘라내고, 아니면 위아래를 잘라낸다. 둘 다 가운데를 기준으로 자르므로 얼굴처럼 가운데에 있는 대상이 남는다.
        if ((double)source.Width / source.Height > target) cropW = (int)Math.Round(source.Height * target); // 옆으로 넓음 → 좌우를 자름
        else cropH = (int)Math.Round(source.Width / target);                                                  // 위아래로 긺 → 위아래를 자름
        var crop = new Rectangle((source.Width - cropW) / 2, (source.Height - cropH) / 2, cropW, cropH);

        using var bitmap = new Bitmap(width, height);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            // [왜] 투명한 PNG를 JPEG로 저장하면 투명 부분이 검게 변한다. 먼저 흰색으로 칠해 두면 흰 배경이 된다.
            g.Clear(Color.White); // 투명 PNG도 JPEG로 바꿀 때 검게 되지 않게
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, width, height), crop, GraphicsUnit.Pixel);
        }

        ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        // [개념] 반복마다 MemoryStream과 EncoderParameters를 새로 만들고 using으로 해제한다. 품질(Encoder.Quality)을 낮출수록 용량이 줄고 화질이 떨어진다.
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
    // [읽는 법] "커버" = 비율을 유지한 채 영역을 빈틈없이 덮도록 확대(둘 중 큰 배율 선택, Max)하고, 넘치는 부분은 SetClip으로 잘라낸다. 반대는 "맞춤(contain)"으로 비율 유지 + 전체가 보이게 축소(Min)한다.
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
