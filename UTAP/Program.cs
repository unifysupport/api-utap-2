using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace UTAP
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateWebHostBuilder(args).Build().Run();
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args) =>
            new WebHostBuilder()
                .UseKestrel()
                .UseUrls("http://0.0.0.0:5000")
                .UseStartup<Startup>()
                .ConfigureAppConfiguration(config => config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true))
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddSingleton(sp => new SMSArbiter(sp.GetService<IOptionsMonitor<UTAPConfiguration>>(), sp.GetService<IHubContext<ChatHub>>()));
                    services.AddSingleton<IHostedService, SMSArbiter>(serviceProvider => serviceProvider.GetService<SMSArbiter>());
                    services.AddSingleton(sp => new ATSquirter(sp.GetService<IOptionsMonitor<UTAPConfiguration>>()));
                    services.AddSingleton<IHostedService, ATSquirter>(serviceProvider => serviceProvider.GetService<ATSquirter>());
                    services.Configure<UTAPConfiguration>(hostContext.Configuration.GetSection("UTAPConfiguration"));
                });
    }

    public class UTAPConfiguration
    {
        public string ATSquirterIP { get; set; }
        public string ConversationsPath { get; set; }
        public string UCCSConString { get; set; }
        public int UCCSReadInterval { get; set; }
        public string LogFilePath { get; set; }
        public string MinimumLogLevel { get; set; }
        public string[] HostURLs { get; set; }
        public int MessageReadLimit { get; set; }
        public int MessageRetryLimit { get; set; }
        public bool ReleaseDisconnectedSims { get; set; }
        public string SMSArbiterIP { get; set; }
    }
}
