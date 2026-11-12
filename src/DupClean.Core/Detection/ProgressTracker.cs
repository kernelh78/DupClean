using System.Diagnostics;

namespace DupClean.Core.Detection;

/// <summary>
/// 스캔 진행 상황을 모아서 "전체 진행률 · 남은 시간 · 처리 속도"로 계산해 내보내는 도구.
///
/// - 여러 스레드가 동시에 Advance를 불러도 안전하다.
/// - 화면 갱신은 최대 초당 약 10번(minIntervalMs)으로 제한해, 파일이 수십만 개여도 화면이 밀리지 않는다.
/// - 남은 시간은 "최근 속도의 이동 평균"으로 계산한다. 처음 1초 동안은 값이 불안정해서 null("계산 중")로 둔다.
/// - 전체 남은 시간 = 현재 단계 남은 시간 + 아직 시작하지 않은 단계의 예상 시간(Plan으로 미리 알려준 작업량 / 기본 속도).
/// </summary>
public sealed class ProgressTracker
{
    /// <summary>
    /// 아직 시작하지 않은 단계의 예상 속도 기본값(보수적): 해시 120MB/s, 유사 이미지 40장/초.
    /// 실제 환경에 맞는 값은 <see cref="SetRateHints"/>로 덮어쓴다. 해당 단계가 시작되면 실측 속도로 대체된다.
    /// </summary>
    internal const double DefaultHashBytesPerSec    = 120d * 1024 * 1024;
    internal const double DefaultSimilarItemsPerSec = 40d;

    private double _hashBytesPerSecHint    = DefaultHashBytesPerSec;
    private double _similarItemsPerSecHint = DefaultSimilarItemsPerSec;

    private readonly IProgress<ScanProgress>? _sink;
    private readonly bool _similarEnabled;
    private readonly Func<long> _clock;
    private readonly long _minIntervalMs;
    private readonly long _startMs;
    private readonly object _gate = new();

    // 현재 단계 상태 (카운터는 Interlocked로, 나머지는 _gate 안에서만 바꾼다)
    private ScanPhase _phase = ScanPhase.Collecting;
    private bool _phaseActive;
    private bool _totalsKnown;
    private string _message = string.Empty;
    private volatile string? _currentFile;
    private long _items, _itemsTotal, _bytes, _bytesTotal;
    private long _phaseStartMs;
    private long _lastEmitMs = long.MinValue / 2;
    private long _lastSampleMs;
    private double _lastSampleWork;
    private double _ema;

    private readonly HashSet<ScanPhase> _completed = [];
    private readonly Dictionary<ScanPhase, (long Items, long Bytes)> _planned = [];

    /// <param name="sink">진행 상황을 받을 곳 (UI). null이면 아무것도 보내지 않는다.</param>
    /// <param name="similarEnabled">유사 이미지 단계를 포함하는지.</param>
    /// <param name="minIntervalMs">진행 알림 최소 간격(ms).</param>
    /// <param name="clock">시계(ms). 테스트에서 가짜 시계를 넣기 위한 용도.</param>
    public ProgressTracker(
        IProgress<ScanProgress>? sink,
        bool similarEnabled,
        long minIntervalMs = 100,
        Func<long>? clock = null)
    {
        _sink           = sink;
        _similarEnabled = similarEnabled;
        _minIntervalMs  = minIntervalMs;
        _clock          = clock ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
        _startMs        = _clock();
    }

    public int PhaseCount => _similarEnabled ? 3 : 2;

    // ── 작업 흐름 알림 ────────────────────────────────────────────

    /// <summary>아직 시작하지 않은 단계의 작업량을 미리 알려준다 (전체 남은 시간 예상에 사용).</summary>
    public void Plan(ScanPhase phase, long items, long bytes)
    {
        lock (_gate) _planned[phase] = (items, bytes);
    }

