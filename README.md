# ZECOLE 통합 런처

VR 유튜브 크리에이터 **제콜(ZECOLE)**이 만든 한글패치와 VR 도구를 **프로그램 하나로 설치·업데이트·원본 복구**하는 Windows 런처입니다.

**[최신 버전 받기 (ZECOLE_Launcher.exe)](https://github.com/zecole-vr/zecole-launcher/releases/latest)** · 설치 없이 바로 실행

## 지원 항목

| 구분 | 게임·도구 |
|---|---|
| PC VR (Steam) | BattleGroupVR2 · CONVRGENCE · Titanfall 2 VR · Gunman Contracts · A Fisherman's Tale |
| Quest 단독 | H3VR2 · PAYDAY: Aces High · Into the Radius 2 · A Fisherman's Tale |
| VR 도구 | Meta VR Glasses FoV Tester |

`github.com/zecole-vr`에 새 한글패치 릴리스가 올라오면 런처를 다시 받지 않아도 목록에 자동으로 나타납니다.

## 주요 기능

- **설치·업데이트·원본 복구**를 한 화면에서. 게임 버전과 패치노트도 함께 표시합니다.
- **안전한 파일 사용**: `zecole-vr` 릴리스 파일만 받고, GitHub에 기록된 SHA-256과 맞을 때만 씁니다. 원본은 이 PC에 백업합니다.
- **Quest 무선 설치**: USB 또는 무선 디버깅으로 연결합니다. 처음 한 번 USB로 무선을 켜면 이후에는 케이블 없이 쓸 수 있고, 재부팅으로 포트가 바뀌어도 같은 기기를 다시 찾습니다.
- **기존 설치기와 호환**: 게임별 설치기(EXE)로 설치한 한글패치도 그대로 인식하고 이어서 관리합니다.

## 필요 환경

- Windows 10/11 64비트, .NET Framework 4.8, Microsoft Edge WebView2 런타임 (Windows 11에는 기본 포함)
- Quest 패치: 개발자 모드와 USB 디버깅. 연결 도구(ADB)는 처음 한 번 Google 약관에 동의한 뒤 자동으로 받습니다. 런처는 ADB를 함께 배포하지 않습니다.
- 코드 서명이 없어 처음 실행할 때 Windows가 "PC 보호" 창을 띄울 수 있습니다. **추가 정보 → 실행**을 눌러 주세요.

오류·제안은 [Issues](https://github.com/zecole-vr/zecole-launcher/issues)에 남겨 주세요.

---

## 개발자용

### 폴더 구성

| 경로 | 내용 |
|---|---|
| `src/` | C# 본체 (net48 WinForms + WebView2). 다운로드·검증, 설치 상태 감지, Steam 감지, 자체 업데이트 |
| `src/Quest/` | Quest 설치 엔진 (변환표 `quest-recipe-v1/v2`, ADB·무선 연결, FoV Tester) |
| `src/Pc/` | PC VR 설치 엔진 (게임별 차분 적용, 원본 백업·복구) |
| `ui/index.html` | 런처 화면 전체 |
| `ui/catalog.json` | 기본 게임 정보·링크 (오프라인일 때도 이 값으로 표시) |
| `ui/licenses/` | 제3자 라이선스 원문. 실행 파일에 함께 들어가 런처의 "오픈소스 고지"에서 열람 |
| `feed/feed.json` | 원격 운영 파일. `main` 브랜치의 이 파일을 고치면 런처를 다시 배포하지 않아도 공지·혜택·링크가 바뀝니다 |
| `native/zlib/` | 공식 zlib 1.2.13으로 빌드한 압축 DLL (재압축 결과를 Python `zlib.compress(x, 9)`와 같게) |
| `tools/` | 패치 데이터 생성 도구 (예: A Fisherman's Tale Quest 변환표) |
| `docs/zecole.json.example` | 새 패치 저장소에 넣는 정보 파일 예시 |

### 빌드

```powershell
.\build.ps1
```

`dist\ZECOLE_Launcher.exe`와 `dist\games\ZECOLE_<게임>_한글패치.exe`가 만들어집니다. 게임별 EXE는 런처와 **같은 파일이고 이름만 다르며**, 실행하면 그 게임 화면으로 바로 열립니다. 런처가 이미 켜져 있으면 새 창 없이 기존 창이 그 게임으로 바뀝니다.

기기 없이 Quest 변환표를 검증하려면:
`ZECOLE_Launcher.exe --quest-selftest <설치기.exe|.zpkg> <원본 파일> <작업 폴더> <결과.json>`

### 새 한글패치 등록하기

`zecole-vr`에 저장소를 만들고 **정식 릴리스**(Draft·Pre-release 제외)를 올리면 다음 실행부터 목록에 나옵니다. 저장소 맨 위에 `zecole.json`을 두면 표시 정보를 덮어쓸 수 있습니다 (`docs/zecole.json.example` 참고).

| 필드 | 뜻 |
|---|---|
| `kind` | `patch`(한글패치) / `mod`(VR 모드) / `tool`(도구) |
| `name`, `sub` | 표시 이름, 부제 |
| `platforms` | `["pcvr"]`, `["quest"]`, 또는 둘 다 |
| `steamAppId` | Steam 앱 번호 (대표 이미지·설치 감지) |
| `dep` | 지원 게임 버전 칸 (`label`, `value`, `note`) |
| `notes` | 버전별 패치노트 요약 (`{"1.2.3": ["…", "…"]}`) |
| `tips`, `hidden` | 알아둘 점, 목록에서 숨기기 |

설치 방식(`install`)은 런처 버전마다 지원 엔진이 다르므로 `zecole.json`이 아니라 `ui/catalog.json`에서 정합니다.

### 런처가 접속하는 곳

`api.github.com`, `raw.githubusercontent.com`, `github.com`(릴리스 파일), `zecole.store`, `www.youtube.com`(채널 RSS), 이미지 CDN(Steam·YouTube), jsDelivr(Pretendard 글꼴). 필요할 때만 Google(ADB)·Meta(Meta VR CLI) 공식 서버. GitHub 응답은 `%LOCALAPPDATA%\ZECOLE\Launcher\cache`에 저장하고 ETag로 바뀐 것만 다시 받습니다.

## 라이선스

- 런처 코드: [MIT](LICENSE) © 2026 ZECOLE
- 포함된 외부 구성 요소와 글꼴: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)
- 게임 이름·로고·이미지는 각 권리자의 것이며, 이 런처와 한글패치는 각 게임의 개발사·배급사와 관련 없는 비공식 팬 제작물입니다.

제작: VR 유튜브 크리에이터 제콜 · [YouTube](https://www.youtube.com/@zecole)
