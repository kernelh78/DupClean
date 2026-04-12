namespace DupClean.Core.Models;

/// <summary>Sparse Hashing 3단계 결과. null이면 해당 단계 미계산.</summary>
public sealed record FileHash(
    FileEntry Entry,
    string?   SampleHash,  // 시작1MB+중간1MB+끝1MB 샘플 해시
    string?   FullHash     // 전체 SHA-256
)
{
    /// <summary>중복 비교에 사용할 최종 키. FullHash 우선, 없으면 SampleHash.</summary>
    public string? BestHash => FullHash ?? SampleHash;
}
