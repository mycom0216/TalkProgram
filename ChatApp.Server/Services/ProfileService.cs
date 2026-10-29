using ChatApp.Common.Models;
using ChatApp.Common.Protocol;
using ChatApp.Server.Core;
using ChatApp.Server.Data;
using ChatApp.Server.Logging;

namespace ChatApp.Server.Services;

// ═══════════════════════════════════════════════════════════════════════════
// [파일 역할] 프로필 사진·배경 사진 바꾸기·지우기·받기.
// [저장]     uploads\profiles\사원번호.img(프로필) / 사원번호.bg(배경) — 사람마다 한 장씩, 바꾸면 덮어씀
//            + DB users.photo_version / background_version(바꾼 시각). 채팅 파일과 달리 보관 기간 정리 대상이 아니다.
// [흐름]     ProfilePhotoUpdate(Data = 종류, Bin = 사진) → 저장 → 내 세션 UserInfo의 버전 갱신 → 접속자 목록 방송
//            → 다른 사람 화면은 버전이 바뀐 것을 보고 필요할 때 ProfilePhotoReq로 새 사진을 한 번 받아 간다(캐시).
// [스레드]   요청한 사람의 수신 스레드에서 실행된다. 각자 자기 파일만 쓰므로 사람끼리 겹치지 않는다.
// ═══════════════════════════════════════════════════════════════════════════
public sealed class ProfileService(UserRepository users, string uploadRoot, ServerLogger log, Action broadcastUserList)
{
    private readonly string _dir = Path.Combine(uploadRoot, "profiles");

    /// <summary>사원번호가 파일 이름으로 안전한지 (경로 조작 차단: 영문·숫자만).</summary>
    private static bool IsSafeNo(string no) => no.Length is > 0 and <= 20 && no.All(char.IsAsciiLetterOrDigit);

    private string PathOf(string no, PhotoKind kind)
        => Path.Combine(_dir, no.ToUpperInvariant() + (kind == PhotoKind.Background ? ".bg" : ".img"));

    private static string KindText(PhotoKind kind) => kind == PhotoKind.Background ? "배경 사진" : "프로필 사진";

    /// <summary>내 사진 바꾸기 (Bin이 비어 있으면 지우기).</summary>
    public void HandleUpdate(ClientSession session, Packet packet)
    {
        UserInfo me = session.User!;
        if (!IsSafeNo(me.EmployeeNo)) return;
        PhotoKind kind = packet.GetData<PhotoInfo>()?.Kind ?? PhotoKind.Profile;
        if (!Enum.IsDefined(kind)) return;
        byte[]? photo = packet.Bin;
        bool remove = photo is null || photo.Length == 0;
        if (!remove && photo!.Length > ProfileRules.MaxBytes(kind))
        {
            Fail(session, kind, $"사진이 너무 큽니다. (최대 {ProfileRules.MaxBytes(kind) / 1024}KB)");
            return;
        }
        if (!remove && !ProfileRules.IsSupportedImage(photo))
        {
            Fail(session, kind, "JPEG 또는 PNG 사진만 쓸 수 있습니다.");
            return;
        }

        string path = PathOf(me.EmployeeNo, kind);
        long version = 0;
        try
        {
            if (remove)
            {
                File.Delete(path);
            }
            else
            {
                Directory.CreateDirectory(_dir);
                // [왜] 임시 파일에 다 쓴 뒤 바꿔치기한다. 쓰는 도중에 다른 사람이 받아 가도 반쪽짜리 사진을 받지 않는다.
                string temp = path + ".tmp";
                File.WriteAllBytes(temp, photo!);
                File.Move(temp, path, overwrite: true);
                version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"{KindText(kind)} 저장 실패({me.EmployeeNo}): {ex.Message}");
            Fail(session, kind, "서버에 사진을 저장하지 못했습니다.");
            return;
        }

        users.SetPhotoVersion(me.EmployeeNo, kind, version);
        session.UpdateUser(u => kind == PhotoKind.Background ? u.WithBackground(version) : u.WithPhoto(version));
        log.Info($"{KindText(kind)} {(remove ? "지움" : "바꿈")}: {me.EmployeeNo}{(remove ? "" : $" ({photo!.Length / 1024}KB)")}");
        // 나에게는 사진을 바로 돌려줘서 화면이 곧바로 바뀌게 하고, 다른 사람에게는 목록 방송으로 "버전이 바뀜"만 알린다
        session.Send(Packet.Create(PacketType.ProfilePhoto, $"{KindText(kind)}을 {(remove ? "지웠습니다" : "바꿨습니다")}.", target: me.EmployeeNo)
            .WithPhoto(kind, version, remove ? null : photo));
        broadcastUserList();
    }

    /// <summary>다른 사람의 사진 받기 (Body = 사원번호, Data = 종류). 사진이 없으면 Bin 없이 버전 0.</summary>
    public void HandleRequest(ClientSession session, Packet packet)
    {
        string no = packet.Body?.Trim() ?? "";
        if (!IsSafeNo(no)) return;
        PhotoKind kind = packet.GetData<PhotoInfo>()?.Kind ?? PhotoKind.Profile;
        if (!Enum.IsDefined(kind)) return;
        long version = users.GetPhotoVersion(no, kind);
        byte[]? photo = null;
        if (version > 0)
        {
            try { photo = File.ReadAllBytes(PathOf(no, kind)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { version = 0; } // 파일이 없어졌으면 "사진 없음"
        }
        session.Send(Packet.Create(PacketType.ProfilePhoto, target: no).WithPhoto(kind, version, photo));
    }

    private static void Fail(ClientSession session, PhotoKind kind, string message)
        => session.Send(Packet.Create(PacketType.ProfilePhoto, message, success: false).WithData(new PhotoInfo { Kind = kind }));
}

internal static class ProfilePacketExtensions
{
    /// <summary>ProfilePhoto 패킷에 종류·버전(Data = PhotoInfo)과 사진 바이트(Bin)를 담는다.</summary>
    public static Packet WithPhoto(this Packet packet, PhotoKind kind, long version, byte[]? photo)
    {
        packet.Bin = photo;
        return packet.WithData(new PhotoInfo { Kind = kind, Version = version });
    }
}
