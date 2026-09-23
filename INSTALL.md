# 처음 사용자 설치 안내

이 안내는 **Qwen Status 상태 앱을 Windows에서 소스로 설치하고 실행하는 방법**입니다. 공개 저장소의 실제 파일과 실행 코드를 기준으로 작성했습니다.

**Qwen 모델, vLLM/ninfer 서버, 공통 대기열, MCP worker, Hermes는 이 저장소에 포함되지 않습니다.** 처음부터 로컬 AI 환경 전체를 만드는 통합 설치 기능은 아직 없습니다. 아래 절차로 상태 앱을 설치해도 별도 서버 없이 AI가 답변하지는 않습니다.

## 1. 사용할 기능과 준비물 확인

| 사용하고 싶은 기능 | 따로 필요한 것 |
| --- | --- |
| 앱 실행·트레이 아이콘 | Windows, Windows PowerShell 5.1, .NET Framework의 C# 컴파일러 |
| 서버 상태·세션 처리량 | 실행 중인 모델 서버의 `/health`, `/metrics` API |
| GPU 상태 | NVIDIA GPU와 드라이버; 센서가 제공하지 않는 값은 표시되지 않을 수 있음 |
| 누적 토큰·최근 입출력·API 환산액 | 이 앱과 호환되는 공통 대기열과 `requests` 기록 폴더 |
| 직접 대화 | 공통 대기열의 `/ui/jobs`, `/ui/conversations` API와 `ui-token.txt` |
| 토큰 테스트 | 공통 대기열의 `token-test.mjs`, Node.js, 준비된 모델 서버 |
| AI 위임 | 별도 MCP worker, Node.js, 연결할 AI 클라이언트의 도구 설정 |
| Hermes 에이전트 | 별도로 설치·설정한 Hermes와 `%LOCALAPPDATA%\hermes\node\hermes.cmd` |
| 앱에서 서버 켜기·끄기 | WSL 및 자신의 서버에 맞는 `start.sh`, `stop.sh` |

Windows PowerShell 5.1은 Windows 10 이상에 기본 포함됩니다. PowerShell 7의 `pwsh` 대신 아래에 적힌 `powershell.exe`를 사용하세요. [Microsoft 안내](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_windows_powershell_5.1?view=powershell-5.1)

24GB GPU는 모델 실행 환경을 위한 목표 사양입니다. 상태 앱 자체에 24GB VRAM이 필요한 것은 아닙니다. 64K/224K 컨텍스트와 속도는 모델·백엔드 설정에 따르며 앱 설치만으로 설정되지 않습니다.

## 2. 소스 내려받기와 설치