    /// <summary>
    /// 이 PC·저장장치에 맞는 예상 속도를 알려준다. 시작 전 단계의 소요 시간을 어림잡는 데만 쓰이며,
    /// 값이 0 이하이면 무시한다.
    /// </summary>
    public void SetRateHints(double hashBytesPerSec, double similarItemsPerSec)
    {
        lock (_gate)
        {
            if (hashBytesPerSec > 0)    _hashBytesPerSecHint    = hashBytesPerSec;
            if (similarItemsPerSec > 0) _similarItemsPerSecHint = similarItemsPerSec;
        }
    }

    /// <summary>
    /// 새 단계 시작. itemsTotal/bytesTotal이 음수면 "전체 양을 아직 모름"(파일 수집 중처럼).
    /// 양을 나중에 알게 되면 <see cref="SetTotals"/>를 부른다.
    /// </summary>
    public void BeginPhase(ScanPhase phase, string message, long itemsTotal = -1, long bytesTotal = -1)
    {
        lock (_gate)
        {
            _phase       = phase;
            _phaseActive = true;
            _message     = message;
            _currentFile = null;
            _totalsKnown = itemsTotal >= 0 && bytesTotal >= 0;
            Interlocked.Exchange(ref _items, 0);
            Interlocked.Exchange(ref _bytes, 0);
            Interlocked.Exchange(ref _itemsTotal, Math.Max(0, itemsTotal));
            Interlocked.Exchange(ref _bytesTotal, Math.Max(0, bytesTotal));
            _phaseStartMs   = _clock();
            _lastSampleMs   = _phaseStartMs;
            _lastSampleWork = 0;
            _ema            = 0;
            _planned.Remove(phase);   // 이제 실제 값을 알고 있음
        }
        Emit(force: true);
    }

    /// <summary>현재 단계의 전체 작업량이 확정됐을 때 호출.</summary>
    public void SetTotals(long itemsTotal, long bytesTotal)
    {
        lock (_gate)
        {
            _totalsKnown = true;
            Interlocked.Exchange(ref _itemsTotal, itemsTotal);
            Interlocked.Exchange(ref _bytesTotal, bytesTotal);
        }
        Emit(force: true);
    }

    /// <summary>작업량이 늘어났을 때(예: 샘플이 같아서 전체 해시가 추가로 필요해짐) 호출.</summary>
    public void AddTotal(long items, long bytes)
    {
        if (items != 0) Interlocked.Add(ref _itemsTotal, items);
        if (bytes != 0) Interlocked.Add(ref _bytesTotal, bytes);
    }

    /// <summary>작업 진행. 여러 스레드에서 호출해도 안전하다.</summary>
    public void Advance(long items = 1, long bytes = 0, string? currentFile = null)
    {
        if (items != 0) Interlocked.Add(ref _items, items);
        if (bytes != 0) Interlocked.Add(ref _bytes, bytes);
        if (currentFile is not null) _currentFile = currentFile;
        Emit(force: false);
    }

    public void SetMessage(string message)
    {
        lock (_gate) _message = message;
    }

    public void EndPhase()
    {
        lock (_gate)
        {
            _completed.Add(_phase);
            _phaseActive = false;
        }
        Emit(force: true);
    }

