
# 📁 중복 파일 관리 프로그램 상세 설계·구현 계획서  
*(Windows PC 용 – “중복 파일 탐지·비교·조작(삭제·이름변경·병합) 도구”)*  

---  

## 1. 프로젝트 개요  

| 구분 | 내용 |
|------|------|
| **프로젝트명** | **DUP‑FIX** – Windows용 중복 파일 탐지·관리 유틸리티 |
| **목표** | 1) 이미지·동영상·문서 등 다양한 파일 형식에서 **중복 파일**을 자동 탐지 2) 중복 파일 간 **실제 내용**을 비교(Exact, Near, Metadata) 3) 사용자가 **삭제 / 이름 변경 / 병합**을 직관적으로 선택·실행 4) 작업 전 **안전장치**(리사이클바인, Undo, 로그)를 제공하여 데이터 손실을 최소화 |
| **대상 사용자** | 일반 PC 사용자, 사진·동영상·문서 정리 작업을 자주 하는 사무/크리에이티브 사용자 |
| **플랫폼** | Windows 10/11 (x64) – .NET 8 기준 |
| **배포 형태** | MSI/EXE + optional MSIX, zip 배포 시 실행 파일 하나(다양한 포터블 옵션) |

---

## 2. 문제 정의  

1. **중복 파일 탐지는 파일을 하나씩 열어 비교하는 방식**이면 비효율적이며 대용량 미디어 파일에서는 메모리·CPU가 과다 소모됩니다.  
2. **파일 이름/날짜만으로 중복을 판단**하면 실제 내용이 달라도 같은 이름으로 오인하거나, 내용이 동일하지만 이름이 다르면 놓칩니다.  
3. **일괄 삭제·이름 변경** 시 실수로 중요한 파일을 잃어버릴 위험이 있습니다.  

→ 따라서 **파일 크기·해시·텍스트/이미지·영상 포맷 특성**을 조합한 **다중 단계 탐지 알고리즘**과 **안전하고 선택적인 조작**을 제공해야 합니다.

---

## 3. 목표와 기대 효과  

| 목표 | 기대 효과 |
|------|------------|
| **정확한 중복 탐지** | MD5/SHA‑256 해시 + 파일 크기·시간 사전 필터 + 이미지/영상용 Perceptual Hash(pHash)·VHASH 활용 → 정확도 99%+ |
| **다양한 조작 옵션** | *삭제 → 리사이클바인에 이동* / *이름 변경 → 사용자 지정 규칙* / *병합 → PDF 합치기, 동영상 연결, 이미지 콜라주* |
| **안전·복구** | 모든 조작은 “Undo” 가능(동작 로그·데이터베이스 보관) → 실수 복구 시간 최소화 |
| **성능** | 50 GB 수준 파일 컬렉션을 **10 분 이내**에 스캔(멀티스레드, 파일 열기 비동기) |
| **확장성** | 새로운 파일 포맷·병합 타입을 플러그인 형태(다양한 라이브러리) 로 추가 가능 |

---

## 4. 기능 요구사항 (Functional Requirements)