앱의 업데이트 기능을 사용할 수 있도록 Git 방식으로 안내합니다. Git이 없다면 [Git for Windows 공식 설치 페이지](https://git-scm.com/install/windows)에서 설치하고, PowerShell 창을 새로 여세요.

시작 메뉴에서 **Windows PowerShell**을 열고 먼저 확인합니다. 보통 관리자 실행은 필요하지 않습니다.

```powershell
git --version
$PSVersionTable.PSVersion
```

Git 버전과 PowerShell `5.1`이 표시되면 다음 블록을 복사해 실행하세요. 아래 명령은 사용자 폴더의 `Apps` 아래에 원본과 실행용 폴더를 따로 만듭니다. 기존 폴더가 있으면 덮어쓰지 않고 중단합니다.

```powershell
$ErrorActionPreference = 'Stop'
$base = Join-Path $env:USERPROFILE 'Apps'
$repo = Join-Path $base 'QwenStatus-public'
$app = Join-Path $base 'QwenStatus'
if ((Test-Path -LiteralPath $repo) -or (Test-Path -LiteralPath $app)) {
    throw '설치 폴더가 이미 있습니다. 아래 업데이트 항목을 확인하세요.'
}
New-Item -ItemType Directory -Path $base -Force | Out-Null
git clone https://github.com/kea9997/qwen-status-tray.git $repo
if ($LASTEXITCODE -ne 0) { throw '소스를 내려받지 못했습니다.' }
New-Item -ItemType Directory -Path $app | Out-Null
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "$repo\update-source.ps1" -Mode Install -Target $app -Repository $repo
if ($LASTEXITCODE -ne 0) { throw '설치 검사가 실패했습니다. 표시된 오류를 확인하세요.' }
```

`설치 완료:`와 버전이 나오면 준비됐습니다. 설치 과정은 소스를 컴파일하고 자체 검사를 실행합니다. 검사 창이 잠깐 나타날 수 있습니다. 모델 서버를 시작하거나 모델 파일을 다운로드하지 않습니다.

최초 설치가 중간에 실패했지만 소스를 정상적으로 받았다면, 오류 원인을 해결한 후 기존 폴더에서 아래 설치 명령만 다시 실행할 수 있습니다. 폴더를 무조건 삭제하거나 다시 복제할 필요는 없습니다.

```powershell
$repo = Join-Path $env:USERPROFILE 'Apps\QwenStatus-public'
$app = Join-Path $env:USERPROFILE 'Apps\QwenStatus'
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "$repo\update-source.ps1" -Mode Install -Target $app -Repository $repo
```

`Code → Download ZIP`으로 소스를 받을 수도 있지만, `.git` 폴더가 없는 ZIP만으로는 앱 내 Git 업데이트 기능이 작동하지 않습니다. ZIP 사용자는 `run-source.ps1`이 있는 폴더에서 실행하고, 이후 소스도 직접 교체해야 합니다.

## 3. 처음 실행하기

PowerShell에서 실행합니다.

```powershell
powershell.exe -NoProfile -STA -ExecutionPolicy RemoteSigned -File "$env:USERPROFILE\Apps\QwenStatus\run-source.ps1"
```

처음에는 오류를 볼 수 있도록 위 명령으로 실행하세요. 이 PowerShell 창은 앱 실행 프로세스이므로 함께 닫으면 앱도 종료될 수 있습니다. 정상 실행을 확인한 뒤에는 아래 바로가기를 사용하면 콘솔 창을 숨길 수 있습니다.

- 시계 옆 알림 영역에 Qwen 아이콘이 생깁니다. 안 보이면 숨겨진 아이콘을 여는 `^`를 확인하세요.
- 아이콘을 눌러 상태창을 열고, 우클릭 메뉴에서 `연결 설정`을 여세요. 설정이 없는 첫 실행에는 안내창이 자동으로 열립니다.
- 서버가 꺼져 있으면 `꺼짐` 표시는 정상입니다. 상태 앱은 모델 서버를 자동으로 켜지 않습니다.
- 상태창의 닫기 버튼은 트레이로 숨깁니다. 앱 자체를 끝내려면 트레이 메뉴의 종료 항목을 사용하세요.

이 실행 방식은 새 QwenStatus EXE를 만들지 않고 .NET Framework로 소스를 컴파일해 실행합니다. `RemoteSigned`는 해당 PowerShell 프로세스에 적용됩니다. 조직 정책·Smart App Control·Defender 때문에 실행이 막힌 환경에서는 보안 기능을 끄는 방식으로 해결하지 말고, 오류 내용과 적용된 정책을 확인하세요. 서명된 일반 사용자용 설치 파일은 아직 제공하지 않습니다.

## 4. 연결 설정하기

기본 포트를 사용하도록 별도 서버와 공통 대기열을 구성한 경우의 예입니다. **주소만 입력한다고 해당 서버가 설치되거나 시작되지는 않습니다.**

| 연결 설정의 항목 | 입력 예 | 의미 |
| --- | --- | --- |
| 모델 서버 URL | `http://127.0.0.1:18021` | 상태 측정을 위한 모델 서버 기본 주소. `/v1`을 붙이지 않음 |
| 공통 대기열 URL | `http://127.0.0.1:18022/queue` | 대기열 상태 조회 주소 |
| 공통 대기열 폴더 | `C:\AI\qwen-gateway` | 예시 경로. 실제 설치된 폴더를 선택해야 함 |

대기열 폴더 안에는 누적 기록용 `requests` 폴더와 직접 대화 인증용 `ui-token.txt`가 필요합니다. 빈 폴더와 가짜 토큰 파일을 만드는 것으로 대체할 수 없습니다. 일반적인 OpenAI 호환 `/v1` 서버만으로 이 앱의 `/ui` 기능까지 제공되지는 않습니다. 공통 대기열과 MCP worker의 배포용 설치 패키지는 현재 이 저장소에서 제공하지 않습니다.

1. 별도로 구성한 모델 서버와 대기열을 사용자가 실행합니다.
2. `연결 확인`을 누릅니다. 모델만 있으면 모델 연결만 성공해도 상태 확인을 시작할 수 있습니다. 대기열 없음은 별도 기능 미구성 상태입니다.
3. `설정 저장`을 누릅니다.
4. `분석 센터 → 업데이트 → 상태 앱 재시작`으로 새 설정을 읽습니다. 모델 서버 재시작은 필요하지 않습니다.

직접 설정 파일을 편집해야 한다면 `settings.example.json`을 참고하세요. 실제 설정은 `%LOCALAPPDATA%\QwenStatus\settings.json`입니다. 이미 설정이 있다면 예제로 덮어쓰지 마세요. `jobsDirectory`는 MCP worker 작업 기록 폴더이며, `gatewayDirectory`와 구분됩니다.

다른 AI 프로그램에서 모델을 호출할 때 쓰는 주소는 **`http://127.0.0.1:18022/v1`**입니다. `18021/v1`로 직접 보내면 공통 대기열을 우회하므로 이 앱의 요청 기록과 집계에 포함되지 않을 수 있습니다. 다른 PC나 웹서비스의 `127.0.0.1`은 이 Windows PC를 뜻하지 않습니다.

## 5. 정상 동작 확인

1. 서버를 실행했을 때 상태가 준비 중에서 대기로 바뀌고, 서버가 지원하는 속도·GPU 지표가 표시되는지 봅니다.
2. **호환 대기열까지 구성한 경우에만** `직접 대화`에서 짧은 질문을 보냅니다. 답변 완료 뒤 같은 대화에서 이어서 질문합니다.
3. 최근 요청·누적 토큰이 갱신되는지 확인합니다. 원문이 안 보이면 상태창의 내용 가리기 설정도 확인하세요.
4. AI 위임은 `AI 위임 설정 → 자동 → 전달문 복사`로 안내를 대상 AI에 전달하고, 도구 연결 후 작은 대표 작업으로 확인합니다. 앱에서 모드를 저장하는 것만으로 외부 AI 설정이 바뀌지는 않습니다.

누적 토큰은 로컬 모델의 처리량이며 유료 AI에서 절약된 토큰을 측정한 값은 아닙니다. API 환산액도 실제 절약액과 구분합니다.

## 6. 바탕화면 바로가기 만들기

첫 실행에 성공한 뒤 Windows PowerShell에서 실행하면 `Qwen 상태창` 바로가기를 만듭니다. 같은 이름의 바로가기가 있으면 중단합니다. 이후에는 바로가기를 더블클릭하면 됩니다.

```powershell
$app = Join-Path $env:USERPROFILE 'Apps\QwenStatus'
$script = Join-Path $app 'run-source.ps1'
if (-not (Test-Path -LiteralPath $script)) { throw '먼저 앱 설치를 완료하세요.' }
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Qwen 상태창.lnk'
if (Test-Path -LiteralPath $shortcutPath) { throw '같은 이름의 바로가기가 이미 있습니다.' }
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
$shortcut.Arguments = '-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy RemoteSigned -File "' + $script + '"'
$shortcut.WorkingDirectory = $app
$shortcut.Save()
```

이 바로가기는 상태 앱만 실행합니다. 모델 서버는 사용자가 직접 켜고 끕니다. Windows 로그인 시 자동 실행은 이 설치 절차에 포함되지 않습니다.

## 7. 업데이트·복구·삭제

- **업데이트:** `분석 센터 → 업데이트`에서 GitHub 버전을 확인하고 설치합니다. 옆의 `QwenStatus-public` 폴더를 보관해야 합니다. 설치가 끝나면 `상태 앱 재시작`을 누릅니다.
- **복구:** 같은 화면의 이전 버전 복구 기능을 이용합니다. 백업은 `%LOCALAPPDATA%\QwenStatus\update-backups`에 있습니다. 복구 후에도 상태 앱을 재시작합니다.
- **삭제:** 트레이에서 상태 앱을 종료한 다음 본인이 만든 실행 폴더와 바로가기를 삭제합니다. 설정·대화·통계는 `%LOCALAPPDATA%\QwenStatus`에 따로 남습니다. 이 데이터까지 지우면 대화와 집계를 잃으므로 먼저 백업 여부를 결정하세요. 모델 서버·대기열·Hermes는 별도 설치 항목입니다.

## 자주 막히는 지점

| 증상 | 먼저 확인할 것 |
| --- | --- |
| `git`을 찾을 수 없음 | Git 설치 후 PowerShell 창을 새로 열었는지 |
| `run-source.ps1` 경로 없음 | 실행 폴더와 내려받기가 완료됐는지. ZIP은 압축 내부가 아닌 풀어 놓은 폴더에서 실행 |
| 실행 정책·코드 서명 때문에 차단 | 조직 정책과 오류 확인. 보안 기능 전체 해제를 설치 조건으로 요구하지 않음 |
| 아이콘을 눌러도 없음 | 숨겨진 아이콘 영역 확인. 다시 실행해도 실패하면 `%LOCALAPPDATA%\QwenStatus\source-launch-error.log` 확인 |
| 서버 켜기·끄기 버튼 비활성 | 개인 서버용 `start.sh`·`stop.sh`가 없으면 정상. 공개 앱에는 해당 스크립트가 없음 |
| 직접 대화가 안 됨 | 모델 서버 외에 호환 대기열, 올바른 폴더, `ui-token.txt`, UI API가 있는지 |
| 토큰 테스트가 안 됨 | 대기열 폴더에 `token-test.mjs`가 있는지, Node.js가 설치됐는지. 필요 시 `QWEN_NODE_EXE` 지정 |
| Hermes를 찾지 못함 | Hermes 별도 설치 및 `%LOCALAPPDATA%\hermes\node\hermes.cmd` 존재 여부 |
| 누적 토큰이 0 | 요청을 공통 대기열로 보냈는지, `requests` 폴더가 맞는지. 직접 백엔드 요청을 자동 집계하지 않음 |
| 업데이트 원본이 없음 | Git으로 받은 `QwenStatus-public` 폴더가 실행 폴더 옆에 있는지 |

해결되지 않으면 [GitHub 이슈](https://github.com/kea9997/qwen-status-tray/issues)에 Windows 버전, 설치 단계, 오류 문구를 적어 주세요. 토큰·인증 파일·대화 원문·개인 경로가 들어 있는 전체 로그는 공개하지 마세요.
