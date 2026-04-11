using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DupClean.Core.Models;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace DupClean.Core.Scanning;

/// <summary>
/// NTFS USN Journal(MFT)을 직접 읽어 파일을 열거.
/// Everything 엔진과 동일한 방식으로, System.IO 대비 수십만 파일 환경에서 5~10배 빠름.
/// Windows NTFS 전용. 관리자 권한 불필요 (읽기 전용 볼륨 접근).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MftScanner : IFileScanner
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MftScanner>();

    // ── P/Invoke 상수 ──────────────────────────────────────────────
    private const uint GENERIC_READ         = 0x80000000;
    private const uint FILE_SHARE_READ      = 0x00000001;
    private const uint FILE_SHARE_WRITE     = 0x00000002;
    private const uint OPEN_EXISTING        = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FSCTL_ENUM_USN_DATA  = 0x000900B3;
    private const uint ERROR_HANDLE_EOF     = 38;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurity, uint dwCreation, uint dwFlags, IntPtr hTemplate);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ref MftEnumData lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileSizeEx(SafeFileHandle hFile, out long lpFileSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileTime(
        SafeFileHandle hFile, out long lpCreationTime,
        out long lpLastAccessTime, out long lpLastWriteTime);

    // ── 구조체 ────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct MftEnumData
    {
        public ulong StartFileReferenceNumber;
        public long  LowUsn;
        public long  HighUsn;
        public ushort MinMajorVersion;
        public ushort MaxMajorVersion;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UsnRecordV2
    {
        public uint   RecordLength;
        public ushort MajorVersion;
        public ushort MinorVersion;
        public ulong  FileReferenceNumber;
        public ulong  ParentFileReferenceNumber;
        public long   Usn;
        public long   TimeStamp;
        public uint   Reason;
        public uint   SourceInfo;
        public uint   SecurityId;
        public uint   FileAttributes;
        public ushort FileNameLength;
        public ushort FileNameOffset;
        // FileName follows in memory
    }

    // ── 공개 API ──────────────────────────────────────────────────

    /// <summary>MFT 스캔 가능 여부를 빠르게 확인 (볼륨 열기 테스트).</summary>
    public static bool IsAvailable(string driveLetter)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        var volumePath = $@"\\.\{driveLetter.TrimEnd('\\', '/')}:";
        using var handle = CreateFile(
            volumePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        return !handle.IsInvalid;
    }

    public async IAsyncEnumerable<FileEntry> ScanAsync(
        string rootPath,
        ScanOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var driveLetter = Path.GetPathRoot(rootPath)?.TrimEnd('\\', '/') ?? "C:";
        var volumePath  = $@"\\.\{driveLetter}";

        var entries = await Task.Run(() => EnumerateMft(volumePath, rootPath, options, ct), ct);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    // ── MFT 열거 (동기, ThreadPool에서 실행) ──────────────────────

    private static IReadOnlyList<FileEntry> EnumerateMft(
        string volumePath, string rootPath, ScanOptions options, CancellationToken ct)
    {
        using var hVolume = CreateFile(
            volumePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

        if (hVolume.IsInvalid)
        {
            Log.Warning("MFT: 볼륨 {Volume} 열기 실패 (오류 {Error}). IoScanner로 폴백.", volumePath, Marshal.GetLastWin32Error());
            throw new IOException($"볼륨 열기 실패: {volumePath}");
        }

        const int BufSize = 65536;
        var buffer     = Marshal.AllocHGlobal(BufSize);
        var frnToInfo  = new Dictionary<ulong, (ulong parentFrn, string name, uint attrs)>();

        try
        {
            var enumData = new MftEnumData
            {
                StartFileReferenceNumber = 0,
                LowUsn  = 0,
                HighUsn = long.MaxValue,
                MinMajorVersion = 2,
                MaxMajorVersion = 3
            };

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (!DeviceIoControl(hVolume, FSCTL_ENUM_USN_DATA,
                        ref enumData, Marshal.SizeOf<MftEnumData>(),
                        buffer, BufSize, out int bytesReturned, IntPtr.Zero))
                {
                    if (Marshal.GetLastWin32Error() == ERROR_HANDLE_EOF) break;
                    Log.Warning("MFT 열거 중 오류: {Error}", Marshal.GetLastWin32Error());
                    break;
                }

                // 첫 8바이트 = 다음 스타트 FRN
                enumData.StartFileReferenceNumber = (ulong)Marshal.ReadInt64(buffer, 0);

                var offset = 8;
                while (offset < bytesReturned)
                {
                    var record = Marshal.PtrToStructure<UsnRecordV2>(buffer + offset);
                    if (record.RecordLength == 0) break;

                    var name = Marshal.PtrToStringUni(
                        buffer + offset + record.FileNameOffset,
                        record.FileNameLength / 2);

                    if (name is not null)
                        frnToInfo[record.FileReferenceNumber] =
                            (record.ParentFileReferenceNumber, name, record.FileAttributes);

                    offset += (int)record.RecordLength;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // FRN 딕셔너리로 전체 경로 재구성
        return BuildEntries(frnToInfo, rootPath, options);
    }

    private static IReadOnlyList<FileEntry> BuildEntries(
        Dictionary<ulong, (ulong parentFrn, string name, uint attrs)> frnToInfo,
        string rootPath,
        ScanOptions options)
    {
        var results  = new List<FileEntry>();
        var pathCache = new Dictionary<ulong, string?>();

        string? ResolvePath(ulong frn, int depth = 0)
        {
            if (depth > 64) return null; // 무한 루프 방지
            if (pathCache.TryGetValue(frn, out var cached)) return cached;
            if (!frnToInfo.TryGetValue(frn, out var info)) return null;

            var parentPath = ResolvePath(info.parentFrn, depth + 1);
            var result = parentPath is null ? null : Path.Combine(parentPath, info.name);
            pathCache[frn] = result;
            return result;
        }

        // 볼륨 루트 FRN 설정 (보통 5)
        pathCache[5] = Path.GetPathRoot(rootPath)?.TrimEnd('\\') ?? "C:";

        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        const uint FILE_ATTRIBUTE_HIDDEN    = 0x02;

        foreach (var (frn, info) in frnToInfo)
        {
            if ((info.attrs & FILE_ATTRIBUTE_DIRECTORY) != 0) continue;
            if (!options.IncludeHidden && (info.attrs & FILE_ATTRIBUTE_HIDDEN) != 0) continue;

            var ext = Path.GetExtension(info.name).ToLowerInvariant();
            if (options.IncludeExtensions.Count > 0 && !options.IncludeExtensions.Contains(ext))
                continue;

            var fullPath = ResolvePath(frn);
            if (fullPath is null) continue;
            if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (options.ExcludePaths.Any(excl =>
                    fullPath.StartsWith(excl, StringComparison.OrdinalIgnoreCase))) continue;

            try
            {
                var fi = new FileInfo(fullPath.Length > 260 ? @"\\?\" + fullPath : fullPath);
                if (fi.Exists)
                    results.Add(new FileEntry(fullPath, fi.Length, fi.LastWriteTimeUtc));
            }
            catch
            {
                // 접근 불가 파일 조용히 스킵
            }
        }

        return results;
    }
}
