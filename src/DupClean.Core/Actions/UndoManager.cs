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

    /// <summary>조작 결과를 DB에 저장하고 TransactionId 반환.</summary>
    public async Task<int> RecordAsync(ActionResult result)
    {
        if (!result.Success) return -1;

        await using var db = await _dbFactory.CreateDbContextAsync();

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

    /// <summary>가장 최근 트랜잭션을 롤백.</summary>
    public async Task<bool> UndoLastAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var tx = await db.Transactions
            .Where(t => t.SessionId == _currentSessionId && !t.IsRolledBack)
            .Include(t => t.FileActions)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        if (tx is null)
        {
            Log.Information("Undo: 롤백할 항목 없음.");
            return false;
        }

        return await RollbackTransaction(db, tx);
    }

    /// <summary>특정 트랜잭션을 롤백.</summary>
    public async Task<bool> UndoAsync(int transactionId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var tx = await db.Transactions
            .Include(t => t.FileActions)
            .FirstOrDefaultAsync(t => t.Id == transactionId && !t.IsRolledBack);

        if (tx is null) return false;
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

    private static async Task<bool> RollbackTransaction(
        DupCleanDbContext db, TransactionEntity tx)
    {
        Log.Information("롤백: 트랜잭션 #{TxId} ({Type})", tx.Id, tx.ActionType);

        var errors = new List<string>();

        foreach (var fa in tx.FileActions)
        {
            try
            {
                switch (tx.ActionType)
                {
                    case "Quarantine":
                        if (fa.NewPath is not null && File.Exists(fa.NewPath))
                        {
                            var dir = Path.GetDirectoryName(fa.OriginalPath);
                            if (dir is not null) Directory.CreateDirectory(dir);
                            File.Move(fa.NewPath, fa.OriginalPath, overwrite: false);
                            Log.Information("복원: {Dest} → {Src}", fa.NewPath, fa.OriginalPath);
                        }
                        break;

                    case "Rename":
                        if (fa.NewPath is not null && File.Exists(fa.NewPath))
                        {
                            File.Move(fa.NewPath, fa.OriginalPath, overwrite: false);
                            Log.Information("이름 복원: {New} → {Old}", fa.NewPath, fa.OriginalPath);
                        }
                        break;

                    case "HardLink":
                        // 하드링크 롤백: 원본에서 복사본을 만들어 중복 위치를 복원
                        if (fa.NewPath is not null && File.Exists(fa.NewPath))
                        {
                            var dir = Path.GetDirectoryName(fa.OriginalPath);
                            if (dir is not null) Directory.CreateDirectory(dir);
                            File.Copy(fa.NewPath, fa.OriginalPath, overwrite: false);
                            Log.Information("하드링크 롤백(복사): {Original} → {Dup}", fa.NewPath, fa.OriginalPath);
                        }
                        break;

                    case "Delete":
                        Log.Warning("Delete 롤백 불가: 휴지통에서 직접 복원하세요. ({Path})", fa.OriginalPath);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("롤백 실패 {Path}: {Msg}", fa.OriginalPath, ex.Message);
                errors.Add(fa.OriginalPath);
            }
        }

        tx.IsRolledBack = true;
        await db.SaveChangesAsync();

        return errors.Count == 0;
    }
}
