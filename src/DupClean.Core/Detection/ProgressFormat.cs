namespace DupClean.Core.Detection;

/// <summary>진행 상황을 사람이 읽기 좋은 한국어 문장으로 바꾸는 도구.</summary>
public static class ProgressFormat
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024L * 1024        => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024L               => $"{bytes / 1024.0:F1} KB",
        _                      => $"{bytes} B",
    };

    /// <summary>경과 시간: "0:07", "12:34", "1:02:03".</summary>
    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    /// <summary>걸린 시간(완료 후 표시): 10초 미만은 "0.6초", 그 이상은 "1:05" 형식.</summary>
    public static string Took(TimeSpan t) =>
        t.TotalSeconds < 10 ? $"{Math.Max(0, t.TotalSeconds):F1}초" : Duration(t);

    /// <summary>남은 시간: "계산 중…", "곧 완료", "약 45초", "약 3분 20초", "약 12분", "약 1시간 5분".</summary>
    public static string Eta(TimeSpan? eta)
    {
        if (eta is not { } t) return "계산 중…";

        var s = (int)Math.Round(t.TotalSeconds);
        if (s < 3) return "곧 완료";

        if (s < 60)
        {
            var r = RoundTo(s, 5);
            return r >= 60 ? "약 1분" : $"약 {r}초";
        }

        if (s < 600)
        {
            var r   = RoundTo(s, 10);
            var min = r / 60;
            var sec = r % 60;
            return sec == 0 ? $"약 {min}분" : $"약 {min}분 {sec}초";
        }

        if (s < 3600) return $"약 {(s + 30) / 60}분";

        var h = s / 3600;
        var m = s % 3600 / 60;
        return m == 0 ? $"약 {h}시간" : $"약 {h}시간 {m}분";
    }

    /// <summary>처리 속도: "180.2 MB/s" 또는 "35개/초".</summary>
    public static string Rate(double rate, bool isBytes)
    {
        if (rate <= 0) return "—";
        return isBytes ? $"{Bytes((long)rate)}/s" : rate < 10 ? $"{rate:F1}개/초" : $"{rate:N0}개/초";
    }

    private static int RoundTo(int value, int step) =>
        (int)Math.Round(value / (double)step, MidpointRounding.AwayFromZero) * step;
}