| 번호 | 기능 | 세부 설명 |
|------|------|-----------|
| **F‑1** | **파일 스캔** | • 사용자가 지정(또는 전체 C:\ 등) 폴더에 대해 확장자 리스트(이미지·동영상·문서)만 필터링<br>• NTFS 변경 알림(Change Journal) 또는 **System.IO.Enumeration** 를 이용해 고속 스캔<br>• 파일 **경로·크기·마지막 수정시간**을 사전 계산해 “가능 중복 후보”를 만든다 |
| **F‑2** | **Exact Hash Comparison** | • 파일 크기가 같은 경우 **SHA‑256** 해시를 비동기로 계산<br>• 동일 해시 → **Exact Duplicate** |
| **F‑3** | **Near Duplicate (이미지·영상)** | • 이미지 → **pHash (64‑bit)** 혹은 **dHash**<br>• 동영상 → **Perceptual Video Hash (pVHash)**(프레임 샘플링 후 이미지 pHash) <br>• 해시 Hamming distance ≤ 8 → **Near Duplicate** |
| **F‑4** | **Metadata 기반 중복** | • 이미지 EXIF(촬영 시간/ISO), 동영상 메타(길이/코덱), 문서(제목·저자) 비교<br>• 동일 메타 데이터 + 크기 일치 → 보조 판단 |
| **F‑5** | **중복 그룹 관리** | • 탐지 결과를 **트리 형태**로 UI에 제공(그룹 → 파일이 리스트)<br>• 각 그룹마다 **시각/텍스트 미리보기** 제공 |
| **F‑6** | **조작 옵션** (선택형) | - **삭제**: 파일을 Windows 리사이클바인에 이동 (`Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., UIOption.AllDialogs, RecycleOption.SendToRecycleBin)`) <br>- **이름 변경**: 사용자 정의 규칙(예: `2024-09-01_01_IMG001.jpg`), 충돌 시 자동 번호 매김 <br>- **병합**: <br> • PDF → `iTextSharp`·`PdfiumViewer`를 이용 페이지 병합 <br> • 동영상 → `FFmpeg`를 이용 순차 연결 (재인코딩 없이 `-c copy`)<br> • 이미지 → `ImageSharp`·`SixLabors.ImageSharp`로 **그리드 콜라주** 생성 (사용자 지정 행·열) |
| **F‑7** | **Undo / History** | • 모든 작업은 **작업 로그 DB(SQLite)**에 기록 <br>• UI에서 “Undo” → DB를 기반으로 원본 위치/이름 복원 (읽기 전용 백업 파일도 남김) |
| **F‑8** | **설정·프로파일** | • JSON/YAML 기반 **스캔 프로파일**(경로·필터·해시 방식·스레드 수) <br>• “자동 실행” 스케줄(예: 매일 02:00) – Windows Task Scheduler와 연동 |
| **F‑9** | **다국어 지원** | • 기본 UI는 한국어·영어 (i18n, RESX) – 추후 확장 가능 |
| **F‑10** | **통계·리포트** | • 중복 파일 현황(총 수·용량·비율) → CSV/PDF 내보내기 |

---

## 5. 비기능 요구사항 (Non‑Functional Requirements)

| 구분 | 요구사항 |
|------|----------|
| **성능** | 1️⃣ **스캔 속도** – 50 GB 파일 10 GB/s I/O 기준, 8 스레드 사용 시 10 분 이하. <br>2️⃣ **메모리 사용** ≤ 500 MB (대용량 파일은 스트림 기반 해시, 메모리 매핑 최소화). |
| **안전성** | - 모든 삭제/이름 변경/병합 작업은 **사용자 확인** 화면을 거침. <br>- 작업 전 전체 백업 파일(읽기 전용 복사본) 자동 생성 (`%LOCALAPPDATA%\DUP-FIX\backup\{timestamp}`) |
| **확장성** | - 새로운 포맷·병합 옵션은 **플러그인 인터페이스** (`IDuplicationAction`) 로 구현 → 동적 로딩(assembly DLL) 가능. |
| **호환성** | - Windows 10/11 x64, .NET 8 LTS. <br>- .NET 6 호환 모드로 빌드 시 레거시 Windows 7 SP1에서도 동작하도록 테스트 (옵션) |
| **보안** | - 실행 시 관리자 권한 필요 없음 (파일 리스트는 사용자가 접근 가능한 경로만). <br>- 외부 DLL(FFmpeg) 로드 시 **강제 서명 검증**. |
| **사용성** | - UI는 **WinUI3 + MVVM**(XAML) 혹은 **WPF** (구형 PC 호환성 고려). <br>- Drag‑&‑Drop 지원, 단축키(F5: 재스캔, Ctrl+U: Undo). |
| **배포·유지보수** | - CI: GitHub Actions → .NET 8, Unit 테스트(xUnit), UI 자동 테스트(AutoIt·Appium). <br>- 릴리즈: GitHub Release (MSI + portable zip). |
| **로깅·모니터링** | - `Serilog` + `RollingFile` 로그 (`%LOCALAPPDATA%\DUP-FIX\logs`). <br>- 실행 시 `--verbose` 옵션으로 콘솔 출력. |
| **법적** | - 외부 라이브러리: **ImageSharp (MIT)**, **FFmpeg (LGPL)**, **iTextSharp (AGPL)** 사용 시 라이선스 확인 필요. AGPL는 병합 기능에만 사용하고, 별도 모듈로 격리(가능하면 PDF·영상 병합은 별도 오픈소스(Apache 2.0) 사용). |

---

## 6. 시스템 아키텍처  

