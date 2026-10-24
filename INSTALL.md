# 처음 사용자 설치 안내

**앱의 설치 도우미**에서 구성 요소를 선택하거나, **AI 설치 프롬프트**를 로컬 명령 실행이 가능한 AI에게 전달하세요. 두 방법 모두 저장소의 같은 설치 스크립트를 사용합니다.

상태 앱, 공통 대기열, MCP worker, 선택형 모델 서버·Hermes 설치 스크립트가 포함됩니다. 모델 파일과 개인 인증 정보는 포함하지 않습니다. 우선 선택은 **Strata FN SC117 IQ3_XXS / IQ2_XS의 기존 연결 또는 설치된 선행 조건 검증**입니다. Strata는 엔진·모델 자동 다운로드를 하지 않으며, 레거시 vLLM/ninfer는 해당 설치 스크립트로 내려받아 준비합니다. **모델 서버는 자동으로 시작하지 않습니다.**

새 PC에서 다운로드부터 GPU 응답까지 이어지는 전체 설치는 아직 검증되지 않았습니다. 설치 성공 표시만으로 사용 준비가 끝났다고 판단하지 말고 연결·응답 검증까지 확인하세요.

## 우선 경로: Strata FN

### 이미 실행 중인 Strata에 연결

설치 도우미 기본값 **기존 Strata FN 연결**에서 실제 loopback URL(예: `http://127.0.0.1:8088`)을 명시하세요. 모델 ID를 비우면 명시적 준비 시 `/health`와 `/v1/models`로 모델·문맥 용량을 확인하여 `setup-state.json`에 저장합니다. 포트가 같아도 다른 서비스·다른 모델·변경된 유효 문맥이면 `Verify`의 `modelReady`는 false입니다. `loaded=false`는 접근 가능일 뿐 준비 완료가 아닙니다. 외부 서버 시작·종료 버튼은 비활성화되며 원래 서버와 설정은 변경하지 않습니다.

승인된 PowerShell 환경의 예시입니다. 주소는 실제 서버에 맞춰 선택하세요.

```powershell
$source = Join-Path $env:USERPROFILE 'Apps\QwenStatus-source'
$app = Join-Path $env:USERPROFILE 'Apps\QwenStrata'
$setup = Join-Path $source 'setup\setup.ps1'
& $setup -Action Plan -InstallRoot $app -Backend existing -ExistingBackend strata -UpstreamUrl http://127.0.0.1:8088 -Json
& $setup -Action Install -InstallRoot $app -Backend existing -ExistingBackend strata -UpstreamUrl http://127.0.0.1:8088
# 앱 연결 변경은 위 Install에 -ConfigureApp을 명시한 경우에만 백업 후 적용
# 대기열 시작을 따로 선택한 경우에만 실행 (모델 서버는 시작하지 않음)
& (Join-Path $app 'setup\setup.ps1') -Action StartGateway -InstallRoot $app
& (Join-Path $app 'setup\setup.ps1') -Action Verify -InstallRoot $app
```

### Strata FN 네이티브 준비 (선행 설치 필요)

이 경로는 **새 PC 전체 자동 설치가 아닙니다.** 다음을 별도로 준비한 뒤 `Strata FN · 네이티브 준비`를 선택하세요. Docker/WSL은 필요 없습니다.

