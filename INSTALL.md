# Strata FN 설치·연결 안내

Qwen Status는 **Strata 전용**입니다. 앱의 `설치 도우미 / AI 설치` 또는 [AI 설치 요청문](AI-INSTALL-PROMPT.md)이 같은 `setup/setup.ps1`을 사용합니다. 기존 서버 연결과 **이미 설치된 선행 조건을 검증하는 Windows 네이티브 준비**를 제공합니다. 엔진·모델 전체 자동 다운로드나 새 GPU PC 원클릭 설치가 아닙니다. 설치만으로 모델·대기열·Telegram을 시작하지 않습니다.

## 1. 앱 준비

- Windows 10/11, .NET Framework, 앱 소스 실행용 Windows PowerShell 5.1.
- 소스·네이티브 원본 확인용 Git for Windows.
- 설치용 승인된 PowerShell 환경(도우미는 PowerShell 7 우선 사용).
- 대기열·MCP worker용 Node.js 22 이상. 없으면 공식 portable 배포본을 SHA256 확인 후 설치 폴더에 준비합니다.
- Hermes는 선택 사항입니다. 전용 Python·Git Bash·홈을 별도로 구성합니다.

새 사용자 소스 폴더에 받으세요. 기존 폴더가 있으면 삭제·덮어쓰기 없이 확인합니다.

```powershell
$source = Join-Path $env:USERPROFILE 'Apps\QwenStatus-source'
if (Test-Path -LiteralPath $source) { throw '기존 소스 경로를 확인하세요.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $source) -Force | Out-Null
git clone https://github.com/kea9997/qwen-status-tray.git $source
if ($LASTEXITCODE -ne 0) { throw '소스 다운로드 실패' }
powershell.exe -NoProfile -STA -File (Join-Path $source 'run-source.ps1')
```

또는 `build.cmd`로 `QwenStatus.new.exe`를 컴파일해 실행하세요. EXE는 서명되지 않았습니다. 실행 정책·서명·Smart App Control·Defender·Device Guard가 차단하면 우회하거나 끄지 말고 승인된 실행 경로를 사용하세요. 상태 앱 자체에는 고성능 GPU가 필요하지 않습니다.

## 2-A. 기존 Strata FN 연결 (기본)

설치 도우미에서 새 전용 설치 폴더와 **기존 Strata FN 연결**을 선택하고 실제 HTTP loopback URL(예: `http://127.0.0.1:8088`)을 명시합니다. 모델 ID를 비우면 명시적 준비 시 `/health`·`/v1/models`로 모델·유효 문맥을 확인하고 `setup-state.json`에 저장합니다. 외부 서버의 시작·종료·설정은 변경하지 않습니다.

```powershell
$app = Join-Path $env:USERPROFILE 'Apps\QwenStrata'
$setup = Join-Path $source 'setup\setup.ps1'
& $setup -Action Plan -InstallRoot $app -Backend existing -UpstreamUrl http://127.0.0.1:8088 -Json
& $setup -Action Install -InstallRoot $app -Backend existing -UpstreamUrl http://127.0.0.1:8088
# 앱 연결 변경을 원할 때만 Install에 -ConfigureApp 추가 (기존 설정 백업)
# 대기열 시작을 따로 선택한 경우에만 실행; 외부 모델은 시작하지 않음
& (Join-Path $app 'setup\setup.ps1') -Action StartGateway -InstallRoot $app
& (Join-Path $app 'setup\setup.ps1') -Action Verify -InstallRoot $app
```

`-ExistingBackend`는 `strata`만 허용하며 기본값도 Strata입니다. `-Backend`는 `existing|strata`만 허용합니다. 지원하지 않는 선택·기존 레거시 설치의 receipt/backend 파일은 명시적으로 거절합니다. 이전 설치나 기록을 지우거나 자동 변환하지 말고 새 전용 폴더를 선택하세요.

## 2-B. Strata FN 네이티브 준비 (선행 설치 필요)

먼저 다음을 별도로 준비한 뒤 **Strata FN · 네이티브 준비**를 선택합니다. 설치된 원본을 검증·등록하는 경로입니다.

