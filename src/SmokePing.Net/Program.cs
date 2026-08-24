using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Serilog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmokePing.Net.Alerting;
using SmokePing.Net.Configuration;
using SmokePing.Net.Probes;
using SmokePing.Net.Services;
using SmokePing.Net.Storage;
using SmokePing.Net.Web;

namespace SmokePing.Net;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = CommandLineOptions.Parse(args);

        if (options.ShowHelp)
        {
            Console.WriteLine(CommandLineOptions.HelpText);
            return 0;
        }

        LoadedConfiguration config;
        try
        {
            config = ConfigLoader.Load(options.ConfigPath, options.SkipUnsupported);
        }
        catch (ConfigurationException ex)
        {
            Console.Error.WriteLine($"Configuration error: {ex.Message}");

            if (!File.Exists(options.ConfigPath) && options.SearchedConfigPaths.Count > 1)
            {
                Console.Error.WriteLine("Looked in:");
                foreach (var candidate in options.SearchedConfigPaths)
                {
                    Console.Error.WriteLine($"  {candidate}");
                }
            }

            return 1;
        }

        var probeRegistry = BuildProbeRegistry(out var probeHttpClientFactory);
        using (probeHttpClientFactory)
        {
            if (!ValidateProbes(config, probeRegistry))
            {
                return 1;
            }

            if (options.CheckOnly)
            {
                Console.WriteLine($"Configuration '{config.ConfigFilePath}' is valid.");
                Console.WriteLine($"  targets:        {config.Targets.Count}");
                Console.WriteLine($"  alerts:         {config.Alerts.Count}");
                Console.WriteLine($"  data directory: {config.DataDirectory}");

                foreach (var skipped in config.SkippedTargets)
                {
                    Console.WriteLine($"  SKIPPED: {skipped}");
                }

                foreach (var warning in config.Warnings)
                {
                    Console.WriteLine($"  WARNING: {warning}");
                }

                // Resolving dynamic hosts here is the point of --check for them: it
                // shows what %gateway% actually found on this machine.
                // Silent: --check prints its own findings, in order.
                var resolver = new HostResolver(
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<HostResolver>.Instance,
                    TimeProvider.System);

                foreach (var target in config.Targets)
                {
                    var host = target.Host;
                    if (target.HasDynamicHost)
                    {
                        var resolved = resolver.Resolve(target.Host);
                        host = resolved is null
                            ? $"{target.Host} (COULD NOT BE RESOLVED)"
                            : $"{target.Host} -> {resolved}";
                    }

                    Console.WriteLine(
                        $"  - {target.Id} -> {host} " +
                        $"({target.ProbeType}, {target.Pings} pings / {target.StepSeconds}s)");
                }

                return 0;
            }
        }

        return await RunAsync(config, options).ConfigureAwait(false);
    }

    private static async Task<int> RunAsync(LoadedConfiguration config, CommandLineOptions options)
    {
        var builder = WebApplication.CreateBuilder();

        ConfigureLogging(builder, config, options);

        // Under the service control manager the process must answer the status
        // handshake and stop on the SCM's signal rather than on Ctrl+C. This is a
        // no-op when the process was started any other way.
        if (options.RunAsService)
        {
            builder.Services.AddWindowsService(service => service.ServiceName = options.ServiceName);
        }

        // A native SmokePing configuration has no equivalent of listenUrl - upstream
        // serves through a CGI - so the command line has to be able to set it.
        builder.WebHost.UseUrls(options.ListenUrl ?? config.Raw.General.ListenUrl);

        builder.Services.AddSmokePingServices(config, poll: !options.NoPolling);

        var app = builder.Build();

        app.UseStaticFiles();
        app.MapSmokePingApi();

        // An unknown API path is an error, not a page. This pattern is more specific
        // than the one below, so it wins for anything under /api.
        app.MapFallback("/api/{**path}", () => Results.NotFound(new { error = "Unknown endpoint." }));

        // Everything else serves the single page application. This also covers "/":
        // routing picks the fallback endpoint before the static file middleware runs,
        // so UseDefaultFiles alone would never get the chance to map "/" to index.html.
        app.MapFallbackToFile("index.html");

        var logger = app.Services.GetRequiredService<ILogger<PollingService>>();

        foreach (var skipped in config.SkippedTargets)
        {
            logger.LogWarning("Skipped target: {Reason}", skipped);
        }

        foreach (var warning in config.Warnings)
        {
            logger.LogWarning("{Warning}", warning);
        }

        logger.LogInformation(
            "{Site} listening on {Url}, data in {DataDirectory}.",
            config.Raw.General.SiteName,
            options.ListenUrl ?? config.Raw.General.ListenUrl,
            config.DataDirectory);

        try
        {
            try
            {
                await app.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException ex) when (FindSocketError(ex) is { } socketError)
            {
                // Almost always a second copy already running, or something else on
                // the port. A stack trace helps nobody diagnose that.
                Console.Error.WriteLine(
                    $"Cannot listen on {options.ListenUrl ?? config.Raw.General.ListenUrl}: {socketError.Message}");
                Console.Error.WriteLine(
                    "Another SmokePing.NET may already be running, or another program holds the port. " +
                    "Change general.listenUrl in the configuration, or stop the other program.");
                return 1;
            }

            await app.WaitForShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            app.Services.GetRequiredService<MeasurementStore>().Dispose();
        }
    }

    /// <summary>
    /// Console always, a rolling file when one is wanted, and the Windows Event Log
    /// under the service control manager - which is where an administrator looks.
    /// </summary>
    private static void ConfigureLogging(WebApplicationBuilder builder, LoadedConfiguration config, CommandLineOptions options)
    {
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u4}] {Message:lj}{NewLine}{Exception}");

        if (options.ResolveLogFile(config) is { } logFile)
        {
            logger = logger.WriteTo.File(
                logFile,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 8 * 1024 * 1024,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss zzz} [{Level:u4}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        }

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(logger.CreateLogger(), dispose: true);

        if (options.RunAsService && OperatingSystem.IsWindows())
        {
            AddEventLog(builder, options.ServiceName);
        }
    }

    /// <summary>
    /// Separated out because the platform guard has to be on the method: the analyser
    /// cannot see that a lambda passed from inside an OperatingSystem.IsWindows check
    /// only ever runs there.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddEventLog(WebApplicationBuilder builder, string sourceName) =>
        builder.Logging.AddEventLog(settings => settings.SourceName = sourceName);

    /// <summary>
    /// Digs the socket error out of the exception chain. Kestrel wraps it twice - in
    /// an AddressInUseException and then an IOException - so the inner exception is
    /// not the one that says what actually went wrong.
    /// </summary>
    private static SocketException? FindSocketError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketException)
            {
                return socketException;
            }
        }

        return null;
    }

    /// <summary>Builds a registry for the validation pass, before the host exists.</summary>
    private static ProbeRegistry BuildProbeRegistry(out SimpleHttpClientFactory httpClientFactory)
    {
        httpClientFactory = new SimpleHttpClientFactory();
        return new ProbeRegistry(
            [new IcmpProbe(), new TcpProbe(), new DnsProbe(), new HttpProbe(httpClientFactory)]);
    }

    private static bool ValidateProbes(LoadedConfiguration config, ProbeRegistry registry)
    {
        var valid = true;
        foreach (var target in config.Targets.Where(t => !registry.Contains(t.ProbeType)))
        {
            Console.Error.WriteLine(
                $"Configuration error: target '{target.Id}' uses unknown probe '{target.ProbeType}'. " +
                $"Available probes: {string.Join(", ", registry.Names)}.");
            valid = false;
        }

        return valid;
    }
}

