using System.Diagnostics;
using System.Text.Json;

// Test-only process and launcher. Never opens real Steam, game, or Starward data.
if (args.Length < 1) return 90;
if (args[0] == "--spawn")
{
    string target = args[1];
    int marker = Array.IndexOf(args, "--source-steam");
    string[] childArgs = args.Skip(2).Take((marker < 0 ? args.Length : marker) - 2).ToArray();
    File.WriteAllText(childArgs[0] + ".launcher.json", JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId, Arguments = args }));
    var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target) };
    foreach (string value in childArgs) start.ArgumentList.Add(value);
    Process.Start(start)!.Dispose();
    return 0; // Deliberately exit before the game: the bridge must track the real child.
}
if (args[0] == "--no-game") return 0;
string output = Path.GetFullPath(args[0]);
int delay = 1400;
int delayIndex = Array.IndexOf(args, "--delay-ms");
if (delayIndex >= 0 && delayIndex + 1 < args.Length) delay = int.Parse(args[delayIndex + 1]);
if (delay is < 0 or > 10000) return 91;
File.WriteAllText(output, JsonSerializer.Serialize(new { Arguments = args, WorkingDirectory = Environment.CurrentDirectory, SteamAppId = Environment.GetEnvironmentVariable("SteamAppId"), SteamGameId = Environment.GetEnvironmentVariable("SteamGameId"), ProcessId = Environment.ProcessId }));
Thread.Sleep(delay);
File.WriteAllText(output + ".exited", "7");
return 7;
