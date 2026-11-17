using DupClean.Core.Data;
using DupClean.Core.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace DupClean.Core.Actions;

/// <summary>
/// SQLite 기반 Undo 관리자.
/// 모든 조작 결과를 저장하고, 트랜잭션 단위로 롤백.
/// </summary>
public sealed class UndoManager
{
    private static readonly ILogger Log = Serilog.Log.ForContext<UndoManager>();

    private readonly IDbContextFactory<DupCleanDbContext> _dbFactory;
    private int _currentSessionId;

    public UndoManager(IDbContextFactory<DupCleanDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>스캔 세션 시작. 이후 모든 조작이 이 세션에 기록됨.</summary>
    public async Task BeginSessionAsync(string scanPath)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        var session = new SessionEntity { ScanPath = scanPath, StartedAt = DateTime.UtcNow };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        _currentSessionId = session.Id;

        Log.Information("세션 시작: #{Id} — {Path}", _currentSessionId, scanPath);
    }

    /// <summary>조작 결과를 DB에 저장하고 TransactionId 반환. 기록할 파일이 없으면 -1.</summary>
    public async Task<int> RecordAsync(ActionResult result)
    {
        if (!result.Success || result.Records.Count == 0) return -1;

        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        // 스캔 없이 기록하는 경우에도 세션이 있어야 한다 (외래 키)
        if (_currentSessionId == 0)
        {
            var session = new SessionEntity { ScanPath = string.Empty, StartedAt = DateTime.UtcNow };
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
            _currentSessionId = session.Id;
        }

        var tx = new TransactionEntity
        {
            SessionId  = _currentSessionId,
            ActionType = result.ActionType,
            CreatedAt  = DateTime.UtcNow
        };
        db.Transactions.Add(tx);
        await db.SaveChangesAsync();

        foreach (var r in result.Records)
        {
            db.FileActions.Add(new FileActionEntity
            {
                TransactionId = tx.Id,
                OriginalPath  = r.OriginalPath,
                NewPath       = r.NewPath,
                FileSize      = r.FileSize,
                Sha256        = r.Sha256
            });
        }

        await db.SaveChangesAsync();
        Log.Information("기록: 트랜잭션 #{TxId}, {Type}, {Count}개 파일", tx.Id, result.ActionType, result.Records.Count);
        return tx.Id;
    }

