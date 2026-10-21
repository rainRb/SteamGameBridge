using System.Text.Json;

// Test-only stand-in. This never opens any game file or Steam configuration.
if (args.Length < 1) return 90;
string output = Path.GetFullPath(args[0]);
int delay = 1300;
int delayIndex = Array.IndexOf(args, "--delay-ms");
if (delayIndex >= 0 && delayIndex + 1 < args.Length) delay = int.Parse(args[delayIndex + 1]);
if (delay is < 0 or > 10000) return 91;
var result = new
{
    Arguments = args,
    WorkingDirectory = Environment.CurrentDirectory,
    SteamAppId = Environment.GetEnvironmentVariable("SteamAppId"),
    SteamGameId = Environment.GetEnvironmentVariable("SteamGameId"),
    ProcessId = Environment.ProcessId
};
File.WriteAllText(output, JsonSerializer.Serialize(result));
Thread.Sleep(delay);
File.WriteAllText(output + ".exited", "7");
return 7;