- [고정 소스·프로필](setup/backends.json)의 Strata **0.2.3.1** 커밋 원본과 Git. 추적 소스가 변경되면 거절합니다.
- CUDA 13 의존성·`numpy`·`jinja2`가 설치된 Python 3.10+ 전용 가상환경의 `Scripts\python.exe`.
- 공식 Strata 엔진 **0.1.48 CUDA 13**과 `BUILD.json`. `EngineSha256`은 별도로 검증한 **실제 strata.exe**의 SHA256 64자리입니다. 압축파일 해시와 다르며 latest-feed fallback은 없습니다.
- 고정 리비전 SC117 **IQ3_XXS / IQ2_XS** 첫 GGUF와 공통 두 번째 GGUF, 해당 가중치에서 준비한 pack(`native_experts.txt`)·tokenizer(`vocab.json`, `merges.txt`)·MTP 디렉터리·JSON 설정. 일반 pack의 이름만 바꾸어 사용하지 마세요.
- 예시 검사 기준 RAM 48GiB 이상, VRAM 23,000MiB 이상 NVIDIA GPU, 명시적 GPU index, 추가 여유 4GiB. IQ3 가중치 약 47.3+28.8GB, IQ2 약 39.7+28.8GB이고 pack·MTP·가상환경 공간은 별도입니다. 4GiB는 모델 저장 공간이 아닙니다.

```powershell
# 경로·해시·GPU는 본인이 검증한 실제 값으로 대체
$native = @{
    InstallRoot = (Join-Path $env:USERPROFILE 'Apps\QwenStrataNative')
    Backend = 'strata'; StrataProfile = 'IQ3_XXS'
    StrataSource = 'C:\AI\Strata'
    StrataPython = 'C:\AI\StrataEnv\Scripts\python.exe'
    StrataConfig = 'C:\AI\PreparedSC117\strata-sc117.json'
    EngineSha256 = '<검증한 strata.exe SHA256 64자리>'
    GpuIndex = 0; ContextTokens = 262144; PoolWorkers = 10
    ResidentBudgetGiB = 26; Prefill = 'auto'
}
& $setup -Action Plan @native -Json
& $setup -Action Install @native
# 모델 시작을 따로 결정한 경우에만 (관리 포트 18021, 대기열 18022)
& (Join-Path $native.InstallRoot 'setup\setup.ps1') -Action StartBackend -InstallRoot $native.InstallRoot -Backend strata
```

IQ2 예시는 `StrataProfile='IQ2_XS'`, `Prefill='16384'`입니다. 두 예시 모두 int8 KV, KV resident 최대 32768, spec 4/min-p 0.5, VRAM reserve 1280MiB, draft vocab cjk를 사용합니다. **측정 예시이지 보편 최적값·속도 보장이 아닙니다.** 문맥(8192~262144)·workers·RAM 예산·prefill을 메모리와 작업에 맞춰 조정하세요. 근사 route skipping·오버클럭은 켜지 않습니다. 사진 기능은 끕니다.

Install과 명시적 Install 재실행은 GGUF·준비 산출물을 해시하므로 오래 걸릴 수 있습니다. Verify는 기록·서비스·모델·유효 문맥을 확인하며 대용량 pack/MTP를 다시 해시하지 않습니다. 새 Start는 Python·엔진·entry·관리 설정 해시를 확인하고 `--engine strata --host 127.0.0.1 --port 18021 --config ... --gpu ...`로 시작합니다. Stop은 기록된 PID·생성 시각·실행 파일·config·설치 경로·유휴 상태를 확인해 **소유 프로세스만** 종료합니다. 포트 충돌이나 외부 서버를 임의 종료하지 않습니다.

## 3. 도우미의 동작과 검증

1. 구성을 선택하고 **설치 전 확인**으로 경로·다운로드·선행 조건을 확인합니다. 읽기 전용입니다.
2. **선택 항목 준비**를 누르고 로그·상태를 확인합니다. 진행 중인 설치를 다른 창에서 중복 제출하지 않습니다.
3. **이 PC 앱 연결도 변경**은 기본 해제입니다. 선택한 경우에만 기존 설정을 백업하고 연결을 반영합니다. 상태 앱 재시작이 필요합니다.
4. 모델·대기열 시작은 별도 선택입니다. 기존 연결은 **대기열 켜기/끄기**만 사용하고, 모델 제어는 원래 도구에서 수행합니다. 네이티브 관리 설치만 **모델 켜기/끄기**를 제공합니다.
5. **연결 검증**의 항목별 결과를 확인합니다. 준비되지 않은 연결은 종료 코드 2이며 파일 준비 실패와 구분합니다.

