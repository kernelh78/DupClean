using DupClean.Core.Detection;
using FluentAssertions;
using Xunit;

namespace DupClean.Tests;

public sealed class ProgressTrackerTests
{
    /// <summary>받은 진행 상황을 그 자리에서 기록하는 수신기 (UI 스레드 없이 테스트하기 위함).</summary>
    private sealed class Sink : IProgress<ScanProgress>
    {
        public List<ScanProgress> Items { get; } = [];
        public void Report(ScanProgress value) { lock (Items) Items.Add(value); }
        public ScanProgress Last { get { lock (Items) return Items[^1]; } }
    }

    private const long MB = 1024L * 1024;

    // ── 남은 시간 ────────────────────────────────────────────────

    [Fact]
    public void Eta_IsCalculatedFromRecentSpeed()
    {
        var now  = 0L;
        var sink = new Sink();
        var t    = new ProgressTracker(sink, similarEnabled: false, minIntervalMs: 0, clock: () => now);

        t.BeginPhase(ScanPhase.Hashing, "해시", 10, 1000 * MB);

        // 1초마다 100MB씩 → 5초 후 500MB 처리, 남은 500MB는 약 5초
        for (var i = 0; i < 5; i++)
        {
            now += 1000;
            t.Advance(0, 100 * MB);
        }

        var p = sink.Last;
        p.PhaseEta.Should().NotBeNull();
        p.PhaseEta!.Value.TotalSeconds.Should().BeApproximately(5, 0.6);
        p.Rate.Should().BeApproximately(100 * MB, 5 * MB);
        p.RateIsBytes.Should().BeTrue();
        p.PhasePercent.Should().BeApproximately(50, 0.1);
    }

    [Fact]
    public void Eta_IsUnknownDuringFirstSecond()
    {
        var now  = 0L;
        var sink = new Sink();
        var t    = new ProgressTracker(sink, false, 0, () => now);

        t.BeginPhase(ScanPhase.Hashing, "해시", 10, 1000 * MB);
        now += 300;
        t.Advance(0, 50 * MB);

        sink.Last.PhaseEta.Should().BeNull("처음 1초는 속도가 불안정해서 '계산 중'으로 둔다");
        sink.Last.TotalEta.Should().BeNull();
    }

    [Fact]
    public void TotalEta_IncludesPlannedLaterPhase()
    {
        var now  = 0L;
        var sink = new Sink();
        var t    = new ProgressTracker(sink, similarEnabled: true, minIntervalMs: 0, clock: () => now);

        t.Plan(ScanPhase.Similar, items: 400, bytes: 0);   // 400장 ÷ 기본 40장/초 = 10초
        t.BeginPhase(ScanPhase.Hashing, "해시", 10, 1000 * MB);

        for (var i = 0; i < 5; i++) { now += 1000; t.Advance(0, 100 * MB); }

        var p = sink.Last;
        p.PhaseEta!.Value.TotalSeconds.Should().BeApproximately(5, 0.6);
        p.TotalEta!.Value.TotalSeconds.Should().BeApproximately(15, 0.6);   // 5초 + 10초
    }

    [Fact]
    public void RateHints_ReplaceDefaults_ForLaterPhases()
    {
        var now  = 0L;
        var sink = new Sink();
        var t    = new ProgressTracker(sink, similarEnabled: true, minIntervalMs: 0, clock: () => now);

        t.SetRateHints(hashBytesPerSec: 1000 * MB, similarItemsPerSec: 400);   // 빠른 PC
        t.Plan(ScanPhase.Similar, items: 400, bytes: 0);                       // 400장 ÷ 400장/초 = 1초
        t.BeginPhase(ScanPhase.Hashing, "해시", 10, 1000 * MB);

        for (var i = 0; i < 5; i++) { now += 1000; t.Advance(0, 100 * MB); }

        sink.Last.TotalEta!.Value.TotalSeconds.Should().BeApproximately(6, 0.6);   // 5초 + 1초
    }

    [Fact]
    public void RateHints_IgnoreNonPositiveValues()
    {
        var now  = 0L;
        var sink = new Sink();
        var t    = new ProgressTracker(sink, true, 0, () => now);

        t.SetRateHints(0, -5);                    // 잘못된 값은 기본값 유지
        t.Plan(ScanPhase.Similar, 400, 0);        // 기본 40장/초 → 10초
        t.BeginPhase(ScanPhase.Hashing, "해시", 10, 1000 * MB);
        for (var i = 0; i < 5; i++) { now += 1000; t.Advance(0, 100 * MB); }

        sink.Last.TotalEta!.Value.TotalSeconds.Should().BeApproximately(15, 0.6);
    }

    [Fact]
    public void Eta_UsesItemsWhenNoBytes()
    {
        var now  = 0L;
        var sink = new Sink();
        var t    = new ProgressTracker(sink, true, 0, () => now);

        t.BeginPhase(ScanPhase.Similar, "유사", 100, 0);
        for (var i = 0; i < 4; i++) { now += 1000; t.Advance(10); }   // 초당 10개, 40개 완료

        var p = sink.Last;
        p.RateIsBytes.Should().BeFalse();
        p.PhaseEta!.Value.TotalSeconds.Should().BeApproximately(6, 0.6);  // 60개 ÷ 10
    }

