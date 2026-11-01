using ChatApp.Common.Models;

namespace ChatApp.Tests;

// 관리 통계 그래프의 시간대 계산 (AdminStatsView). 한국(UTC+9) 기준으로 자정 경계를 확인한다.
public class StatsViewTests
{
    private static readonly TimeZoneInfo Kst = TimeZoneInfo.CreateCustomTimeZone("KST-test", TimeSpan.FromHours(9), "KST", "KST");
    private static readonly DateTime Today = new(2026, 10, 29); // 한국 날짜

    // 테스트용 시간 기록을 만듭니다. 입력한 시각은 UTC(세계 표준 시각)입니다.
    private static HourCount H(int month, int day, int hourUtc, int count, DateTimeKind kind = DateTimeKind.Utc)
        => new() { HourUtc = new DateTime(2026, month, day, hourUtc, 0, 0, kind), Count = count };

    // 이 테스트는 “UTC 15시는 한국 다음날 0시라 오늘로 센다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void UTC_15시는_한국_다음날_0시라_오늘로_센다()
    {
        int[] daily = AdminStatsView.Daily([H(10, 28, 15, 7), H(10, 28, 14, 3)], Today, Kst);
        Assert.Equal(7, daily[6]); // 10-29 00시 (오늘)
        Assert.Equal(3, daily[5]); // 10-28 23시 (어제)
    }

    // 이 테스트는 “일곱날보다 오래된 것과 미래는 버린다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 일곱날보다_오래된_것과_미래는_버린다()
    {
        // 10-22 23시 KST(= 10-22 14시 UTC)는 범위 밖, 10-23 00시 KST(= 10-22 15시 UTC)는 첫날
        int[] daily = AdminStatsView.Daily([H(10, 22, 14, 100), H(10, 22, 15, 1), H(10, 29, 15, 50)], Today, Kst);
        Assert.Equal(1, daily[0]);
        Assert.Equal(1, daily.Sum());
    }

    // 이 테스트는 “시간대별은 한국 시각으로 묶는다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void 시간대별은_한국_시각으로_묶는다()
    {
        int[] hours = AdminStatsView.HourOfDay([H(10, 29, 0, 4), H(10, 28, 0, 6), H(10, 29, 13, 2)], Today, Kst);
        Assert.Equal(10, hours[9]);  // UTC 0시 = 한국 9시 (이틀치 합)
        Assert.Equal(2, hours[22]);  // UTC 13시 = 한국 22시
        Assert.Equal(12, hours.Sum());
    }

    // 이 테스트는 “Kind가 빠진 시각도 UTC로 본다” 상황이 제대로 처리되는지 확인합니다.
    [Fact]
    public void Kind가_빠진_시각도_UTC로_본다()
    {
        int[] daily = AdminStatsView.Daily([H(10, 28, 15, 5, DateTimeKind.Unspecified)], Today, Kst);
        Assert.Equal(5, daily[6]);
    }
}
