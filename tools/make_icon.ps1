# DupClean 앱 아이콘(.ico) 생성 도구.
#
# 디자인: astryx(neutral) 톤 — 어두운 둥근 사각형 위에 겹친 두 장의 문서 ("중복 파일").
# 사용법:  powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1
# 결과:    src\DupClean.UI\Assets\Icon\DupClean.ico  (16/20/24/32/40/48/64/128/256 px)
#
# - 48px 미만의 작은 크기는 문서 안의 줄을 빼고 단순하게 그려서 또렷하게 보이게 한다.
# - 저장 형식은 표준 방식: 256px만 PNG, 그보다 작은 크기는 BMP(DIB).
#   (작은 크기를 PNG로 저장하면 일부 도구가 읽지 못한다)
# - 색을 바꾸고 싶으면 아래 $Colors 만 고치고 다시 실행하면 된다.

param(
    [string]$OutFile = (Join-Path $PSScriptRoot "..\src\DupClean.UI\Assets\Icon\DupClean.ico")
)

Add-Type -AssemblyName System.Drawing

$Colors = @{
    Background = "#1B1B1B"   # astryx neutral accent
    BackSheet  = "#7A7A7A"
    FrontSheet = "#F4F4F4"
    Line       = "#B5B5B5"
}
$Sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Fill-Rounded($g, [string]$color, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($color))
    $path  = New-RoundedRect $x $y $w $h $r
    $g.FillPath($brush, $path)
    $path.Dispose(); $brush.Dispose()
}

# 1024x1024 원본을 그린다. $detail 이 true 면 문서 안에 줄을 그린다.
function New-Master([bool]$detail) {
    $bmp = New-Object System.Drawing.Bitmap 1024, 1024, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    Fill-Rounded $g $Colors.Background 40 40 944 944 230                 # 바탕
    Fill-Rounded $g $Colors.BackSheet  232 196 420 540 64               # 뒤 문서
    Fill-Rounded $g $Colors.Background 350 278 464 584 86               # 앞 문서 둘레(뒤 문서와 구분되는 틈)
    Fill-Rounded $g $Colors.FrontSheet 372 300 420 540 64               # 앞 문서

    if ($detail) {
        Fill-Rounded $g $Colors.Line 436 420 292 38 19
        Fill-Rounded $g $Colors.Line 436 510 292 38 19
        Fill-Rounded $g $Colors.Line 436 600 190 38 19
    }
    $g.Dispose()
    return $bmp
}

function Resize-Bitmap($src, [int]$size) {
    $dst = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    return $dst
}

# .ico 안에 들어가는 BMP(DIB) 데이터: 헤더 + 픽셀(아래에서 위로, BGRA) + AND 마스크
function Get-IconDib($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $raw = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2))   # BITMAPINFOHEADER (높이는 마스크 포함 2배)
    $bw.Write([uint16]1);  $bw.Write([uint16]32); $bw.Write([uint32]0)         # planes, bpp, 압축 없음
    $bw.Write([uint32]0);  $bw.Write([int32]0);   $bw.Write([int32]0)          # 이미지 크기, 해상도
    $bw.Write([uint32]0);  $bw.Write([uint32]0)                                # 팔레트
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($raw, $y * $stride, $w * 4) }
    $maskRow = [int][math]::Ceiling($w / 32.0) * 4
    $bw.Write((New-Object byte[] ($maskRow * $h)))                             # 투명도는 알파 채널로 처리하므로 마스크는 0
    $bw.Flush()
    return $ms.ToArray()
}

$detailed = New-Master $true
$simple   = New-Master $false

$images = foreach ($s in $Sizes) {
    $master = if ($s -ge 48) { $detailed } else { $simple }
    $bmp = Resize-Bitmap $master $s
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray(); $ms.Dispose()
    } else {
        $bytes = Get-IconDib $bmp
    }
    [pscustomobject]@{ Size = $s; Bytes = $bytes }
    $bmp.Dispose()
}
$detailed.Dispose(); $simple.Dispose()

# .ico 파일로 묶기
$dir = Split-Path -Parent $OutFile
New-Item -ItemType Directory -Force $dir | Out-Null
$fs = [System.IO.File]::Create($OutFile)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0)                 # reserved
$bw.Write([uint16]1)                 # type = icon
$bw.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }   # 256은 0으로 표기
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)                   # 색 수, reserved
    $bw.Write([uint16]1); $bw.Write([uint16]32)              # planes, bits per pixel
    $bw.Write([uint32]$img.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img.Bytes) }   # 함수가 반환한 배열은 object[]로 풀려 있어 byte[]로 변환
$bw.Dispose(); $fs.Dispose()

"만들어짐: $((Resolve-Path $OutFile).Path)  ($($images.Count)개 크기, $([math]::Round((Get-Item $OutFile).Length / 1KB, 1)) KB)"
