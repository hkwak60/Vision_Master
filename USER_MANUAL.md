# Vision Issue Tracker 사용자 매뉴얼

## 1. 프로그램 목적

Vision Issue Tracker는 생산 라인의 Vision Inspection Instrument 이슈를 기록, 검색, 보고서화하고 SW/Algo 버전 변경 이력을 관리하기 위한 Windows 데스크톱 프로그램입니다.

주요 사용 목적:
- 라인별/비전별 이슈 기록
- 미해결 이슈 추적
- 조건별 검색 및 Excel 보고서 생성
- 비전별 SW Version / Algo Version 관리
- 버전 업데이트 시 Monitoring 이슈 자동 등록

## 2. 실행 및 배포 파일

### 실행 파일

배포용 실행 파일:

```text
release\VisionIssueTracker.exe
```

Python 설치 여부와 관계없이 실행되도록 빌드되어 있습니다.

### DB 파일 위치

프로그램은 실행 파일이 있는 위치 기준으로 아래 DB를 사용합니다.

```text
data\vision_issues.db
```

처음 실행 시 DB가 없으면 자동으로 생성됩니다.

### 배포 시 필요한 파일 구조

새 PC에서 빈 DB로 시작할 경우:

```text
VisionIssueTracker.exe
```

기존 데이터를 같이 배포할 경우:

```text
VisionIssueTracker.exe
data\
  vision_issues.db
```

예시:
- 작업자 노트북 4대가 같은 공유 폴더의 프로그램을 실행하려면 공유 폴더에 `VisionIssueTracker.exe`와 `data\vision_issues.db`를 같이 둡니다.
- 기존 기록을 유지하려면 반드시 `data\vision_issues.db`를 함께 복사해야 합니다.

주의:
- SQLite DB는 여러 PC가 동시에 저장할 때 충돌 가능성이 있습니다.
- 동시에 여러 명이 자주 저장하는 운영이면 추후 서버 DB 방식이 더 안전합니다.

## 3. 기본 구성

### 라인

```text
1-1
1-2
2-1
2-2
```

### 비전 종류

```text
Pinhole
Pouch Align
Lead
Sealing
Lead Align
Welding(+)
Welding(-)
```

### 작업자

우측 상단 작업자 선택에서 이슈 작성자를 선택합니다.

```text
Hojun Kwak
Kijung Kim
Jihoon Yun
Jisub Yun
```

예시:
- Jihoon Yun이 이슈를 작성하는 경우 우측 상단에서 `Jihoon Yun`을 선택한 뒤 저장합니다.

### 언어

우측 상단 언어 선택에서 변경할 수 있습니다.

```text
English
한국어
```

프로그램 시작 기본 언어는 한국어입니다.

## 4. 미해결 이슈 탭

미해결 이슈 탭은 현재 조치가 필요하거나 모니터링 중인 이슈를 보여줍니다.

표시되는 상태:
- Action Required: 조치 필요
- Monitoring: 모니터링

Resolved 상태는 미해결 이슈 탭에서 제외되고 검색 탭에서 확인할 수 있습니다.

### 대시보드 카드

상단 카드:
- Action Required
- Monitoring
- Resolved Today
- Active

예시:
- Action Required가 3이면 현재 즉시 조치가 필요한 이슈가 3건입니다.
- Active는 Action Required와 Monitoring을 합친 수량입니다.

### 이슈 상세 보기

테이블에서 이슈를 선택하면 오른쪽 상세 패널에 내용이 표시됩니다.

표시 항목:
- Title
- Status
- Line / Instrument
- Category
- Issue Time
- Logged By
- Downtime Duration
- Description

상세 패널은 세로 스크롤이 가능합니다.

### 버튼 기능

Refresh:
- 최신 이슈 목록을 다시 불러옵니다.

Edit:
- 선택한 이슈를 `이슈 등록 / 수정` 탭으로 불러옵니다.

Resolved:
- 선택한 이슈를 해결 완료 처리합니다.
- Downtime Duration이 비어 있으면 발생 시간 기준으로 자동 계산됩니다.