    /// <summary>되돌릴 수 있는 작업이 남아 있는지 (이전 스캔·이전 실행에서 한 작업 포함).</summary>
    public async Task<bool> HasUndoableAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        return await db.Transactions.AnyAsync(t => !t.IsRolledBack);
    }

    /// <summary>
    /// 가장 최근 작업을 되돌린다. 스캔을 새로 했거나 프로그램을 다시 켠 뒤에도 이전 작업을 되돌릴 수 있도록
    /// 세션과 무관하게 전체 기록에서 찾는다. 되돌릴 작업이 없으면 null.
    /// </summary>
    public async Task<UndoResult?> UndoLastAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        var tx = await db.Transactions
            .Where(t => !t.IsRolledBack)
            .Include(t => t.FileActions)
            .OrderByDescending(t => t.Id)
            .FirstOrDefaultAsync();

        if (tx is null)
        {
            Log.Information("Undo: 롤백할 항목 없음.");
            return null;
        }

        return await RollbackTransaction(db, tx);
    }

    /// <summary>특정 트랜잭션을 롤백. 없거나 이미 되돌렸으면 null.</summary>
    public async Task<UndoResult?> UndoAsync(int transactionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        var tx = await db.Transactions
            .Include(t => t.FileActions)
            .FirstOrDefaultAsync(t => t.Id == transactionId && !t.IsRolledBack);

        if (tx is null) return null;
        return await RollbackTransaction(db, tx);
    }

    /// <summary>현재 세션의 전체 트랜잭션 목록 반환.</summary>
    public async Task<IReadOnlyList<TransactionEntity>> GetHistoryAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Transactions
            .Where(t => t.SessionId == _currentSessionId)
            .Include(t => t.FileActions)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    // ── 롤백 구현 ────────────────────────────────────────────────

    private static async Task<UndoResult> RollbackTransaction(
        DupCleanDbContext db, TransactionEntity tx)
    {
        Log.Information("롤백: 트랜잭션 #{TxId} ({Type})", tx.Id, tx.ActionType);

        var restored      = 0;
        var notRestorable = 0;
        var failures      = new List<string>();

        foreach (var fa in tx.FileActions)
        {
            try
            {
                switch (tx.ActionType)
                {
                    case "Quarantine":
                    case "Rename":
                        if (fa.NewPath is null || !File.Exists(Safe(fa.NewPath)))
                        {
                            failures.Add($"{fa.OriginalPath}: 보관된 파일이 없음 (이미 정리됐거나 옮겨짐)");
                            break;
                        }
                        if (File.Exists(Safe(fa.OriginalPath)))
                        {
                            failures.Add($"{fa.OriginalPath}: 원래 자리에 같은 이름의 파일이 이미 있음");
                            break;
                        }
                        EnsureDirectory(fa.OriginalPath);
                        File.Move(Safe(fa.NewPath), Safe(fa.OriginalPath), overwrite: false);
                        restored++;
                        Log.Information("복원: {Dest} → {Src}", fa.NewPath, fa.OriginalPath);
                        break;

                    case "HardLink":
                        // 하드링크 롤백: 남긴 파일의 복사본을 만들어 링크를 독립된 파일로 되돌린다.
                        // 원래 자리에는 링크가 놓여 있으므로, 임시 파일에 복사한 뒤 바꿔치기한다.
                        if (fa.NewPath is null || !File.Exists(Safe(fa.NewPath)))
                        {
                            failures.Add($"{fa.OriginalPath}: 원본 파일이 없음");
                            break;
                        }
                        EnsureDirectory(fa.OriginalPath);
                        var temp = Safe(fa.OriginalPath) + ".dupclean-undo-" + Guid.NewGuid().ToString("N")[..8];
                        try
                        {
                            File.Copy(Safe(fa.NewPath), temp, overwrite: false);
                            File.Move(temp, Safe(fa.OriginalPath), overwrite: true);
                        }
                        catch
                        {
                            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 임시 파일 정리 실패 무시 */ }
                            throw;
                        }
                        restored++;
                        Log.Information("하드링크 롤백(복사): {Original} → {Dup}", fa.NewPath, fa.OriginalPath);
                        break;

                    case "Delete":
                        // 휴지통으로 보낸 파일은 프로그램이 꺼내 올 수 없다. 성공으로 세지 않는다.
                        notRestorable++;
                        Log.Warning("Delete 롤백 불가: 휴지통에서 직접 복원하세요. ({Path})", fa.OriginalPath);
                        break;

                    default:
                        failures.Add($"{fa.OriginalPath}: 알 수 없는 작업 종류 ({tx.ActionType})");
                        break;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{fa.OriginalPath}: {ex.Message}");
            }
        }

        foreach (var f in failures) Log.Warning("롤백 안 됨: {Detail}", f);

        // 다시 시도해도 결과가 같으므로(복원된 파일은 이미 제자리) 이 작업은 "처리함"으로 표시하고,
        // 다음 되돌리기는 그 이전 작업으로 넘어간다. 실제 결과는 UndoResult로 정직하게 알린다.
        tx.IsRolledBack = true;
        await db.SaveChangesAsync();

        return new UndoResult(tx.ActionType, restored, notRestorable, failures);
    }

    private const string LongPathPrefix = @"\\?\";

    private static string Safe(string path) =>
        path.Length > 260 && !path.StartsWith(LongPathPrefix, StringComparison.Ordinal)
            ? LongPathPrefix + path
            : path;

    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}

/// <summary>되돌리기 결과. 실제로 복원된 수와 그렇지 못한 수를 구분한다.</summary>
/// <param name="ActionType">되돌린 작업 종류 (Quarantine / HardLink / Delete / Rename).</param>
/// <param name="Restored">실제로 제자리에 복원된 파일 수.</param>
/// <param name="NotRestorable">프로그램이 복원할 수 없는 파일 수 (휴지통 삭제).</param>
/// <param name="Failures">복원에 실패한 파일과 사유.</param>
public sealed record UndoResult(
    string ActionType,
    int Restored,
    int NotRestorable,
    IReadOnlyList<string> Failures)
{
    public bool AllRestored => NotRestorable == 0 && Failures.Count == 0;
}
