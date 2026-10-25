# 다른 AI에게 전달할 Strata FN 설치 요청

로컬 파일·명령 실행 도구가 있는 AI에게 아래 요청문을 전달하세요. 일반 웹 채팅은 이 PC에 자동 연결되지 않습니다. 비밀번호·API 키·Telegram 토큰은 적지 마세요.

---

이 Windows PC에 Qwen Status와 선택한 **Strata FN** 구성을 준비해 주세요. 실제 설치 스크립트를 읽고 실행하며 계획만으로 완료했다고 하지 마세요. 기존 개인 환경은 보존해 주세요.

## 내 선택

- 저장소: `https://github.com/kea9997/qwen-status-tray.git`
- 기존 소스 절대 경로: `[있으면 입력, 없으면 새 사용자 폴더에 clone]`
- 새 전용 설치 폴더: `[예: C:\Users\내이름\Apps\QwenStrata]`
- 구성: `[기본: 기존 Strata FN 연결 / Strata FN 네이티브 준비]`
- 기존 연결 URL: `[예: http://127.0.0.1:8088, 실제 loopback origin 명시]`
- 실제 모델 ID: `[알면 입력, 모르면 Install에서 health/models로 확인]`
- 네이티브 선택 시: `[IQ3_XXS / IQ2_XS]`, `[고정 소스·전용 Python·준비된 SC117 JSON·검증한 strata.exe SHA256·GPU index]`
- GPU·VRAM·RAM: `[모르면 읽기 전용 확인]`
- 튜닝: `[예시 context 262144 / workers 10 / resident 26GiB / IQ3 prefill auto, IQ2 16384; 메모리와 작업에 맞게 조정]`
- Hermes 설치: `[예 / 아니요]`
- 현재 앱 연결도 새 경로로 변경: `[예 / 아니요, 기본 아니요]`
- 준비 후 서버 시작·짧은 실제 응답 검증: `[예 / 아니요, 지정 없으면 시작 의사 확인]`

선택이 불분명하면 설치 범위를 결정하는 데 필요한 정보만 질문해 주세요. 이 제품은 Strata만 지원합니다. 다른 엔진으로 대체하거나 이전 설치를 자동 변환하지 마세요.

## 읽을 파일·보존 원칙

먼저 `INSTALL.md`, `setup/setup.ps1`, `setup/strata-install.ps1`, `setup/strata-common.ps1`, `setup/backends.json`, `runtime/README.md`를 읽으세요. Hermes 선택 시 `setup/HERMES.md`와 `setup/hermes-install.ps1`도 확인하세요.

네이티브 준비는 **이미 설치된** 고정 Strata 0.2.3.1 소스·전용 Python/CUDA 의존성·공식 엔진 0.1.48·준비된 SC117 GGUF/pack/tokenizer/MTP를 검증하는 경로입니다. 엔진·모델 자동 다운로드나 새 GPU PC 전체 자동 설치가 아닙니다. 선행 조건이 없으면 남은 단계로 보고하고 임의 다운로드 관리기나 다른 설치 경로를 만들지 마세요. 모델 리비전·GGUF 해시는 manifest를 따르고 일반 pack의 이름만 SC117로 바꾸지 마세요. 프로필은 측정 예시이며 최적값·속도 보장이 아닙니다.

기존 저장소·설정·모델·프로필·로그·서버를 삭제·덮어쓰기·재설치하지 마세요. 다른 설치의 프로세스를 kill/restart/stop하지 마세요. 18021/18022 충돌은 소유자와 사용할 환경을 확인하세요. 기존 앱 설정 전체를 예제로 바꾸지 마세요. 사용자 연결 변경 선택이 있을 때만 백업 후 수정하세요.

드라이버·라이선스·재부팅·계정 로그인 등 사용자 단계는 대신 동의하지 마세요. 실행 정책·서명·Device Guard·Defender·Smart App Control을 끄거나 우회하지 마세요. 인증 파일 내용을 읽어 보고하거나 비밀번호·키·토큰을 출력·로그·저장소·배포물에 넣지 마세요. 인증은 사용자가 공식 서비스에 직접 입력하게 하세요.

설치와 시작은 별개입니다. 시작을 선택한 경우에만 해당 설치의 명시적 Start를 실행하세요. Telegram·원격 에이전트·서비스·로그인 자동 실행을 설정하지 마세요. 진행 중인 준비는 기존 프로세스·로그로 추적하고 중복 제출하지 마세요.

## 같은 설치기로 진행