- [고정 소스·모델 정보](setup/backends.json)의 Strata 0.2.3.1 커밋 원본과 Git. 추적 소스가 변경되어 있으면 거절합니다.
- CUDA 13 의존성과 `numpy`, `jinja2`가 설치된 Python 3.10+ 전용 가상환경의 `Scripts\python.exe`. 실행 정책·보안 설정은 변경하지 않습니다.
- 공식 Strata 엔진 **0.1.48 CUDA 13** 및 `BUILD.json`. 입력하는 SHA256은 별도로 검증한 **실제 strata.exe 파일**의 해시이며 압축파일 해시와 다릅니다. 자동 엔진 취득·latest feed fallback은 없습니다.
- 고정 리비전 SC117의 선택한 IQ3_XXS 또는 IQ2_XS 첫 GGUF와 공통 두 번째 GGUF, 이 가중치에서 준비한 pack(`native_experts.txt`)·tokenizer(`vocab.json`, `merges.txt`)·MTP 디렉터리와 JSON 설정. 일반 모델 pack을 SC117 이름으로 바꾸어 사용하지 마세요. 공개 SC117 가중치에는 HF gated 토큰을 요구하지 않습니다.
- 예시 준비 검사 기준 RAM 48GiB 이상, VRAM 23,000MiB 이상 NVIDIA GPU, 명시적 GPU index, 추가 여유 4GiB. 모델 자체는 IQ3 약 47.3+28.8GB, IQ2 약 39.7+28.8GB이며 pack·MTP·가상환경 등의 공간은 별도입니다. 4GiB는 모델 다운로드 공간이 아닙니다.

```powershell
# 예시 경로/해시/GPU는 본인이 검증한 실제 값으로 대체하세요.
$native = @{
    InstallRoot = (Join-Path $env:USERPROFILE 'Apps\QwenStrataNative')
    Backend = 'strata'; StrataProfile = 'IQ3_XXS'
    StrataSource = 'C:\AI\Strata'
    StrataPython = 'C:\AI\StrataEnv\Scripts\python.exe'
    StrataConfig = 'C:\AI\PreparedSC117\strata-sc117.json'
    EngineSha256 = '<검증한 strata.exe SHA256 64자리>'
    GpuIndex = 0; ContextTokens = 262144; PoolWorkers = 10; ResidentBudgetGiB = 26; Prefill = 'auto'
}
& $setup -Action Plan @native -Json
& $setup -Action Install @native
# 시작하기로 따로 결정한 경우에만 (관리 포트 18021, 대기열 18022)
& (Join-Path $native.InstallRoot 'setup\setup.ps1') -Action StartBackend -InstallRoot $native.InstallRoot -Backend strata
```

IQ2 예시는 `StrataProfile='IQ2_XS'`, `Prefill='16384'`입니다. 두 예시 모두 int8 KV, KV resident 최대 32768, spec 4/min-p 0.5, VRAM reserve 1280MiB, draft vocab cjk를 사용합니다. **측정 예시이지 보편 최적값·속도 보장이 아닙니다.** 문맥(8192~262144)·workers·RAM 예산·prefill은 메모리와 작업에 맞춰 조정하세요. 근사 route skipping·오버클럭은 켜지 않습니다.

Install은 GGUF와 준비 산출물을 해시 검사하므로 오래 걸릴 수 있으며 모델을 시작하지 않습니다. 같은 선택의 명시적 Install 재실행이 전체 재검증입니다. Verify는 기록·서비스·모델·유효 문맥을 확인하고 대용량 pack/MTP를 다시 해시하지 않습니다. 새 Start는 Python·엔진·entry·관리 설정의 해시를 검사하며 `--engine strata --host 127.0.0.1 --port 18021 --config ... --gpu ...`로 시작합니다. Stop은 소스 파일 변경으로 막히지 않도록 기록된 PID·생성 시각·실행 파일·config·설치 경로와 유휴 상태를 확인해 소유 프로세스만 종료합니다. 기존 외부 서버나 포트 충돌 프로세스를 임의 종료하지 않습니다.

이 게이트웨이의 Strata 경로는 **텍스트·도구 호출 전용**입니다. upstream이 images를 광고해도 `/capabilities`는 `vision_ready=false`이며 이미지 입력을 거절합니다. `/tokenize`가 없는 고정 소스에서는 `/v1/messages/count_tokens`로 텍스트·도구 메시지를 변환해 계산합니다. 누락된 속도·KV 지표는 0으로 만들지 않고 미제공으로 표시합니다. 벤치마크는 실제 문맥에 들어가는 프로필만 제공하며 실행은 별도 선택입니다.

## 1. 준비물과 선택

