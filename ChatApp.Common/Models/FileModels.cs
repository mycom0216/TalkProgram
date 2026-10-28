using System.Text;

namespace ChatApp.Common.Models;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 역할] 파일·사진·동영상 전송에 쓰는 데이터 모양(DTO)과 공통 규칙(FileRules).
//   - FileOffer : "이 파일을 올리겠다"는 요청 (클라이언트 → 서버)
//   - SharedFile: 대화에 올라간 파일 정보 (서버 → 클라이언트, 말풍선 표시용)
//   - FileRules : 최대 크기, 조각 크기, 확장자 분류, 파일 이름 정리 등 서버·클라이언트 공통 규칙
// [연결 관계] 클라이언트 FileTransferService / MainForm, 서버 FileService / MessageStore가 사용한다.
// [왜] 규칙을 Common 한 곳에 두면 클라이언트와 서버가 다른 기준을 쓰는 실수를 막을 수 있다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>파일 종류. 말풍선 모양(사진 미리보기 / 동영상 ▶ 카드 / 일반 파일 카드)을 정하는 데 쓴다.</summary>
public enum FileKind { File, Image, Video }

/// <summary>파일 올리기 요청 (클라이언트 → 서버, FileUploadReq의 Data).</summary>
public sealed class FileOffer
{
    /// <summary>클라이언트가 만든 GUID("N" 형식, 32자). 이후 모든 조각 패킷의 Target.</summary>
    // [왜] ID를 클라이언트가 만들면, 서버 응답을 기다리지 않고도 화면에 "보내는 중" 말풍선을 바로 띄우고
    //   이후 진행률·완료 패킷을 이 ID로 짝지을 수 있다. 서버는 형식(IsValidFileId)과 중복(DB PRIMARY KEY)을 검사한다.
    // [개념] GUID: 전 세계에서 사실상 겹치지 않는 128비트 무작위 값. "N" 형식은 하이픈 없는 16진수 32글자.
    public string FileId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }  // 바이트 단위. 3GB까지 담으려고 long

    /// <summary>방에 보낼 때 방 번호. 1:1이면 null.</summary>
    // [개념] int? : null이 될 수 있는 int (Nullable 값 형식). "값 없음"을 0 같은 특수값 대신 null로 표현한다.
    public int? RoomId { get; set; }

    /// <summary>1:1로 보낼 때 받는 사람 사원번호. 방이면 null.</summary>
    public string? ToEmployeeNo { get; set; }

    /// <summary>이미지 미리보기(JPEG, 최대 FileRules.MaxThumbnailBytes). 보내는 쪽이 만든다. 없으면 파일 카드로 표시.</summary>
    // [왜] 미리보기를 서버가 아니라 보내는 쪽(Windows 클라이언트)이 만든다. 서버는 리눅스에서도 돌 수 있어
    //   Windows 전용 이미지 라이브러리(System.Drawing)를 쓰기 어렵고, 서버 CPU 부담도 줄어든다.
    public byte[]? Thumbnail { get; set; }
}

/// <summary>대화에 올라간 파일 정보 (FileMsg, FileDownloadRes의 Data).</summary>
// [왜] 원본 파일 내용은 들어 있지 않다. 받는 사람이 말풍선을 클릭했을 때 FileDownloadReq로 따로 받는다.
//   (방에 100명이 있을 때 100명 모두에게 3GB를 미리 보내면 네트워크가 마비된다 → "필요할 때 받기")
public sealed class SharedFile
{
    public string FileId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public FileKind Kind { get; set; }
    public int? RoomId { get; set; }
    public string? ToEmployeeNo { get; set; }
    public string SenderNo { get; set; } = "";
    public string SenderName { get; set; } = "";
    public byte[]? Thumbnail { get; set; }
    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    /// <summary>화면 표시용 크기 문자열 (예: "160.6 MB").</summary>
    // [주의] get 전용 속성도 JSON에 포함되어 전송된다(받는 쪽에서는 set이 없어 무시됨). 크기가 작아 그대로 둔다.
    public string SizeText => FileRules.FormatSize(Size);
}