/// <summary>Command line handling for the daemon.</summary>
public sealed class CommandLineOptions
{
    public const string HelpText = """
        SmokePing.NET - latency measurement and smoke graphs for Windows

        Usage: SmokePing.Net [options]

          --config <path>   Configuration file: JSON, or SmokePing's own format
          --listen <url>    Web interface address (default: http://localhost:8081)
          --check           Validate the configuration and exit
          --service         Run under the Windows service control manager
          --service-name    Service name to register as (default: SmokePingNet)
          --log-file <path> Write a log file (always on in service mode)
          --skip-unsupported Drop targets this version cannot measure instead of
                            refusing the whole file - useful when adopting an
                            existing SmokePing configuration
          --no-polling      Serve the web interface without taking measurements
          --help            Show this help

        The web interface address is taken from general.listenUrl in the configuration.
        Relative paths are resolved against the working directory first and then
        against the directory holding the executable, so a service started by the
        control manager still finds its configuration.
        """;

    public string ConfigPath { get; private init; } = Path.Combine("config", "smokeping.json");

    /// <summary>Every location the configuration was looked for, for the error message.</summary>
    public IReadOnlyList<string> SearchedConfigPaths { get; private init; } = [];

    public bool CheckOnly { get; private init; }

    public bool NoPolling { get; private init; }

