# DupClean — 진행 보고서 (2026-11-24)

> 기간: 2026-11-17 ~ 2026-11-24
> 이전 보고서: `report_fable_11.17.md` (전체 점검, 안전 수정 계획), `report_v0.2.md` (v0.2 개발·라이선스 키 문제)
> 다음 계획: `plan_1124.md` (AI 활용)

---

## 1. 요약

| 항목 | 상태 |
|------|------|
| 안전 문제 7건 수정 | ✅ 완료, master 병합 (PR #4). 테스트 111개 통과 |
| TIFF 임시 제외 | ✅ 유사 이미지 분석에서 제외 (완전 중복 검사는 가능) |
| LICENSE | ✅ MIT, Copyright (c) 2026 Finevalue |
| 개발 도구 (.NET SDK 8.0.428) | ✅ 이 PC에 설치 |
| x64 · ARM64 빌드 | ✅ `publish.bat` 한 번으로 두 버전 생성 (PR #6) |
| AI 활용 계획 | ✅ `plan_1124.md` 작성 (PR #5). 시험(PoC)은 사진·동영상 폴더 지정 대기 |
| master 브랜치 보호 | ✅ 설정 완료 |
| 브랜치 정리 | ✅ GitHub에는 master만 남음 |
| ImageSharp 라이선스 키 | ⏳ 정정 재신청 완료 (2026-11-24), 새 키 대기 |

---

## 2. 완료한 일

### 2.1 안전 수정과 라이선스 (PR #4, 2026-11-24 병합)
상세 내용은 `report_fable_11.17.md` 10장.
- 안전 문제 S1~S7 수정 + 하드링크 되돌리기 버그, 숨겨진 실패, 스캔 후 바뀐 파일, 큰 파일 오판, 전역 오류 처리.
- TIFF를 유사 이미지 분석에서 임시 제외 (ImageSharp 업그레이드 후 되돌림).
- `LICENSE` 추가: MIT, Finevalue (https://finevalue.kr). 실행 파일 회사 정보도 Finevalue.
- 테스트 111개 (기존 83 + 신규 28) 전부 통과.

### 2.2 소스 공개 범위 결정
- **핵심은 공개, Pro 기능은 비공개** (보고서 7.5의 B안). Pro 기능을 만들 때 별도 비공개 저장소를 만든다.

### 2.3 개발 환경
- 이 PC에 .NET SDK 8.0.428 설치 (2026-11-17).
- **이 PC는 ARM64 기종**(20코어, RTX Spark 그래픽)이다. v0.2 측정 PC(Ryzen 9, x64)와 다르므로 **속도 측정값을 v0.2 숫자와 직접 비교하지 않는다.**

### 2.4 x64 · ARM64 빌드 (PR #6)
- `win-arm64` 배포 설정 추가. `publish.bat`이 두 버전을 차례로 만든다.

| 버전 | 위치 | 크기 |
|------|------|------|
| x64 (일반 Intel·AMD PC) | `dist\win-x64\DupClean.exe` | 약 83MB |
| ARM64 (ARM 기종) | `dist\win-arm64\DupClean.exe` | 약 78MB |

- 확인: 두 파일이 각각 맞는 CPU용인지 실행 파일 정보로 확인, 이 PC에서 둘 다 창이 정상적으로 뜸.
- 이 PC에서 창이 뜨는 시간: ARM64판 약 1.5초, x64판 약 5.5초 (x64는 변환 실행).
- 바뀐 점: x64판 위치가 `dist\DupClean.exe` → `dist\win-x64\DupClean.exe`.
- `publish.bat`은 빌드 전에 이전 결과물을 지운다 (실패한 빌드를 성공으로 착각하지 않게).

### 2.5 AI 활용 계획 (PR #5)
`plan_1124.md`에 정리. 요점:
- AI는 **비슷한 사진**과 **화질만 다른 같은 동영상**에 효과가 있고, 완전 중복에는 쓰지 않는다.
- 주력은 PC 안에서 도는 **작은 이미지 특징 모델**. 대화형 AI(로컬 소형, Claude·Gemini)는 애매한 경우의 보조, 클라우드는 선택 기능.
- 바로 넣지 않고 실제 사진·동영상으로 시험(PoC)부터.

### 2.6 GitHub 저장소 정리
**master 브랜치 보호 (2026-11-24 설정)**

| 설정 | 상태 |
|------|------|
| 강제 덮어쓰기(force push) 금지 | 켬 |
| 삭제 금지 | 켬 |
| PR로만 병합 (직접 올리기 금지) | 켬 |
| PR 승인 필요 인원 | 0명 (본인 PR도 바로 병합 가능) |
| 관리자 계정에도 적용 | 켬 |
| 자동 검사 통과 필요 | 꺼 둠 → CI를 만들면 켜기 |

→ **앞으로 모든 변경은 브랜치 → PR → 병합 순서로만** master에 들어간다.

**브랜치 정리**

| 브랜치 | 처리 |
|--------|------|
| `feature/progress-eta-dark-theme-perf`, `docs/imagesharp-license-status`, `chore/imagesharp-license-prep` | 병합 완료 확인 후 삭제 |
| `docs/progress-history` | 병합 안 된 문서 커밋 1개 → PR #7로 병합 후 삭제 |
| `main` | master와 별개 기록인 4월 초기 시제품(커밋 3개). **태그 `archive/main-2026-04`로 보관** 후 삭제 |
| `docs/ai-plan`, `build/arm64` | PR #5, #6 병합 후 삭제 |

**병합된 PR**

| PR | 내용 | 병합일 |
|:-:|------|--------|
| #4 | 안전 문제 7건 수정, LICENSE | 2026-11-24 |
| #5 | AI 활용 계획 문서 | 2026-11-24 |
| #6 | x64 · ARM64 빌드 | 2026-11-24 |
| #7 | 11/14 진행 이력 (report_v0.2 10절) | 2026-11-24 |

---

## 3. ImageSharp 라이선스 키 현황

### 3.1 확인한 사실
- **신청 이메일은 네이버(kernelh@naver.com)**다. Six Labors 메일은 Gmail이 아니라 네이버로 온다. (Gmail을 검색했을 때 아무것도 나오지 않은 이유)
- 첫 키: `noreply@sixlabors.com`에서 **2026-11-13 00:14** 수신. 라이선스 ID `ctm_01m7855fcf1rxd1d4z1wrgv5rm`, Community, 만료 2028-02-10.
  - **기록 정정**: `report_v0.2.md`에는 첫 키 수신일이 2026-11-14로 적혀 있으나, 메일상으로는 **2026-11-13**이다.
- 이 키는 Assembly 이름 3개가 한 칸에 쉼표로 들어가 이름 하나로 발급돼 쓸 수 없다 (`report_v0.2.md` 6.5).

### 3.2 2026-11-24 조치
- **정정 재신청 제출** (https://licensing.sixlabors.com): Email kernelh@naver.com, Kind OpenSource, Assembly `DupClean.Core` / `DupClean.Tests` / `DupClean`을 **"Add assembly"로 한 칸씩**, Website는 GitHub 저장소. Message에 기존 라이선스 ID와 "assembly 범위 키로 1회 조기 교체" 요청을 적음.
- **지원팀 문의 메일 초안**: Gmail 임시 보관함에 `help@sixlabors.com` 앞으로 작성해 둠 (아직 보내지 않음). 하루 이틀 지나도 키가 오지 않으면 보낸다. 가능하면 네이버에서 첫 키 메일에 답장하는 방식으로.
- Six Labors 안내: 오픈소스 신청은 키가 자동 발급된다. 처리 기간은 적혀 있지 않다.

### 3.3 키가 오면 할 일
1. 첨부된 `sixlabors.lic`를 `D:\Code\DupClean\sixlabors.lic`로 저장 (`.gitignore`에 들어 있어 커밋되지 않음).
2. **업그레이드 전에 Assembly 이름 검증부터** (`report_v0.2.md` 6.4 체크리스트 1번).
3. ImageSharp 3.2.0 이상으로 업그레이드, TIFF 임시 제외 되돌리기.
4. 테스트, `publish.bat`(x64·ARM64), 취약점 경고 사라짐 확인 후 PR.

---

## 4. 남은 일

| 항목 | 우선순위 | 상태 / 필요한 것 |
|------|:-:|------|
| ImageSharp 키 → 업그레이드 | 높음 | 새 키 대기 (3장) |
| 화면 클릭 시험 (스캔 → 선택 → 격리 → 되돌리기) | 높음 | 안전 수정 후 화면 흐름을 직접 눌러 본 적 없음. 직접 확인 또는 자동 화면 시험 작성 |
| AI 시험(PoC) | 중 | 사진 폴더·동영상 폴더 지정과 진행 승인 (`plan_1124.md` 7장) |
| 화면 편의 기능 | 중 | 격리함 화면, 실행 전 요약, 여러 폴더 검사 등 (`report_fable_11.17.md` 5장) |
| GitHub Actions CI | 중 | 만들면 브랜치 보호의 "자동 검사 통과 필요"를 켠다 |
| 코드 서명·릴리스 | 출시 전 | 릴리스 0개. 서명이 없으면 Windows 경고 창이 뜸 |

## 5. 결정이 필요한 것

- MFT 고속 모드 삭제 여부 (`report_fable_11.17.md` 9장)
- 병합·PDF 리포트·플러그인 기능을 계획에서 뺄지
- 측정·검증 절차를 `tools/`·`workflows/`에 남길지 (매뉴얼 신규 작성 승인)
- AI 관련 결정 5가지 (`plan_1124.md` 8장)
