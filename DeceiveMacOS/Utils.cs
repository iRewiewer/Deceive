using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deceive;

internal static class Utils
{
    internal static string DeceiveVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            if (version is null)
                return "v0.0.0";
            return "v" + version.Major + "." + version.Minor + "." + version.Build;
        }
    }

    private static IEnumerable<Process> GetProcesses()
    {
		List<Process> riotCandidates = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Where(process => process.Id != Process.GetCurrentProcess().Id).ToList();
        riotCandidates.AddRange(Process.GetProcessesByName("LeagueClient"));
        riotCandidates.AddRange(Process.GetProcessesByName("RiotClientServices"));
        return riotCandidates;
    }

    // Return the currently running Riot Client process, or null if none are running.
    public static Process GetRiotClientProcess() => Process.GetProcessesByName("RiotClientServices").FirstOrDefault();

    // Checks if there is a running LCU/LoR/VALORANT/RC or Deceive instance.
    public static bool IsClientRunning() => GetProcesses().Any();

    // Kills the running LCU/LoR/VALORANT/RC or Deceive instance, if applicable.
    public static void KillProcesses()
    {
        foreach (var process in GetProcesses())
        {
            process.Refresh();
            if (process.HasExited)
                continue;
            process.Kill();
            process.WaitForExit();
        }
    }

    // Checks for any installed Riot Client configuration,
    // and returns the path of the client if it does. Else, returns null.
    public static string? GetRiotClientPath()
    {
		// Find the RiotClientInstalls file.
		string installPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Riot Games/RiotClientInstalls.json");
        if (!File.Exists(installPath))
            return null;

        try
        {
            // occasionally this deserialization may error, because the RC occasionally corrupts its own
            // configuration file (wtf riot?). we will return null in that case, which will cause a prompt
            // telling the user to launch a game normally once
            var data = JsonSerializer.Deserialize<JsonNode>(File.ReadAllText(installPath));
            var rcPaths = new List<string?> { data?["rc_default"]?.ToString(), data?["rc_live"]?.ToString(), data?["rc_beta"]?.ToString() };

            return rcPaths.FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }
}
