// ─────────────────────────────────────────────────────────────────────────────
// ImageHelper.cs
// 역할  : 사진 파일 → 작은 JPEG 미리보기(썸네일) 바이트 만들기, 받은 미리보기 바이트 → Image 되살리기,
//         클립보드 캡처 이미지를 임시 PNG 파일로 저장하기.
// 스레드: CreateThumbnail은 MainForm.SendFiles에서 Task.Run(작업 스레드)으로 호출된다. 큰 사진을 줄이는 데
//         시간이 걸려도 화면(UI 스레드)이 멈추지 않게 하기 위함이다. 각 호출이 자기 Image 객체만 쓰므로 동시에 불려도 안전하다.
//         Load는 ChatFile.Thumbnail(UI 스레드)에서, SaveClipboardImage는 ChatWindow(UI 스레드)에서 호출된다.
// 연결  : 만든 미리보기는 FileOffer.Thumbnail로 서버에 함께 올라가고, 받는 쪽은 SharedFile.Thumbnail로 받아
//         BubbleChatView가 말풍선 안에 그린다. 원본 사진 전체를 받지 않고도 바로 사진을 볼 수 있게 하는 장치다.
// ─────────────────────────────────────────────────────────────────────────────
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ChatApp.Common.Models;

namespace ChatApp.Client.Ui;

/// <summary>이미지 미리보기(썸네일) 만들기 / 읽기.</summary>
internal static class ImageHelper
{
    // 미리보기의 긴 변 최대 픽셀. 말풍선 최대 크기(약 220~260px)보다 조금 크게 만들어 고배율 화면에서도 선명하게.
    private const int ThumbnailMaxSide = 360;
    // 받은 미리보기로 허용하는 가장 긴 변 (만드는 쪽은 360px이므로 넉넉하게)
    private const int MaxPreviewSide = 2048;
    // [개념] EXIF: 카메라·휴대폰이 사진 파일 안에 함께 저장하는 정보(촬영 날짜, 기종, 방향 등).
    //        0x0112는 그중 "방향(Orientation)" 항목의 번호(태그 ID)다.
    private const int ExifOrientation = 0x0112;

    /// <summary>
    /// 이미지 파일로 JPEG 미리보기를 만든다(긴 변 360px). 이미지가 아니거나 읽지 못하면 null → 파일 카드로 보낸다.
    /// 휴대폰 사진은 EXIF 회전 정보대로 돌려서 만든다.
    /// </summary>
    // [왜] 미리보기를 "보내는 쪽"에서 만든다. 서버(콘솔 프로그램, 리눅스에서도 돌 수 있음)는 그림을 다루지 않아도 되고,
    //      받는 사람은 수 MB짜리 원본 대신 수십 KB 미리보기만 받아 바로 화면에 띄울 수 있다.
    public static byte[]? CreateThumbnail(string path)
    {
        // 확장자로 사진이 아니라고 판단되면 바로 포기 (동영상·문서는 파일 카드로 표시)
        if (FileRules.GetKind(path) != FileKind.Image) return null;
        try
        {
            // [주의] 아주 큰 사진(예: 100MB 파노라마)을 통째로 메모리에 펼치면 메모리가 부족해질 수 있어 건너뛴다
            if (new FileInfo(path).Length > FileRules.MaxThumbnailSourceSize) return null; // 너무 큰 사진은 파일 카드로
            // [개념] FileShare.ReadWrite: 다른 프로그램(사진 편집기 등)이 이 파일을 열고 있어도 읽을 수 있게 허용한다.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // [개념] Image.FromStream: 파일 내용을 해석해 그림 객체로 만든다(GDI+).
            //        validateImageData: true → 그림 데이터가 올바른지 미리 검사해서 깨진 파일이면 여기서 예외가 나게 한다.
            //        useEmbeddedColorManagement: false → 파일에 든 색 프로필은 무시(미리보기라 정확한 색 보정은 필요 없음).
            using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            ApplyExifRotation(source);

            // 긴 변이 360px이 되도록 비율(scale) 계산. 원래 작은 사진은 키우지 않도록 Math.Min(1.0, ...)
            double scale = Math.Min(1.0, (double)ThumbnailMaxSide / Math.Max(source.Width, source.Height));
            // [주의] 아주 길쭉한 사진은 짧은 변이 0이 될 수 있어 최소 1px로 막는다(0 크기 Bitmap은 예외)
            int w = Math.Max(1, (int)(source.Width * scale)), h = Math.Max(1, (int)(source.Height * scale));

            // [개념] Bitmap = 메모리 위의 빈 도화지. Graphics.FromImage로 그 도화지에 그릴 붓을 얻는다.
            using var thumb = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(thumb))
            {
                g.Clear(Color.White); // 투명 PNG도 JPEG로 바꾸므로 흰 바탕
                // [개념] InterpolationMode(보간): 그림을 줄일 때 여러 픽셀을 어떻게 섞어 한 픽셀로 만들지.
                //        HighQualityBicubic이 가장 부드럽고 선명하다(대신 느리지만 작업 스레드라 괜찮다).
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // PixelOffsetMode.HighQuality: 픽셀 중심을 정확히 맞춰 가장자리가 반 픽셀 밀리는 현상을 막는다
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, 0, 0, w, h);
            }

