using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Roster.Platform.Credentials;
using Roster.Runner;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();
builder.AddRosterPlatform();
builder.Services.Configure<RunnerOptions>(builder.Configuration.GetSection("Runner"));
builder.Services.AddHostedService<RunnerService>();

using IHost host = builder.Build();

// Fail at start, not in the middle of someone's assignment, if the vault key is missing: every model call with a
// stored credential needs it. The vault's constructor says what to set.
_ = host.Services.GetRequiredService<SecretVault>();

await host.RunAsync();