```
+--------------------------------------------------------------+
|                     Presentation (UI)                        |
|  WPF/WinUI3 (MVVM)  <---->   ViewModels (Observable)          |
|        |                                           ^         |
|        | (Commands)                               |         |
|        v                                           |         |
+--------------------------------------------------------------+
|                     Application Services                     |
|  +-------------------+   +-------------------+   +-----------+ |
|  | ScanEngine        |   | DuplicateEngine   |   | ActionEngine| |
|  |  - FileProvider   |   |  - ExactHash      |   |  - Delete   | |
|  |  - Async Traversal|   |  - pHash          |   |  - Rename   | |
|  |  - ExtensibleFilters|  |  - MetadataCmp    |   |  - Merge    | |
|  +-------------------+   +-------------------+   +-----------+ |
|          |                         |                     ^ |
|          | (Data)                | (Result)            | |
|          v                         v                     | |
+--------------------------------------------------------------+
|                     Infrastructure (Data)                     |
|  +-------------------+   +-------------------+   +-----------+ |
|  |   DB (SQLite)     |   |   Config (JSON)   |   |   Log     | |
|  +-------------------+   +-------------------+   +-----------+ |
+--------------------------------------------------------------+
```

### 핵심 구성 요소  

| 이름 | 역할 | 주요 API/컴포넌트 |
|------|------|-------------------|
| **FileProvider** | 파일·폴더 열거, NTFS 파일 변경 알림. `DirectoryInfo.EnumerateFiles`, `FindFirstFile`(P/Invoke) | 비동기 `IAsyncEnumerable<FileInfo>` |
| **HashCalculator** | 파일별 해시 계산 (SHA‑256, pHash). 멀티스레드 `Parallel.ForEach` + `BlockingCollection`. `System.Security.Cryptography.SHA256` + `ImageSharp`/`FreeImage` (pHash) | `Task<byte[]> ComputeHashAsync(Stream)` |
| **DuplicateEngine** | 해시 기반 그룹핑, pHash 기반 Near Duplicate, 메타데이터 매칭. | `Dictionary<string, List<FileInfo>>` (hashKey → list) |
| **ActionEngine** | 사용자가 선택한 동작 구현. `IDuplicationAction` 인터페이스(Delete, Rename, Merge). 각 액션은 **Undo 로그**를 남긴다. | `Microsoft.VisualBasic.FileIO` (Recycle Bin), `System.IO.File.Move`, `FFmpeg` CLI (Process.Start) |
| **Undo/History DB** | 작업 기록을 SQLite에 저장. `Job`, `Action`, `TargetFile`, `Timestamp` 테이블. | `EntityFrameworkCore.Sqlite` |
| **ConfigManager** | JSON/YAML 파일을 읽고 동적으로 스캔 프로파일 로드. | `Microsoft.Extensions.Configuration` |
| **Logger** | `Serilog` → 파일 로그 + 콘솔. | `Serilog.Sinks.File`, `Serilog.Sinks.Console` |

---

## 7. 상세 기술 스펙

### 7.1 언어·프레임워크

| 항목 | 선택 사유 |
|------|-----------|
| **C# 12** (target .NET 8) | 최신 async/await, source generators, Null‑Reference 안전성 |
| **UI** | **WPF** (구형 Windows 7도 지원) + **MVVM** (CommunityToolkit.Mvvm) <br>또는 **WinUI 3** (MSIX 배포 시 권장) |
| **비동기·멀티스레드** | `async/await`, `Task.Run`, `Parallel.ForEach`, `BlockingCollection` |
| **파일 포맷 처리** | 이미지·동영상: **ImageSharp** (MIT) + **FFmpeg.AutoGen** (LGPL) <br>문서(PDF): **iText7 Community** (AGPL) → AGPL은 별도 플러그인으로 제공하거나 **PdfiumViewer** 사용(자바스크립트 사용 시 AGPL 위험 회피) |
| **데이터베이스** | **SQLite** (EF Core) – 경량, 파일 기반, 로그/Undo 보관 |
| **설정** | `JSON` (user‑config.json) + `Microsoft.Extensions.Configuration` |
| **로깅** | **Serilog** + `RollingFile` sink (max 10 MB per file, retain 30 days) |
| **패키징** | **WiX** (MSI) + **MSIX** (앱스토어 배포 옵션) 혹은 **单一 exe** (self‑contained) |

### 7.2 파일 탐지 로직

1. **프리 필터**  
   - `FileInfo.Length` ± 10 KB 차이가 나면 **중복 후보**가 될 수 없음 (설정 파일에서 허용 오차 조정).  
   - `LastWriteTime`이 30 분 이내 차이이면 **가능** (동시 작업에서 동일 파일이 복사된 경우 방지).  

