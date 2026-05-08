using BibClientService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "BibClientWatchdog");
builder.Services.AddHostedService<BibClientWorker>();

var host = builder.Build();
host.Run();