/// <summary>방 파일 보관함의 과거 페이지 요청. Before/BeforeFileId는 직전 페이지에서 가장 오래된 항목.</summary>
public sealed class RoomFilesRequest
{
    public int RoomId { get; set; }
    public DateTime? Before { get; set; }
    public string? BeforeFileId { get; set; }
}

/// <summary>방 파일 보관함의 메타데이터 페이지. 원본과 미리보기 바이트는 포함하지 않는다.</summary>
public sealed class RoomFilesBatch
{
    public int RoomId { get; set; }
    public List<SharedFile> Files { get; set; } = [];
    public bool HasMore { get; set; }
}

/// <summary>파일 전송 규칙. 서버와 클라이언트가 같은 값을 쓴다.</summary>
public static class FileRules
{
    // [개념] 3L : 숫자 뒤의 L은 long 리터럴. 3 * 1024 * 1024 * 1024를 int로 계산하면 21억을 넘어 오버플로가 나므로
    //   처음 값부터 long으로 만들어 계산 전체가 64비트로 되게 한다.
    public const long DefaultMaxFileSize = 3L * 1024 * 1024 * 1024; // 3GB
    // [왜] 조각이 너무 작으면 패킷 수가 많아져 느리고(3GB / 64KB = 약 5만 개), 너무 크면 조각 하나를 보내는 동안
    //   채팅 패킷이 오래 기다린다. 256KB는 그 사이의 절충값이다.
    public const int ChunkSize = 256 * 1024;                   // 조각 크기. Base64로 늘어나도(약 350KB) 패킷 최대(1MB)보다 작다
    public const long MaxThumbnailSourceSize = 50L * 1024 * 1024; // 이보다 큰 사진은 미리보기를 만들지 않는다(메모리 보호)
    // [왜] 미리보기는 FileMsg 패킷에 실려 방 사람 모두에게 가므로 작게 제한한다(패킷 최대 1MB도 넘지 않게).
    public const int MaxThumbnailBytes = 200 * 1024;
    public const int MaxFileNameLength = 120;
    // [왜] 한 사람이 파일 100개를 동시에 보내면 서버 스레드·디스크·네트워크를 독차지한다. 동시에 3개까지만 허용한다.
    public const int MaxActiveTransfers = 3;                   // 한 사람이 동시에 올리기/내려받기 할 수 있는 개수(각각)

    // [개념] HashSet: 중복 없는 집합. Contains(포함 여부 확인)가 리스트보다 훨씬 빠르다(평균 O(1)).
    // [개념] static readonly: 프로그램이 시작될 때 한 번 만들어지고 다시 대입할 수 없는 공유 값.
    //   (집합 내용 자체는 바꿀 수 있지만 이 코드에서는 읽기만 하므로 여러 스레드가 동시에 읽어도 안전하다)
    public static readonly HashSet<string> ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff"];
    public static readonly HashSet<string> VideoExtensions =
        [".mp4", ".mov", ".avi", ".mkv", ".wmv", ".webm", ".m4v", ".flv", ".3gp"];

    /// <summary>열면 프로그램이 실행되는 위험한 확장자. 클라이언트가 열기 전에 한 번 더 묻는다.</summary>
    // [주의] 메신저는 악성 프로그램이 퍼지는 흔한 통로다. 막지는 않되(사내에서 프로그램 파일을 주고받을 수 있으니)
    //   "열기" 전에 경고해서 실수로 실행하는 것을 막는다.
    private static readonly HashSet<string> ExecutableExtensions =
        [".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
         ".hta", ".lnk", ".reg", ".cpl", ".jar", ".dll", ".pif", ".appx", ".msix",
         // 바로 가기·디스크 이미지(안의 파일은 경고 표시가 사라짐)·도움말·관리 콘솔 등 실행으로 이어지는 형식
         ".url", ".iso", ".img", ".vhd", ".vhdx", ".chm", ".msc", ".application", ".xll", ".one", ".psm1", ".sct",
         ".gadget", ".inf", ".scf", ".appref-ms", ".settingcontent-ms",
         // 매크로가 들어 있을 수 있는 Office 문서
         ".docm", ".xlsm", ".pptm", ".xlam", ".dotm", ".xltm", ".potm", ".ppam", ".sldm"];