2. **해시 계산**  
   - 파일 크기 기준 **분할**: 1 GB 이하 파일은 `SHA256` 직접 계산, >1 GB 파일은 **멀티패스** (초기 16 MiB 체크섬 → 동일하면 완전 해시) → CPU 오버헤드 최소화.  

3. **Near Duplicate (이미지·영상)**  
   - **이미지**: `ImageSharp` 로 8×8 블록 평균 색상 → 64‑bit pHash. `HammingDistance(pHash1, pHash2) ≤ 8`이면 **유사 이미지**.  
   - **동영상**: 프레임 샘플링(30FPS, 30초 구간) → 이미지 pHash 평균 → 동일 규칙 적용.  

4. **메타데이터 비교**  
   - 이미지: EXIF `DateTimeOriginal` → 1 분 차이 이내 → 보조 판단.  
   - 동영상: `ffprobe` 로 `duration`·`codec_name` → 동일 + 해시 유사 → 그룹화.  

5. **그룹 최종 판단**  
   - `ExactGroup` (동일 해시) → **절대 중복** (삭제·병합 권장)  
   - `NearGroup` (pHash 등) → **유사 중복** (복제본·백업) → 사용자가 직접 검증(미리보기) 후 진행.  

### 7.3 조작 옵션 구현 상세

| 옵션 | 구현 포인트 | 위험 방지 |
|------|-------------|-----------|
| **삭제** | `FileSystem.DeleteFile(..., RecycleOption.SendToRecycleBin)` 로 리사이클바인에 이동. <br>동시 작업 시 `FileShare.ReadWrite` 로 열고 `TryDelete` 로 예외 처리. | - 작업 전 *선택* 화면에 **예/아니오** 선택 UI 제공.<br>- 리사이클바인에서 복구 가능. |
| **이름 변경** | 사용자 정의 규칙 → `RenameRule` 객체(정규식 기반). <br>예: `"{FolderName}_{Sequence:D3}{Extension}"`. <br>충돌 시 자동 `Sequence++` 로 번호 매김. | - 이름 변경 전 **시뮬레이션 모드** 제공(파일은 실제 rename 하지 않고 로그에 “Would rename X → Y”). |
| **병합** | <ul><li>PDF: iText7 로 `PdfDocument` 합치기, 페이지 번호에 앞에 prefix 추가(예: `srcA_001`).</li><li>동영상: FFmpeg `-filter_complex concat` 로 연결, 재인코딩 없이 `-c copy`. <li>이미지: `ImageSharp` 로 `Resize` → `Canvas`에 배치, 지정된 행·열 수에 따라 자동 콜라주 파일 저장. </li></ul> | - 실제 파일은 **임시 폴더**에 저장 후 **원본 폴더에 이동** (원자성 보장).<br>- 작업 전 **시뮬레이션** + **백업 파일** 생성(읽기 전용 복사). |
| **Undo** | 모든 조작은 `IUndoable` 인터페이스 구현: `Execute()`, `Rollback()` 메서드. <br>Rollback은 DB에 저장된 원본/이름 정보를 바탕으로 원복. | - 롤백도 동일 로직을 거치므로 파일 권한/잠금 문제 최소화. |

### 7.4 UI 흐름 (예시)

1. **시작 화면**  
   - 검사 대상 폴더 선택 (폴더 피커, Drag‑Drop).  
   - 오른쪽 상단에 “**스캔 프로파일**” 선택 드롭다운(기본/사용자 정의).  

2. **스캔 실행**  
   - 진행 바, 현재 파일 수, 해시 계산 진행 중 텍스트.  
   - “잠시 멈추기(일시정지)” 버튼 → `CancellationTokenSource`.  

3. **중복 결과**  
   - **트리 뷰**: `Exact`, `Similar`, `Metadata Only` 그룹.  
   - 각 파일 타일 → 썸네일(이미지·동영상), 문서 아이콘(PDF).  
   - 파일 클릭 → 오른쪽 미리보기(내부 뷰어).  

4. **조치 선택**  
   - 전체 선택 → “**삭제**”, “**이름 변경**”, “**병합**” 버튼.  
   - “**Undo**” 버튼 → 최근 작업 선택 후 복구.  

5. **작업 확인**  
   - “**Dry‑Run**” (시뮬레이션) → 로그에 “Would …” 기록, UI에 “실제 작업 수행 여부” 확인.  
   - 실제 실행 전 “**이 작업은 영구적으로 수행됩니다**” 경고창.  

