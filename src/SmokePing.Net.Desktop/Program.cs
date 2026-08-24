using System.Runtime.Versioning;
using System.Windows.Forms;

namespace SmokePing.Net.Desktop;

/// <summary>
/// Entry point for the native desktop client.
///
/// The client is a viewer: it reads from a running daemon over HTTP rather than
/// opening the measurement files, because the daemon owns those and two writers would
/// corrupt them. Pass --standalone with a configuration file to start a daemon inside
/// this process instead, which makes the desktop client usable on its own.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = DesktopOptions.Parse(args);

        if (options.ShowHelp)
        {
            MessageBox.Show(DesktopOptions.HelpText, "SmokePing.NET", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();

        if (options.StandaloneConfigPath is { } configPath)
        {
            // The daemon hosts the same web server the client talks to, so one process
            // measures, stores and displays without any file being opened twice.
            StartDaemon(configPath);
        }

        if (!Uri.TryCreate(options.Server, UriKind.Absolute, out var server))
        {
            MessageBox.Show(
                $"'{options.Server}' is not a valid server address.",
                "SmokePing.NET",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        Application.Run(new MainForm(new SmokePingClient(server)));
        return 0;
    }

    /// <summary>
    /// Runs the daemon on a background thread. Failures surface through the client's
    /// connection status rather than as a dialog, so a bad configuration looks the
    /// same as an unreachable server.
    /// </summary>
    private static void StartDaemon(string configPath)
    {
        var thread = new Thread(() => SmokePing.Net.Program.Main(["--config", configPath]).GetAwaiter().GetResult())
        {
            IsBackground = true,
            Name = "smokeping-daemon",
        };

        thread.Start();
    }
}

/// <summary>Command line handling for the desktop client.</summary>
public sealed class DesktopOptions
{
    public const string HelpText = """
        SmokePing.NET desktop client

        Usage: SmokePing.Net.Desktop [options]

          --server <url>        Daemon to connect to (default: http://localhost:8081)
          --standalone <path>   Run a daemon in this process using the given configuration
          --help                Show this help
        """;

    /// <summary>Address used when none is given on the command line.</summary>
    public const string DefaultServer = "http://localhost:8081";

    public string Server { get; private init; } = DefaultServer;

    public string? StandaloneConfigPath { get; private init; }

    public bool ShowHelp { get; private init; }

    public static DesktopOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var server = DefaultServer;
        string? standalone = null;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server" or "-s" when i + 1 < args.Length:
                    server = args[++i];
                    break;
                case "--standalone" when i + 1 < args.Length:
                    standalone = args[++i];
                    break;
                default:
                    showHelp = true;
                    break;
            }
        }

        return new DesktopOptions
        {
            Server = server,
            StandaloneConfigPath = standalone,
            ShowHelp = showHelp,
        };
    }
}
