# DupClean — 개발 계획서 v0.2
*(Windows PC용 중복 파일 탐지·비교·정리 도구)*

> **v0.1 → v0.2 변경 요약**
> - 보강내용 섹션을 본문에 완전히 통합
> - 기술 스택 "A or B" 선택지를 모두 확정
> - iText7(AGPL) → PdfSharp(MIT)로 교체 결정
> - Sparse Hashing, MFT 스캔, 하드링크 교체, 격리폴더 기능 추가
> - MVP → 확장 순서로 개발 단계 재편

---

## 1. 프로젝트 개요

| 구분 | 내용 |
|------|------|
| **프로젝트명** | DupClean — Windows용 중복 파일 탐지·정리 유틸리티 |
| **목표** | 이미지·동영상·문서에서 중복 파일을 자동 탐지하고, 안전하게 삭제·이름변경·병합·하드링크 교체를 지원 |
| **대상 사용자** | 사진·동영상·문서를 대량 관리하는 일반/크리에이티브 사용자 |
| **플랫폼** | Windows 10/11 (x64), .NET 8 LTS |
| **배포 형태** | Self-Contained EXE (단일 실행 파일) + MSI 설치본 |

---

## 2. 문제 정의

1. **파일 이름/날짜만으로는 중복 판단이 부정확하다.** 이름이 달라도 내용이 같거나, 이름이 같아도 내용이 다를 수 있다.
2. **일반 System.IO 스캔은 대용량에서 느리다.** 수십만 개 파일 환경에서 I/O 병목이 발생한다.
3. **일괄 삭제는 위험하다.** 실수로 원본을 지우면 복구가 어렵다.
4. **유사 이미지(편집본, 리사이즈)는 해시로 잡히지 않는다.** 눈으로 봐야 같은 사진인지 알 수 있는 케이스가 많다.

---

## 3. 목표 및 기대 효과

| 목표 | 기대 효과 |
|------|-----------|
| 정확한 다단계 중복 탐지 | 완전 중복(SHA-256) + 유사 중복(pHash) + 메타데이터 보조 → 정확도 99%+ |
| 안전한 조작 | 격리폴더 이동 → 30일 유예 → 완전 삭제 / Undo 지원 |
| 고성능 스캔 | NTFS MFT 직접 읽기 + Sparse Hashing → 50GB 컬렉션 10분 이내 |
| 디스크 절약 | 삭제 대신 하드링크 교체 옵션 → 경로 구조 유지하면서 용량 확보 |

---

## 4. 확정된 기술 스택

| 항목 | 선택 | 이유 |
|------|------|------|
| 언어/런타임 | C# 12 / .NET 8 LTS | async/await, Span<T>, source generators 활용 |
| UI | **WPF + CommunityToolkit.Mvvm** | Windows 7 호환, 성숙한 에코시스템, MVVM 패턴 적합 |
| 파일 스캔 | **NTFS MFT 직접 읽기** (P/Invoke, USN Journal) → 폴백: `System.IO.Enumeration` | Everything 엔진 방식. 수십만 파일 스캔 속도 10배+ |
| 이미지 처리 | **SixLabors.ImageSharp (MIT)** | pHash, 콜라주, 썸네일 생성 |
| 동영상 처리 | **FFmpeg** (온디맨드 다운로드) | 최초 병합 기능 사용 시 "다운로드하시겠습니까?" 팝업 → 사용자 폴더에 설치. LGPL 배포 이슈 우회 |
| PDF 처리 | **PdfSharp (MIT)** | iText7 AGPL 라이선스 리스크 완전 제거 |
| 데이터베이스 | **SQLite + EF Core** | 경량, 파일 기반, Undo 로그 보관 |
| 설정 | JSON + `Microsoft.Extensions.Configuration` | |
| 로깅 | **Serilog** + RollingFile | `%LOCALAPPDATA%\DupClean\logs` |
| 패키징 | Self-Contained EXE (`PublishSingleFile`) + WiX MSI | |
| CI | **GitHub Actions** (build + xUnit test) | |

---

## 5. 기능 요구사항

### 5.1 파일 스캔 (F-1)

