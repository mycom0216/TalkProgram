namespace ChatApp.Common.Models;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] AdminStatsView.cs - 관리 통계(AdminStats)를 "화면에 보여 줄 모양"으로 묶는 계산
// - 서버는 시간별(UTC 정시) 개수만 보낸다. 여기서 보는 사람의 시간대로 바꿔 날짜별·시간대별로 더한다.
// - 화면(WinForms)과 떨어진 순수 계산이라 Common에 두고 단위 테스트(StatsViewTests)로 확인한다.
//   [왜] 시간대 계산은 "자정 근처"에서 틀리기 쉽다(UTC 15시 = 한국 다음 날 0시). 화면을 띄우지 않고 검증하려고 분리했다.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>관리 통계 그래프용 계산 (시간대 변환, 날짜·시간대별 합계).</summary>
public static class AdminStatsView
{
    /// <summary>서버 시각(UTC)을 그 시간대 시각으로. JSON에서 Kind가 빠져 와도(Unspecified) UTC로 본다.</summary>
    public static DateTime ToLocal(DateTime time, TimeZoneInfo zone)
    {
        DateTime utc = time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
    }

    /// <summary>
    /// 날짜별 합계. 결과[days-1] = today, 결과[0] = today-(days-1)일. 범위 밖은 버린다.
    /// </summary>
    /// <param name="today">그 시간대의 오늘 날짜 (시각은 무시)</param>
    public static int[] Daily(IEnumerable<HourCount> hours, DateTime today, TimeZoneInfo zone, int days = 7)
    {
        int[] result = new int[days];
        DateTime first = today.Date.AddDays(-(days - 1));
        foreach (HourCount h in hours)
        {
            int index = (ToLocal(h.HourUtc, zone).Date - first).Days;
            if (index >= 0 && index < days) result[index] += h.Count;
        }
        return result;
    }

    /// <summary>시간대별(0~23시) 합계. 최근 days일(오늘 포함) 것만 더한다.</summary>
    public static int[] HourOfDay(IEnumerable<HourCount> hours, DateTime today, TimeZoneInfo zone, int days = 7)
    {
        int[] result = new int[24];
        DateTime first = today.Date.AddDays(-(days - 1));
        foreach (HourCount h in hours)
        {
            DateTime local = ToLocal(h.HourUtc, zone);
            if (local.Date >= first && local.Date <= today.Date) result[local.Hour] += h.Count;
        }
        return result;
    }
}
