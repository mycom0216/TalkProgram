# ChatApp — C# 멀티스레드 채팅 프로그램 "사내톡"

C# / .NET 10 TCP 소켓 기반 1:N 사내 메신저. 서버는 접속자마다 수신·송신 스레드를 따로 두어 동시 채팅을 처리한다.
WinForms 클라이언트, MariaDB, TLS 암호화, 파일 전송(최대 3GB), 관리자 기능·감사 로그를 갖췄다.

[![Build](https://github.com/mycom0216/ChatApp/actions/workflows/dotnet-desktop.yml/badge.svg)](https://github.com/mycom0216/ChatApp/actions/workflows/dotnet-desktop.yml)

## 팀

| 구분 | 내용 |
| --- | --- |
| 팀명 | 다같이채팅 |
| 팀장 | 박은정 |
| 팀원 | 반재영, 장선화 |

## 구조

```
ChatApp.slnx
├─ ChatApp.Common/         공통 (패킷 정의, 직렬화, 송수신 도우미)
├─ ChatApp.Server/         서버 (Accept 스레드, 세션별 수신 스레드, 로그 스레드)
├─ ChatApp.Client/         WinForms 클라이언트 "사내톡" (로그인, 친구 목록 창, 1:1·채팅방 대화창)
├─ ChatApp.ConsoleClient/  검증용 콘솔 클라이언트
├─ ChatApp.LoadTest/       부하 테스트 도구 (가상 사용자 N명)
├─ ChatApp.Tests/          단위·통합 테스트 (xUnit, DB 없이 실행)
├─ db/                     DB 스키마와 마이그레이션 스크립트
├─ docs/ARCHITECTURE.md    아키텍처 문서 (구성도·스레드·프로토콜·DB·보안)
├─ docs/SERVICE.md         서버 운영 가이드 (Windows 서비스 등록·업데이트·문제 해결)
└─ docs/TESTING.md         테스트 결과서
```

스레드 구성(서버):

| 스레드 | 개수 | 역할 |
| --- | --- | --- |
| Main | 1 | 관리자 콘솔 입력 (/users, /rooms, /notice, /approve, /suspend, /audit 등, /help) |
| Accept | 1 | 접속 대기, 접속마다 ClientSession 생성 |
| Session-N | 접속자 수 | TLS 핸드셰이크 → 패킷 수신 → PacketHandler (올리는 파일 조각도 여기서 임시 파일에 씀) |
| Send-N | 접속자 수 | 송신 큐 → 소켓. 일반 큐(채팅)를 파일 큐보다 먼저 보냄 |
| Download-N | 내려받는 파일 수 (1인당 최대 3) | 파일을 256KB씩 읽어 송신 큐(용량 제한)에 넣음 |
| Logger | 1 | 로그 큐 → 콘솔/파일 |
| Heartbeat | 1 | 5초마다 전체 연결 점검, 45초 동안 아무 패킷도 없으면 끊음 |
| DbWriter | 1 | 대화 저장(배치)·지난 대화 불러오기 (큐 순서대로) |
| AuditWriter | 1 | 감사 로그 큐 → DB audit_logs (최대 100건씩 묶어 저장) |

## 처음 준비 (MariaDB)

1. MariaDB에 root로 접속해 `C:\Users\LMS\Downloads\ChatApp\ChatApp\db\schema.sql` 전체를 실행한다. (파일 안의 `여기에_DB_비밀번호`를 먼저 바꾼다)
2. `ChatApp.Server/appsettings.example.json`을 복사해 같은 폴더에 `appsettings.json`을 만든다.
3. `appsettings.json`의 `ConnectionString` 비밀번호와 `AllowedEmailDomains`(가입 허용 이메일 도메인 목록, ["*"]면 제한 없음)을 수정한다.
   - `appsettings.json`은 `.gitignore`에 있어 GitHub에 올라가지 않는다.
4. 이미 DB를 만들었다면 추가 스크립트를 순서대로 한 번씩 실행한다. cmd 어느 폴더에서 실행해도 된다.

```
"C:\Program Files\MariaDB 13.0\bin\mysql.exe" -u root -p chatapp < "C:\Users\LMS\Downloads\ChatApp\ChatApp\db\migrate_002_files.sql"
"C:\Program Files\MariaDB 13.0\bin\mysql.exe" -u root -p chatapp < "C:\Users\LMS\Downloads\ChatApp\ChatApp\db\migrate_003_status.sql"
"C:\Program Files\MariaDB 13.0\bin\mysql.exe" -u root -p chatapp < "C:\Users\LMS\Downloads\ChatApp\ChatApp\db\migrate_004_admin.sql"
"C:\Program Files\MariaDB 13.0\bin\mysql.exe" -u root -p chatapp < "C:\Users\LMS\Downloads\ChatApp\ChatApp\db\migrate_005_rooms_messages.sql"
```

   - 002 = 파일 전송, 003 = 상태 메시지, 004 = 관리자 기능, 005 = 영구·비공개 방, 답장·공감·삭제, 읽음 표시, 검색 인덱스. 이미 실행한 것을 다시 실행해도 문제없다(IF NOT EXISTS).
   - 서버는 시작할 때 DB 버전(schema_version)을 확인하고, 005가 빠져 있으면 실행할 명령을 알려 주고 켜지지 않는다.
5. 백업: `db\backup.bat`을 작업 스케줄러에 매일 등록한다 (사용법은 파일 안 주석).

## TLS 암호화 (기본 켜짐)

모든 통신(로그인 비밀번호, 대화, 파일)은 TLS 1.2/1.3으로 암호화된다.

- **서버**: 처음 실행할 때 `certs/server.pfx`(자체 서명 인증서, RSA 2048, 5년)를 만들고 **SHA-256 지문**을 콘솔에 출력한다. 서버 콘솔에서 `/tls`를 입력하면 다시 볼 수 있다.
  - 회사에 공인/사내 CA 인증서가 있으면 `appsettings.json`의 `Tls.CertificatePath`, `Tls.CertificatePassword`에 넣는다. 이 경우 클라이언트에 확인 창이 뜨지 않는다.
  - `certs/`와 `*.pfx`는 개인 키가 들어 있어 `.gitignore`에 있다. 절대 올리지 않는다.
  - 평문으로 테스트하려면 `"Tls": { "Enabled": false }`로 둔다. 이 경우 서버가 경고를 출력한다.
- **클라이언트**: 처음 접속하면 서버 인증서 지문을 보여 주고 신뢰할지 묻는다(TOFU, SSH의 known_hosts와 같은 방식). 신뢰한 지문은 `%LocalAppData%\ChatApp\client.json`에 저장된다.
  - 다음 접속부터는 자동으로 비교한다. 지문이 다르면(서버 인증서 교체 또는 중간자 공격) 경고 창을 띄우고, 사용자가 확인하기 전에는 연결하지 않는다.
  - 자동 재접속 중에 지문이 바뀌면 재시도를 멈추고 알린다.
  - 로그인 화면 → 서버 설정 → "보안 연결(TLS) 사용" 체크로 끌 수 있다(평문 서버용).
- 콘솔 클라이언트는 `--plain`으로 평문 연결을 쓴다. 부하 테스트는 기본 TLS로 접속하며 인증서를 확인하지 않는다(시험 도구라서). 평문으로 돌리려면 `--plain`.

확인 결과: 중계 프로그램으로 오가는 바이트를 그대로 저장해 비교했다. 평문 모드에서는 비밀번호와 대화 내용이 그대로 보였고, TLS 모드에서는 둘 다 한 번도 보이지 않았다.

## 관리자 기능

첫 관리자는 **서버 콘솔**에서 지정한다: `/grant 사원번호`. 관리자가 한 명도 없으면 서버가 시작할 때 안내가 나온다.
관리자는 메인 창 위쪽의 내 이름을 누르고 **관리자 메뉴...** 를 연다. 같은 일을 서버 콘솔 명령으로도 할 수 있다(`/help`).

| 기능 | 관리자 창 | 서버 콘솔 |
| --- | --- | --- |
| 가입 승인·거절 (`AutoApprove: false`일 때) | 가입 승인 / 가입 거절 | `/pending`, `/approve`, `/reject` |
| 정지·퇴사 처리 (접속 중이면 바로 끊음, 사유 표시) | 정지 / 퇴사 처리 / 사용 재개 | `/suspend 번호 사유`, `/resign`, `/activate` |
| 로그인 5회 실패 잠금 풀기 | 잠금 해제 | `/unlock` |
| 비밀번호 초기화 (임시 비밀번호, 다음 로그인 때 변경 강제) | 비밀번호 초기화 | `/resetpw` |
| 강제 로그아웃 | 강제 로그아웃 | `/kick 번호 사유` |
| 관리자 지정·해제 | 관리자 지정 / 해제 | `/grant`, `/revoke` |
| 감사 로그 검색 | 감사 로그 탭 | `/audit [개수] [사원번호]` |

안전장치는 이렇다.

- 관리 요청마다 서버가 권한을 다시 확인한다. 관리자가 아닌 사람의 시도는 거절하고 감사 로그에 `ADMIN_DENIED`로 남긴다.
- 자기 자신은 정지·퇴사·해제·강제 로그아웃할 수 없다. 마지막 관리자도 정지·퇴사·해제할 수 없다.
- 임시 비밀번호로 로그인하면 비밀번호를 바꾸기 전까지 서버가 채팅·방 입장을 막는다. 임시 비밀번호 자체는 어떤 로그에도 남기지 않는다.

감사 로그(`audit_logs`)에 남는 기록: 로그인 성공·실패(IP 포함), 로그아웃, 회원가입, 비밀번호 변경, 파일 보냄·받음, 방 생성, 공지, 모든 관리 동작.
기록은 전용 `AuditWriter` 스레드가 묶어서 저장하므로, DB가 잠깐 느려져도 로그인·채팅이 같이 느려지지 않는다.

## 실행

Visual Studio 2026에서 `ChatApp.slnx` 열기 → 솔루션 속성 → 여러 시작 프로젝트로 Server와 Client 지정.

또는 터미널에서:

```bash
dotnet run --project ChatApp.Server              # 기본 포트 9000
dotnet run --project ChatApp.ConsoleClient       # 창을 2~3개 띄워서 테스트
```

클라이언트: 시작하면 `1) 로그인 2) 회원가입` 메뉴가 나온다. 로그인 후 그냥 입력 = 전체 채팅, `/w 사원번호 내용` = 귓속말, `/quit` = 종료
콘솔 파일 명령: `/file 경로` (방에 파일), `/dm 사원번호 경로` (1:1 파일), `/get 파일ID [저장경로]` (받기)

## 배포 (설치 파일)

솔루션 폴더에서 `publish.bat`을 실행하면 `publish` 폴더가 만들어진다.

- `publish\client` — 직원 PC용 단일 exe (.NET을 따로 설치하지 않아도 실행된다)
- `publish\server` — 서버 PC에 복사할 서버 프로그램 + DB 스크립트 + 설정 예시
- `publish\사내톡-설치-버전.exe` — [Inno Setup 6](https://jrsoftware.org/isinfo.php)이 설치되어 있으면 함께 만들어진다. 관리자 권한 없이 설치되고, 바로 가기와 제거 항목이 생긴다 (`installer\ChatApp.iss`)

새 버전을 낼 때는 `ChatApp.Client.csproj`의 `<Version>`을 올리고, 서버 `appsettings.json`의 `ClientLatestVersion`(같은 값)과 `ClientUpdateUrl`(설치 파일 위치)을 적는다. 옛 버전 사용자에게 "새 버전" 안내가 뜨고, 메뉴에서 바로 설치 파일을 열 수 있다.

## 테스트

```bash
dotnet test ChatApp.Tests      # 또는 Visual Studio: 테스트 → 모든 테스트 실행
```

- 단위·통합 테스트 82개를 돈다. 패킷 직렬화, 패킷 경계(TCP 분할 수신), 파일 이름 정리, 비밀번호 해시, 설정 파일, TLS 지문 고정, 실제 서버에 소켓 접속하는 테스트가 들어 있다.
- DB 없이 돌아서 GitHub Actions가 푸시마다 자동으로 실행한다(위 Build 배지).
- DB가 필요한 로그인·관리자 흐름은 실제 MariaDB로 시나리오 테스트를 했다. 결과와 화면 점검표는 [docs/TESTING.md](docs/TESTING.md)에 있다.

## 부하 테스트 (ChatApp.LoadTest)

가상 사용자 N명이 동시에 접속해 "전체 채팅"에서 일정 간격으로 메시지를 보내고, 모두가 모든 메시지를 빠짐없이·순서대로·얼마나 빨리 받는지 잰다.
서버가 같은 PC에서 돌고 있으면 서버 메모리·스레드·CPU도 잰다. 결과는 `loadtest-날짜-N명.txt`로 저장된다.

```bash
dotnet run --project ChatApp.LoadTest -c Release -- 127.0.0.1 9000 300 20 2000   # 주소 포트 사용자수 보내는시간(초) 메시지간격(ms)
```

- 가상 사용자 계정(LT0001…)은 처음 실행 때 자동 가입된다(서버 `AllowedEmailDomains`가 `["*"]`여야 함).
- 끝난 뒤 정리: `DELETE FROM messages WHERE sender_no LIKE 'LT%'; DELETE FROM users WHERE employee_no LIKE 'LT%';`

측정 결과 (2코어 8GB 리눅스 VM, 서버·테스트 도구·MariaDB가 한 대에서 같이 실행, 모두 같은 방):

| 사용자 | 전송 | 서버가 전달한 패킷 | 유실 | 순서 어긋남 | 지연 중앙값 / 99% | 서버 메모리 | 로그인 전체 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 100명 | 50개/초 | 5,000개/초 | 0 | 0 | 4ms / 15ms | 124MB | 5.0초 |
| 300명 | 148개/초 | 42,177개/초 | 0 | 0 | 129ms / 515ms | 178MB | 15.4초 |
| 500명 | 99개/초 | 46,771개/초 | 0 | 0 | 276ms / 867ms | 287MB | 31.9초 |
| 300명 **TLS** | 149개/초 | 43,202개/초 | 0 | 0 | 178ms / 567ms | 232MB | 18.8초 |

TLS를 켜면 300명 기준 지연 중앙값이 129ms에서 178ms로, 메모리가 178MB에서 232MB로 늘었다. 테스트 도구 300명분의 암호화도 같은 2코어에서 돌아간 결과라서, 실제 서버에서는 차이가 더 작다.

부하 테스트로 찾아 고친 병목 (300명 기준 개선 전 → 후):

1. 같은 방 메시지를 받는 사람마다 JSON으로 다시 바꾸던 것 → 한 번만 바꿔 함께 사용(`Packet.ToWireBytes`)
2. 로그인할 때마다 전체 접속자 목록을 모두에게 즉시 방송(N³) → 200ms마다 묶어서 1번(`PacketHandler.FlushLists`)
   - 지연 중앙값 458ms → 129ms, 99% 3,034ms → 515ms, 서버 메모리 718MB → 178MB, 로그인 전체 27.7초 → 15.4초

## 진행 상황

- [x] 1단계: 설계, 패킷 정의, 솔루션 생성
- [x] 2단계: 핵심 통신 (멀티스레드 서버, 전체 채팅, 귓속말, 접속자 목록, 입·퇴장 알림, 로그)
- [x] 3단계: WinForms 클라이언트 (로그인·회원가입 화면, 부서별 접속자 트리, 더블클릭 귓속말, 연결 끊김 표시)
- [x] 4단계 일부: 사원번호 회원가입/로그인(MariaDB), 사내 이메일 도메인 검증, 비밀번호 해시, 로그인 5회 실패 잠금, 부서별 접속자 목록
- [x] 4단계 채팅방: 기본 방 "전체 채팅", 방 만들기(최대 인원)·입장·나가기, 빈 방 자동 삭제, 방 단위 채팅
- [x] 사내 이메일 도메인 여러 개 허용(설정 파일), 회원가입 화면에서 도메인 선택
- [x] 메신저 스타일 UI: 친구 목록 창 + 따로 뜨는 1:1·채팅방 대화창, 말풍선, 안 읽은 메시지 배지, 작업 표시줄 깜빡임
- [x] 대화 기록: DB 작업 스레드가 배치 저장, 방 입장·1:1 창 열 때 최근 50개 불러오기(파일 포함)
- [x] 5단계 파일 전송: 파일·사진·동영상(최대 3GB), 256KB 조각 전송(전송 중에도 채팅 가능), 사진 미리보기, 진행률, [+] 메뉴(사진/동영상/파일 따로 선택)·끌어다 놓기·Ctrl+V, 서버 보관(DB files + uploads 폴더), 방 멤버/1:1 당사자만 받기
- [x] 하트비트: 클라이언트 15초마다 Ping, 서버·클라이언트 모두 45초 무응답이면 끊김 처리
- [x] 자동 재접속: 2→4→8→16→30초 간격 재시도, 자동 재로그인, 참여 방 복구, 끊긴 동안의 대화 채우기. 같은 계정 새 로그인 시 이전 연결은 ForceLogout(재접속 안 함)
- [x] 파일 전송 취소: 보내는 중·받는 중 말풍선 클릭(또는 오른쪽 클릭 → 전송 취소). 서버 임시 파일 정리, 받기 재시도 시 요청 ID로 옛 조각 걸러냄
- [x] 상태 표시: 온라인·자리 비움·회의 중·방해 금지 + 상태 메시지, 10분 무입력 시 자동 자리 비움
- [x] 트레이 아이콘(X = 트레이로), 새 메시지 알림 창, @이름 멘션 강조·알림
- [x] 대화 검색 (Ctrl+F)
- [x] TLS 암호화: 서버 인증서 자동 생성, 클라이언트 지문 확인·고정(TOFU), 지문 변경 시 연결 거부, 핸드셰이크 10초 제한(세션 스레드에서 처리)
- [x] 관리자 기능: 가입 승인·거절, 정지·퇴사(즉시 끊김), 잠금 해제, 임시 비밀번호 발급과 변경 강제, 강제 로그아웃, 관리자 지정. 관리자 창과 서버 콘솔 명령
- [x] 감사 로그: 로그인·파일·관리 동작 기록(전용 기록 스레드), 관리자 창에서 검색
- [x] 비밀번호 변경 (내 이름 메뉴)
- [x] 6단계 테스트: xUnit 단위·통합 테스트 56개(CI 자동 실행), 관리자 시나리오 32개, TLS 검증, 부하 테스트(100·300·500명)
- [x] 6단계 문서화: README, 아키텍처 문서(docs/ARCHITECTURE.md), 테스트 결과서(docs/TESTING.md), 코드 주석, CI 빌드 배지
- [x] 서버 안정성: 연결 수·IP당 연결 수 제한, 로그인 제한 시간, 보내기 큐 용량(바이트) 제한, 프로토콜 버전 확인, 모르는 패킷 무시, 예외로 스레드가 죽지 않게 정리
- [x] 보안·운영: 로그인·가입 속도 제한(IP별), 로그인 시간 차 공격 방지, 보관 기간 자동 정리(대화 365일·파일 180일·감사 730일), DB 백업 스크립트, DB 버전 확인
- [x] 클라이언트 개선: 전용 송신 스레드(파일을 보내는 중에도 채팅이 바로 나감), 받은 파일에 "인터넷에서 받음" 표시, 위험 확장자 추가, 로그아웃, 창 자원(GDI) 누수 정리
- [x] 답장·공감(이모지 6가지)·메시지 삭제(24시간, 관리자는 모두), 읽음 숫자(카카오톡의 노란 숫자)
- [x] 영구 채팅방(서버를 다시 켜도 유지)·비공개 방과 초대, 멤버/접속 인원 표시
- [x] 접속하지 않은 사람에게 1:1 메시지·파일, 로그인 때 "안 읽은 1:1" 알림, 동료 찾기(오프라인 포함)
- [x] 전체 대화 검색(서버 DB, 내가 볼 수 있는 대화만), 결과에서 그 메시지로 이동
- [x] 배포: publish.bat, Inno Setup 설치 파일, 새 버전 안내