            // [왜] 미리보기 크기 상한(MaxThumbnailBytes, 200KB)을 넘으면 서버가 거절하므로,
            //      화질을 82 → 65 → 45로 낮춰 가며 크기가 들어갈 때까지 다시 압축해 본다.
            foreach (long quality in new long[] { 82, 65, 45 })
            {
                byte[] jpeg = EncodeJpeg(thumb, quality);
                if (jpeg.Length <= FileRules.MaxThumbnailBytes) return jpeg;
            }
            return null; // 그래도 크면 미리보기 없이 파일 카드로
        }
        // [개념] 예외 필터(when): 아래 종류의 예외만 잡고, 그 밖의 예상 못 한 예외는 그대로 위로 올려 버그를 숨기지 않는다.
        //  - ArgumentException/ExternalException/OutOfMemoryException: GDI+가 그림을 해석하지 못할 때 내는 예외들
        //    (GDI+는 지원하지 않는 형식에도 "메모리 부족" 예외를 내는 오래된 습성이 있다)
        //  - IOException/UnauthorizedAccessException: 파일이 지워졌거나 읽을 권한이 없을 때
        catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException or ExternalException or UnauthorizedAccessException)
        {
            return null; // GDI+가 못 읽는 형식(webp, heic 등) → 파일 카드
        }
    }

    /// <summary>받은 미리보기 바이트 → Image. 깨진 데이터면 null.</summary>
    // [주의] 다른 사람이 보낸(=믿을 수 없는) 데이터이므로, 깨졌거나 조작된 바이트여도 프로그램이 죽지 않게 예외를 잡는다.
    public static Image? Load(byte[]? data)
    {
        // [개념] 속성 패턴: data is not { Length: > 0 } = "null이거나 길이가 0이면"
        if (data is not { Length: > 0 }) return null;
        try
        {
            // [개념] MemoryStream: 바이트 배열을 파일처럼 읽을 수 있게 감싸는 스트림
            using var stream = new MemoryStream(data);
            using var image = Image.FromStream(stream);
            // [주의] 바이트는 작아도(수십 KB) 가로·세로가 수만 픽셀인 "압축 폭탄" 그림이 올 수 있다.
            //        그대로 복사하면 수 GB 메모리를 잡아 프로그램이 멈춘다. 미리보기는 360px이면 충분하므로 큰 것은 버린다.
            if (image.Width is <= 0 or > MaxPreviewSide || image.Height is <= 0 or > MaxPreviewSide) return null;
            // [주의] Image.FromStream으로 만든 그림은 "스트림이 살아 있는 동안만" 안전하게 쓸 수 있다(GDI+가 나중에 다시 읽음).
            //        스트림은 using으로 곧 닫히므로, new Bitmap(image)로 픽셀을 복사한 독립된 그림을 만들어 돌려준다.
            return new Bitmap(image); // 스트림과 분리된 복사본
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException)
        {
            return null;
        }
    }

    /// <summary>클립보드 이미지(캡처 등)를 임시 PNG로 저장하고 경로를 돌려준다.</summary>
    // [왜] 파일 전송 엔진(FileTransferService)은 "파일 경로"를 받아 보내는 구조이므로,
    //      클립보드에만 있는 캡처 이미지는 먼저 임시 폴더에 파일로 저장한 뒤 그 경로를 넘긴다.
    public static string SaveClipboardImage(Image image)
    {
        // %TEMP%\사내톡 폴더 (윈도우가 관리하는 임시 폴더라 나중에 정리되어도 문제없음)
        string folder = Path.Combine(Path.GetTempPath(), "사내톡");
        Directory.CreateDirectory(folder); // 이미 있으면 아무 일도 하지 않는다
        CleanOldCaptures(folder);
        // 파일 이름에 날짜·시각(밀리초)과 짧은 무작위 글자를 넣는다.
        // [주의] 초 단위만 쓰면 1초 안에 두 번 붙여 넣었을 때 같은 이름이 되어, 아직 보내는 중인 첫 캡처 파일을 덮어쓴다.
        string path = Path.Combine(folder, $"캡처_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..4]}.png");
        // [왜] 캡처 화면(글자·선이 많음)은 PNG(무손실)가 JPEG보다 글자가 깨지지 않는다
        image.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>하루 넘게 지난 캡처 임시 파일을 지운다 (보내기가 끝난 지 오래된 것). 지우지 못하면(사용 중) 그냥 둔다.</summary>
    private static void CleanOldCaptures(string folder)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "캡처_*.png"))
            {
                if (DateTime.Now - File.GetLastWriteTime(file) < TimeSpan.FromDays(1)) continue;
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// [개념] JPEG 품질 인코딩: JPEG은 화질을 0~100으로 정할 수 있고, 낮출수록 파일이 작아지는 대신 뭉개진다.
    /// Image.Save(stream, ImageFormat.Jpeg)만 쓰면 품질을 정할 수 없어서, JPEG 인코더(codec)를 찾아 Quality 값을 직접 넘긴다.
    /// </summary>
    private static byte[] EncodeJpeg(Image image, long quality)
    {
        // 설치된 이미지 인코더 중 JPEG 형식(FormatID가 같은 것)을 찾는다
        ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        // [주의] "Encoder"라는 이름이 System.Text.Encoder와 겹칠 수 있어 전체 이름(System.Drawing.Imaging.Encoder)으로 쓴다
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var output = new MemoryStream(); // 파일 대신 메모리에 저장해서 바이트 배열로 꺼낸다
        image.Save(output, codec, parameters);
        return output.ToArray();
    }

    /// <summary>
    /// [개념] EXIF 회전: 휴대폰은 사진을 돌려서 저장하지 않고, "이 사진은 90도 돌려서 보라"는 방향 값만 EXIF에 적어 둔다.
    /// 그대로 그리면 사진이 옆으로 누워 보이므로, 방향 값(1~8)에 맞게 실제 픽셀을 돌리거나 뒤집는다.
    /// </summary>
    private static void ApplyExifRotation(Image image)
    {
        // 방향 정보가 없는 사진(대부분의 캡처·웹 이미지)은 그대로 둔다
        if (!image.PropertyIdList.Contains(ExifOrientation)) return;
        byte[]? value = image.GetPropertyItem(ExifOrientation)?.Value;
        if (value is not { Length: > 0 }) return;
        // EXIF 표준의 방향 값 → 돌리기/뒤집기 방법 (1은 정상이라 아무것도 안 함)
        // [개념] switch 식(expression): 값에 따라 결과 하나를 고르는 짧은 문법. _ 는 "그 밖의 모든 값".
        RotateFlipType rotate = value[0] switch
        {
            2 => RotateFlipType.RotateNoneFlipX,    // 좌우 반전
            3 => RotateFlipType.Rotate180FlipNone,  // 180도
            4 => RotateFlipType.Rotate180FlipX,     // 상하 반전
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,   // 시계 방향 90도 (휴대폰을 세로로 들고 찍은 사진에 가장 흔함)
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,  // 반시계 방향 90도
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (rotate != RotateFlipType.RotateNoneFlipNone) image.RotateFlip(rotate);
    }
}