    /// <summary>전체 완료. 진행률을 100%로 맞춘다.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            foreach (var p in Phases()) _completed.Add(p);
            _phaseActive = false;
        }
        Emit(force: true);
    }

    // ── 내부 계산 ────────────────────────────────────────────────

    private IEnumerable<ScanPhase> Phases()
    {
        yield return ScanPhase.Collecting;
        yield return ScanPhase.Hashing;
        if (_similarEnabled) yield return ScanPhase.Similar;
    }

    private double Weight(ScanPhase p) => _similarEnabled
        ? p switch { ScanPhase.Collecting => 0.05, ScanPhase.Hashing => 0.45, _ => 0.50 }
        : p switch { ScanPhase.Collecting => 0.08, ScanPhase.Hashing => 0.92, _ => 0 };

    private static string TitleOf(ScanPhase p) => p switch
    {
        ScanPhase.Collecting => "파일 목록 수집",
        ScanPhase.Hashing    => "중복 파일 검사",
        _                    => "유사 이미지 분석",
    };

    private void Emit(bool force)
    {
        if (_sink is null) return;

        var now = _clock();
        if (!force && now - Volatile.Read(ref _lastEmitMs) < _minIntervalMs) return;

        ScanProgress snapshot;
        lock (_gate)
        {
            if (!force && now - _lastEmitMs < _minIntervalMs) return;
            _lastEmitMs = now;
            snapshot = BuildSnapshot(now);
        }
        _sink.Report(snapshot);
    }

    private ScanProgress BuildSnapshot(long now)
    {
        var items      = Interlocked.Read(ref _items);
        var bytes      = Interlocked.Read(ref _bytes);
        var itemsTotal = Interlocked.Read(ref _itemsTotal);
        var bytesTotal = Interlocked.Read(ref _bytesTotal);

        var usesBytes = bytesTotal > 0;
        double work   = usesBytes ? bytes : items;
        double total  = usesBytes ? bytesTotal : itemsTotal;

        // 처리 속도: 최근 속도를 더 크게 반영하는 이동 평균 (갑작스런 출렁임 완화)
        if (_phaseActive)
        {
            var dt = now - _lastSampleMs;
            if (dt >= 200)
            {
                var inst = (work - _lastSampleWork) * 1000.0 / dt;
                _ema = _ema <= 0 ? inst : 0.2 * inst + 0.8 * _ema;
                _lastSampleMs   = now;
                _lastSampleWork = work;
            }
        }

        // 현재 단계 진행 비율 (0~1)
        double fraction;
        if (!_phaseActive)          fraction = _completed.Contains(_phase) ? 1 : 0;
        else if (!_totalsKnown)     fraction = 0;
        else if (total > 0)         fraction = Math.Min(1, work / total);
        else                        fraction = 1;   // 할 일이 하나도 없음

        // 현재 단계 남은 시간
        TimeSpan? phaseEta = null;
        if (_phaseActive && _totalsKnown)
        {
            var remaining = Math.Max(0, total - work);
            if (remaining <= 0)
                phaseEta = TimeSpan.Zero;
            else if (_ema > 0 && now - _phaseStartMs >= 1000)
                phaseEta = TimeSpan.FromSeconds(remaining / _ema);
        }

        // 전체 남은 시간 = 현재 단계 + 아직 시작 안 한 단계(예상)
        TimeSpan? totalEta = null;
        if (phaseEta is { } pe && _phase != ScanPhase.Collecting)
        {
            var seconds = pe.TotalSeconds;
            foreach (var p in Phases().Where(p => p > _phase))
                seconds += EstimateSeconds(p);
            totalEta = TimeSpan.FromSeconds(seconds);
        }

        // 전체 진행률
        double overall = 0;
        foreach (var p in Phases())
        {
            if (_completed.Contains(p) && !(_phaseActive && p == _phase))
                overall += Weight(p);
            else if (_phaseActive && p == _phase)
                overall += Weight(p) * fraction;
        }

        var phaseNumber = Math.Min((int)_phase, PhaseCount);
        return new ScanProgress(_message, (int)Math.Min(items, int.MaxValue), (int)Math.Min(itemsTotal, int.MaxValue))
        {
            Phase           = _phase,
            PhaseNumber     = phaseNumber,
            PhaseCount      = PhaseCount,
            PhaseTitle      = TitleOf(_phase),
            CurrentFile     = _currentFile,
            BytesDone       = bytes,
            BytesTotal      = bytesTotal,
            OverallPercent  = Math.Clamp(overall * 100, 0, 100),
            PhasePercent    = fraction * 100,
            IsIndeterminate = _phaseActive && !_totalsKnown,
            Elapsed         = TimeSpan.FromMilliseconds(now - _startMs),
            PhaseEta        = phaseEta,
            TotalEta        = totalEta,
            Rate            = _ema,
            RateIsBytes     = usesBytes,
        };
    }

    private double EstimateSeconds(ScanPhase p)
    {
        if (!_planned.TryGetValue(p, out var w)) return 0;
        return p switch
        {
            ScanPhase.Hashing => w.Bytes / _hashBytesPerSecHint,
            ScanPhase.Similar => w.Items / _similarItemsPerSecHint,
            _                 => 0,
        };
    }
}