| 구성 | 준비물 |
| --- | --- |
| 상태 앱 실행 | Windows 10/11, Windows PowerShell 5.1, .NET Framework |
| 소스 받기 | [Git for Windows](https://git-scm.com/install/windows) |
| 설치 스크립트 | 승인된 PowerShell 실행 환경. 설치 도우미는 PowerShell 7 우선 사용 |
| 대기열·MCP worker | Node.js 22 이상. 없으면 공식 portable 배포본을 체크섬 확인 후 설치 폴더에 준비 |
| Strata 네이티브 준비 | 위 선행 설치·SC117 파일·RAM/VRAM 기준. Docker/WSL 불필요 |
| 레거시 새 GPU 서버 | 지원 NVIDIA GPU·드라이버, RAM 32GB 이상, Docker Desktop의 Linux 컨테이너·WSL2 GPU 환경, 충분한 Windows·Docker 저장 공간 |
| Hermes | 선택 사항. 전용 Python·Git Bash·Hermes core+messaging과 별도 홈을 설치기가 구성 |

Docker Desktop·WSL 설치, 라이선스 동의, Windows 기능 활성화와 재부팅, 드라이버 설치는 필요한 경우 사용자가 직접 완료합니다. [Docker의 Windows 설치 안내](https://docs.docker.com/desktop/setup/install/windows-install/)를 따르세요. 정책 차단 시 실행 정책·Smart App Control·Device Guard를 끄거나 우회하지 말고 승인된 실행 환경을 사용하세요.

상태 앱 자체에는 고성능 GPU가 필요하지 않습니다. 현재 모델 설치 프로필은 다음과 같습니다.

| 선택 | 현재 지원 범위 |
| --- | --- |
| Strata FN 기존 연결 (기본) | loopback URL의 health/models로 실제 모델·문맥 확인, 외부 서버 제어 안 함 |
| Strata FN 네이티브 준비 | 선행 설치된 SC117 IQ3/IQ2 검증·관리 설정 생성, 자동 다운로드 안 함 |
| 레거시 공통 구성·기존 서버 연결 | 새 모델 서버 없이 대기열·worker 준비 |
| vLLM·표준 | 24GB급 RTX 3090/4090/5090 계열, 64Ki 프로필 |
| ninfer·표준 | RTX 4090 Ada용 사전 변환 모델, RTX 5090 Blackwell용 별도 엔진·CPU 변환, 224Ki 프로필 |
| ninfer·무검열 | 24GB급 RTX 5090 계열 Blackwell 프로필. 본인 Hugging Face 접근 동의·인증 필요 |
| 지원하지 않는 조합 | vLLM+무검열, RTX 3090+ninfer. 다른 모델로 자동 대체하지 않음 |

모델 준비용 여유 공간은 프로필별 약 30~110GiB 이상이며 Docker의 별도 디스크에도 공간이 필요합니다. 64Ki/224Ki는 구성 목표로, 모든 PC의 속도·가용 용량을 보장하지 않습니다. 정확한 조건·고정 소스·모델 리비전은 [백엔드 안내](setup/backend-README.md)와 [backends.json](setup/backends.json)을 확인하세요.

## 2. 소스 내려받고 앱 열기

시작 메뉴에서 **Windows PowerShell**을 여세요. Git 설치 후에는 새 창을 엽니다. 보통 관리자 실행은 필요하지 않습니다. 아래 명령은 새 소스 폴더를 사용합니다. 기존 폴더가 있으면 삭제·덮어쓰기 없이 그 경로를 확인하세요.

```powershell
$source = Join-Path $env:USERPROFILE 'Apps\QwenStatus-source'
if (Test-Path -LiteralPath $source) { throw '소스 폴더가 이미 있습니다. 기존 경로를 확인하세요.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $source) -Force | Out-Null
git clone https://github.com/kea9997/qwen-status-tray.git $source
if ($LASTEXITCODE -ne 0) { throw '소스 다운로드 실패' }
powershell.exe -NoProfile -STA -File (Join-Path $source 'run-source.ps1')
```

`run-source.ps1`은 **Windows PowerShell 5.1과 .NET Framework**로 상태 앱 소스를 컴파일해 실행합니다. 설치 작업용 PowerShell 7과 역할이 다릅니다. 정책으로 막히면 오류를 확인하고 승인된 실행 경로를 마련해야 합니다. 우회 플래그를 추가하지 마세요.

시계 옆 Qwen 아이콘을 찾으세요. 보이지 않으면 숨겨진 아이콘 `^`를 엽니다. 서버가 없거나 꺼져 있으면 `꺼짐` 표시는 정상입니다. ZIP으로 받을 수도 있지만 `.git`이 없어 Git 기반 업데이트에 제약이 있습니다.

## 3-A. 앱에서 설치하기

1. 트레이 우클릭 → **설치 도우미 / AI 설치**를 엽니다.
2. 새 전용 설치 경로를 입력합니다. 예: `C:\Users\사용자이름\Apps\QwenLocal`. 기존 Hermes·모델 폴더를 대상으로 선택하지 마세요.
3. 백엔드·모델과 필요한 경우 `Hermes 함께 설치`를 선택합니다. 기존 Strata 서버를 유지할 때는 기본값 **기존 Strata FN 연결**을 유지하고 URL을 명시하세요. 기존 vLLM/ninfer는 해당 연결 항목을 고르세요. gated 모델은 본인이 접근 동의·인증을 마친 뒤 개인 HF 토큰 파일의 경로를 선택합니다. UI에 토큰 문자열을 붙이지 않습니다.
4. **설치 전 확인**으로 경로·다운로드·선행 조건을 확인합니다. 서버·설정을 변경하지 않는 단계입니다.
5. **선택 항목 준비**를 누릅니다. 창의 실행 로그·상태에서 진행 상황과 수동 조치를 확인하세요. 진행 중인 작업을 다른 창에서 중복 실행하지 마세요.
6. **연결 검증**을 누릅니다. 아직 서버를 켜지 않았다면 연결 실패와 설치 실패를 구분해야 합니다.

도우미의 **대기열 켜기/끄기**는 공통 대기열만 제어하며 모델을 시작하지 않습니다. 연결 검증은 필요한 연결이 준비되지 않았으면 종료 코드 2로 표시합니다. 해당 항목과 로그를 확인하세요.

**이 PC 상태 앱 연결 설정도 선택한 설치 폴더로 변경**은 기본 해제입니다. 현재 앱을 새 설치에 연결하려는 경우에만 선택하세요. 선택 시 기존 설정을 백업하고 새 경로를 반영하며, 해제하면 기존 연결을 유지합니다.

별도 폴더에 설치했다면 트레이에서 상태 앱만 종료한 뒤 그 폴더의 앱을 실행합니다. 서버나 다른 AI 프로그램을 종료할 필요는 없습니다.

```powershell
$app = Join-Path $env:USERPROFILE 'Apps\QwenLocal'
powershell.exe -NoProfile -STA -File (Join-Path $app 'run-source.ps1')
```

기존 설정이 없는 새 환경은 앱 재시작 후 설치 폴더의 runtime·Node 기본 경로를 사용합니다. 기존 설정이 있는 환경은 체크한 경우에만 변경합니다. 별도 연결이 필요하면 앱의 `연결 설정`에서 해당 항목만 적용하고, `%LOCALAPPDATA%\QwenStatus\settings.json`을 예제로 통째로 덮어쓰지 마세요.

## 3-B. 다른 AI에게 설치 맡기기

도우미의 **AI 설치 프롬프트 복사**를 누르거나 [AI-INSTALL-PROMPT.md](AI-INSTALL-PROMPT.md)를 복사합니다. 로컬 파일 읽기·명령 실행 도구가 있는 AI에게 소스 경로·설치 경로·GPU·원하는 구성을 함께 알려주세요. 비밀번호나 토큰은 붙이지 마세요.

일반 웹 채팅은 이 PC의 파일·`127.0.0.1`에 자동 연결되지 않습니다. 로컬 도구가 없는 AI는 실행 안내만 제공할 수 있습니다. 두 경로 모두 같은 `Plan → Install → Verify`를 사용합니다.

직접 진행하려면 설치용 PowerShell 7 창에서 다음 예시를 사용하세요. **공통 구성만 준비하는 예시**이며, 레거시 새 모델 서버가 필요하면 지원 조합을 확인한 뒤 `-Backend`를 `vllm`, `ninfer`, `both`로 바꿉니다. Strata는 위의 전용 예시를 사용합니다.

```powershell
$source = Join-Path $env:USERPROFILE 'Apps\QwenStatus-source'
$app = Join-Path $env:USERPROFILE 'Apps\QwenLocal'
$setup = Join-Path $source 'setup\setup.ps1'
& $setup -Action Plan -InstallRoot $app -Backend existing -Model standard -Json
& $setup -Action Install -InstallRoot $app -Backend existing -Model standard
& $setup -Action Verify -InstallRoot $app -Backend existing -Model standard
```

Hermes 선택 시 Plan·Install에 `-IncludeHermes`, 현재 앱 연결 변경을 선택한 경우에만 `-ConfigureApp`을 추가합니다. gated 모델은 개인 접근 동의·인증 후 `-HfTokenFile`에 본인 토큰 파일의 절대 경로를 전달합니다. 토큰 자체를 인자로 넣지 마세요. 인증·GPU 선택은 [백엔드 안내](setup/backend-README.md)를 따르며, 지원하지 않는 조합을 표준 모델로 바꾸지 않습니다.

## 4. 직접 시작하고 실제 응답 확인하기

설치 직후 서버·대기열·Telegram은 자동 시작되지 않습니다. 준비가 끝나면 앱의 서버 시작 동작 또는 아래 명령으로 명시적으로 시작합니다. 두 백엔드를 설치했어도 같은 GPU·포트에 동시에 시작하지 마세요.

```powershell
$app = Join-Path $env:USERPROFILE 'Apps\QwenLocal'
$setup = Join-Path $app 'setup\setup.ps1'
# 새 vLLM 표준 서버와 이 설치의 대기열을 시작하기로 선택한 경우
& $setup -Action StartBackend -InstallRoot $app -Backend vllm -Model standard
& $setup -Action Verify -InstallRoot $app -Backend vllm -Model standard
```

기존 ninfer를 연결할 때 Plan·Install에 `-ExistingBackend ninfer`를 지정합니다(기본 vllm). 설치 폴더의 기존 `backend.txt`는 자동으로 덮어쓰지 않습니다.

기존 서버는 재시작하지 않습니다. 필요할 때 `-Action StartGateway -Backend existing`으로 대기열만 시작하세요. `18022`가 사용 중이면 기존 프로세스를 임의 종료하지 않고 어느 대기열을 사용할지 결정해야 합니다.

| 확인 대상 | 성공 근거 |
| --- | --- |
| 모델 | 선택한 upstream의 `/health` 준비 완료 및 `/v1/models`의 저장된 모델 일치. Strata는 `service=strata`, `loaded=true`, 실제 유효 문맥도 일치; 레거시는 `qwen3.8-27b` |
| 대기열 | `18022/health` 준비 완료, `/queue` 응답 |
| 직접 대화 | 짧은 질문·후속 질문 완료, 요청 기록·사용량 갱신 |
| MCP 위임 선택 시 | 대상 AI에 worker 등록 후 작은 작업 제출·결과 회수 성공 |
| Hermes 선택 시 | 전용 설치 검증과 사용자의 첫 실행 후 로컬 모델 응답 |
| 성능 확인 선택 시 | 유휴 서버의 `quick` 토큰 테스트와 저장된 보고서 |

검증 종료 코드만 보지 말고 항목별 값과 실제 요청 결과를 확인하세요. 모델 로딩 중에는 원래 프로세스와 로그를 확인하며 기다립니다. 컨테이너 시작 성공은 GPU 추론 성공의 증거가 아닙니다.

일반 AI 호출 주소는 **`http://127.0.0.1:18022/v1`**입니다. `18021/v1` 직접 호출은 공통 대기열을 우회합니다. 직접 대화용 `ui-token.txt`는 대기열 첫 실행 시 새로 생성되며 배포물에 미리 넣지 않습니다. 실행·MCP 등록은 [runtime/README.md](runtime/README.md), Hermes·Telegram 수동 설정은 [setup/HERMES.md](setup/HERMES.md)를 참고하세요.

## 5. 오류와 보관

| 증상 | 먼저 확인할 것 |
| --- | --- |
| 스크립트·서명 정책 차단 | 오류와 조직 정책. 보안 기능 우회 없이 승인된 실행 환경 사용 |
| Docker/GPU 검사 실패 | Linux 엔진·WSL2 GPU·드라이버·지원 GPU·Windows와 Docker 여유 공간 |
| 모델 접근 거부 | 본인 Hugging Face 접근 동의·인증. 타인의 토큰을 복사하지 않음 |
| 포트 충돌 | 기존 서버·대기열 소유자. 임의 kill·재시작 금지 |
| Hermes 재설치 거부 | 기존 `hermes`를 덮어쓰지 않음. Verify·설치 기록 확인 후 새 경로 선택 |
| 위임 도구가 AI에 없음 | worker 설치와 해당 AI의 MCP 등록은 별도. 앱의 모드 변경만으로 등록되지 않음 |

설치 기록은 `setup-state.json`, 로그는 설치 도우미·`setup-logs`, Hermes 기록은 `hermes\install-result.json`·`hermes\install.log`에서 확인합니다. 실패한 단계만 확인해 재시도하고 진행 중인 작업이나 전체 설치를 무조건 다시 실행하지 마세요.

재배포에 개인 설정·대화·UI 토큰·Hermes 프로필·인증 캐시·모델·실행 로그를 포함하지 마세요. [GitHub 이슈](https://github.com/kea9997/qwen-status-tray/issues)에는 GPU/Windows 정보와 민감정보를 가린 오류만 공유하세요.

로컬 처리량은 유료 AI 절약량과 다릅니다. 설치를 돕는 AI·위임 판단·검토·외부 서비스에 별도 사용량이 발생할 수 있으며 **OpenAI 사용량 0을 보장하지 않습니다.**

검증 기록(2026-09-24): 별도 임시 폴더의 공통 구성 설치, 기존 Node 재사용, 기존 설정 보존, 미완료 연결의 종료 코드 2 및 앱 검사 7종을 통과했습니다. 모델 이미지 빌드·가중치 다운로드·CPU 변환·GPU 추론 설치 경로는 이번 검사에서 실행하지 않았습니다.

## 6. 다시 열기와 업데이트

설치 폴더의 `run-source.ps1`을 Windows PowerShell 5.1에서 실행하면 상태 앱을 다시 엽니다. 바탕화면 바로가기는 대상에 `powershell.exe -NoProfile -STA -File "설치폴더\run-source.ps1"`을 지정하세요. 서버와 대기열은 상태 앱 종료와 별도로 유지됩니다. Windows 시작 프로그램 등록은 자동으로 하지 않습니다.

소스 폴더에서 변경 내용을 확인한 뒤 `git pull --ff-only`로 최신 소스를 받습니다. 별도 설치 폴더는 **최신 소스 폴더의** `update-source.ps1 -Mode Install -Target <설치폴더> -Repository <Git소스폴더>`로 업데이트합니다. 이전 설치기에는 새 파일 목록이 없을 수 있으므로 최초 통합 설치 업데이트는 이 경로를 사용하세요. 이후 앱의 `분석 센터 → 업데이트`를 사용할 수 있습니다. 앱은 최신 소스가 적용된 뒤 재시작해야 하며 모델 서버는 자동 재시작하지 않습니다.
