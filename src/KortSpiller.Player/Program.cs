using KortSpiller.Player;

if (args.Length > 0 && args[0].Equals("auth", StringComparison.OrdinalIgnoreCase))
{
    var config = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile("appsettings.local.json", optional: true)
        .AddEnvironmentVariables("KORTSPILLER_")
        .Build();
    var spotify = config.GetSection("Spotify").Get<SpotifyOptions>() ?? new SpotifyOptions();
    return await SpotifyAuth.RunAsync(spotify, args.Contains("--manual"), CancellationToken.None);
}

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("KORTSPILLER_");

builder.Services.Configure<SpotifyOptions>(builder.Configuration.GetSection("Spotify"));
builder.Services.Configure<MappingOptions>(builder.Configuration.GetSection("Mapping"));
builder.Services.Configure<PlayerOptions>(builder.Configuration.GetSection("Player"));
builder.Services.Configure<VolumeOptions>(builder.Configuration.GetSection("Volume"));
builder.Services.Configure<PinOptions>("Button", builder.Configuration.GetSection("PauseButton"));
builder.Services.Configure<PinOptions>("Led", builder.Configuration.GetSection("StatusLed"));

builder.Services.AddSingleton(sp => new TokenStore(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SpotifyOptions>>().Value.TokenFile));
builder.Services.AddHttpClient<SpotifyClient>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<MappingSync>(sp,
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("mapping")));
builder.Services.AddSingleton<VolumeState>();
builder.Services.AddSingleton<StatusLed>();
builder.Services.AddSingleton<CardHandler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MappingSync>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<StatusLed>());
builder.Services.AddHostedService<VolumeKnob>();
builder.Services.AddHostedService<PauseButton>();
builder.Services.AddHostedService<ScannerService>();

await builder.Build().RunAsync();
return 0;