6. **결과 리포트**  
   - 완료 화면에 **요약**(중복 수·삭제된 용량·병합된 파일).  
   - “**CSV 내보내기**” → `report_20251101_0234.csv`.  

---

## 8. 데이터 흐름 & 클래스 다이어그램 (핵심)

```
FileSystemScanner
    └─> FileInfo (DTO)
            └─> FileHash (SHA256, pHash)
                    └─> DuplicateKey (string)

DuplicateEngine
    └─> Group<FileInfo> (ExactGroup, NearGroup, MetaGroup)

ActionEngine
    └─> DeleteAction
          └─> DeleteFile(file)  // RecycleBin
    └─> RenameAction
          └─> RenameFile(oldPath, newPath)
    └─> MergeAction
          └─> MergeGroup(Group<FileInfo> group)
                ├─ PDF → PDFMerger
                ├─ Video → VideoMerger (FFmpeg)
                └─ Image → ImageCollage (ImageSharp)

UndoManager
    └─> IUndoable
            ├─ Execute()
            └─ Rollback()
    └─> HistoryDB (SQLite)

Config
    └─ ScanProfile (List<Extension>, ThreadCount, HashAlgorithm, Sensitivity)
    └─ RenameRule (Regex, Replacement)

UI (MainWindow)
    └─ MainViewModel
            ├─ ScanCommand (async)
            ├─ SelectActionCommand
            ├─ UndoCommand
            └─ ReportCommand
```

---

## 9. 프로젝트 일정 (예시, 4개월 스프린트 기반)

| 단계 | 기간 | 주요 산출물 | 담당 |
|------|------|------------|------|
| **A. 요구분석·설계** | 2 주 | 요구사항 정의서, 아키텍처 다이어그램, DB 스키마 | PM, 아키텍 |
| **B. 프로토타입** | 3 주 | 파일 스캔·해시 모듈 (콘솔 테스트), 기본 UI(폴더선택 → 트리) | 개발자 1, 2 |
| **C. 코어 엔진** | 4 주 | `DuplicateEngine`(Exact + pHash), `ActionEngine`(Delete/Undo), 로그/Undo DB | 개발자 1, 2 |
| **D. UI & UX** | 3 주 | MVVM UI, 미리보기 뷰어, 조작 옵션 UI, 다국어 리소스 | UI 디자이너, 개발자 |
| **E. 외부 연동** | 3 주 | FFmpeg CLI wrapper, PDFMerger, ImageCollage 모듈, 플러그인 로딩 | 개발자 2 |
| **F. 안전·오류 처리** | 2 주 | 백업/복구 로직, 예외 핸들링, 테스트 스위트 (xUnit) | QA, 개발자 |
| **G. 성능 최적화** | 2 주 | 멀티스레드 스캔, 스트리밍 해시, 프로파일링 (dotTrace) | 성능 엔지니어 |
| **H. 베타 테스트** | 2 주 | 10명(내부) 베타, 버그 리포트, UI/UX 개선 | QA, PM |
| **I. 배포·문서** | 2 주 | MSI/MSIX 패키지, 사용자 매뉴얼(한/영), API 문서 | 릴리즈 엔지니어 |
| **J. 마감** | 1 주 | 전체 코드 리뷰, CI/CD 파이프라인 구축, 버전 태그 | 팀 전체 |

*총 소요: 약 18 주(4.5개월) + 버퍼(2주) = **6개월** 정도로 잡으면 리스크 최소.*

---

## 10. 위험·완화 전략