- NTFS MFT 직접 읽기로 고속 열거. 지원 안 되는 드라이브(FAT32, 외장)는 `System.IO.Enumeration` 폴백.
- 확장자 필터(이미지·동영상·문서) + 제외 경로 설정 가능.
- 파일 **경로·크기·수정시간**을 먼저 수집해 "중복 후보 풀" 생성.
- **Long Path 지원**: `\\?\` 접두사 활용. MAX_PATH(260자) 초과 경로 정상 처리.

### 5.2 다단계 중복 탐지

#### F-2 — Exact Duplicate (완전 중복)

**Sparse Hashing 3단계 전략** (대용량 파일 I/O 최적화):

```
1단계: 파일 크기가 동일한 파일끼리만 후보로 묶기
2단계: [시작 1MB + 중간 1MB + 끝 1MB] 샘플 해시 비교
       → 불일치면 탈락. 일치하면 3단계로
3단계: 전체 SHA-256 계산 → 동일 해시 = Exact Duplicate
```

- 1GB 이하: 전체 SHA-256 직접 계산 (샘플링 생략).
- 1GB 초과: Sparse Hashing 적용으로 CPU·I/O 낭비 최소화.

#### F-3 — Near Duplicate (유사 중복, 이미지·동영상)

- **이미지**: ImageSharp로 8×8 평균 색상 블록 → 64-bit pHash.
  `HammingDistance ≤ 8` → Near Duplicate (threshold 설정 파일에서 조정 가능).
- **동영상**: ffprobe로 30초 구간 프레임 샘플링 → 이미지 pHash 평균 비교.
- Near Duplicate는 UI에서 **나란히 보기(Side-by-Side)** 제공. 사용자가 직접 확인 후 진행.

#### F-4 — Metadata 보조 판단

- 이미지 EXIF `DateTimeOriginal` ± 1분, 동영상 `duration`·`codec_name` 일치 → 보조 증거.
- 단독으로 중복 판정하지 않음. Exact 또는 Near와 함께 표시.

### 5.3 중복 그룹 UI (F-5)

- 결과를 **트리 뷰**로 표시: `Exact` / `Similar` / `Metadata Only` 3개 그룹.
- 각 파일: 썸네일(이미지·동영상), 문서 아이콘.
- **Side-by-Side 비교**: Near Duplicate 파일 두 개를 나란히 표시 + Pixel Difference 하이라이트.
- 파일 속성 비교 테이블: 해상도, 비트레이트, 크기, 생성일 등.

### 5.4 스마트 자동 선택 (F-6)

수천 개 중복 그룹을 일일이 고를 수 없는 사용자를 위한 **Auto-Select Rules**:

| 규칙 | 설명 |
|------|------|
| 이름 기반 | 파일명에 "copy", "복사본", "(1)", " - 복사" 포함된 것 우선 선택 |
| 경로 깊이 | 경로가 더 짧은(얕은) 것을 원본으로 보존 |
| 날짜 기반 | 가장 오래된 것 보존 / 가장 최신 것 보존 (선택) |
| 마스터 폴더 보호 | 지정된 폴더(예: `D:\Original`) 내 파일은 선택에서 제외 |

### 5.5 조작 옵션 (F-7)

| 옵션 | 구현 | 안전장치 |
|------|------|---------|
| **격리 이동** | 파일을 `.dup_trash\{원본경로구조}` 폴더로 이동. 30일 후 자동 삭제 (기본값). 기간 설정 가능. | 구조 유지 이동 → 복구 쉬움 |
| **삭제** | Windows 리사이클바인으로 이동 (`RecycleOption.SendToRecycleBin`) | 리사이클바인에서 직접 복구 가능 |
| **하드링크 교체** | 중복 파일을 원본의 하드링크로 교체. 경로는 유지되고 디스크 공간만 절약. | NTFS 전용. 드라이브 간 불가. 실행 전 체크 |
| **이름 변경** | 사용자 정의 규칙(예: `{FolderName}_{Sequence:D3}{Extension}`). 충돌 시 자동 번호 매김. | Dry-Run 시뮬레이션 모드 제공 |
| **병합** | PDF: PdfSharp 페이지 합치기. 동영상: FFmpeg `-c copy` 순차 연결. 이미지: ImageSharp 그리드 콜라주. | 임시 폴더에 먼저 생성 → 원본 폴더로 원자적 이동 |
| **Undo** | 모든 작업은 `IUndoable` 인터페이스. SQLite에 트랜잭션 단위 기록. Rollback은 역순 실행. | 복합 조작도 트랜잭션 단위로 묶어 일괄 복구 |

### 5.6 설정 및 자동화 (F-8)

- JSON 기반 **스캔 프로파일**: 경로·필터·해시 방식·스레드 수·pHash threshold.
- Windows Task Scheduler 연동: 지정 시간 자동 스캔 실행.
- `.dup_trash` 자동 정리 스케줄 (기본 30일).

### 5.7 리포트 (F-9)

- 완료 화면: 중복 수·절약된 용량·처리된 파일 요약.
- CSV 내보내기: `dupclean_report_{timestamp}.csv`.
- PDF 리포트: PdfSharp로 요약 생성 (선택).

---

## 6. 비기능 요구사항

| 구분 | 요구사항 |
|------|---------|
| **성능** | 50GB / 8스레드 기준 10분 이내 스캔. MFT 스캔 활성 시 System.IO 대비 5~10배 빠름. 메모리 ≤ 500MB. |
| **안전성** | 모든 파괴적 작업(삭제·이름변경·병합) 전 사용자 확인 화면. Dry-Run 모드 항상 제공. |
| **Long Path** | `\\?\` 접두사로 MAX_PATH(260자) 초과 경로 완전 지원. |
| **권한** | 기본 실행에 관리자 권한 불필요. 시스템 폴더 접근 필요 시 UAC 상승 옵션. |
| **보안** | FFmpeg 등 외부 바이너리 로드 시 강제 서명 검증. 외부 DLL 동적 로딩 전 해시 확인. |
| **확장성** | 새 파일 포맷·조작은 `IDuplicationAction` 플러그인 인터페이스로 추가 가능 (DLL 동적 로딩). |
| **호환성** | Windows 10/11 x64. Self-Contained 빌드로 .NET 8 런타임 별도 설치 불필요. |
| **로깅** | Serilog + RollingFile (파일당 최대 10MB, 30일 보관). `--verbose` 옵션으로 콘솔 출력. |

---

## 7. 시스템 아키텍처

```
+----------------------------------------------------------+
|                    Presentation (WPF)                    |
|  Views (XAML)  <---->  ViewModels (CommunityToolkit.Mvvm) |
+----------------------------------------------------------+
              |                          ^
         Commands                    Results
              v                          |
