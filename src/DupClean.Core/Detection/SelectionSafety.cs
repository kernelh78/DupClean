using DupClean.Core.Models;

namespace DupClean.Core.Detection;

/// <summary>
/// 삭제·격리를 실행하기 전에 "그룹의 파일을 전부 고른 경우"를 찾아낸다.
///
/// - 완전 중복 그룹을 전부 고르면 원본까지 사라진다 → 실행을 막아야 한다.
/// - 유사 이미지 그룹은 전부 지우는 것이 맞는 경우도 있다(전부 필요 없는 사진) → 막지는 않되 경고한다.
/// </summary>
public static class SelectionSafety
{
    /// <param name="ExactGroupsFullySelected">모든 파일이 선택된 완전 중복 그룹 수 (실행 차단 대상).</param>
    /// <param name="SimilarGroupsFullySelected">모든 파일이 선택된 유사 이미지 그룹 수 (경고 대상).</param>
    public sealed record Report(int ExactGroupsFullySelected, int SimilarGroupsFullySelected)
    {
        public bool MustBlock => ExactGroupsFullySelected > 0;
        public bool ShouldWarn => SimilarGroupsFullySelected > 0;
    }

    /// <param name="groups">그룹마다 (종류, 전체 파일 수, 선택된 파일 수).</param>
    public static Report Analyze(IEnumerable<(DuplicateKind Kind, int Total, int Selected)> groups)
    {
        int exact = 0, similar = 0;
        foreach (var (kind, total, selected) in groups)
        {
            if (total <= 0 || selected < total) continue;
            if (kind == DuplicateKind.Exact) exact++;
            else similar++;
        }
        return new Report(exact, similar);
    }
}
