# 다른 AI에게 전달할 설치 요청

아래 요청문을 로컬 파일·명령 실행 도구가 있는 AI에게 전달하세요. 경로와 원하는 구성을 적으면 됩니다. 비밀번호·API 키·Hugging Face/Telegram 토큰은 적지 마세요. 일반 웹 채팅은 이 PC에 자동 연결되지 않습니다.

---

이 Windows PC에 Qwen Status와 내가 선택한 로컬 AI 구성 요소를 설치해 주세요. 저장소의 실제 설치 스크립트를 읽고 실행하며 계획 설명만으로 완료했다고 하지 마세요. 기존 개인 환경은 보존해 주세요.

내 환경과 선택:

- 저장소: `https://github.com/kea9997/qwen-status-tray.git`
- 기존 소스의 절대 경로: `[있으면 입력, 없으면 새 사용자 폴더에 clone]`
- 새 설치 폴더: `[예: C:\Users\내이름\Apps\QwenLocal]`
- GPU·VRAM·RAM: `[모르면 읽기 전용으로 확인]`
- 백엔드: `[existing / vllm / ninfer / both]`
- 모델: `[standard / uncensored]`
- Hermes 설치: `[예 / 아니요]`
- 현재 상태 앱 연결도 새 경로로 변경: `[예 / 아니요, 기본 아니요]`
- 설치 후 서버 시작·짧은 실제 응답 검증: `[예 / 아니요, 지정 없으면 설치 후 시작 의사 확인]`

선택이 불분명하면 기존 환경을 먼저 확인하고, 설치 범위를 결정하는 데 필요한 정보만 질문해 주세요.

## 읽을 파일과 보존 원칙

먼저 `INSTALL.md`, `setup/setup.ps1`, `setup/backends.json`, `setup/backend-README.md`, `runtime/README.md`를 읽으세요. Hermes 선택 시 `setup/HERMES.md`와 `setup/hermes-install.ps1`도 확인하세요. 현재 스크립트에 없는 옵션이나 성공 결과를 추측하지 마세요. 선택한 경로·구성·다운로드와 필요한 수동 단계를 짧게 알려 주세요.

기존 저장소·설정·모델·프로필·로그·서버를 삭제·덮어쓰기·재설치하지 마세요. 다른 설치의 프로세스를 kill/restart/stop하지 마세요. `18021`/`18022`가 사용 중이면 소유자와 사용할 환경을 확인하세요. 기존 설정 파일 전체를 예제로 바꾸지 마세요.

사용자가 선택한 범위의 설치는 진행하되 Docker Desktop·WSL·Windows 기능·드라이버·라이선스 동의·재부팅·계정 로그인은 필요한 사용자 단계로 남겨 주세요. 동의를 대신 체크하거나 묵시적으로 수락하지 마세요. 실행 정책·서명·Device Guard·Defender·Smart App Control을 우회하거나 끄지 마세요.

개인 인증 파일의 내용을 읽어서 보고하지 마세요. 비밀번호·API 키·HF/Telegram 토큰을 대화·출력·공개 로그·저장소·배포 파일에 넣지 마세요. 인증은 내가 공식 서비스에 직접 입력하게 하고 준비 여부만 확인하세요. gated 모델의 개인 토큰 파일은 문서화된 경로로 사용하며 내용을 출력하거나 복사하지 마세요.

설치와 서버 시작은 별개입니다. 위에서 시작을 선택한 경우에만 해당 설치의 명시적 Start를 실행하세요. Telegram·원격 에이전트·서비스·로그인 자동 실행을 임의로 설정하지 마세요. 진행 중인 설치는 기존 프로세스·로그로 추적하고 다시 제출하지 마세요.

## 같은 설치기로 진행

1. 소스가 없으면 사용자 폴더의 새 경로에 Git으로 받으세요. 기존 경로는 삭제하거나 새 clone으로 덮어쓰지 마세요.
2. 상태 앱은 **Windows PowerShell 5.1**에서 `powershell.exe -NoProfile -STA -File <소스경로>\run-source.ps1`로 실행합니다. 트레이의 **설치 도우미 / AI 설치**가 같은 설치기를 사용합니다. 이는 .NET Framework 앱 실행이며 모델 서버 시작이 아닙니다.
3. 설치 작업은 승인된 **PowerShell 7을 우선 사용**하세요. 상태 앱의 실행 환경과 혼동하지 마세요. 정책 우회 플래그를 붙이지 말고 막힌 경우 필요한 공식 설치·승인 단계를 설명하세요.
4. `Plan → Install → Verify`를 같은 선택으로 진행하세요. 다음은 공통 구성만 설치하는 예시입니다. 내 선택에 맞게 바꾸되 실제 매개변수와 대조하세요.

```powershell
$source = 'C:\Users\내이름\Apps\QwenStatus-source'
$app = 'C:\Users\내이름\Apps\QwenLocal'
$setup = Join-Path $source 'setup\setup.ps1'
& $setup -Action Plan -InstallRoot $app -Backend existing -Model standard -Json
& $setup -Action Install -InstallRoot $app -Backend existing -Model standard
& $setup -Action Verify -InstallRoot $app -Backend existing -Model standard
```

