using System.Runtime;
using Lookback.Core.Bus;
using Lookback.Recorder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Keep gen-2 collections from blocking capture and audio threads.
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
// stdout is reserved for the LOOKBACK_READY handshake; logs go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.Configure<RecorderOptions>(builder.Configuration.GetSection("Recorder"));
builder.Services.AddSingleton<InProcessEventBus>();
builder.Services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<InProcessEventBus>());
builder.Services.AddHostedService<StreamHostService>();

await builder.Build().RunAsync().ConfigureAwait(false);
