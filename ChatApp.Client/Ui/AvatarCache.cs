using ChatApp.Client.Network;
using ChatApp.Common.Models;
using ChatApp.Common.Protocol;

namespace ChatApp.Client.Ui;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] AvatarCache.cs - 프로필 사진 보관함 (메모리 + 내 PC 디스크)
// - 접속자·친구 목록에서 UserInfo.PhotoVersion을 볼 때마다 Note()를 부른다.
//   ① 같은 버전이 메모리에 있으면 끝 ② 디스크(%LocalAppData%\ChatApp\photos)에 있으면 읽기 ③ 없으면 서버에 한 번만 요청
// - 서버 응답(ProfilePhoto)은 Handle()로 받아 디스크에 저장하고 Changed로 알린다 → 메인 창이 목록·대화창을 다시 그린다.
// - [왜] 사진은 사람 수만큼 있고 잘 바뀌지 않는다. 버전으로 비교하면 바뀐 사람의 사진만 받아 오고, 다음 실행 때는 디스크에서 바로 보인다.
// - 스레드: 모두 UI 스레드에서만 부른다(메인 창의 패킷 처리와 그리기). 그래서 lock이 없다.
// ─────────────────────────────────────────────────────────────────────────────
internal sealed class AvatarCache(ChatClient client, string dataFolder)
{
    private readonly string _dir = Path.Combine(dataFolder, "photos");
    private readonly Dictionary<string, (long Version, Image Image)> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _requested = new(StringComparer.OrdinalIgnoreCase); // 서버에 요청한 버전 (중복 요청 방지)

    /// <summary>어떤 사람의 사진이 생기거나 바뀌거나 지워졌다.</summary>
    public event Action? Changed;

    /// <summary>사원번호의 사진 (없으면 null → 이니셜 아바타로 그린다).</summary>
    public Image? Get(string? employeeNo) => employeeNo is not null && _images.TryGetValue(employeeNo, out var e) ? e.Image : null;

    public void Note(UserInfo user) => Note(user.EmployeeNo, user.PhotoVersion);

    /// <summary>"이 사람의 사진 버전은 이렇다"를 알린다. 필요하면 디스크에서 읽거나 서버에 요청한다.</summary>
    public void Note(string employeeNo, long version)
    {
        if (string.IsNullOrEmpty(employeeNo)) return;
        if (version <= 0)
        {
            if (_images.Remove(employeeNo, out var old))
            {
                old.Image.Dispose();
                Changed?.Invoke();
            }
            return;
        }
        if (_images.TryGetValue(employeeNo, out var current) && current.Version >= version) return;
        if (LoadFromDisk(employeeNo, version) is { } image)
        {
            Set(employeeNo, version, image);
            return;
        }
        if (_requested.TryGetValue(employeeNo, out long asked) && asked >= version) return;
        _requested[employeeNo] = version;
        client.Send(Packet.Create(PacketType.ProfilePhotoReq, body: employeeNo));
    }

    /// <summary>서버가 보낸 사진 (ProfilePhoto, Success=true).</summary>
    public void Handle(Packet packet)
    {
        string no = packet.Target ?? "";
        if (no.Length == 0) return;
        long version = packet.GetData<PhotoInfo>()?.Version ?? 0;
        _requested.Remove(no);
        if (version <= 0 || packet.Bin is not { Length: > 0 } bytes)
        {
            Note(no, 0);
            return;
        }
        if (Decode(bytes) is not { } image) return;
        SaveToDisk(no, version, bytes);
        Set(no, version, image);
    }

    private void Set(string no, long version, Image image)
    {
        if (_images.Remove(no, out var old)) old.Image.Dispose();
        _images[no] = (version, image);
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

    private static string SafeName(string no) => new(no.Where(char.IsAsciiLetterOrDigit).ToArray());

    private string FileOf(string no, long version) => Path.Combine(_dir, $"{SafeName(no).ToUpperInvariant()}_{version}.img");

    private Image? LoadFromDisk(string no, long version)
    {
        try
        {
            string path = FileOf(no, version);
            return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void SaveToDisk(string no, long version, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            // 같은 사람의 옛 버전 파일은 지운다 (사진을 자주 바꿔도 폴더가 커지지 않게)
            foreach (string old in Directory.EnumerateFiles(_dir, $"{SafeName(no).ToUpperInvariant()}_*.img"))
                File.Delete(old);
            File.WriteAllBytes(FileOf(no, version), bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // 디스크 캐시는 없어도 동작한다
    }
}