+----------------------------------------------------------+
|                   Application Services                   |
|  ┌──────────────┐  ┌────────────────┐  ┌──────────────┐ |
|  │  ScanEngine  │  │DuplicateEngine │  │ ActionEngine │ |
|  │  - MFT 스캔  │  │  - ExactHash   │  │  - Quarantine│ |
|  │  - IO 폴백   │  │  - pHash       │  │  - HardLink  │ |
|  │  - LongPath  │  │  - Metadata    │  │  - Delete    │ |
|  └──────────────┘  └────────────────┘  │  - Rename    │ |
|                                        │  - Merge     │ |
|                                        └──────────────┘ |
+----------------------------------------------------------+
              |                          |
+----------------------------------------------------------+
|                   Infrastructure                         |
|  ┌───────────┐  ┌─────────────┐  ┌───────────────────┐  |
|  │SQLite DB  │  │Config (JSON)│  │ Serilog (File/Con)│  |
|  │(Undo Log) │  │(ScanProfile)│  │                   │  |
|  └───────────┘  └─────────────┘  └───────────────────┘  |
+----------------------------------------------------------+
```

### 핵심 컴포넌트

| 컴포넌트 | 역할 |
|---------|------|
| `MftScanner` | NTFS MFT 직접 읽기로 고속 파일 열거. FAT/외장은 `System.IO.Enumeration` 폴백. |
| `SparseHasher` | Sparse Hashing 3단계 + 전체 SHA-256 계산. `IAsyncEnumerable<FileHash>`. |
| `PHashCalculator` | ImageSharp 기반 64-bit pHash. Hamming distance 비교. |
| `DuplicateEngine` | 해시 기반 그룹핑, pHash 기반 Near Duplicate, 메타데이터 보조. |
| `SmartSelector` | Auto-Select Rules 엔진. 이름/경로/날짜/마스터폴더 규칙 적용. |
| `ActionEngine` | `IDuplicationAction` 기반 조작 실행. 격리이동·하드링크·삭제·이름변경·병합. |
| `UndoManager` | `IUndoable` 트랜잭션 관리. SQLite에 기록. Rollback 역순 실행. |
| `QuarantineManager` | `.dup_trash` 관리. 만료 파일 자동 정리. |
| `FfmpegProvider` | FFmpeg 존재 여부 확인 → 없으면 온디맨드 다운로드 안내. |

---

## 8. 데이터 모델

### SQLite 스키마

```sql
-- 작업 세션
CREATE TABLE Session (
    Id       INTEGER PRIMARY KEY,
    ScanPath TEXT    NOT NULL,
    StartedAt DATETIME NOT NULL
);

