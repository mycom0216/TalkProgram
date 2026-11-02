using ChatApp.Client.Network;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Ui;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] AvatarCache.cs - 프로필 사진·배경 사진 보관함 (메모리 + 내 PC 디스크)
// - 프로필 사진: 접속자·친구 목록에서 UserInfo.PhotoVersion을 볼 때마다 Note()를 부른다(목록에 바로 보여야 하므로 미리 받음).
// - 배경 사진: 프로필 카드를 열 때만 NoteBackground()를 부른다(크고 자주 안 보므로 필요할 때만 받음).
// - 순서: ① 같은 버전이 메모리에 있으면 끝 ② 디스크(%LocalAppData%\ChatApp\photos)에 있으면 읽기 ③ 없으면 서버에 한 번만 요청
// - 서버 응답(ProfilePhoto)은 Handle()로 받아 디스크에 저장하고 Changed로 알린다 → 화면이 다시 그린다.
// - 스레드: 모두 UI 스레드에서만 부른다(메인 창의 패킷 처리와 그리기). 그래서 lock이 없다.
// ─────────────────────────────────────────────────────────────────────────────
//
// ───────────────────────────────────────────────────────────────────────────
// [공부 노트 · 면접 대비]
//
// ■ 한 줄 요약
//   프로필·배경 사진 보관함. "버전 번호"로 캐시가 최신인지 판단하고, 메모리 → 디스크 → 서버 순으로 찾는다. 사진이 도착/변경/삭제되면 Changed 이벤트로 화면이 다시 그리게 한다. 호출하는 곳은 MainForm(Note/Handle)과 ProfileCardForm(NoteBackground)이다.
//
// ■ 핵심 흐름: Note(kind, 사원번호, 버전)
//   ① 버전 ≤ 0 → 사진이 없는 사람(또는 지운 사람): 메모리 사진을 버리고(Dispose) Changed로 알림
//   ② 메모리에 같거나 더 새 버전이 있다 → 끝(캐시 적중)
//   ③ 디스크(photos 폴더, 파일명 "사원번호_버전.img/.bg")에 있다 → 읽어서 메모리에 올리고 끝
//   ④ 없다 → 서버에 ProfilePhotoReq를 보낸다. 같은 버전을 이미 요청했으면(_requested) 다시 보내지 않는다(중복 요청 방지)
//   ⑤ 서버 응답(ProfilePhoto)은 Handle()이 받아 → 그림으로 변환(Decode) → 디스크 저장 → 메모리 등록 → Changed
//   "버전"이 핵심 열쇠다: 사진을 바꿀 때마다 서버가 버전을 올리고, 접속자·친구 목록(UserInfo)에 버전만 실려 온다. 클라이언트는 자기 캐시 버전과 비교해 다를 때만 새로 받는다.
//
// ■ 이 파일에서 나오는 개념과 검색어
//   - 버전 번호로 캐시 무효화 ......................... "cache invalidation version number etag"
//   - 메모리/디스크 2단 캐시 .......................... "two level cache memory disk"
//   - 중복 요청 방지(_requested) ...................... "request deduplication in-flight requests"
//   - 복합 키 사전 + 대소문자 무시 비교기(IEqualityComparer) .. "custom IEqualityComparer tuple key Dictionary"
//   - Image.FromStream 스트림 붙잡기 ................... "Image.FromStream keep stream open copy to Bitmap"
//   - 디스크 파일명 정리(SafeName) .................... "sanitize filename path traversal"
//   - 주 생성자(primary constructor) .................. "C# 12 primary constructor class parameters"
//
// ■ 면접 예상 질문과 답
//   Q1. 프로필 사진을 어떻게 가져오고 갱신하나요?
//       A. 사진 자체가 아니라 "버전 번호"만 목록 정보에 실려 옵니다. 클라이언트는 자기 캐시의 버전과 비교해 새 버전일 때만 서버에 사진을 요청하고, 받은 사진은 디스크에 저장해 다음 실행 때는 서버 요청 없이 씁니다. 사진이 바뀌면 버전이 올라가므로 옛 캐시가 자동으로 무시됩니다.
//   Q2. 같은 사진을 여러 번 요청하지 않게 한 방법은요?
//       A. 요청한 버전을 _requested에 기록해 두고, 같은 또는 더 낮은 버전 요청은 보내지 않습니다. 응답이 오면 기록을 지웁니다. 목록이 자주 갱신되어 같은 사람이 계속 나와도 요청이 쌓이지 않습니다.
//   Q3. Decode에서 왜 Bitmap으로 한 번 더 복사하나요?
//       A. Image.FromStream으로 만든 그림은 원본 스트림을 계속 붙잡고 있어서, 스트림을 닫으면 그릴 때 오류가 납니다. 새 Bitmap으로 복사해 스트림과의 연결을 끊습니다(메모리를 한 번 더 쓰는 대신 안전).
//   Q4. 사원번호를 파일 이름에 그대로 써도 되나요?
//       A. SafeName이 영문·숫자만 남기고 대문자로 바꿔, "../" 같은 경로 조작 문자나 대소문자 차이로 인한 중복 파일을 막습니다.
//   Q5. 메모리에서 사진을 교체할 때 이전 사진은 어떻게 하나요?
//       A. Set()이 이전 이미지를 Dispose한 뒤 새 이미지를 넣습니다. 모두 UI 스레드에서만 호출되므로(그래서 lock이 없음) 그리는 도중에 해제되는 일이 없습니다.
//
// ■ 알아 둘 점 (코드는 바꾸지 않음)
//   - 요청을 보낸 뒤 응답이 영영 오지 않으면 _requested에 기록이 남아, 같은 버전은 다시 요청하지 않는다(더 높은 버전이 오거나 프로그램을 다시 켜면 해결). 실패 응답은 Handle에서 Success=true인 경우만 오므로(코드 주석 기준) 실패 시 재시도 규칙은 별도로 없다.
//   - 디스크 캐시는 사람마다 최신 버전 1개만 남기지만, 퇴사·삭제된 사용자의 파일은 정리하지 않는다(용량이 작아 문제는 적다).
//   - 메모리 캐시에는 개수 제한이 없다(회사 규모의 사진 수라면 충분하지만, 아주 큰 규모에서는 LRU 같은 제한이 필요).
//   - 여러 곳에서 Get()으로 같은 Image 객체를 공유해 그린다. 그 객체를 호출한 쪽이 Dispose하면 안 된다(소유는 이 캐시).
// ───────────────────────────────────────────────────────────────────────────
/// <summary>프로필·배경 사진을 서버에서 받아 메모리와 디스크(dataFolder\photos)에 보관하는 캐시.</summary>
internal sealed class AvatarCache(ChatClient client, string dataFolder)
{
    private readonly string _dir = Path.Combine(dataFolder, "photos");
    // 키: (종류, 사원번호)
    // [읽는 법] 키 = (사진 종류, 사원번호) 튜플, 값 = (버전, 그림). KeyComparer가 사원번호의 대소문자를 무시하게 비교한다(기본 비교기는 튜플 안의 문자열을 대소문자 구분으로 본다).
    private readonly Dictionary<(PhotoKind, string), (long Version, Image Image)> _images = new(new KeyComparer());
    private readonly Dictionary<(PhotoKind, string), long> _requested = new(new KeyComparer()); // 서버에 요청한 버전 (중복 요청 방지)