Delete:
- 선택한 이슈를 삭제합니다.
- 삭제 전 확인창이 표시됩니다.

예시:
1. `1-1 Pinhole` 이슈를 선택합니다.
2. 해결이 끝났으면 `Resolved` 버튼을 누릅니다.
3. 해당 이슈는 미해결 이슈 탭에서 사라지고 검색 탭에서 `Resolved` 상태로 확인됩니다.

## 5. 이슈 등록 / 수정 탭

새 이슈를 입력하거나 기존 이슈를 수정하는 탭입니다.

### 라인 선택

라인 버튼 중 하나를 선택합니다.

예시:

```text
1-1
```

### 비전 선택

비전 버튼은 복수 선택이 가능합니다.

예시 1:

```text
Pinhole
```

예시 2:

```text
Welding(+) / Welding(-)
```

여러 비전에 같은 이슈가 발생하면 해당 비전을 모두 선택합니다.

### Issue Time

발생 시간을 입력합니다.

형식:

```text
YYYY-MM-DD HH:MM
```

날짜 입력칸을 클릭하면 달력이 표시됩니다. 시간은 `HH : MM` 형식으로 스핀 버튼을 사용해 조정할 수 있습니다.

예시:

```text
2026-06-18 08:35
```

### Category / Subcategory

사용 가능한 분류:

```text
Hardware
- Camera
- Lighting

Software
- Program Crash
- Program Update
- UI
- PLC
- Other

Recipe
- Overkill
- Underkill
- Add Measure
- Bypass/Unbypass

Camera Grab Fail
Production
Other
```

예시:
- 카메라 연결 불량: `Hardware > Camera`
- 프로그램 다운: `Software > Program Crash`
- PLC 통신 이상: `Software > PLC`
- 과검출: `Recipe > Overkill`
- 카메라 Grab 실패: `Camera Grab Fail`

### Status

사용 가능한 상태:

```text
Action Required
Monitoring
Resolved
```

상태 의미:
- Action Required: 조치가 아직 필요한 상태
- Monitoring: 조치는 했지만 결과를 지켜보는 상태
- Resolved: 해결 완료

### Downtime Duration

다운타임 시간을 입력합니다.

기본값:

```text
00:00
```

예시:
- 다운타임 없음: `00:00`
- 35분 정지: `00:35`
- 1시간 20분 정지: `01:20`

### Title

이슈 제목을 짧고 명확하게 입력합니다.

좋은 예:

```text
Pinhole camera disconnect
Welding program crash during auto run
Lead Align overkill after recipe change
```

피하면 좋은 예:

```text
Issue
Problem
Check needed
```

### Description

상세 증상과 상황을 입력합니다.

예시:

```text
1-1 Pinhole inspection 중 camera timeout 발생.
재시작 후 정상 복귀했으나 동일 증상 재발 가능성 있어 Monitoring 필요.
```

### Resolution Notes

조치 내용을 입력합니다.

예시:

```text
Camera cable 재체결 후 vision program restart.
10 lot monitoring 결과 재발 없음.
```

### 저장 예시

예시 상황:
- 라인: 1-1
- 비전: Pinhole
- 발생 시간: 2026-06-18 08:35
- 카테고리: Hardware > Camera
- 상태: Action Required
- 제목: Pinhole camera disconnect
- 설명: Camera connection lost during production.

입력 후 `Save`를 누르면 미해결 이슈 탭에 표시됩니다.

## 6. 검색 및 엑셀 보고서 탭

이슈를 조건별로 검색하고 Excel 파일로 저장하는 탭입니다.

### 검색 조건

사용 가능한 필터:
- Status
- Line
- Category
- Subcategory
- Keyword
- From
- To
- Vision Filter

날짜 범위는 기본적으로 DB에 있는 첫 이슈 시간과 가장 최근 이슈 시간으로 설정됩니다.

### Vision Filter

비전 필터도 복수 선택이 가능합니다.

