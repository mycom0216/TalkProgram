using System.Net;
using System.Text;
using ChatApp.Server.Core;
using ChatApp.Server.Logging;

Console.OutputEncoding = Encoding.UTF8;

int port = args.Length > 0 && int.TryParse(args[0], out int p) ? p : 9000;

string logPath = Path.Combine(AppContext.BaseDirectory, "logs", $"server-{DateTime.Now:yyyyMMdd}.log");
using var log = new ServerLogger(logPath);

var server = new ChatServer(IPAddress.Any, port, log);
server.Start();

Console.WriteLine("명령어: /users (접속자 목록), /notice 내용 (공지), /quit (서버 종료)");

// 메인 스레드는 관리자 콘솔 입력만 담당한다. (Accept·수신·로그는 각자 다른 스레드)
while (true)
{
    string? line = Console.ReadLine();
    if (line is null || line == "/quit") break;

    if (line == "/users")
    {
        var names = server.Sessions.GetNicknames();
        Console.WriteLine($"접속 {server.Sessions.ConnectedCount}명 / 로그인 {names.Count}명: {string.Join(", ", names)}");
    }
    else if (line.StartsWith("/notice "))
    {
        server.Notice(line["/notice ".Length..]);
    }
}

server.Stop();
