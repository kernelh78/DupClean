# DupClean — 개발 진행 보고서 v0.1

> 기준일: 2026-04-12
> 계획서 대비 진행 상황 및 실측 데이터 정리

---

## 1. 전체 진행 요약

| Phase | 계획 | 상태 | 비고 |
|-------|------|------|------|
| Phase 1 — MVP | 8주 | ✅ 완료 | Exact 중복 탐지 + 격리/삭제/Undo |
| Phase 2 — 유사 중복 + 스마트 선택 | 4주 | ✅ 완료 | pHash + Side-by-Side UI + SmartSelector |
| Phase 3 — 고급 기능 | 4주 | ✅ 완료 (병합 제외) | 하드링크, QuarantineManager, 마스터폴더 UI |
| Phase 4 — 안정화 및 배포 | 4주 | 🔄 진행 중 | EXE 패키징 완료 / CI·다국어 미완 |

**현재 위치: Phase 3 완료(병합 기능 제외) / Phase 4 진행 중**

---

## 2. 완료된 기능 목록

### Phase 1 — MVP

#### F-1 파일 스캔
- ✅ `System.IO.Enumeration` 기반 파일 열거 (IO 표준 모드)
- ✅ NTFS MFT 고속 스캐너 구현 (관리자 권한 시 자동 활성화, 폴백 지원)
- ✅ 확장자 필터 (이미지·동영상·문서)
- ✅ Long Path 지원 (`\\?\` 접두사, MAX_PATH 260자 초과 경로 처리)

#### F-2 Exact Duplicate (완전 중복)
- ✅ Sparse Hashing 3단계 전략 구현
  - 파일 크기 그룹핑 → 샘플 해시 → 전체 SHA-256
  - `DirectHashThresholdBytes`: 계획 1GB → **실제 32MB** (성능 최적화)
- ✅ `SparseHasher` 단위 테스트 7개 통과

#### Phase 1 Action Engine
- ✅ **격리 이동** (`QuarantineAction`) — `.dup_trash` 폴더로 구조 유지 이동
- ✅ **삭제** (`DeleteAction`) — Windows 휴지통으로 이동
- ✅ **Undo** (`UndoManager`) — SQLite 기반 트랜잭션 기록·복원

#### 데이터 / 인프라
- ✅ SQLite + EF Core (`DupCleanDbContext`) — Session / ActionTransaction / FileAction 스키마
- ✅ Serilog RollingFile — `%LOCALAPPDATA%\DupClean\logs\dupclean-{date}.log`
- ✅ `DupCleanServiceFactory` — UI가 EF Core 직접 참조 없이 서비스 생성

#### WPF UI (Phase 1)
- ✅ 폴더 선택 + 텍스트 입력 (`OpenFolderDialog`, `Microsoft.Win32`)
- ✅ 스캔 진행 바 + 실시간 상태 메시지 (`Progress<ScanProgress>`)
- ✅ Exact 중복 그룹 목록 (`ListView` + 썸네일)
- ✅ 격리 / 삭제 / Undo 버튼 + 확인 다이얼로그
- ✅ 취소 (`CancellationToken`)

---

### Phase 2 — 유사 중복 + 스마트 선택

#### F-3 Near Duplicate (유사 중복)
- ✅ `PHashCalculator` — ImageSharp 기반 64-bit aHash (8×8 그레이스케일 평균)
- ✅ Hamming Distance ≤ 8 → Near Duplicate 판정 (`ScanOptions.PHashThreshold`)
- ✅ Union-Find 알고리즘으로 그룹 클러스터링 (`FindSimilarGroups`)
- ✅ `PHashCache` — SQLite 기반 (경로+크기+수정시간 → 해시) 캐시, 재스캔 시 ImageSharp 로딩 생략
- ✅ `PHashCalculator` 단위 테스트 5개 통과

#### F-5 중복 그룹 UI
- ✅ `DuplicateKind.Exact` / `DuplicateKind.Similar` 분리 표시
- ✅ **그룹 필터 탭** — 전체 / 완전 중복 / 유사 이미지 탭 전환 (실시간 카운트)
- ✅ **Side-by-Side 비교 뷰** — Similar 그룹 선택 시 이미지 카드 나란히 표시
- ✅ 썸네일 지연 로딩 (`BitmapImage`, `DecodePixelWidth=280`, `BitmapCacheOption.OnLoad`)
- ✅ 우클릭 → **📂 폴더에서 보기** (탐색기에서 파일 선택 상태로 열기)

#### F-6 스마트 자동 선택 (SmartSelector)
- ✅ `SmartSelector` 규칙 엔진 구현
  - 마스터 폴더 보호 (지정 폴더 파일 절대 미선택)
  - 이름 패턴 (`copy`, `복사`, `복사본`, `(1)~(5)`, ` - 복사`)
  - 경로 깊이 (얕은 경로 보존)
  - 날짜 (오래된 파일 보존, 타이브레이커)
- ✅ **자동 선택 토글** — 한 번 더 누르면 전체 선택 해제
- ✅ `SmartSelectorTests` 10개 테스트 통과

---

### Phase 3 — 고급 기능

#### 하드링크 교체
- ✅ `HardLinkAction` — P/Invoke `CreateHardLinkW` 기반
  - 사전 검사: 같은 드라이브, NTFS 포맷, 원본 존재 여부
  - Undo 시 원본에서 복사본 생성으로 복원
- ✅ `HardLinkActionTests` 5개 테스트 통과

#### QuarantineManager 자동 정리
- ✅ 앱 시작 시 30일 초과 격리 파일 백그라운드 자동 정리

#### 설정 (AppSettings)
- ✅ `AppSettings` + `AppSettingsManager` — JSON 영속화 (`settings.json`)
- ✅ `SettingsWindow` — 마스터 폴더 추가/제거 UI, pHash 임계값 슬라이더
- ✅ 설정값이 실제 스캔에 반영 (`ComputePHash`, `PHashThreshold` → `ScanOptions`)

#### 기타 UI 개선
- ✅ 스캔 시작 시 스캐너 모드 상태바 표시 (`[NTFS MFT 고속 모드]` / `[관리자 권한 필요]`)

---

### Phase 4 — 안정화 및 배포

#### 패키징
- ✅ **Self-Contained Single-File EXE** (`dist/DupClean.exe`, ~82MB)
  - .NET 8 런타임 내장, Windows 10/11 x64, 설치 불필요
  - `publish.bat` 스크립트로 원클릭 빌드
- ✅ 버전 정보 추가 (`v1.0.0`)

#### CSV 내보내기 (F-9)
- ✅ 스캔 결과 CSV 저장 (`📊 CSV 내보내기` 버튼)
  - 컬럼: 그룹번호, 종류, 파일명, 크기, 수정일, 전체경로
  - UTF-8 BOM 적용 (Excel 한글 정상 표시)

---

## 3. 테스트 현황

| 테스트 클래스 | 테스트 수 | 결과 |
|-------------|---------|------|
| `SparseHasherTests` | 7 | ✅ 전부 통과 |
| `DuplicateEngineTests` | 5 | ✅ 전부 통과 |
| `PHashCalculatorTests` | 5 | ✅ 전부 통과 |
| `SmartSelectorTests` | 10 | ✅ 전부 통과 |
| `HardLinkActionTests` | 5 | ✅ 전부 통과 |
| `QuarantineManagerTests` | 3 | ✅ 전부 통과 |
| **합계** | **35** | **✅ 35/35** |

---

## 4. 성능 실측 데이터

> 실제 사용 로그 (`dupclean-20260411.log`) 기준

| 폴더 | 파일 수 | 소요 시간 | Exact | Similar | 비고 |
|------|---------|----------|-------|---------|------|
| OneDrive\사진 | 1,433 | 330ms | 29 | — | pHash 미실행 |
| D:\PIC (1차) | 1,959 | 261,035ms (4m21s) | 400 | 207 | 초기 실행, 캐시 없음 |
| D:\PIC (2차) | 1,959 | 101,504ms (1m42s) | 400 | 221 | **pHash 캐시 히트, 61% 단축** |
| F:\IPAD PIC | 5,840 | 45,139ms (45s) | 161 | 167 | |
| E:\ext bk\Pic | 358 | 18,797ms | 0 | 80 | Exact 없는 유사만 |
| E:\Z폴드 5 사진 | 6,068 | 848ms | — | — | 결과 없음 (해시만) |

---

## 5. 계획 대비 주요 변경사항

| 항목 | 계획 | 실제 구현 | 이유 |
|------|------|----------|------|
| DirectHashThreshold | 1GB | **32MB** | 사진 파일 평균 크기 고려, 성능 대폭 개선 |
| pHash 알고리즘 | 8×8 평균 색상 블록 | **aHash** | 동일한 개념, ImageSharp L8으로 구현 |
| SmartSelector 규칙 수 | 5가지 | **4가지** | Metadata 보조 판단 규칙 제외 |
| 병합 기능 | Phase 3 포함 | **미구현** | 사용 빈도 낮음, 향후 추가 예정 |

---

## 6. 남은 작업

| 항목 | 우선순위 | 비고 |
|------|---------|------|
| GitHub Actions CI | 중 | 푸시 시 자동 빌드+테스트 |
| 한/영 다국어 | 낮음 | UI 텍스트 리소스 분리 |
| 병합 기능 (PDF/동영상/이미지) | 낮음 | PdfSharp, FFmpeg 의존성 필요 |

---

## 7. 현재 프로젝트 구조

```
src/
├── DupClean.Core/
│   ├── Actions/
│   │   ├── DeleteAction.cs          ✅
│   │   ├── HardLinkAction.cs        ✅
│   │   ├── IDuplicationAction.cs    ✅
│   │   ├── QuarantineAction.cs      ✅
│   │   ├── QuarantineManager.cs     ✅
│   │   └── UndoManager.cs           ✅
│   ├── Data/
│   │   ├── AppSettings.cs           ✅
│   │   ├── DupCleanDbContext.cs     ✅
│   │   ├── DupCleanServiceFactory.cs ✅
│   │   └── UndoManager.cs           ✅
│   ├── Detection/
│   │   ├── DuplicateEngine.cs       ✅
│   │   └── SmartSelector.cs         ✅
│   ├── Hashing/
│   │   ├── PHashCalculator.cs       ✅
│   │   ├── PHashCache.cs            ✅
│   │   └── SparseHasher.cs          ✅
│   ├── Models/
│   │   ├── DuplicateGroup.cs        ✅
│   │   ├── FileEntry.cs             ✅
│   │   ├── FileHash.cs              ✅
│   │   └── ScanOptions.cs           ✅
│   └── Scanning/
│       ├── FileScannerFactory.cs    ✅
│       ├── IoScanner.cs             ✅
│       └── MftScanner.cs            ✅
├── DupClean.UI/
│   ├── App.xaml.cs                  ✅
│   ├── Converters/                  ✅
│   ├── ViewModels/
│   │   ├── MainViewModel.cs         ✅
│   │   └── SettingsViewModel.cs     ✅
│   └── Views/
│       ├── MainWindow.xaml          ✅
│       └── SettingsWindow.xaml      ✅
└── DupClean.Tests/
    ├── DuplicateEngineTests.cs      ✅
    ├── HardLinkActionTests.cs       ✅
    ├── PHashCalculatorTests.cs      ✅
    ├── SmartSelectorTests.cs        ✅
    └── SparseHasherTests.cs         ✅
```