예시:
- `Pinhole`만 검색
- `Welding(+)`와 `Welding(-)`를 동시에 검색
- `Lead`, `Lead Align` 관련 이슈 검색

### Quick Filter

버튼:
- Today
- This Week
- Action Required
- Monitoring
- Camera Grab Fail
- Recipe Issues
- Clear

예시:
- 오늘 발생한 이슈만 보고 싶으면 `Today`
- Recipe 관련 이슈만 보고 싶으면 `Recipe Issues`
- 필터를 초기화하려면 `Clear`

### Excel 저장

검색 결과를 Excel로 저장하려면 `Excel` 버튼을 누릅니다.

Excel 보고서 특징:
- 검색 결과만 저장됩니다.
- ID는 실제 DB ID가 아니라 검색 결과의 순번으로 표시됩니다.
- Issue Time 기준으로 정렬됩니다.

예시:
- 전체 100건 중 Camera Grab Fail 2건만 검색 후 Excel 저장하면 ID는 `1`, `2`로 저장됩니다.

### 삭제

검색 결과 테이블에서 이슈를 선택하고 `Delete`를 누르면 삭제할 수 있습니다.

삭제 전 확인창이 표시됩니다.

## 7. 버전 기록 탭

비전별 SW Version과 Algo Version을 관리하는 탭입니다.

구성:
- Version Dashboard
- Version Update
- Version Description

## 8. Version Dashboard

각 라인/비전의 최신 버전 상태를 표시합니다.

표시 단위:
- 4개 라인
- 7개 비전

총 28개 조합의 현재 버전을 확인할 수 있습니다.

카드 표시 내용:
- Line
- SW Version
- Algo Version
- Last Updated

최근 7일 내 업데이트된 항목은 색상으로 강조됩니다.

예시:

```text
1-1
SW 1.2.3
Algo 4.5.6
2026-06-18 09:10
```

버전 정보가 없으면 `No Version`으로 표시됩니다.

### 대시보드 Excel 추출

우측 상단 `대시보드 추출` 버튼으로 현재 Version Dashboard 정보를 Excel로 저장할 수 있습니다.

Excel 항목:
- Line
- Vision
- Group
- SW Version
- Algo Version
- Last Updated
- Logged By
- Description

예시 사용:
- 현재 전체 라인/비전 버전 현황을 회의 자료로 저장
- 특정 날짜 기준 version snapshot 보관

## 9. Version Group

버전 그룹은 버전 템플릿을 관리하기 위한 묶음입니다.

```text
Welding
- Welding(+)
- Welding(-)

Common
- Pinhole
- Pouch Align
- Lead Align

New Lead
- Lead

Sealing
- Sealing
```

중요:
- 그룹은 버전 후보를 묶는 단위입니다.
- 실제 적용 버전은 각 라인/비전별로 다를 수 있습니다.

예시:
- Common 그룹에 SW 1.5.0이 있어도 `Pinhole`만 먼저 업데이트하고 `Pouch Align`은 이전 버전을 유지할 수 있습니다.

## 10. Version Update

새 버전 적용 기록을 입력하는 영역입니다.

입력 항목:
- Version Group
- Version Template
- Update Time
- SW Version
- Algo Version
- 모니터링 이슈 등록
- Line
- Vision
- Description

### Version Template

선택한 그룹의 최근 버전 템플릿 최대 3개가 표시됩니다.

용도:
- 최신 버전 재사용
- 이전 버전 선택 후 롤백 기록

예시:
- Welding 그룹에서 최근 템플릿:

```text
SW 1.3.0 / Algo 2.8.1
SW 1.2.5 / Algo 2.7.9
SW 1.2.0 / Algo 2.7.0
```

롤백 시 이전 템플릿을 선택한 뒤 적용할 라인/비전을 선택하고 저장합니다.

### Update Time

버전 적용 시간을 수기로 입력합니다.

형식:

```text
YYYY-MM-DD HH:MM
```

예시:

```text
2026-06-18 10:20
```

