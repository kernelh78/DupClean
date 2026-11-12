namespace DupClean.Core.Detection;

/// <summary>스캔 단계. 번호가 곧 화면에 표시되는 단계 순서.</summary>
public enum ScanPhase
{
    /// <summary>파일 목록 수집.</summary>
    Collecting = 1,

    /// <summary>중복 후보 해시 계산 (완전 중복 검사).</summary>
    Hashing = 2,

    /// <summary>유사 이미지 분석 (pHash).</summary>
    Similar = 3,
}

/// <summary>
/// 스캔 진행 상황. UI는 이 값만 보고 진행 바·남은 시간·속도를 그린다.
/// 앞의 3개 값(Message, Done, Total)은 이전 버전과의 호환용.
/// </summary>
public sealed record ScanProgress(string Message, int Done, int Total)
{
    public double Percent => Total > 0 ? (double)Done / Total * 100 : 0;

    /// <summary>현재 단계.</summary>
    public ScanPhase Phase { get; init; } = ScanPhase.Collecting;

    /// <summary>현재 단계 번호 (1부터).</summary>
    public int PhaseNumber { get; init; } = 1;

    /// <summary>전체 단계 수 (유사 이미지 탐지를 끄면 2, 켜면 3).</summary>
    public int PhaseCount { get; init; } = 1;

    /// <summary>현재 단계 이름 (예: "중복 파일 검사").</summary>
    public string PhaseTitle { get; init; } = string.Empty;

    /// <summary>지금 처리 중인 파일 경로.</summary>
    public string? CurrentFile { get; init; }

    /// <summary>현재 단계에서 처리한 바이트 / 처리해야 할 전체 바이트.</summary>
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }

    /// <summary>전체 작업 대비 진행률 (0~100). 진행 바에 사용.</summary>
    public double OverallPercent { get; init; }

    /// <summary>현재 단계의 진행률 (0~100).</summary>
    public double PhasePercent { get; init; }

    /// <summary>전체 양을 아직 모르는 상태 (파일 수집 중). 진행 바는 "움직이는 막대"로 표시.</summary>
    public bool IsIndeterminate { get; init; }

    /// <summary>스캔 시작 후 경과 시간.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>현재 단계가 끝날 때까지 남은 예상 시간. 아직 모르면 null.</summary>
    public TimeSpan? PhaseEta { get; init; }

    /// <summary>전체가 끝날 때까지 남은 예상 시간. 아직 모르면 null.</summary>
    public TimeSpan? TotalEta { get; init; }

    /// <summary>최근 처리 속도. RateIsBytes가 true면 바이트/초, 아니면 개/초.</summary>
    public double Rate { get; init; }
    public bool RateIsBytes { get; init; }
}

/// <summary>스캔 최종 결과.</summary>
public sealed record ScanResult(
    int ScannedFiles,
    IReadOnlyList<DupClean.Core.Models.DuplicateGroup> Groups,
    long ElapsedMs,
    long TotalReclaimableBytes
);
