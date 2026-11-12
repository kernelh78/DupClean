using DupClean.Core.Detection;
using FluentAssertions;
using Xunit;

namespace DupClean.Tests;

public sealed class ProgressFormatTests
{
    [Theory]
    [InlineData(1,     "곧 완료")]
    [InlineData(2,     "곧 완료")]
    [InlineData(12,    "약 10초")]
    [InlineData(47,    "약 45초")]
    [InlineData(58,    "약 1분")]
    [InlineData(60,    "약 1분")]
    [InlineData(95,    "약 1분 40초")]    // 95초 → 10초 단위 반올림 100초
    [InlineData(200,   "약 3분 20초")]
    [InlineData(300,   "약 5분")]
    [InlineData(900,   "약 15분")]
    [InlineData(3600,  "약 1시간")]
    [InlineData(5400,  "약 1시간 30분")]
    public void Eta_IsReadable(int seconds, string expected)
        => ProgressFormat.Eta(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Fact]
    public void Eta_Null_MeansCalculating()
        => ProgressFormat.Eta(null).Should().Be("계산 중…");

    [Theory]
    [InlineData(7,    "0:07")]
    [InlineData(754,  "12:34")]
    [InlineData(3723, "1:02:03")]
    public void Duration_IsFormatted(int seconds, string expected)
        => ProgressFormat.Duration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Theory]
    [InlineData(600,    "0.6초")]
    [InlineData(9400,   "9.4초")]
    [InlineData(65000,  "1:05")]
    public void Took_UsesSecondsWhenShort(int ms, string expected)
        => ProgressFormat.Took(TimeSpan.FromMilliseconds(ms)).Should().Be(expected);

    [Fact]
    public void Rate_ShowsBytesOrItems()
    {
        ProgressFormat.Rate(180 * 1024 * 1024, true).Should().Be("180.0 MB/s");
        ProgressFormat.Rate(35, false).Should().Be("35개/초");
        ProgressFormat.Rate(3.5, false).Should().Be("3.5개/초");
        ProgressFormat.Rate(0, true).Should().Be("—");
    }

    [Fact]
    public void Bytes_IsFormatted()
    {
        ProgressFormat.Bytes(512).Should().Be("512 B");
        ProgressFormat.Bytes(1536).Should().Be("1.5 KB");
        ProgressFormat.Bytes(3L * 1024 * 1024 * 1024).Should().Be("3.0 GB");
    }
}
