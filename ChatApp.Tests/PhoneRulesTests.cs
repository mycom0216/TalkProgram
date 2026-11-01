using ChatApp.Common.Models;

namespace ChatApp.Tests;

// 휴대폰 번호 정리·가리기 (친구 찾기·회원가입에서 사용)
public class PhoneRulesTests
{
// ===============
    // 이 테스트는 “여러 형식을 숫자만으로 정리한다” 상황이 제대로 처리되는지 확인합니다.
    [Theory]
    [InlineData("010-1234-5678", "01012345678")]
    [InlineData("010 1234 5678", "01012345678")]
    [InlineData("01012345678", "01012345678")]
    [InlineData("+82 10-1234-5678", "01012345678")]
    [InlineData("016-123-4567", "0161234567")]
    public void 여러_형식을_숫자만으로_정리한다(string input, string expected)
        => Assert.Equal(expected, PhoneRules.Normalize(input));

    // 일반 전화번호나 길이가 맞지 않는 번호는 휴대폰 번호로 보지 않는지 확인합니다.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("02-123-4567")]      // 일반 전화
    [InlineData("010-123")]          // 너무 짧음
    [InlineData("010-1234-56789")]   // 너무 김
    [InlineData("012-1234-5678")]    // 없는 앞자리
    public void 휴대폰_번호가_아니면_null(string? input)
        => Assert.Null(PhoneRules.Normalize(input));

// ===============
    // 이 테스트는 “가운데를 가리고 보기 좋게 바꾼다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 가운데를_가리고_보기_좋게_바꾼다()
    {
        Assert.Equal("010-****-5678", PhoneRules.Mask("01012345678"));
        Assert.Equal("016-***-4567", PhoneRules.Mask("0161234567"));
        Assert.Equal("010-1234-5678", PhoneRules.Format("01012345678"));
        Assert.Equal("016-123-4567", PhoneRules.Format("0161234567"));
    }
}