### 라인/비전 복수 선택

라인과 비전을 복수로 선택할 수 있습니다.

예시:
- `1-1`, `1-2` 라인에 Welding(+)만 업데이트
- `2-1` 라인에 Welding(+)와 Welding(-) 동시 업데이트
- Common 그룹에서 Pinhole, Pouch Align만 먼저 업데이트

### 모니터링 이슈 등록

`☑ 모니터링 이슈 등록`이 켜져 있으면 버전 저장 시 이슈가 자동 생성됩니다.

자동 생성 이슈:
- Category: Software
- Subcategory: Program Update
- Status: Monitoring
- Issue Time: Version Update의 Update Time과 동일
- Downtime Duration: 00:00

예시:

버전 업데이트:

```text
Line: 1-1
Vision: Pinhole
SW Version: 1.5.0
Algo Version: 3.2.1
Update Time: 2026-06-18 10:20
```

자동 생성 이슈:

```text
Software > Program Update
Monitoring
Program Update - 1-1 Pinhole SW 1.5.0 / Algo 3.2.1
```

## 11. Version Description

Version Description 패널은 그룹별 버전 템플릿을 확인, 수정, 삭제하는 영역입니다.

### 그룹 선택

4개 그룹 중 하나를 선택합니다.

```text
Welding
Common
New Lead
Sealing
```

선택한 그룹의 최근 버전 템플릿 최대 3개가 리스트로 표시됩니다.

### 버전 내용 수정

수정 가능 항목:
- SW Version
- Algo Version
- Description

수정 후 `Save Version`을 누르면 저장됩니다.

수정 영향:
- 해당 그룹/SW/Algo로 기록된 version history도 함께 업데이트됩니다.
- Version Dashboard에도 변경 내용이 반영됩니다.

예시:

수정 전:

```text
SW Version: 1.5.0
Algo Version: 3.2.1
Description: ROI threshold update
```

수정 후:

```text
SW Version: 1.5.1
Algo Version: 3.2.2
Description: ROI threshold update and PLC handshake delay fix
```

### 버전 삭제

삭제할 버전을 선택하고 `Delete Version`을 누릅니다.

삭제 영향:
- 해당 버전 템플릿이 삭제됩니다.
- 해당 그룹/SW/Algo로 적용됐던 version history 기록도 삭제됩니다.
- Version Dashboard는 남아있는 이전 기록 기준으로 자동 갱신됩니다.

예시:
- `Welding / SW 1.3.0 / Algo 2.8.1`을 삭제하면 해당 버전으로 표시되던 라인/비전은 이전 버전으로 돌아가거나, 이전 기록이 없으면 `No Version`으로 표시됩니다.

주의:
- 버전 삭제 시 자동 생성됐던 이슈 로그는 삭제되지 않습니다.
- 이슈 로그 삭제가 필요하면 미해결 이슈 탭 또는 검색 탭에서 별도로 삭제합니다.

## 12. 권장 입력 규칙

### 제목 작성 규칙

권장 형식:

```text
[Vision] + symptom
```

예시:

```text
Pinhole camera timeout
Lead Align overkill after recipe update
Welding(-) program crash
```

### Description 작성 규칙

아래 정보를 포함하면 검색과 분석에 유리합니다.

```text
1. 발생 상황
2. 증상
3. 임시 조치
4. 재발 여부
5. 추가 확인 필요 사항
```

예시:

```text
Auto run 중 Welding(-) inspection program crash 발생.
Lot change 직후 2회 재발.
Program restart 후 정상 복귀했으며 동일 조건에서 Monitoring 필요.
```

### Resolution Notes 작성 규칙

예시:

```text
Recipe parameter rollback.
Camera exposure value restored from 1200 to 950.
20 trays monitoring result: no recurrence.
```

## 13. 일반 작업 예시

### 예시 A: 카메라 하드웨어 이슈 등록

