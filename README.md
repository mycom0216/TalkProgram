# ChatApp — C# 멀티스레드 채팅 프로그램

C# / .NET 10 TCP 소켓 기반 다중 채팅 프로그램. 서버는 클라이언트 1명당 수신 스레드 1개를 두어 동시 채팅을 처리한다.

## 구조

```
ChatApp.slnx
├─ ChatApp.Common/         공통 (패킷 정의, 직렬화, 송수신 도우미)
├─ ChatApp.Server/         서버 (Accept 스레드, 세션별 수신 스레드, 로그 스레드)
└─ ChatApp.ConsoleClient/  1단계 검증용 콘솔 클라이언트
```

스레드 구성(서버):

| 스레드 | 개수 | 역할 |
| --- | --- | --- |
| Main | 1 | 관리자 콘솔 입력 (/users, /notice, /quit) |
| Accept | 1 | 접속 대기, 접속마다 ClientSession 생성 |
| Session-N | 접속자 수 | 패킷 수신 → PacketHandler |
| Logger | 1 | 로그 큐 → 콘솔/파일 |

## 실행

Visual Studio 2026에서 `ChatApp.slnx` 열기 → 솔루션 속성 → 여러 시작 프로젝트로 Server와 ConsoleClient 지정.

또는 터미널에서:

```bash
dotnet run --project ChatApp.Server              # 기본 포트 9000
dotnet run --project ChatApp.ConsoleClient       # 창을 2~3개 띄워서 테스트
```

클라이언트 명령어: 그냥 입력 = 전체 채팅, `/w 닉네임 내용` = 귓속말, `/quit` = 종료

## 진행 상황

- [x] 1단계: 설계, 패킷 정의, 솔루션 생성
- [x] 2단계: 핵심 통신 (멀티스레드 서버, 전체 채팅, 귓속말, 접속자 목록, 입·퇴장 알림, 로그)
- [ ] 3단계: WinForms 클라이언트
- [ ] 4단계: 회원가입/로그인(DB), 채팅방
- [ ] 5단계: 파일 전송, 하트비트
- [ ] 6단계: 테스트, 문서화