    /// <summary>어떤 사람의 사진이 생기거나 바뀌거나 지워졌다.</summary>
    // [개념] 사진이 생기거나 바뀌거나 지워질 때마다 불린다. 구독하는 창(ProfileCardForm, MainForm의 목록 등)은 Invalidate()로 다시 그린다. 구독을 해제하지 않으면 닫힌 창이 계속 호출되므로 FormClosed에서 -= 해야 한다.
    public event Action? Changed;

    /// <summary>프로필 사진 (없으면 null → 이니셜 아바타로 그린다).</summary>
    public Image? Get(string? employeeNo) => Find(PhotoKind.Profile, employeeNo);

    /// <summary>배경 사진 (없으면 null → 기본 그라데이션).</summary>
    public Image? GetBackground(string? employeeNo) => Find(PhotoKind.Background, employeeNo);

    /// <summary>사용자 정보에 적힌 프로필 사진 버전을 알린다. 캐시에 없으면 디스크에서 읽거나 서버에 요청한다.</summary>
    public void Note(UserInfo user) => Note(user.EmployeeNo, user.PhotoVersion);

    /// <summary>사원번호의 프로필 사진 버전을 알린다. version이 0 이하면 사진이 없다는 뜻이라 캐시에서 지운다.</summary>
    public void Note(string employeeNo, long version) => Note(PhotoKind.Profile, employeeNo, version);

    /// <summary>사용자 정보에 적힌 배경 사진 버전을 알린다. 캐시에 없으면 디스크에서 읽거나 서버에 요청한다.</summary>
    public void NoteBackground(UserInfo user) => Note(PhotoKind.Background, user.EmployeeNo, user.BackgroundVersion);

    private Image? Find(PhotoKind kind, string? no) => no is not null && _images.TryGetValue((kind, no), out var e) ? e.Image : null;