    // ── 전체 진행률 ──────────────────────────────────────────────

    [Fact]
    public void OverallPercent_NeverGoesBackwards_AcrossPhases()
    {
        var now  = 0L;
        var sink = new Sink();
        var t    = new ProgressTracker(sink, true, 0, () => now);

        t.BeginPhase(ScanPhase.Collecting, "수집");
        for (var i = 0; i < 20; i++) { now += 100; t.Advance(); }
        t.EndPhase();

        t.BeginPhase(ScanPhase.Hashing, "해시", 10, 100 * MB);
        for (var i = 0; i < 10; i++) { now += 100; t.Advance(1, 10 * MB); }
        t.EndPhase();

        t.BeginPhase(ScanPhase.Similar, "유사", 50, 0);
        for (var i = 0; i < 50; i++) { now += 100; t.Advance(); }
        t.EndPhase();
        t.Complete();

        var percents = sink.Items.Select(p => p.OverallPercent).ToList();
        percents.Zip(percents.Skip(1), (a, b) => b >= a - 0.0001).Should().OnlyContain(ok => ok);
        percents[^1].Should().BeApproximately(100, 0.001);
    }

    [Fact]
    public void Collecting_IsIndeterminate()
    {
        var sink = new Sink();
        var t    = new ProgressTracker(sink, true, 0, () => 0);

        t.BeginPhase(ScanPhase.Collecting, "수집");
        t.Advance(5);

        sink.Last.IsIndeterminate.Should().BeTrue();
        sink.Last.Done.Should().Be(5);
        sink.Last.PhaseNumber.Should().Be(1);
        sink.Last.PhaseCount.Should().Be(3);
    }

    [Fact]
    public void PhaseCount_IsTwo_WhenSimilarDisabled()
    {
        var sink = new Sink();
        var t    = new ProgressTracker(sink, similarEnabled: false, 0, () => 0);
        t.BeginPhase(ScanPhase.Collecting, "수집");
        sink.Last.PhaseCount.Should().Be(2);
    }

    [Fact]
    public void PhaseWithNothingToDo_ReportsFullFraction()
    {
        var sink = new Sink();
        var t    = new ProgressTracker(sink, true, 0, () => 0);

        t.BeginPhase(ScanPhase.Similar, "유사", 0, 0);   // 전부 캐시에서 가져와 계산할 게 없음

        sink.Last.IsIndeterminate.Should().BeFalse();
        sink.Last.PhasePercent.Should().Be(100);
        sink.Last.PhaseEta.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void AddTotal_GrowsWork_AndPercentStaysWithinRange()
    {
        var now  = 0L;
        var sink = new Sink();
        var t    = new ProgressTracker(sink, false, 0, () => now);

        t.BeginPhase(ScanPhase.Hashing, "해시", 2, 100 * MB);
        now += 100; t.Advance(1, 100 * MB);
        t.AddTotal(0, 100 * MB);     // 샘플이 같아서 전체 해시가 더 필요해짐
        now += 100; t.Advance(0, 10 * MB);

        sink.Last.PhasePercent.Should().BeInRange(0, 100);
        sink.Last.BytesTotal.Should().Be(200 * MB);
    }

    // ── 화면 갱신 빈도 제한 / 동시성 ──────────────────────────────

    [Fact]
    public void Reports_AreThrottled()
    {
        var now  = 0L;
        var sink = new Sink();
        var t    = new ProgressTracker(sink, false, minIntervalMs: 100, clock: () => now);

        t.BeginPhase(ScanPhase.Collecting, "수집");     // 강제 알림 1회
        var before = sink.Items.Count;

        for (var i = 0; i < 10_000; i++) t.Advance();   // 시간이 안 흘렀으므로 알림 없어야 함
        sink.Items.Count.Should().BeLessThanOrEqualTo(before + 1);

        now += 150;
        t.Advance();
        sink.Items.Count.Should().BeLessThanOrEqualTo(before + 2);
        sink.Last.Done.Should().Be(10_001, "알림을 건너뛰어도 집계는 빠짐없이 쌓인다");
    }

    [Fact]
    public async Task Advance_IsThreadSafe()
    {
        var sink = new Sink();
        var t    = new ProgressTracker(sink, false, minIntervalMs: 0);

        t.BeginPhase(ScanPhase.Collecting, "수집");
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 5_000; i++) t.Advance(1, 10);
        })));
        t.EndPhase();

        sink.Last.Done.Should().Be(40_000);
        sink.Last.BytesDone.Should().Be(400_000);
    }

    [Fact]
    public void NullSink_DoesNotThrow()
    {
        var t = new ProgressTracker(null, true);
        t.BeginPhase(ScanPhase.Collecting, "수집");
        t.Advance(100);
        t.EndPhase();
        t.Complete();
    }
}