| 위험 | 설명 | 완화 방법 |
|------|------|-----------|
| **중복 탐지 오탐·미탐** | 해시 충돌, pHash 오분류. | - SHA‑256은 거의 충돌 없음. <br>- pHash는 **threshold**를 설정(하드웨어 테스트). <br>- “시뮬레이션” 모드에서 수동 검증 UI 제공. |
| **대용량 파일 메모리 초과** | 전체 파일을 메모리에 로드하면 OOM. | - `Stream` 기반 `ComputeHash` (즉시 버퍼링). <br>- 파일 크기 기준 `maxStreamSize = 1GB` 까지만 메모리 매핑. |
| **파일 잠금/권한 오류** | 사용자가 잠긴 파일을 삭제하려 할 때 오류 발생. | - `FileShare.ReadWrite` 옵션으로 열고, 오류 시 로그에 저장하고 다음 파일 처리. <br>- UI에 “잠긴 파일: 자동 스킵” 알림. |
| **외부 도구(FFmpeg) 라이선스** | LGPL 코드 사용 시 배포 제한. | - FFmpeg 바이너리를 외부 **압축 파일**에 포함하고, 사용 시 로컬에 설치된 기존 FFmpeg을 감지해 사용. <br>- 배포 시 “LGPL 사용에 대한 사용자 동의” 화면 제공. |
| **UI에서 Undo 불가능** | 복합 조작(예: 파일 이름을 바꾼 뒤 또 다른 파일을 삭제) → 복구가 복잡. | - 모든 변경을 **트랜잭션** 단위(예: “그룹 내 일괄 조작”) 로 묶고, 트랜잭션 로그에 기록. <br>- Undo는 전체 트랜잭션을 역순으로 복구. |
| **배포 시 관리자 권한 요구** | 리사이클바인에 파일 이동은 일반 사용자에게도 가능하지만, 일부 경로(시스템 폴더) 삭제는 권한 필요. | - 권한 체크 로직을 추가하고, 필요 시 **UAC** 상승을 유도 (전용 “관리자 모드” 옵션). |
| **빌드·배포 환경 차이** | 개발 PC와 고객 PC에 .NET 런타임 버전 차이. | - **Self‑Contained** (`PublishSingleFile`, `ReadyToRun`) 로 빌드해 .NET 8 런타임 없이도 실행 가능. |
| **성능 저하 (스캔 중 UI 잠김)** | UI 스레드가 오래 걸리면 사용성 저하. | - `IAsyncEnumerable` 로 스캔 결과를 UI에 **프론트엔드 버퍼링** (Observer). <br>- 진행 바와 실시간 리스트 업데이트. |

---

## 11. 테스트 전략  

| 구분 | 내용 | 도구/프레임워크 |
|------|------|-----------------|
| **단위 테스트** | `HashCalculator.ComputeSHA256()`, `pHash` 정확도, `DuplicateEngine.Group()` 로직 | xUnit, FluentAssertions |
| **통합 테스트** | 전체 파이프라인: 폴더 스캔 → 중복 그룹 → Delete/Rename/Merge (시뮬레이션) → Undo 회복 | `Microsoft.VisualStudio.TestTools.UnitTest` + **InMemoryFileSystem** (Microsoft.VisualStudio.TestTools) |
| **성능 테스트** | 100 GB 폴더 (예: 샘플 데이터) 스캔 시간, 메모리 프로파일 | dotTrace, PerfView |
| **UI 자동 테스트** | 버튼 클릭, 트리 선택, Undo 복구 확인 | UIAutomation, AutoIt, Playwright for Windows (Desktop) |
| **보안 테스트** | 파일 잠금/권한, 외부 도구 실행 제한, DLL 사전 검증 | OWASP ZAP (for UI), `Process` 권한 검증 |
| **회귀 테스트** | 버전 릴리즈마다 전체 시나리오 자동 실행 (GitHub Actions) | GitHub Actions + Azure Pipelines (Build + Test) |

---

## 12. 배포·운영

1. **빌드 파이프라인**  
   - GitHub Actions → `dotnet build -c Release` → `PublishSingleFile` + `PublishTrimmed` 옵션으로 **Self‑Contained EXE** 생성.  
   - `WiX` 프로젝트를 별도 `MSI` 로 빌드, `WiX` + `MSIX` (코드 서명 인증서) 로 배포.  

2. **버전 관리**  
   - `SemVer` (`MAJOR.MINOR.PATCH`).  
   - 주요 변경(새 파일 포맷·플러그인) → MAJOR, 기능 추가 → MINOR, 버그 수정 → PATCH.  

3. **문서 제공**  
   - `README.md` + `docs/` 폴더에 **설치·사용·스크린샷·FAQ** (한글·영어).  
   - `--help` 옵션: 콘솔에 명령어 목록 및 기본 프로파일 설명.  

4. **지원·업데이트**  
   - GitHub Releases에 최신 MSI와 `portable.zip` 제공.  
   - 자동 업데이트 옵션 (`AppUpdateService`) → GitHub API를 이용해 새 버전 확인 후 다운로드·교체.  

---

## 13. 비용·인력 추정 (예시)

