using ChatApp.Common.Models;

namespace ChatApp.Tests;

// 파일 전송 규칙: 경로 조작 공격 차단, 위험한 확장자, 파일 종류 구분, 크기 표시.
public class FileRulesTests
{
    // 사용자가 파일 이름에 폴더 경로나 특수 문자를 넣어도 안전한 이름으로 바꾸는지 확인합니다.
    [Theory]
    [InlineData(@"..\..\Windows\system32\evil.dll", "evil.dll")]   // 경로가 섞인 이름 → 마지막 이름만
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("보고서:최종?.docx", "보고서_최종_.docx")]           // Windows에서 못 쓰는 글자 → _
    [InlineData("  이름.txt.  ", "이름.txt")]                         // 끝의 점·공백 제거
    [InlineData("", "file")]
    [InlineData(null, "file")]
    public void 파일_이름을_안전하게_정리한다(string? input, string expected)
        => Assert.Equal(expected, FileRules.SanitizeFileName(input));

    // 이 테스트는 “너무 긴 이름은 확장자를 살려서 자른다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 너무_긴_이름은_확장자를_살려서_자른다()
    {
        string name = new string('가', 300) + ".pdf";
        string cleaned = FileRules.SanitizeFileName(name);
        Assert.Equal(FileRules.MaxFileNameLength, cleaned.Length);
        Assert.EndsWith(".pdf", cleaned);
    }

    // 서버가 만든 파일 번호 모양만 받아들이고, 이상한 번호는 거절하는지 확인합니다.
    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false)] // 대문자 불가 (서버가 만드는 형식만)
    [InlineData("../../../../../../../../etc/passwd", false)]
    [InlineData("0123456789abcdef", false)]
    [InlineData(null, false)]
    public void 파일_ID는_32자리_소문자_16진수만_허용한다(string? id, bool valid)
        => Assert.Equal(valid, FileRules.IsValidFileId(id));

    // 이 테스트는 “실행 파일을 구분한다” 상황이 제대로 처리되는지 확인합니다.
    [Theory]
    [InlineData("setup.EXE", true)]
    [InlineData("script.ps1", true)]
    [InlineData("바로가기.lnk", true)]
    [InlineData("문서.pdf", false)]
    [InlineData("확장자없음", false)]
    public void 실행_파일을_구분한다(string name, bool executable)
        => Assert.Equal(executable, FileRules.IsExecutable(name));

    // 이 테스트는 “확장자로 사진 동영상 파일을 구분한다” 상황이 제대로 처리되는지 확인합니다.
    [Theory]
    [InlineData("사진.JPG", FileKind.Image)]
    [InlineData("영상.mp4", FileKind.Video)]
    [InlineData("자료.zip", FileKind.File)]
    public void 확장자로_사진_동영상_파일을_구분한다(string name, FileKind kind)
        => Assert.Equal(kind, FileRules.GetKind(name));

    // 이 테스트는 “크기를 읽기 쉽게 표시한다” 상황이 제대로 처리되는지 확인합니다.
    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void 크기를_읽기_쉽게_표시한다(long bytes, string expected)
        => Assert.Equal(expected, FileRules.FormatSize(bytes).Replace(',', '.')); // 소수점이 쉼표인 지역 설정 대비

    // 이 테스트는 “최대 크기는 3GB이고 조각은 패킷 한도보다 작다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 최대_크기는_3GB이고_조각은_패킷_한도보다_작다()
    {
        Assert.Equal(3L * 1024 * 1024 * 1024, FileRules.DefaultMaxFileSize);
        // 조각은 Base64로 약 4/3배가 되므로, 그래도 패킷 최대 크기(1MB)보다 작아야 한다
        Assert.True(FileRules.ChunkSize * 4 / 3 + 1024 < Common.Protocol.PacketSerializer.MaxBodySize);
    }
}
