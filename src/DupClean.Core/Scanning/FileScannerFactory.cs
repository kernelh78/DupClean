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
            var drive  = Path.GetPathRoot(rootPath)?.TrimEnd('\\', '/') ?? "C:";
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

    /// <summary>현재 환경에서 MFT 스캔이 가능한지, 그 이유와 함께 반환.</summary>
    public static (bool available, string reason) CheckMftAvailability(string rootPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return (false, "Windows가 아님");

        var drive  = Path.GetPathRoot(rootPath)?.TrimEnd('\\', '/') ?? "C:";
        var letter = drive.TrimEnd(':');

        if (MftScanner.IsAvailable(letter))
            return (true, "NTFS MFT 고속 모드");

        // 에러 원인 추정
        try
        {
            var driveInfo = new DriveInfo(drive);
            if (!string.Equals(driveInfo.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                return (false, $"NTFS 아님 ({driveInfo.DriveFormat})");
        }
        catch { /* 드라이브 정보 읽기 실패 */ }

        return (false, "관리자 권한 필요 (볼륨 핸들 열기 실패)");
    }
}
