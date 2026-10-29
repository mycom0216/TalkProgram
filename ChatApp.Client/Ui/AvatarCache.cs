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
internal sealed class AvatarCache(ChatClient client, string dataFolder)
{
    private readonly string _dir = Path.Combine(dataFolder, "photos");
    // 키: (종류, 사원번호)
    private readonly Dictionary<(PhotoKind, string), (long Version, Image Image)> _images = new(new KeyComparer());
    private readonly Dictionary<(PhotoKind, string), long> _requested = new(new KeyComparer()); // 서버에 요청한 버전 (중복 요청 방지)

    /// <summary>어떤 사람의 사진이 생기거나 바뀌거나 지워졌다.</summary>
    public event Action? Changed;

    /// <summary>프로필 사진 (없으면 null → 이니셜 아바타로 그린다).</summary>
    public Image? Get(string? employeeNo) => Find(PhotoKind.Profile, employeeNo);

    /// <summary>배경 사진 (없으면 null → 기본 그라데이션).</summary>
    public Image? GetBackground(string? employeeNo) => Find(PhotoKind.Background, employeeNo);

    public void Note(UserInfo user) => Note(user.EmployeeNo, user.PhotoVersion);
    public void Note(string employeeNo, long version) => Note(PhotoKind.Profile, employeeNo, version);
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
        if (_images.TryGetValue(key, out var current) && current.Version >= version) return;
        if (LoadFromDisk(kind, no, version) is { } image)
        {
            Set(key, version, image);
            return;
        }
        if (_requested.TryGetValue(key, out long asked) && asked >= version) return;
        _requested[key] = version;
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
            foreach (string old in Directory.EnumerateFiles(_dir, $"{SafeName(no)}_*{Extension(kind)}"))
                File.Delete(old);
            File.WriteAllBytes(FileOf(kind, no, version), bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // 디스크 캐시는 없어도 동작한다
    }

    /// <summary>사원번호는 대소문자를 구분하지 않는다.</summary>
    private sealed class KeyComparer : IEqualityComparer<(PhotoKind Kind, string No)>
    {
        public bool Equals((PhotoKind Kind, string No) x, (PhotoKind Kind, string No) y)
            => x.Kind == y.Kind && string.Equals(x.No, y.No, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((PhotoKind Kind, string No) key)
            => HashCode.Combine(key.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(key.No));
    }
}