-- 조작 트랜잭션 (Undo 단위)
CREATE TABLE ActionTransaction (
    Id        INTEGER PRIMARY KEY,
    SessionId INTEGER REFERENCES Session(Id),
    ActionType TEXT NOT NULL,  -- 'Quarantine', 'HardLink', 'Delete', 'Rename', 'Merge'
    CreatedAt DATETIME NOT NULL,
    IsRolledBack INTEGER DEFAULT 0
);

-- 개별 파일 조작 기록
CREATE TABLE FileAction (
    Id              INTEGER PRIMARY KEY,
    TransactionId   INTEGER REFERENCES ActionTransaction(Id),
    OriginalPath    TEXT NOT NULL,
    NewPath         TEXT,
    FileSize        INTEGER,
    Sha256          TEXT
);
```

### 격리 폴더 구조

```
.dup_trash/
└── 2026-04-11_14-30-25/     ← 조작 시각
    ├── C/                    ← 드라이브명
    │   └── Users/norain/Photos/IMG_001.jpg
    └── D/
        └── Backup/IMG_001.jpg
```

---

## 9. UI 흐름

```
[1. 시작]
  └─ 폴더 선택 (폴더 피커 / Drag-Drop)
  └─ 스캔 프로파일 선택 (기본 / 사용자 정의)
  └─ [스캔 시작] 버튼

[2. 스캔 진행]
  └─ 진행 바 + 현재 파일 수 + 해시 진행률
  └─ 일시정지 (CancellationToken)

[3. 결과 화면]
  └─ 트리 뷰: Exact / Similar / Metadata Only
  └─ 파일 선택 → 오른쪽 미리보기 (썸네일, 속성)
  └─ Near Duplicate → Side-by-Side 비교 뷰
  └─ [Auto-Select] → 규칙 선택 팝업

[4. 조작 선택]
  └─ [격리 이동] / [삭제] / [하드링크 교체] / [이름 변경] / [병합]
  └─ [Dry-Run] → "Would do..." 로그만 출력
  └─ 실제 실행 전 확인 경고창

[5. 완료]
  └─ 요약 (처리 수·절약 용량)
  └─ [Undo] 버튼
  └─ [CSV 내보내기]