| 역할 | 인원 | 월 평균 급여 (KRW) | 4개월 (16주) 비용 |
|------|------|-------------------|-------------------|
| 프로젝트 매니저 | 0.5 FTE | 6,000,000 | 1,200,000 |
| 개발자 (C#) – 2명 | 2 FTE | 7,000,000 | 5,600,000 |
| UI/UX 디자이너 | 0.25 FTE | 6,000,000 | 300,000 |
| QA 엔지니어 | 0.5 FTE | 5,500,000 | 1,100,000 |
| 시스템/빌드 엔지니어 (DevOps) | 0.25 FTE | 6,500,000 | 400,000 |
| **총 인건비** | - | - | **≈ 9.6 백만원** |

*외부 라이선스/도구 비용*  
- **FFmpeg** 바이너리: 무료(오픈소스) – 배포 시 0원.  
- **iText7** (AGPL) 사용 시 별도 라이선스 구매(또는 Apache 2.0 대체) → 약 0~500,000원.  

**예산 전체 (인건비 + 기타):** 약 **1억원** (인건비 9.6M + 부가세·예비비 등).

---

## 14. 향후 확장 로드맵 (버전 1.1~1.3)

| 버전 | 주요 추가 기능 |
|------|----------------|
| **1.1** | **클라우드 연동** – 탐지 결과를 OneDrive/Google Drive와 동기화, 백업 제안. |
| **1.2** | **플러그인 마켓** – 외부 개발자들이 `IDuplicationAction` 구현 후 업로드·설치 (Marketplace). |
| **1.3** | **AI 기반 중복 판단** – TensorFlow Lite 모델을 활용해 이미지/동영상 내용 유사도 학습·점수 제공, 자동 “안전 복제본” 생성. |

---

## 15. 결론

- **핵심**: 정확한 해시·pHash 기반 중복 탐지 + **안전·Undo** 설계 → 일반 사용자도 실수로 파일을 삭제하거나 오버라이트할 위험을 크게 줄일 수 있음.  
- **가치**: 기업·사무실 환경에서 디스크 용량 절감(수십 GB~TB 단위), 사진·동영상 컬렉션 자동 정리, 중요한 문서(백업) 관리 자동화.  
- **현실성**: 현재 .NET 8, C# 12, FFmpeg, iText7 등 최신 라이브러리를 활용하면 **6개월** 내에 완전한 제품화 가능.  

위 설계서는 **기능·성능·안전·유지보수**를 모두 만족하는 구조이며, 필요에 따라 플러그인·AI 확장이 용이합니다. 프로젝트를 진행한다면 제시된 일정·인력·예산 범위 내에서 **MVP**를 먼저 제공하고, 사용자 피드백을 기반으로 차세대 기능을 추가하는 방식이 권장됩니다.  

--- 

> **다음 단계**  
> 1️⃣ 상세 요구사항 워크숍 (Stakeholder와 1시간 미팅) → `RenameRule`, `ScanProfile` 옵션 구체화  
> 2️⃣ 레포지터리(예: GitHub) 초기화 → `src/Engine` 폴더에 `FileScanner.cs`, `DuplicateEngine.cs` 스캇 구현 시작  
> 3️⃣ 매일 **데모** → 팀 내 UI/UX 검증, 피드백 반영  



보강내용

작성해주신 DUP-FIX 상세 설계·구현 계획서는 기술 스택 선정(.NET 8, SQLite, FFmpeg 등)과 아키텍처, 그리고 무엇보다 사용자의 데이터 안전(Undo, Recycle Bin)을 고려한 매우 완성도 높은 계획서입니다.
전문 개발자이자 IT 환경에 정통한 사용자의 관점에서, 이 프로젝트의 실무적 완성도를 높이고 리스크를 줄이기 위해 보강하거나 추가로 검토할 만한 내용들을 정리해 드립니다.

1. 기술적 보강 및 최적화 (Technical Enhancements)
1.1 하드웨어 가속 및 I/O 최적화
* DirectStorage/MFT 활용: Windows 환경이므로, 수만 개의 파일을 스캔할 때 System.IO보다 성능이 뛰어난 NTFS Master File Table (MFT) 직접 읽기 방식을 검토해 보세요. (예: Everything 엔진이 사용하는 방식). 스캔 속도를 혁신적으로 줄일 수 있습니다.
* GPU 가속 pHash: 이미지나 영상의 pHash 계산 시, 수천 장 이상의 파일을 처리한다면 Compute Shader나 **Cuda(Managed)**를 이용한 병렬 처리를 고려할 수 있습니다. 64GB RAM과 M5 Max/DGX 환경을 보유하고 계시므로 개발 단계에서 큰 이점이 됩니다.
1.2 해시 계산 전략 고도화 (Sparse Hashing)
* 대용량 파일(4GB 이상)의 경우 전체 SHA-256을 계산하는 것은 I/O 낭비가 심합니다.
* Sparse Hash 전략: 파일의 [시작 1MB / 중간 1MB / 끝 1MB]만 추출하여 먼저 비교하고, 여기서 일치할 경우에만 전체 해시를 진행하는 3단계 필터를 권장합니다.
1.3 하드링크(Hard Link) 및 심볼릭 링크 처리
* 중복 파일을 단순히 삭제하는 대신, **'하드링크로 교체'**하는 옵션을 추가해 보세요.
* 용량은 확보하면서도 기존 경로의 파일 구조를 깨뜨리지 않아 개발자나 숙련된 사용자에게 매우 유용한 기능이 됩니다.

2. 기능 요구사항 보강 (Feature Refinement)
2.1 스마트 선택(Smart Selection) 로직
사용자가 수천 개의 중복 그룹을 일일이 확인하기는 어렵습니다. 다음과 같은 **자동 선택 규칙(Auto-Select Rules)**이 필요합니다.
* 경로가 더 짧은 것을 남기기 / 더 긴 것을 남기기
* 파일명에 "copy", "복사본"이 포함된 것을 우선 선택
* 최신 수정 날짜 혹은 가장 오래된 날짜 파일 남기기
* 특정 마스터 폴더(예: P:\Original_Photos)에 있는 파일은 보호하고 나머지만 선택
2.2 샌드박스 미리보기 (Deep Compare)
* 이미지 나란히 보기(Side-by-Side): 유사 중복(pHash)의 경우 두 이미지를 나란히 띄우고 차이점(Pixel Difference)을 하이라이트 해주는 기능을 UI에 포함하면 신뢰도가 높아집니다.
* 파일 속성 비교 테이블: 해상도, 비트레이트, 생성일 등을 테이블로 대조하여 어떤 파일이 '고화질 원본'인지 사용자가 판단하게 도와야 합니다.

3. 안전성 및 사용자 경험 (Safety & UX)
3.1 "이동"을 통한 격리 (Quarantine)
* 삭제(Recycle Bin)가 불안한 사용자를 위해, 중복된 파일들을 특정 **'격리 폴더(.dup_trash)'**로 구조를 유지하며 이동시킨 뒤 30일 후 자동 삭제하는 옵션을 제안합니다.
3.2 롱 패스(Long Path) 및 권한 문제
* Windows에서 MAX_PATH(260자) 제한을 넘는 경로 처리(\\?\ 접두사 활용)와 시스템 파일/읽기 전용 파일에 대한 예외 처리를 비기능 요구사항에 명시하십시오.

4. 라이선스 및 배포 전략 보강
4.1 FFmpeg/iText 라이선스 우회 전략
* iText7 (AGPL): 상업적 이용 시 리스크가 큽니다. PDF 병합이 핵심이 아니라면 PdfSharp (MIT) 또는 QuestPDF를 검토해 보세요.
* FFmpeg: 설치 프로그램에 포함하기보다, 최초 실행 시 "병합 기능을 위해 FFmpeg을 다운로드하시겠습니까?"라고 묻고 사용자 폴더에 다운로드하는 방식을 취하면 LGPL 배포 이슈에서 자유롭습니다.

5. 시각적 이해를 돕기 위한 구조도 (Triggering Diagrams)
프로그램의 핵심인 다단계 탐지 알고리즘의 흐름을 시각화하면 구현 단계에서 로직 혼선을 방지할 수 있습니다.

보강된 마일스톤 제언 (추가 스프린트)
* Sprint 0.5 (I/O Stress Test): 100만 개 이상의 파일이 있는 환경에서 스캔 엔진의 메모리 릭(Leak) 테스트를 설계 단계에 추가하는 것을 추천합니다.
* Sprint 5.5 (Power User Tools): 정규식 기반 일괄 이름 변경(Bulk Rename) 기능의 프리셋(날짜별 정리 등)을 강화하는 단계가 있으면 좋습니다.
이 계획서는 매우 탄탄합니다. 특히 Vibe Coding 스타일을 선호하신다면, 초기 인터페이스(IDuplicationAction, IHashProvider)만 잘 정의해두고 AI 에이전트에게 구현을 맡기기에 아주 적합한 구조입니다. 추가로 궁금하신 구현 디테일이 있다면 말씀해 주세요!

관련하여 더 구체적인 UI 레이아웃 설계나 SQLite 스키마 구성이 필요하신가요?