`-Backend`는 `existing|vllm|ninfer|both`, `-Model`은 `standard|uncensored`입니다. `existing`으로 ninfer를 연결한다면 `-ExistingBackend ninfer`를 추가하세요(기본 vllm). Hermes 선택 시 Plan·Install에 `-IncludeHermes`, 현재 앱 연결 변경을 선택했을 때만 `-ConfigureApp`을 붙이세요. gated 모델은 본인의 접근 동의·인증 후 `-HfTokenFile`에 개인 토큰 파일의 절대 경로를 전달하며 토큰 문자열은 넣지 마세요. UI에서도 파일 경로만 선택합니다. 대상 설치 경로도 절대 경로입니다. 제공된 설치기를 임의의 외부 자동 설치 도구로 대체하지 마세요.

기존 앱 설정이 없는 새 환경은 설치 폴더의 runtime·portable Node 기본 경로를 사용합니다. 기존 설정이 있으면 연결 변경을 선택한 경우에만 백업 후 수정하고, 설치된 앱을 다시 열어 적용을 확인하세요.

구성별 확인 사항:

- 공통 대기열·MCP worker 소스는 `runtime`에 있습니다. Node.js 22 이상이 없으면 공식 portable 배포본을 체크섬 확인 후 설치 폴더에 준비합니다. 비공개 개인 worker나 별도 npm 의존성을 임의로 가져오지 마세요.
- 백엔드는 `backends.json`의 고정 소스 커밋·모델 리비전·이미지 digest를 사용합니다. vLLM 표준은 24GB급 RTX 3090/4090/5090 프로필입니다. ninfer 표준은 4090 Ada와 5090 Blackwell의 엔진·모델 준비 경로가 다르므로 산출물을 섞지 마세요.
- vLLM+무검열, RTX 3090+ninfer는 현재 지원하지 않습니다. ninfer+무검열은 지원되는 RTX 5090 Blackwell 경로와 본인 Hugging Face 접근 동의·인증이 필요합니다. 지원되지 않으면 이유를 보고하고 중단하세요. 표준 모델로 몰래 대체하거나 검사를 제거하지 마세요.
- GPU 선택·Blackwell 변환·gated 인증의 세부 옵션은 현재 `setup/backend-install.ps1`과 백엔드 문서를 확인하세요. Windows 모델 준비 공간과 Docker Linux 디스크 공간을 따로 확인하세요. 다운로드·CPU 변환·Docker 빌드는 실제 GPU 추론 검증과 다릅니다.
- Hermes는 `InstallRoot\hermes` 전용 Python·소스·`profiles\qwen` 홈·Git Bash를 사용합니다. 기존 AppData Hermes 프로필·계정·토큰을 읽거나 복사하지 마세요. `hermes\install-result.json`의 `environment.QWEN_HERMES_PYTHON/ROOT/HOME/GIT_BASH`를 사용하고 inherited `HERMES_PROFILE/CONFIG/ENV`가 전용 홈을 덮어쓰지 않게 하세요. Telegram과 첫 실행 동의는 내가 직접 처리합니다.
- 일반 모델 요청은 **`http://127.0.0.1:18022/v1`**을 사용하세요. `18021/v1` 직접 호출로 대기열 통합 성공을 대신 증명하지 마세요. 다른 PC나 웹서비스의 localhost는 이 PC가 아닙니다.

## 실제 검증과 완료 보고

내가 서버 시작을 선택했다면 설치 폴더의 `setup/setup.ps1 -Action StartBackend` 또는 필요한 `StartGateway`로 해당 설치만 시작하세요. 기존 서버는 재시작하지 마세요. 로딩 중에는 원래 프로세스·로그를 확인하며 기다리고 같은 GPU·포트에 두 백엔드를 동시에 올리지 마세요.

선택한 구성에 해당하는 검증을 실행하고 **실제 결과**를 남겨 주세요.

1. 통합 `Verify`의 Node·gateway/worker 소스·모델 health·queue health·기록 폴더·UI 토큰 존재 여부. 종료 코드 0만 보지 말고 항목별 값을 확인하고 토큰 내용은 출력하지 마세요.
2. 모델 `/health`, `/v1/models`의 `qwen3.8-27b`, 대기열 `/health`·`/queue` 응답.
3. 공통 대기열을 통한 짧은 질문·후속 질문 완료와 사용량·요청 기록. 가능하면 앱 직접 대화도 확인하되 원문이나 전체 로그를 공개할 필요는 없습니다.
4. MCP 위임 선택 시 대상 AI에 `runtime/worker/server.mjs`를 등록하고 작은 작업 제출·결과 회수. 등록만으로 도구 호출 성공을 주장하지 마세요.
5. Hermes 선택 시 전용 설치 Verify와 내 첫 실행 후 로컬 모델 연결. 계정 입력 등 사용자 단계는 완료될 때까지 미완료로 표시하세요.
6. 성능 확인을 원한 경우에만 유휴 서버에서 `quick` 토큰 테스트를 실행하고 보고서 경로를 남기세요. 대형 64Ki/224Ki 검사를 임의로 시작하지 마세요.

서버 시작을 선택하지 않았거나 사용자 단계가 남았다면 파일 준비까지 검증하고 실행·추론은 **미실행/미완료**로 구분하세요. 이 배포판의 새 PC GPU 설치는 아직 종단 간 검증되지 않았습니다. 정적 검사나 다른 PC의 과거 성능을 현재 성공 근거로 쓰지 마세요.

마지막에는 한국어로 설치 위치·버전/프로필·변경한 설정·실제 검증 결과·남은 사용자 단계·로그/보고서 경로를 간단히 알려 주세요. 실패를 숨기지 말고 성공한 단계는 불필요하게 다시 실행하지 마세요. **OpenAI 사용량 0이나 유료 토큰 절약량을 보장하지 마세요.** 로컬 처리량과 설치를 돕는 AI·위임 판단·검토의 사용량은 별개입니다.