```

단축키: `F5` 재스캔, `Ctrl+Z` Undo, `Space` 미리보기 토글.

---

## 10. 개발 단계 (MVP → 확장)

### Phase 1 — MVP (8주)

> 핵심 파이프라인만. 화려한 UI 없이 동작하는 것이 목표.

| 주차 | 작업 | 산출물 |
|------|------|--------|
| 1~2 | `MftScanner` + `System.IO` 폴백. LongPath 지원. 단위 테스트. | 콘솔에서 파일 목록 출력 |
| 3~4 | `SparseHasher` (SHA-256, Sparse Hashing 3단계). 단위 테스트. | 중복 후보 그룹 출력 |
| 5~6 | 기본 WPF UI: 폴더 선택 → 스캔 → Exact 결과 트리 뷰. | MVP UI |
| 7~8 | `ActionEngine` (격리이동·삭제·Undo). SQLite 로그. | 실제 파일 조작 가능 |

**Phase 1 완료 기준:** 특정 폴더의 완전 중복 파일을 탐지하고 격리 이동까지 동작함.

---

### Phase 2 — 유사 중복 + 스마트 선택 (4주)

| 주차 | 작업 | 산출물 |
|------|------|--------|
| 9~10 | `PHashCalculator` + Near Duplicate 탐지. Side-by-Side 비교 UI. | 유사 이미지 탐지 |
| 11~12 | `SmartSelector` (Auto-Select Rules 5가지). UI 통합. | 일괄 자동 선택 |

---

### Phase 3 — 고급 기능 (4주)

| 주차 | 작업 | 산출물 |
|------|------|--------|
| 13~14 | 하드링크 교체 (`ActionEngine` 확장). `QuarantineManager` 자동 정리 스케줄. | |
| 15~16 | 병합 기능: PdfSharp(PDF), FFmpeg 온디맨드(동영상), ImageSharp(콜라주). | |

---

### Phase 4 — 안정화 및 배포 (4주)

| 주차 | 작업 | 산출물 |
|------|------|--------|
| 17~18 | 성능 테스트 (100만 파일 I/O stress), 메모리 프로파일 (dotTrace). | 성능 최적화 |
| 19~20 | MSI 패키지(WiX), Self-Contained EXE. GitHub Actions CI. 한/영 다국어. | 배포 가능 v1.0 |

---

## 11. 위험 및 완화

| 위험 | 완화 |
|------|------|
| MFT 읽기 권한 거부 | 즉시 `System.IO.Enumeration` 폴백. 사용자에게 조용히 전환 (로그 기록). |
| pHash 오분류 | Threshold 사용자 조정 가능. Near Duplicate는 삭제 전 반드시 수동 확인 UI 거침. |
| 하드링크 교체 실패 | 드라이브 간 하드링크 불가 시 사전 체크 후 알림. NTFS 전용 명시. |
| FFmpeg 다운로드 실패 | 병합 기능만 비활성화. 나머지 기능은 정상 동작. |
| 대용량 파일 메모리 초과 | Sparse Hashing으로 전체 로드 방지. `Stream` 기반 SHA-256. |
| UI 스레드 잠김 | `IAsyncEnumerable` + `Progress<T>` 로 실시간 UI 업데이트. |
| Long Path 처리 오류 | `\\?\` 접두사 전용 경로 처리 유틸리티 함수 단위 테스트 필수. |

---

## 12. 테스트 전략

| 구분 | 내용 | 도구 |
|------|------|------|
| 단위 테스트 | `SparseHasher`, `PHashCalculator`, `SmartSelector`, `UndoManager` 각 로직 | xUnit + FluentAssertions |
| 통합 테스트 | 폴더 스캔 → 중복 탐지 → 조작 → Undo 전체 파이프라인 | xUnit + 가상 파일 시스템 |
| 성능 테스트 | 100만 파일 / 100GB 환경 스캔 시간 + 메모리 프로파일 | dotTrace, PerfView |
| I/O Stress 테스트 | 장시간 스캔 중 메모리 릭 여부 확인 (Phase 4) | dotMemory |
| UI 자동 테스트 | 버튼 클릭 → Undo 복구 시나리오 | UIAutomation |

---

## 13. 향후 확장 로드맵

| 버전 | 기능 |
|------|------|
| **v1.1** | 클라우드 연동 — OneDrive/Google Drive 중복 탐지 및 백업 제안 |
| **v1.2** | Bulk Rename 프리셋 — 날짜별 정리, 이벤트명 일괄 변경 등 파워 유저 도구 |
| **v1.3** | AI 유사도 판정 — TensorFlow Lite 모델 기반 이미지/영상 내용 유사도 점수 제공 |
| **v1.4** | 플러그인 마켓 — 외부 개발자 `IDuplicationAction` 구현 업로드·설치 |

---

## 14. 결론

DupClean은 **정확한 탐지 + 안전한 조작**이 핵심이다.
완전 중복은 Sparse Hashing으로 빠르게, 유사 중복은 pHash + 사용자 확인으로 안전하게.
삭제 전에 격리폴더로 옮기고, 실수하면 Undo로 되돌린다.
하드링크 교체로 경로 구조를 깨지 않고 디스크 공간을 회수한다.

**다음 단계:** Phase 1 — `MftScanner` 콘솔 프로토타입 구현 시작.
