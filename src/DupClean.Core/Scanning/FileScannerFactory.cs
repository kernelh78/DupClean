using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DupClean.Core.Models;
using Serilog;

namespace DupClean.Core.Scanning;

/// <summary>
/// 환경에 따라 최적 스캐너를 선택.
/// Windows NTFS → MftScanner. 그 외 → IoScanner.
/// </summary>
public static class FileScannerFactory
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(FileScannerFactory));

    public static IFileScanner Create(string rootPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var drive = Path.GetPathRoot(rootPath)?.TrimEnd('\\', '/') ?? "C:";
            var letter = drive.TrimEnd(':');

            if (MftScanner.IsAvailable(letter))
            {
                Log.Information("스캐너: MFT (NTFS 고속 모드) — {Drive}", drive);
                return new MftScanner();
            }
        }

        Log.Information("스캐너: IO (System.IO 표준 모드)");
        return new IoScanner();
    }
}