    /// <summary>파일 이름의 확장자로 사진/동영상/일반 파일을 구분한다.</summary>
    public static FileKind GetKind(string fileName)
    {
        string ext = Extension(fileName);
        if (ImageExtensions.Contains(ext)) return FileKind.Image;
        if (VideoExtensions.Contains(ext)) return FileKind.Video;
        return FileKind.File;
    }

    public static bool IsExecutable(string fileName) => ExecutableExtensions.Contains(Extension(fileName));

    /// <summary>".JPG" → ".jpg" 처럼 소문자 확장자. 점이 없으면 빈 문자열.</summary>
    public static string Extension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        // [개념] fileName[dot..] : 범위 연산자. dot 위치부터 끝까지 잘라 낸 부분 문자열(Substring과 같음).
        // [개념] ToLowerInvariant: 언어 설정(터키어 등)과 관계없이 항상 같은 규칙으로 소문자 변환. 비교용 문자열에 쓴다.
        return dot < 0 ? "" : fileName[dot..].ToLowerInvariant();
    }

    /// <summary>GUID "N" 형식(16진수 32자)인지. 파일 ID를 경로에 쓰므로 엄격하게 확인한다.</summary>
    // [주의] 서버는 파일 ID를 디스크 파일 이름으로 쓴다. "../../windows/system32" 같은 값이 들어오면
    //   폴더 밖의 파일을 건드릴 수 있다(경로 조작 공격). 0-9, a-f 32글자만 허용하면 원천 차단된다.
    // [개념] id is { Length: 32 } : 속성 패턴. "null이 아니고 Length가 32이면" 참.
    // [개념] c is >= '0' and <= '9' or >= 'a' and <= 'f' : 관계·논리 패턴. 범위 검사를 짧게 쓴다.
    public static bool IsValidFileId(string? id)
        => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string NewFileId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// 파일 이름 정리: 경로 부분 제거, 쓸 수 없는 문자(\/:*?"&lt;&gt;| 와 제어 문자)를 _로, 너무 길면 확장자를 살려 자른다.
    /// 서버(리눅스/윈도우)와 클라이언트가 똑같이 동작하도록 OS 기준이 아닌 고정 목록을 쓴다.
    /// </summary>
    // [주의] 받은 파일 이름은 "남이 보낸 값"이다. "C:\Windows\a.exe"나 "..\..\a.txt"처럼 경로가 섞여 있으면
    //   저장할 때 엉뚱한 곳에 써질 수 있으므로 마지막 이름 부분만 남긴다.
    public static string SanitizeFileName(string? name)
    {
        // [개념] ??= : 왼쪽이 null일 때만 오른쪽 값을 대입한다.
        name ??= "";
        // 경로 구분자(/ 또는 \) 중 마지막 위치 뒤만 남긴다
        int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0) name = name[(slash + 1)..];

        // [개념] StringBuilder: 문자열을 여러 번 이어 붙일 때 쓰는 가변 버퍼. string을 +로 반복하면 매번 새 문자열이 만들어져 느리다.
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(c < 32 || "\\/:*?\"<>|".Contains(c) ? '_' : c); // 제어 문자(32 미만)와 금지 문자는 _로
        // 앞뒤 공백·점 제거 (Windows는 이름 끝의 점/공백을 허용하지 않는다)
        name = sb.ToString().Trim().Trim('.');
        if (name.Length == 0) name = "file";

        if (name.Length > MaxFileNameLength)
        {
            // 확장자는 살리고 앞부분을 자른다 (확장자가 없어지면 어떤 프로그램으로 열지 모른다)
            string ext = Extension(name);
            if (ext.Length > 10) ext = "";
            name = name[..(MaxFileNameLength - ext.Length)] + ext;
        }
        return name;
    }

    /// <summary>바이트 수 → "1.5 MB" 같은 읽기 쉬운 문자열.</summary>
    // [개념] switch 식 + 관계 패턴(< 1024 => ...): 위에서부터 처음 맞는 줄의 값을 돌려준다. _ 는 "나머지 전부".
    // [개념] {값:0.#} : 서식 문자열. 소수점 아래 한 자리까지(0이면 생략) 표시한다.
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