| 확인 대상 | 성공 근거 |
| --- | --- |
| 모델 | `/health`의 `service=strata`, `loaded=true`, `/v1/models`와 저장한 실제 모델·유효 문맥 일치 |
| 대기열 | `18022/health` 준비 완료, `/queue` 응답, UI 토큰 생성 |
| 직접 대화 | 별도로 선택한 짧은 질문·후속 질문 완료와 요청 기록·usage |
| MCP 선택 시 | 대상 AI에 worker 등록 후 작은 작업 제출·결과 회수 |
| Hermes 선택 시 | 전용 설치 Verify 및 사용자의 첫 실행 후 로컬 모델 응답 |
| 성능 확인 선택 시 | 유휴 서버에서 quick 검사와 보고서 저장 |

`loaded=false`는 접근 가능일 뿐 준비 완료가 아닙니다. 같은 포트의 다른 서비스·다른 모델·변경된 유효 문맥도 준비 완료로 보지 않습니다. 로딩 중에는 원래 프로세스·로그로 추적하며, 프로세스 시작 성공을 추론 성공으로 보고하지 마세요.

일반 모델 요청은 **`http://127.0.0.1:18022/v1`**로 보내세요. 직접 upstream 호출은 FIFO를 우회합니다. 게이트웨이는 **텍스트·도구 호출 전용**이며 images 광고와 무관하게 이미지를 거절합니다. `/tokenize`가 없으면 `/v1/messages/count_tokens`로 텍스트·도구 메시지를 변환합니다. 실제 문맥에 출력 예약을 포함하고, 누락된 속도·KV 지표는 만들지 않습니다.

## 4. 선택 기능·문제 해결·업데이트

Hermes 선택 시 Plan·Install에 `-IncludeHermes`를 추가합니다. 정확한 Strata 모델 ID를 receipt에서 읽어 전용 프로필에 사용하며 계정·Telegram 설정은 사용자가 직접 입력합니다. [Hermes 안내](setup/HERMES.md) · [런타임/MCP](runtime/README.md)

포트 충돌은 소유자를 먼저 확인하고 임의 kill·재시작하지 마세요. 네이티브 준비 실패는 설치된 고정 원본·CUDA/Python 의존성·해시·RAM/VRAM·공간을 확인합니다. Hermes 재설치는 기존 폴더를 덮어쓰지 않습니다. 도구가 AI에 없으면 worker 파일 준비와 클라이언트의 실제 MCP 등록을 구분하세요.

설치 기록은 `setup-state.json`, 로그는 도우미·`setup-logs`, Strata 기록은 `strata/install.json`, Hermes 기록은 `hermes/install-result.json`·`hermes/install.log`에 있습니다. 토큰·계정·원문·전체 로그를 공개 저장소나 이슈에 올리지 마세요. 원문을 화면에서 가려도 파일은 남습니다.

앱을 다시 열려면 승인된 Windows PowerShell 5.1에서 설치 폴더의 `run-source.ps1`을 실행하거나 빌드한 EXE를 실행합니다. 상태 앱 종료는 모델·대기열 종료가 아닙니다. 시작 프로그램은 자동 등록하지 않습니다.

업데이트는 변경 내용을 확인한 Git 소스의 `update-source.ps1 -Mode Install -Target <설치폴더> -Repository <Git소스폴더>`를 사용합니다. 최신 소스의 파일 목록을 사용해야 합니다. 이후 `분석 센터 → 업데이트`도 사용할 수 있습니다. 앱만 재시작하고 서버는 자동 재시작하지 않습니다. 기존 사용자 설치·설정·기록을 제거하지 않습니다.

## 검증 범위

모의 loopback 서버와 격리 폴더로 검사하도록 구성되어 있으며 실제 명령은 [런타임 검증 안내](runtime/README.md)에 있습니다. 파일 준비·단위 검사와 새 PC GPU 설치·추론 성공은 별개입니다. 새 PC에서 엔진·모델 취득부터 GPU 응답까지의 종단 간 설치를 보장하지 않습니다. 로컬 처리량은 유료 AI 절약량이 아니며 OpenAI 사용량 0을 보장하지 않습니다.