    /// <summary>Drop targets that cannot be measured rather than refusing to start.</summary>
    public bool SkipUnsupported { get; private init; }

    /// <summary>Overrides the address the web interface binds to.</summary>
    public string? ListenUrl { get; private init; }

    public bool ShowHelp { get; private init; }

    public bool RunAsService { get; private init; }

    public string ServiceName { get; private init; } = "SmokePingNet";

    public string? LogFile { get; private init; }

    /// <summary>
    /// Where to write the log file, or null when logging to the console is enough.
    /// Service mode always logs to a file because there is no console to read.
    /// </summary>
    public string? ResolveLogFile(Configuration.LoadedConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (LogFile is not null)
        {
            return LogFile;
        }

        if (!RunAsService)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(config.ConfigFilePath) ?? AppContext.BaseDirectory;
        return Path.Combine(directory, "logs", "smokeping.log");
    }

    public static CommandLineOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var configPath = Path.Combine("config", "smokeping.json");
        var checkOnly = false;
        var noPolling = false;
        var skipUnsupported = false;
        string? listenUrl = null;
        var showHelp = false;
        var runAsService = false;
        var serviceName = "SmokePingNet";
        string? logFile = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" or "-c" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--check":
                    checkOnly = true;
                    break;
                case "--service":
                    runAsService = true;
                    break;
                case "--service-name" when i + 1 < args.Length:
                    serviceName = args[++i];
                    break;
                case "--log-file" when i + 1 < args.Length:
                    logFile = args[++i];
                    break;
                case "--no-polling":
                    noPolling = true;
                    break;
                case "--skip-unsupported":
                    skipUnsupported = true;
                    break;
                case "--listen" when i + 1 < args.Length:
                    listenUrl = args[++i];
                    break;
                default:
                    showHelp = true;
                    break;
            }
        }

        var resolvedConfig = ResolveExistingFile(configPath, out var searched);

        return new CommandLineOptions
        {
            ConfigPath = resolvedConfig,
            SearchedConfigPaths = searched,
            CheckOnly = checkOnly,
            NoPolling = noPolling,
            SkipUnsupported = skipUnsupported,
            ListenUrl = listenUrl,
            ShowHelp = showHelp,
            RunAsService = runAsService,
            ServiceName = serviceName,

            // A log file need not exist yet, so it is not searched for.
            LogFile = logFile is null ? null : Path.GetFullPath(logFile),
        };
    }

    /// <summary>
    /// Resolves a path that is expected to exist already.
    ///
    /// A relative path is tried against the working directory and the directory
    /// holding the executable, and then against their parents. Both fallbacks earn
    /// their keep: the service control manager starts processes in the system
    /// directory, and "dotnet run" starts them in the project directory - in neither
    /// case does a path relative to the repository root resolve on its own.
    ///
    /// Returns the working-directory interpretation when nothing is found, so the
    /// error names the path the user actually typed.
    /// </summary>
    private static string ResolveExistingFile(string path, out IReadOnlyList<string> searched)
    {
        if (Path.IsPathRooted(path))
        {
            searched = [path];
            return path;
        }

        var candidates = new List<string>();

        foreach (var root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(root);

            // Far enough to climb out of bin/<configuration>/<framework>.
            for (var depth = 0; depth < 5 && directory is not null; depth++, directory = directory.Parent)
            {
                var candidate = Path.GetFullPath(Path.Combine(directory.FullName, path));
                if (!candidates.Contains(candidate, StringComparer.Ordinal))
                {
                    candidates.Add(candidate);
                }
            }
        }

        searched = candidates;
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
