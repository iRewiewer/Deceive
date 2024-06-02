using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Deceive;

internal static class StartupHandler
{
    public static string DeceiveTitle => "Deceive " + Utils.DeceiveVersion;

    // Arguments are parsed through System.CommandLine.DragonFruit.
    /// <param name="args">The game to be launched, or automatically determined if not passed.</param>
    /// <param name="gamePatchline">The patchline to be used for launching the game.</param>
    /// <param name="riotClientParams">Any extra parameters to be passed to the Riot Client.</param>
    /// <param name="gameParams">Any extra parameters to be passed to the launched game.</param>
    [STAThread]
    public static async Task Main(LaunchGame args = LaunchGame.LoL, string gamePatchline = "live", string? riotClientParams = null, string? gameParams = null)
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
        try
        {
            await StartDeceiveAsync(args, gamePatchline, riotClientParams, gameParams);
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
        }
    }

    /// Actual main function. Wrapped into a separate function so we can catch exceptions.
    private static async Task StartDeceiveAsync(LaunchGame game, string gamePatchline, string? riotClientParams, string? gameParams)
    {
        // Refuse to do anything if the client is already running, unless we're specifically
        // allowing that through League/RC's --allow-multiple-clients.
        if (Utils.IsClientRunning() && !(riotClientParams?.Contains("allow-multiple-clients") ?? false))
        {
            Utils.KillProcesses();
            await Task.Delay(2000); // Riot Client takes a while to die
        }

        try
        {
            File.WriteAllText(Path.Combine(Persistence.DataDir, "debug.log"), string.Empty);
            Trace.Listeners.Add(new TextWriterTraceListener(Path.Combine(Persistence.DataDir, "debug.log")));
            Debug.AutoFlush = true;
            Trace.WriteLine(DeceiveTitle);
        }
        catch
        {
            // ignored; just don't save logs if file is already being accessed
        }

		// Step 1: Open a port for our chat proxy, so we can patch chat port into clientconfig.
		TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
		int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Trace.WriteLine($"Chat proxy listening on port {port}");

		// Step 2: Find the Riot Client.
		string? riotClientPath = Utils.GetRiotClientPath();

        // If the riot client doesn't exist, the user is either severely outdated or has a bugged install.
        if (riotClientPath is null)
        {
            return;
        }

        // Use the persisted launch game (which defaults to prompt).
        game = Persistence.GetDefaultLaunchGame();

		string? launchProduct = game switch
        {
            LaunchGame.LoL => "league_of_legends",
            LaunchGame.RiotClient => null,
            var x => throw new Exception("Unexpected LaunchGame: " + x)
        };

		// Step 3: Start proxy web server for clientconfig
		ConfigProxy proxyServer = new ConfigProxy(port);

		// Step 4: Launch Riot Client (+game)
		ProcessStartInfo startArgs = new ProcessStartInfo { FileName = riotClientPath, Arguments = $"--client-config-url=\"http://127.0.0.1:{proxyServer.ConfigPort}\"" };

        if (launchProduct is not null)
            startArgs.Arguments += $" --launch-product={launchProduct} --launch-patchline={gamePatchline}";

        if (riotClientParams is not null)
            startArgs.Arguments += $" {riotClientParams}";

        if (gameParams is not null)
            startArgs.Arguments += $" -- {gameParams}";

        Trace.WriteLine($"About to launch Riot Client with parameters:\n{startArgs.Arguments}");
		Process? riotClient = Process.Start(startArgs);

        // Kill Deceive when Riot Client has exited, so no ghost Deceive exists.
        if (riotClient is not null)
        {
            ListenToRiotClientExit(riotClient);
        }

		MainController mainController = new MainController();

		// Step 5: Get chat server and port for this player by listening to event from ConfigProxy.
		bool servingClients = false;
        proxyServer.PatchedChatServer += (_, args) =>
        {
            Trace.WriteLine($"The original chat server details were {args.ChatHost}:{args.ChatPort}");

            // Step 6: Start serving incoming connections and proxy them!
            if (servingClients)
                return;
            servingClients = true;
            mainController.StartServingClients(listener, args.ChatHost, args.ChatPort);
        };

        // Loop infinitely and handle window messages/tray icon.
        //Application.Run(mainController);
    }

    private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Log all unhandled exceptions
        Trace.WriteLine(e.ExceptionObject as Exception);
        Trace.WriteLine(Environment.StackTrace);
    }

    private static void ListenToRiotClientExit(Process riotClientProcess)
    {
        riotClientProcess.EnableRaisingEvents = true;
        riotClientProcess.Exited += async (sender, e) =>
        {
            Trace.WriteLine("Detected Riot Client exit.");
            await Task.Delay(3000); // wait for a bit to ensure this is not a relaunch triggered by the RC

			Process? newProcess = Utils.GetRiotClientProcess();
            if (newProcess is not null)
            {
                Trace.WriteLine("A new Riot Client process spawned, monitoring that for exits.");
                ListenToRiotClientExit(newProcess);
            }
            else
            {
                Trace.WriteLine("No new clients spawned after waiting, killing ourselves.");
                Environment.Exit(0);
            }
        };
    }
}