상태 앱은 승인된 Windows PowerShell 5.1에서 `powershell.exe -NoProfile -STA -File <소스경로>\run-source.ps1` 또는 빌드한 EXE로 실행합니다. 설치 작업은 승인된 PowerShell 7을 우선 사용합니다. 정책 우회 플래그를 추가하지 마세요.

```powershell
$source = 'C:\Users\내이름\Apps\QwenStatus-source'
$app = 'C:\Users\내이름\Apps\QwenStrata'
$setup = Join-Path $source 'setup\setup.ps1'
& $setup -Action Plan -InstallRoot $app -Backend existing -UpstreamUrl http://127.0.0.1:8088 -Json
& $setup -Action Install -InstallRoot $app -Backend existing -UpstreamUrl http://127.0.0.1:8088
& $setup -Action Verify -InstallRoot $app
```

`-Backend`는 `existing|strata`, `-ExistingBackend`는 기본값인 `strata`만 허용합니다. Attach는 실제 loopback URL·선택형 `-ModelId`를 사용하며 모델·유효 문맥을 receipt에 저장하고 외부 서버는 제어하지 않습니다. 네이티브는 `-Backend strata -StrataProfile IQ3_XXS|IQ2_XS`와 `-StrataSource`, `-StrataPython`, `-StrataConfig`, `-EngineSha256`, `-GpuIndex`가 필요합니다. 세부 RAM/VRAM·공간·조정 범위는 INSTALL을 따르세요. 지원하지 않는 기존 backend 파일/receipt는 새 전용 폴더로 준비하고 원본은 그대로 두세요.

Hermes 선택 시 Plan·Install에 `-IncludeHermes`, 현재 앱 연결 변경을 선택했을 때만 `-ConfigureApp`을 붙입니다. Hermes는 receipt의 실제 Strata 모델을 전용 프로필에 사용합니다. 기존 AppData Hermes 계정·토큰·프로필을 읽거나 복사하지 마세요. 실행 시 전용 `QWEN_HERMES_PYTHON/ROOT/HOME/GIT_BASH`를 사용하며 inherited `HERMES_PROFILE/CONFIG/ENV`가 덮어쓰지 않게 합니다. 첫 실행 동의·Telegram은 사용자가 처리합니다.

Node.js 22가 없으면 공식 portable만 체크섬 확인 후 준비합니다. 모델 요청은 **`http://127.0.0.1:18022/v1`**로 보내세요. upstream 직접 호출로 공통 대기열 성공을 대신 증명하지 마세요. 게이트웨이는 텍스트·도구 호출 전용이며 이미지 입력을 거절합니다. 근사 route skipping·오버클럭은 켜지 않습니다.

## 실제 검증·보고

시작을 선택했다면 해당 설치의 `StartBackend`(네이티브) 또는 `StartGateway`(기존 연결의 대기열만)를 실행하세요. 기존 서버를 재시작하지 마세요. 시작을 선택하지 않았다면 실행·추론은 미실행으로 구분하세요.

1. Verify의 Node·gateway/worker 파일·모델·대기열·기록 폴더·UI 토큰 항목을 확인하세요. 토큰 내용은 출력하지 않습니다.
2. 모델 `/health`의 `service=strata`, `loaded=true`와 `/v1/models`의 실제 모델·저장한 유효 문맥을 확인합니다. unloaded·다른 서비스·다른 모델은 준비 완료가 아닙니다. 대기열 health/queue도 별도로 확인합니다.
3. 별도로 승인된 짧은 질문·후속 질문과 요청 기록·usage를 확인합니다. 로딩 중에는 원래 프로세스·로그로 추적합니다.
4. MCP 선택 시 실제 클라이언트 등록 후 작은 작업의 제출·회수, Hermes 선택 시 전용 Verify·사용자의 첫 실행 후 응답까지 구분합니다.
5. 성능 확인을 원한 경우에만 유휴 서버에서 quick을 실행합니다. 대형 문맥 검사를 임의로 시작하지 않습니다.

새 PC 엔진·모델 취득부터 GPU 응답까지의 종단 간 설치는 아직 보장되지 않습니다. 정적 검사·과거 성능을 현재 성공 근거로 쓰지 마세요. 마지막에는 한국어로 설치 위치·버전/프로필·변경 설정·실제 검증·남은 사용자 단계·로그/보고서 경로를 간단히 보고하세요. 실패·미실행을 숨기거나 성공한 단계를 불필요하게 반복하지 마세요. **OpenAI 사용량 0·유료 토큰 절약량을 보장하지 마세요.**
