using ChatApp.Service;

// ─────────────────────────────────────────────────────────────────────────────
// [파일 요약] ChatApp.Service 진입점 - 사내톡 서버를 Windows 서비스로 돌리는 호스트
// - 등록·해제: 배포 폴더의 install-server-service.bat / uninstall-server-service.bat (관리자 권한)
// - 서비스 이름(SCM): "ChatApp Server", 표시 이름: "사내톡 서버"
// - 콘솔에서 바로 실행해도 동작한다(서비스가 아닐 때는 일반 콘솔 앱처럼 Ctrl+C로 종료).
// ─────────────────────────────────────────────────────────────────────────────

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "ChatApp Server");
// 서버가 /quit으로 정상 종료하는 데 최대 20초 + 여유
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services.AddHostedService<ServerSupervisor>();
await builder.Build().RunAsync();