1. 우측 상단 작업자 선택: `Jihoon Yun`
2. `이슈 등록 / 수정` 탭 선택
3. 라인: `1-1`
4. 비전: `Pinhole`
5. Issue Time: `2026-06-18 08:35`
6. Category: `Hardware`
7. Subcategory: `Camera`
8. Status: `Action Required`
9. Downtime Duration: `00:15`
10. Title: `Pinhole camera disconnect`
11. Description 입력
12. `Save`

결과:
- 미해결 이슈 탭에 Action Required 이슈로 표시됩니다.

### 예시 B: 프로그램 업데이트 기록

1. `버전 기록` 탭 선택
2. Version Group: `Common`
3. Update Time: `2026-06-18 10:20`
4. SW Version: `1.5.0`
5. Algo Version: `3.2.1`
6. `☑ 모니터링 이슈 등록` 켜기
7. Line: `1-1`, `1-2`
8. Vision: `Pinhole`, `Pouch Align`
9. Description:

```text
False reject 개선을 위해 ROI threshold 및 Add Measure logic update.
```

10. `Save Version Update`

결과:
- 선택한 라인/비전에 버전 기록 생성
- Version Dashboard 업데이트
- Software > Program Update / Monitoring 이슈 자동 생성

### 예시 C: 이전 버전으로 롤백 기록

1. `버전 기록` 탭 선택
2. Version Group: `Welding`
3. Version Template에서 이전 버전 선택
4. Line: `2-1`
5. Vision: `Welding(+)`, `Welding(-)`
6. Update Time 입력
7. Description:

```text
New version에서 intermittent grab delay 발생하여 이전 안정 버전으로 rollback.
```

8. `Save Version Update`

결과:
- Version Dashboard가 이전 버전으로 표시됩니다.
- Monitoring 이슈가 자동 생성됩니다.

### 예시 D: 검색 후 Excel 보고서 저장

1. `검색 및 엑셀 보고서` 탭 선택
2. Category: `Software`
3. Subcategory: `Program Update`
4. From / To 날짜 확인
5. `Search`
6. `Excel`

결과:
- Program Update 관련 이슈만 Excel 파일로 저장됩니다.

## 14. 데이터 백업

백업해야 할 파일:

```text
data\vision_issues.db
```

권장 백업 방식:
- 매일 작업 종료 후 날짜를 붙여 복사

예시:

```text
vision_issues_2026-06-18.db
```

복구 방법:
1. 프로그램 종료
2. 기존 `data\vision_issues.db`를 백업본으로 교체
3. 프로그램 재실행

## 15. 문제 해결

### 프로그램은 실행되지만 데이터가 안 보일 때

확인할 것:
- 실행 중인 `VisionIssueTracker.exe` 옆에 `data\vision_issues.db`가 있는지 확인
- 다른 폴더의 exe를 실행하고 있지 않은지 확인

### 다른 PC에서 데이터가 다르게 보일 때

원인:
- 각 PC가 서로 다른 위치의 DB를 사용 중일 수 있습니다.

해결:
- 모든 작업자가 같은 공유 폴더의 `VisionIssueTracker.exe`를 실행하도록 합니다.
- 또는 같은 `data\vision_issues.db`를 사용하도록 폴더 구조를 맞춥니다.

### Excel 저장이 안 될 때

확인할 것:
- 저장하려는 Excel 파일이 이미 열려 있지 않은지 확인
- 저장 위치에 쓰기 권한이 있는지 확인
- 파일명이 너무 길거나 특수문자가 포함되어 있지 않은지 확인

### 삭제한 이슈/버전 복구

현재 프로그램에는 삭제 취소 기능이 없습니다.

복구하려면:
- 백업해둔 `vision_issues.db` 파일로 복구해야 합니다.

## 16. 운영 주의사항

- 삭제 전에는 반드시 확인창 내용을 확인합니다.
- 버전 삭제는 Version Dashboard 표시에도 영향을 줍니다.
- 여러 사용자가 동시에 저장하는 환경에서는 충돌 가능성이 있습니다.
- 중요한 변경 전에는 DB 파일을 백업하는 것이 좋습니다.