    /// <summary>"이 사람의 사진 버전은 이렇다"를 알린다. 필요하면 디스크에서 읽거나 서버에 요청한다.</summary>
    private void Note(PhotoKind kind, string no, long version)
    {
        if (string.IsNullOrEmpty(no)) return;
        var key = (kind, no);
        if (version <= 0)
        {
            if (_images.Remove(key, out var old))
            {
                old.Image.Dispose();
                Changed?.Invoke();
            }
            return;
        }
        // [읽는 법] 캐시 적중 판정: 같거나 더 새 버전이 이미 있으면 아무것도 하지 않는다. ">="인 이유: 서버 목록이 옛 버전을 보내와도 더 새 사진을 지우지 않기 위해서다.
        if (_images.TryGetValue(key, out var current) && current.Version >= version) return;
        if (LoadFromDisk(kind, no, version) is { } image)
        {
            Set(key, version, image);
            return;
        }
        if (_requested.TryGetValue(key, out long asked) && asked >= version) return;
        _requested[key] = version;
        // [읽는 법] 요청 본문(Body)에 사원번호, 데이터(PhotoInfo)에 종류(프로필/배경)를 담는다. 응답은 Handle()로 돌아온다.
        client.Send(Packet.Create(PacketType.ProfilePhotoReq, body: no).WithData(new PhotoInfo { Kind = kind }));
    }

    /// <summary>서버가 보낸 사진 (ProfilePhoto, Success=true).</summary>
    public void Handle(Packet packet)
    {
        string no = packet.Target ?? "";
        if (no.Length == 0 || packet.GetData<PhotoInfo>() is not { } info) return;
        _requested.Remove((info.Kind, no));
        if (info.Version <= 0 || packet.Bin is not { Length: > 0 } bytes)
        {
            Note(info.Kind, no, 0);
            return;
        }
        // [왜] 서버가 보낸 바이트가 그림이 아니면(Decode가 null) 디스크에도 저장하지 않고 버린다. 깨진 파일이 캐시에 남지 않게 하는 순서(변환 → 저장 → 등록).
        if (Decode(bytes) is not { } image) return;
        SaveToDisk(info.Kind, no, info.Version, bytes);
        Set((info.Kind, no), info.Version, image);
    }

    private void Set((PhotoKind, string) key, long version, Image image)
    {
        if (_images.Remove(key, out var old)) old.Image.Dispose();
        _images[key] = (version, image);
        Changed?.Invoke();
    }

    /// <summary>바이트 → 그림. [주의] Image.FromStream은 스트림을 계속 붙잡으므로 새 Bitmap으로 복사해서 스트림을 놓아준다.</summary>
    private static Image? Decode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using Image temp = Image.FromStream(ms);
            return new Bitmap(temp);
        }
        catch (ArgumentException) { return null; } // 그림이 아닌 데이터
    }

    // [왜] 파일 이름에 쓸 문자열을 영문·숫자로 제한한다. 서버가 준 사원번호가 경로 문자를 포함해도 폴더 밖으로 나가지 못하게 하는 방어(경로 조작 방지).
    private static string SafeName(string no) => new string(no.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
    private static string Extension(PhotoKind kind) => kind == PhotoKind.Background ? ".bg" : ".img";
    private string FileOf(PhotoKind kind, string no, long version) => Path.Combine(_dir, $"{SafeName(no)}_{version}{Extension(kind)}");

    private Image? LoadFromDisk(PhotoKind kind, string no, long version)
    {
        try
        {
            string path = FileOf(kind, no, version);
            return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void SaveToDisk(PhotoKind kind, string no, long version, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            // 같은 사람의 옛 버전 파일은 지운다 (사진을 자주 바꿔도 폴더가 커지지 않게)
            // [왜] 같은 사람의 옛 버전 파일은 지운다. 패턴 "사원번호_*.img"로 그 사람 것만 골라서 지우고, 배경(.bg)과 프로필(.img)은 확장자로 구분되어 서로 지우지 않는다.
            foreach (string old in Directory.EnumerateFiles(_dir, $"{SafeName(no)}_*{Extension(kind)}"))
                File.Delete(old);
            File.WriteAllBytes(FileOf(kind, no, version), bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // 디스크 캐시는 없어도 동작한다
    }

    /// <summary>사원번호는 대소문자를 구분하지 않는다.</summary>
    private sealed class KeyComparer : IEqualityComparer<(PhotoKind Kind, string No)>
    {
        /// <summary>사진 종류가 같고 사원번호가 대소문자 무시로 같으면 같은 키로 본다.</summary>
        public bool Equals((PhotoKind Kind, string No) x, (PhotoKind Kind, string No) y)
            => x.Kind == y.Kind && string.Equals(x.No, y.No, StringComparison.OrdinalIgnoreCase);

        /// <summary>Equals와 맞도록 사진 종류와 대소문자 무시 사원번호로 해시를 만든다.</summary>
        public int GetHashCode((PhotoKind Kind, string No) key)
            => HashCode.Combine(key.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(key.No));
    }
}
