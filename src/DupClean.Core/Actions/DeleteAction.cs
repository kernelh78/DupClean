using System.Runtime.Versioning;
using DupClean.Core.Models;
using Serilog;

namespace DupClean.Core.Actions;

/// <summary>파일을 Windows 휴지통으로 이동. 복구 가능.</summary>
[SupportedOSPlatform("windows")]
public sealed class DeleteAction : IDuplicationAction
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DeleteAction>();

    public string ActionType => "Delete";

    public async Task<ActionResult> ExecuteAsync(
        IReadOnlyList<FileEntry> targets,
        CancellationToken ct = default)
    {
        var records = new List<FileActionRecord>();
        var errors  = new List<string>();

        foreach (var file in targets)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await Task.Run(() =>
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        file.SafePath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin),
                    ct);

                records.Add(new FileActionRecord(file.FullPath, null, file.Size, null));
                Log.Information("휴지통 이동: {Path}", file.FullPath);
            }
            catch (Exception ex)
            {
                Log.Warning("삭제 실패 {Path}: {Msg}", file.FullPath, ex.Message);
                errors.Add($"{file.FileName}: {ex.Message}");
            }
        }

        if (errors.Count > 0 && records.Count == 0)
            return ActionResult.Fail(ActionType, string.Join("; ", errors));

        return ActionResult.Ok(ActionType, records);
    }
}
