# 릴리스 작업 규칙

배포할 때마다 지켜야 할 것과, 작업 폴더를 어떻게 정리하는지 정리한 문서입니다.

---

## 1. 릴리스 절차

1. 수정 → `CHANGELOG.md` 항목 추가 → `ThrowMe.csproj` 의 `Version`·`AssemblyVersion`·`FileVersion` 올리기
2. 커밋 → `main` push
3. **커밋된 코드만으로** 단일 파일 exe 빌드 (아래 2절)
4. `gh release create vX.Y.Z <exe> --target <커밋 SHA>` 로 릴리스
5. `dist\ThrowMe-X.Y.Z\` 에 보관
6. **임시 빌드 폴더 정리** (아래 2절)

버전 규칙: `주.부.수정` — 기능 추가는 부, 버그·조정은 수정을 올립니다.

---

## 2. `_r1161` 같은 폴더는 왜 생기나

릴리스용 exe 는 **작업 중인 파일이 섞이지 않도록** 커밋된 트리에서만 빌드합니다.
그래서 매번 임시 worktree 를 만들어 거기서 `dotnet publish` 를 합니다.

```powershell
git worktree add --detach C:\claudeProject\Slimey\_r1161 <커밋SHA>
# publish ...
git worktree remove C:\claudeProject\Slimey\_r1161 --force
```

마지막 줄이 **실패할 때** 폴더가 남습니다. 빌드 직후에는 MSBuild·백신·탐색기가
`bin`/`obj` 안 파일을 아직 붙잡고 있어서 삭제가 거부되는 일이 잦습니다.
`git worktree remove` 는 등록만 지우고 폴더 삭제에 실패하면 그대로 두기 때문에,
git 목록에는 안 보이는데 디스크에는 남는 상태가 됩니다.

### 규칙

- **임시 worktree 이름은 `_r<버전>` 형태로 통일합니다.** 정리 스크립트가 이 이름으로 찾습니다.
- **릴리스가 끝나면 반드시 정리 스크립트를 돌립니다.** 실패해도 다음 실행 때 지워집니다.
- 지우기 전에 `git worktree list` 에 없는지 확인합니다. 등록된 것은 테마 worktree
  (`ThrowMe-basketball` 등)이므로 **지우면 안 됩니다.**

---

## 3. `dist` 는 최신 5개만 남깁니다

한 빌드가 **약 156MB** 라, 쌓이면 금방 수 GB 가 됩니다.
릴리스는 GitHub 에 모두 남아 있으므로 로컬 사본을 오래 둘 이유가 없습니다.

- **보관: 최신 5개 버전**
- 판단 기준은 **버전 번호**입니다(수정 시각이 아님). 옛 버전을 다시 빌드해도 순서가 꼬이지 않습니다.
- `ThrowMe-X.Y.Z` 형식이 **아닌** 폴더는 건드리지 않습니다.
  예: `_멀티PC-설정` — 손으로 쓴 설정 안내와 방 접속 정보라 지우면 안 됩니다.

> 더 오래된 버전이 필요하면 GitHub 릴리스에서 다시 받으면 됩니다.
> https://github.com/Bulkcoding/ThrowMe/releases

---

## 4. 정리 스크립트

```powershell
# 미리보기(아무것도 지우지 않음)
powershell -ExecutionPolicy Bypass -File tools\release-cleanup.ps1 -WhatIf

# 실제 정리 — 남은 _r* 폴더 제거 + dist 최신 5개만 유지
powershell -ExecutionPolicy Bypass -File tools\release-cleanup.ps1

# 보관 개수를 바꾸려면
powershell -ExecutionPolicy Bypass -File tools\release-cleanup.ps1 -Keep 3
```

하는 일:

1. `C:\claudeProject\Slimey` 의 `_r*` 폴더 중 **git 에 등록되지 않은 것**만 삭제
2. `git worktree prune` 으로 폴더가 사라진 등록 정보 정리
3. `dist` 의 `ThrowMe-X.Y.Z` 를 버전 내림차순으로 정렬해 **상위 N개만 남기고 삭제**

파일이 잠겨 삭제가 안 되면 `LOCKED` 로 표시하고 넘어갑니다.
Visual Studio·탐색기를 닫고 다시 실행하면 지워집니다.

> 이 스크립트는 **ASCII 로만** 작성돼 있습니다. Windows PowerShell 5.1 은 `.ps1` 을
> ANSI 로 읽어서, 한글 주석이 있으면 문자열이 깨지며 파싱 오류가 납니다.
> 설명은 이 문서에 두고 스크립트는 영문으로 유지합니다.
