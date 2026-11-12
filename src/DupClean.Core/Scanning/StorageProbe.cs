using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace DupClean.Core.Scanning;

/// <summary>저장장치 종류.</summary>
public enum StorageKind
{
    /// <summary>알 수 없음 (외장 USB 케이스, 네트워크 드라이브 등 — 조회가 안 되는 경우).</summary>
    Unknown,

    /// <summary>SSD / NVMe: 여러 파일을 동시에 읽어도 빠르다.</summary>
    Ssd,

    /// <summary>HDD(회전 디스크): 동시에 여러 파일을 읽으면 헤드가 왔다 갔다 해서 오히려 느려진다.</summary>
    Hdd,
}

/// <summary>스캔할 폴더가 어떤 저장장치에 있는지 알아내고, 거기에 맞는 스레드 수를 정한다.</summary>
public static class StorageProbe
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(StorageProbe));

    private const uint FILE_SHARE_READ  = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING    = 3;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint StorageDeviceSeekPenaltyProperty = 7;
    private const uint PropertyStandardQuery = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlags, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer, int nInBufferSize,
        out DeviceSeekPenaltyDescriptor lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>경로가 있는 드라이브의 종류를 반환. 관리자 권한은 필요 없다. 실패하면 Unknown.</summary>
    public static StorageKind Detect(string path)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return StorageKind.Unknown;

            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return StorageKind.Unknown;               // 네트워크 경로

            if (new DriveInfo(root).DriveType == DriveType.Network)
                return StorageKind.Unknown;

            // 접근 권한 0으로 볼륨을 열면 관리자가 아니어도 장치 정보를 물어볼 수 있다.
            using var handle = CreateFile(
                $@"\\.\{root.TrimEnd('\\')}", 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) return StorageKind.Unknown;

            var query = new StoragePropertyQuery
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType  = PropertyStandardQuery,
            };

            var ok = DeviceIoControl(
                handle, IOCTL_STORAGE_QUERY_PROPERTY,
                ref query, Marshal.SizeOf<StoragePropertyQuery>(),
                out var result, Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(),
                out _, IntPtr.Zero);

            if (!ok) return StorageKind.Unknown;
            return result.IncursSeekPenalty ? StorageKind.Hdd : StorageKind.Ssd;
        }
        catch (Exception ex)
        {
            Log.Debug("저장장치 종류 확인 실패 {Path}: {Msg}", path, ex.Message);
            return StorageKind.Unknown;
        }
    }

    /// <summary>
    /// 저장장치 종류에 맞는 스레드 수.
    /// Hash = 동시에 읽을 파일 수(디스크 부하), Image = 이미지 디코딩 스레드 수.
    ///
    /// HDD 값은 실측으로 정했다 (WD 8TB 5,400rpm급 SATA HDD, 사진 3,000장/8.8GiB, 캐시 비운 상태):
    ///   - 해시: 동시 읽기 1개 48초 &lt; 2개 66초 &lt; 4개 75초 &lt; 32개 115초  → 1개씩 순서대로 읽는 게 가장 빠르다.
    ///   - 이미지: 8개 79초 &lt; 16개 84초 &lt; 4개 88초 ≪ 32개 180초 이상 → 8개. 너무 많으면 헤드가 왔다 갔다 해서 급격히 느려진다.
    /// 연결 방식 때문에 종류를 알 수 없는 경우(USB 케이스 등)는 HDD일 수도 SSD일 수도 있어 중간값을 쓴다.
    /// </summary>
    public static (int Hash, int Image) Tune(StorageKind kind, int logicalCores)
    {
        var cores = Math.Max(1, logicalCores);
        return kind switch
        {
            StorageKind.Ssd => (cores, cores),
            StorageKind.Hdd => (1, Math.Min(cores, 8)),
            _               => (Math.Min(cores, 2), Math.Min(cores, 8)),
        };
    }

    /// <summary>
    /// 남은 시간 어림용 기본 속도 (시작 전 단계의 소요 시간 예상에만 쓰이며, 실제로 시작되면 실측값으로 대체된다).
    /// HashBytesPerSec = 해시 단계 처리량, SimilarItemsPerSec = 유사 이미지 단계 장/초.
    /// HDD는 디스크가 병목이라 스레드를 늘려도 빨라지지 않는다 (실측: 해시 약 100MB/s, 이미지 약 30장/초).
    /// </summary>
    public static (double HashBytesPerSec, double SimilarItemsPerSec) RateHints(StorageKind kind, int imageThreads)
    {
        const double MB = 1024 * 1024;
        return kind switch
        {
            StorageKind.Ssd => (800 * MB, imageThreads * 15d),
            StorageKind.Hdd => (100 * MB, 30d),
            _               => (250 * MB, Math.Min(imageThreads * 15d, 60d)),
        };
    }
}
