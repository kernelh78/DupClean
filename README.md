# DupClean

**Windows 중복 파일 탐지 및 정리 도구**

[![.NET](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-blue)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)

[English](README.en.md) | 한국어

---

## 개요

DupClean은 사진·동영상·문서에서 중복 파일을 자동으로 탐지하고 안전하게 정리하는 Windows 전용 데스크톱 애플리케이션입니다.

단순 파일명 비교가 아닌 **SHA-256 해시**와 **pHash(이미지 유사도)** 를 함께 사용해 완전 중복과 유사 이미지를 모두 잡아냅니다.

---

## 주요 기능

### 탐지
| 기능 | 설명 |
|------|------|
| **완전 중복 탐지** | Sparse Hashing 3단계 + SHA-256으로 내용이 완전히 같은 파일 탐지 |
| **유사 이미지 탐지** | pHash + Hamming Distance로 편집·리사이즈된 유사 이미지 탐지 |
| **Side-by-Side 비교** | 유사 이미지를 나란히 놓고 직접 눈으로 확인 |
| **그룹 필터** | 완전 중복 / 유사 이미지 탭으로 분리해서 보기 |

### 정리
| 기능 | 설명 |
|------|------|
| **격리 이동** | `.dup_trash` 폴더로 이동 (30일 자동 정리, 복원 가능) |
| **휴지통 삭제** | Windows 휴지통으로 이동 (복원 가능) |
| **하드링크 교체** | 중복 파일을 삭제하지 않고 하드링크로 교체 — 경로 유지, 디스크 공간 절약 (NTFS 전용) |
| **Undo** | 모든 작업을 SQLite에 기록, 언제든지 한 단계씩 되돌리기 |

### 자동화
| 기능 | 설명 |
|------|------|
| **자동 선택** | 이름 패턴(복사본, copy...) · 경로 깊이 · 날짜 규칙으로 삭제 대상 자동 선택 |
| **마스터 폴더 보호** | 지정 폴더 내 파일은 자동 선택에서 제외 |
| **CSV 내보내기** | 스캔 결과를 CSV 파일로 저장 |

### 사용성·성능
| 기능 | 설명 |
|------|------|
| **진행 상황 표시** | 단계(1/3~3/3), 진행률, 처리량(건수·용량), 처리 속도, 경과 시간, **남은 시간(예상)**, 현재 처리 중인 파일 |
| **라이트 / 다크 / 시스템 테마** | Windows 설정을 따라가거나 직접 선택. 디자인은 astryx(neutral) 기준 |
| **빠른 재스캔** | 파일 해시와 유사 이미지 결과를 SQLite에 저장해 **변경 없는 파일은 다시 읽지 않음** |
| **HDD/SSD 자동 최적화** | 저장장치 종류를 감지해 동시에 읽는 파일 수를 조절 (HDD는 적게) |

---

## 스크린샷

> 스캔 결과 화면 — 완전 중복 목록 / 유사 이미지 Side-by-Side 비교

---

## 시작하기

### 요구 사항
- Windows 10 / 11 (x64)
- .NET 8 런타임 (Self-Contained 버전은 불필요)

### 설치 없이 바로 실행
[Releases](../../releases) 페이지에서 `DupClean.exe` 다운로드 후 실행.

### 소스에서 빌드
```bash
git clone https://github.com/kernelh78/DupClean.git
cd DupClean
dotnet build src/DupClean.sln
dotnet run --project src/DupClean.UI/DupClean.UI.csproj
```

### 배포용 EXE 빌드
```bash
dotnet publish src/DupClean.UI/DupClean.UI.csproj -p:PublishProfile=win-x64 -c Release
# → dist/DupClean.exe (단일 파일, ~82MB, .NET 런타임 내장)
```

---

## 사용 방법

1. **폴더 선택** — 검사할 폴더를 입력하거나 "폴더 선택" 클릭
2. **스캔** — ▶ 스캔 (F5) 버튼 클릭
3. **결과 확인** — 완전 중복 / 유사 이미지 탭으로 필터
4. **선택** — 수동 체크박스 또는 ✨ 자동 선택
5. **정리** — 격리 이동 / 삭제 / 하드링크 교체 선택 후 실행
6. **실수했다면** — ↩ Undo (Ctrl+Z)

---

## 설정

⚙ 버튼으로 설정 창 열기 (☀/🌙 버튼으로 라이트·다크를 바로 전환할 수도 있음):
- **화면 테마**: 시스템 설정 / 라이트 / 다크 (선택 즉시 미리 보기, 저장해야 유지)
- **마스터 폴더**: 이 폴더의 파일은 자동 선택에서 절대 제외
- **pHash 임계값**: 유사 이미지 판정 민감도 (1=엄격, 16=느슨, 기본 8)
- **pHash 비활성화**: 이미지 유사도 탐지를 끄면 스캔 속도 대폭 향상

---

## 기술 스택

| 항목 | 선택 |
|------|------|
| 언어/런타임 | C# 12 / .NET 8 LTS |
| UI | WPF + CommunityToolkit.Mvvm |
| 디자인 | astryx(neutral) 디자인 토큰을 WPF로 이식, 글꼴 Figtree (한글은 맑은 고딕) |
| 이미지 처리 | SixLabors.ImageSharp (아래 라이선스 참고) |
| 데이터베이스 | SQLite + EF Core (Undo 로그), SQLite 캐시 (파일 해시·pHash) |
| 로깅 | Serilog + RollingFile |
| 테스트 | xUnit |

---

## 로그 위치

`%LOCALAPPDATA%\DupClean\logs\dupclean-{날짜}.log`

## 데이터 위치

`%LOCALAPPDATA%\DupClean\` — 설정(`settings.json`), Undo 기록(`dupclean.db`), 캐시(`filehash.db`, `phash.db`).
캐시는 지워도 안전합니다(다음 스캔이 처음처럼 느려질 뿐).

---

## 라이선스

MIT License

### 서드파티 라이선스
| 구성 요소 | 라이선스 | 비고 |
|-----------|----------|------|
| SixLabors.ImageSharp | Six Labors Split License | 오픈소스·소규모(연 매출 100만 달러 미만) 사용은 무료이나, **3.2.0 이상은 빌드 시 라이선스 키 필요**. 현재 3.1.12 사용 중이며 알려진 취약점이 남아 있어 키 발급 후 업그레이드 예정 ([상세](report_v0.2.md)) |
| Figtree 글꼴 | SIL Open Font License 1.1 | `src/DupClean.UI/Assets/Fonts/OFL.txt` |
| astryx 디자인 토큰 | MIT (Meta) | 색상·모서리 값만 참고, 코드는 사용하지 않음 |
