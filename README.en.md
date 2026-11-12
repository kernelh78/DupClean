# DupClean

**Duplicate File Detector & Cleaner for Windows**

[![.NET](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)

[한국어](README.md) | English

---

## Overview

DupClean is a Windows desktop application that automatically detects and safely cleans up duplicate files across photos, videos, and documents.

Unlike simple filename comparison, DupClean uses both **SHA-256 hashing** and **pHash (perceptual hashing)** to catch both exact duplicates and visually similar images.

---

## Key Features

### Detection
| Feature | Description |
|---------|-------------|
| **Exact Duplicate Detection** | 3-stage Sparse Hashing + SHA-256 to find byte-identical files |
| **Similar Image Detection** | pHash + Hamming Distance to find edited or resized versions of the same image |
| **Side-by-Side Comparison** | View similar images side by side to decide manually |
| **Group Filter** | Switch between Exact Duplicates / Similar Images tabs |

### Cleanup
| Feature | Description |
|---------|-------------|
| **Quarantine** | Move to `.dup_trash` folder (auto-purge after 30 days, restorable) |
| **Recycle Bin Delete** | Send to Windows Recycle Bin (restorable) |
| **Hard Link Replace** | Replace duplicates with NTFS hard links — keeps all paths, reclaims disk space (NTFS only) |
| **Undo** | All actions recorded in SQLite; undo one step at a time |

### Automation
| Feature | Description |
|---------|-------------|
| **Auto-Select** | Automatically marks files for deletion using name patterns (copy, 복사본...), path depth, and date rules |
| **Master Folder Protection** | Files in designated folders are never auto-selected |
| **CSV Export** | Save scan results to a CSV file |

### Usability & Performance
| Feature | Description |
|---------|-------------|
| **Live Progress** | Phase (1/3–3/3), progress, throughput (files / bytes), speed, elapsed time, **estimated time remaining**, and the file being processed |
| **Light / Dark / System Theme** | Follows Windows or set manually. Design based on astryx (neutral) |
| **Fast Re-scan** | File hashes and similar-image results are cached in SQLite, so **unchanged files are not read again** |
| **HDD/SSD Auto-tuning** | Detects the storage type and adjusts how many files are read in parallel (fewer on HDD) |

---

## Screenshots

> Scan results — Exact duplicate list / Similar image side-by-side comparison

---

## Getting Started

### Requirements
- Windows 10 / 11 (x64)
- .NET 8 Runtime (not required for Self-Contained build)

### Run Without Installing
Download `DupClean.exe` from the [Releases](../../releases) page and run it.

### Build from Source
```bash
git clone https://github.com/kernelh78/DupClean.git
cd DupClean
dotnet build src/DupClean.sln
dotnet run --project src/DupClean.UI/DupClean.UI.csproj
```

### Build Release EXE
```bash
dotnet publish src/DupClean.UI/DupClean.UI.csproj -p:PublishProfile=win-x64 -c Release
# → dist/DupClean.exe (single file, ~82MB, .NET runtime bundled)
```

---

## How to Use

1. **Select Folder** — Type a path or click "폴더 선택"
2. **Scan** — Click ▶ 스캔 (F5)
3. **Review Results** — Use the Exact / Similar filter tabs
4. **Select Files** — Check manually or use ✨ Auto-Select
5. **Clean** — Choose Quarantine / Delete / Hard Link Replace
6. **Made a Mistake?** — ↩ Undo (Ctrl+Z)

---

## Settings

Click ⚙ to open Settings (the ☀/🌙 button switches light/dark instantly):
- **Theme**: System / Light / Dark (previewed immediately; press Save to keep it)
- **Master Folders**: Files in these folders are always excluded from auto-select
- **pHash Threshold**: Similar image sensitivity (1 = strict, 16 = loose, default 8)
- **Disable pHash**: Turn off similar image detection for much faster scans

---

## Tech Stack

| Item | Choice |
|------|--------|
| Language / Runtime | C# 12 / .NET 8 LTS |
| UI | WPF + CommunityToolkit.Mvvm |
| Design | astryx (neutral) design tokens ported to WPF; Figtree font (Malgun Gothic for Korean) |
| Image Processing | SixLabors.ImageSharp (see License below) |
| Database | SQLite + EF Core (Undo log), SQLite caches (file hashes, pHash) |
| Logging | Serilog + RollingFile |
| Testing | xUnit |

---

## Log Location

`%LOCALAPPDATA%\DupClean\logs\dupclean-{date}.log`

## Data Location

`%LOCALAPPDATA%\DupClean\` — settings (`settings.json`), undo history (`dupclean.db`), caches (`filehash.db`, `phash.db`).
The caches are safe to delete (the next scan is just as slow as the first).

---

## License

MIT License

### Third-party licenses
| Component | License | Note |
|-----------|---------|------|
| SixLabors.ImageSharp | Six Labors Split License | Free for open-source and small (< USD 1M annual revenue) use, but **versions 3.2.0+ require a license key at build time**. Currently on 3.1.12, which has known vulnerabilities; upgrade planned once a key is issued ([details](report_v0.2.md)) |
| Figtree font | SIL Open Font License 1.1 | `src/DupClean.UI/Assets/Fonts/OFL.txt` |
| astryx design tokens | MIT (Meta) | Only color / radius values were referenced; no code is used |
