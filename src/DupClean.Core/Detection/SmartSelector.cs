using DupClean.Core.Models;

namespace DupClean.Core.Detection;

/// <summary>
/// 중복 그룹에서 "삭제 대상"을 자동 선택하는 규칙 엔진.
/// 규칙 우선순위: 마스터 폴더 보호 → 이름 패턴 → 경로 깊이 → 파일 날짜
/// </summary>
public sealed class SmartSelector
{
    // 복사본으로 간주하는 이름 패턴 (대소문자 무시)
    private static readonly string[] CopyPatterns =
        ["copy", "복사", "복사본", " - 복사", "(1)", "(2)", "(3)", "(4)", "(5)"];

    private readonly IReadOnlyList<string> _masterFolders;

    /// <param name="masterFolders">보호할 폴더 경로 목록 (여기 포함된 파일은 절대 선택 안 함).</param>
    public SmartSelector(IReadOnlyList<string>? masterFolders = null)
    {
        _masterFolders = masterFolders ?? [];
    }

    /// <summary>
    /// 그룹 내에서 삭제 대상 파일 목록을 반환.
    /// 가장 "좋은" 파일 하나를 남기고 나머지를 반환.
    /// </summary>
    public IReadOnlyList<FileEntry> SelectFromGroup(IReadOnlyList<FileEntry> files)
    {
        if (files.Count <= 1) return [];

        // 마스터 폴더에 있는 파일은 후보에서 제외
        var candidates = files
            .Where(f => !IsInMasterFolder(f))
            .ToList();

        // 전부 마스터 폴더면 아무것도 선택 안 함
        if (candidates.Count == 0) return [];

        // 마스터 폴더 파일이 있으면, 마스터 외 파일 전부 삭제 대상
        var masterFiles = files.Except(candidates).ToList();
        if (masterFiles.Count > 0)
            return candidates;

        // 마스터 폴더 없음 → 점수 계산으로 "최고" 파일 선정
        var best = files
            .OrderBy(f => Score(f))
            .First();

        return files.Where(f => f != best).ToList();
    }

    // 낮을수록 "좋은" 파일 (보존 대상)
    private int Score(FileEntry f)
    {
        var score = 0;

        // 이름에 복사 패턴 포함 → 나쁨
        var name = Path.GetFileNameWithoutExtension(f.FileName);
        if (CopyPatterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase)))
            score += 100;

        // 경로 깊이 (구분자 수): 깊을수록 나쁨
        score += f.FullPath.Count(c => c == '\\' || c == '/');

        // 오래된 파일이 원본일 가능성 높음 → 최신 파일 나쁨
        // 1970년부터 초 단위 차이 → 상대 순서만 필요하므로 Ticks 비교
        // (나중에 생성된 파일 score +)
        // → 날짜는 정렬 타이브레이커로만 사용
        // 직접 OrderBy에서 처리하므로 여기서는 0

        return score;
    }

    // 동점일 때 최신 파일이 뒤로 가도록 파일 날짜 비교
    public IReadOnlyList<FileEntry> SelectFromGroupWithDate(IReadOnlyList<FileEntry> files)
    {
        if (files.Count <= 1) return [];

        var candidates = files.Where(f => !IsInMasterFolder(f)).ToList();
        if (candidates.Count == 0) return [];

        var masterFiles = files.Except(candidates).ToList();
        if (masterFiles.Count > 0) return candidates;

        var best = files
            .OrderBy(f => Score(f))
            .ThenBy(f => f.LastModified) // 오래된 파일 우선 보존
            .First();

        return files.Where(f => f != best).ToList();
    }

    private bool IsInMasterFolder(FileEntry f) =>
        _masterFolders.Any(folder =>
            f.FullPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
}
