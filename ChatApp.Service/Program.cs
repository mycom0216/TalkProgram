using ChatApp.Service;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "ChatApp Server");
builder.Services.AddHostedService<ServerSupervisor>();
await builder.Build().RunAsync();
