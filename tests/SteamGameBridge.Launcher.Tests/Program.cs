using System.Diagnostics;
using System.Text.Json;
using SteamGameBridge.App;
using SteamGameBridge.Core;

if (args.Length is < 2 or > 3) throw new ArgumentException("launcher-tests <fake exe> <fixture directory> [published exe]");
string builtFake = Path.GetFullPath(args[0]);
string fixture = Path.Combine(Path.GetFullPath(args[1]), "launch-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
foreach (string path in Directory.GetFiles(Path.GetDirectoryName(builtFake)!)) File.Copy(path, Path.Combine(fixture, Path.GetFileName(path)));
string fake = Path.Combine(fixture, "ZenlessZoneZero.exe"), wrapper = Path.Combine(fixture, "FixtureLauncher.exe");
File.Copy(fake, wrapper);
File.WriteAllText(Path.Combine(fixture, "config.ini"), "game_biz=nap_cn\n");
string specPath = Path.Combine(fixture, "launch.json");
string output = Path.Combine(fixture, "game.json");
string[] native = [output, "--delay-ms", "1400", "--label", "中文 with spaces, \"quotes\", C:\\fixture\\", "--dx11", "--dx12"];
var request = new SetupRequest(fixture, fixture, Path.Combine(fixture, "unused.vdf"), fake, "unused.exe", GameArguments: WindowsArguments.Join(native));
string[] command = ["--profile", specPath, "--steam-command", "X:\\never-run\\HYP.exe", "--international-only"];
int passed = 0;
void Assert(bool value, string name) { if (!value) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
void Refuses(Action action, string fragment, string name) { try { action(); } catch (Exception ex) { Assert(ex.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase), name + ": " + ex.Message); return; } throw new Exception("FAIL expected rejection: " + name); }
void Spec(SetupRequest r, string id = "4162040") => File.WriteAllText(specPath, JsonSerializer.Serialize(new LaunchSpec(id, r)));
void Steam(string? id) { Environment.SetEnvironmentVariable("SteamAppId", id); Environment.SetEnvironmentVariable("SteamGameId", id); }
void ClearOutput() { foreach (string path in new[] { output, output + ".exited", output + ".launcher.json" }) if (File.Exists(path)) File.Delete(path); }
string? oldApp = Environment.GetEnvironmentVariable("SteamAppId"), oldGame = Environment.GetEnvironmentVariable("SteamGameId");
try
{
    Spec(request); Steam(null);
    Refuses(() => NativeLauncher.Run(command), "Steam", "missing Steam context"); Assert(!File.Exists(output), "context refusal starts nothing");
    Steam("4162040"); Environment.SetEnvironmentVariable("SteamGameId", "123");
    Refuses(() => NativeLauncher.Run(command), "Steam", "mismatched SteamGameId"); Steam("4162040");
    Refuses(() => NativeLauncher.Run(["--profile", specPath, "--steam-command"]), "启动命令", "missing original command");
    Spec(request with { GameArguments = "login_auth_ticket=fixture" }); Refuses(() => NativeLauncher.Run(command), "票据", "login ticket rejected");
    Spec(request);
    var watch = Stopwatch.StartNew(); var task = Task.Run(() => NativeLauncher.Run(command));
    while (!File.Exists(output) && !task.IsCompleted && watch.ElapsedMilliseconds < 10000) Thread.Sleep(20);
    Assert(File.Exists(output), "direct game launched"); Assert(!task.IsCompleted, "waits while game lives"); Assert(NativeLauncher.BridgeRunning("4162040"), "session lock visible");
    Refuses(() => NativeLauncher.Run(command), "已有", "duplicate AppID session refused");
    Assert(task.GetAwaiter().GetResult() == 7, "exit code propagated"); Assert(watch.ElapsedMilliseconds >= 1300, "direct lifetime observed");
    Assert(File.Exists(output + ".exited"), "game exited before bridge"); Assert(!NativeLauncher.BridgeRunning("4162040"), "lock released");
    using (var doc = JsonDocument.Parse(File.ReadAllText(output)))
    {
        Assert(doc.RootElement.GetProperty("Arguments").EnumerateArray().Select(x => x.GetString()).SequenceEqual(native), "Unicode quotes and conflicting flags preserve order");
        Assert(doc.RootElement.GetProperty("WorkingDirectory").GetString() == fixture, "game working directory");
        Assert(doc.RootElement.GetProperty("SteamAppId").GetString() == "4162040", "inherits Steam context");
    }
    foreach (string id in new[] { "4732690", "3513350", "1671200", "1234567" })
    {
        ClearOutput(); Steam(id); Spec(request, id);
        Assert(NativeLauncher.Run(command) == 7, "dynamic AppID launched: " + id);
        using var report = JsonDocument.Parse(File.ReadAllText(output)); Assert(report.RootElement.GetProperty("SteamAppId").GetString() == id, "dynamic context: " + id);
    }
    Steam("4162040");
    foreach (var mode in new[] { LaunchMode.OfficialLauncher, LaunchMode.ExistingWrapper })
    {
        ClearOutput();
        Spec(request with { LaunchMode = mode, LauncherExe = wrapper, LauncherArguments = "--spawn {game} {gameArgs}" + (mode == LaunchMode.ExistingWrapper ? " --source-steam {steamCommand}" : ""), WaitSeconds = 5 });
        var statuses = new List<LaunchStatus>(); watch.Restart();
        Assert(NativeLauncher.Run(command, report: statuses.Add) == 7, mode + " follows actual game after launcher exits");
        Assert(watch.ElapsedMilliseconds >= 1300 && File.Exists(output + ".exited"), mode + " lifetime observed");
        using var child = JsonDocument.Parse(File.ReadAllText(output)); using var parent = JsonDocument.Parse(File.ReadAllText(output + ".launcher.json"));
        int gamePid = child.RootElement.GetProperty("ProcessId").GetInt32(), launcherPid = parent.RootElement.GetProperty("ProcessId").GetInt32();
        Assert(gamePid != launcherPid && statuses.Where(x => x.GamePid.HasValue).All(x => x.GamePid == gamePid), mode + " reports only real game PID");
        Assert(child.RootElement.GetProperty("Arguments").EnumerateArray().Select(x => x.GetString()).SequenceEqual(native), mode + " game params explicit");
        bool forwarded = parent.RootElement.GetProperty("Arguments").EnumerateArray().Any(x => x.GetString() == "--international-only");
        Assert(forwarded == (mode == LaunchMode.ExistingWrapper), mode + " Steam original command only explicit placeholder");
    }
    ClearOutput(); Spec(request with { LaunchMode = LaunchMode.OfficialLauncher, LauncherExe = wrapper, LauncherArguments = "--no-game", WaitSeconds = 1 });
    Refuses(() => NativeLauncher.Run(command), "超时", "launcher without game times out"); Assert(!NativeLauncher.BridgeRunning("4162040"), "timeout releases lock");
    Spec(request);
    var start = new ProcessStartInfo(fake) { UseShellExecute = false, WorkingDirectory = fixture }; foreach (var arg in native) start.ArgumentList.Add(arg);
    using (var independent = Process.Start(start)!) { Refuses(() => NativeLauncher.Run(command), "已经运行", "existing path is not adopted"); independent.WaitForExit(); Assert(independent.ExitCode == 7, "independent process untouched"); }
    var identity = new GameProcessIdentity(2345, DateTime.UtcNow, 1, fake);
    Assert(GameProcessTracker.Matches(identity, fake, identity.CreatedUtc.AddSeconds(-1), 1), "path time session match");
    Assert(!GameProcessTracker.Matches(identity, wrapper, identity.CreatedUtc.AddSeconds(-1), 1), "same name/path mismatch rejected");
    Assert(!GameProcessTracker.Matches(identity, fake, identity.CreatedUtc.AddSeconds(1), 1), "old creation rejected");
    Assert(!GameProcessTracker.Matches(identity, fake, identity.CreatedUtc.AddSeconds(-1), 2), "different session rejected");
    Assert(StarwardNotifier.BuildUri("zzz", 2345) == "starward://playtime/nap_cn?pid=2345", "ZZZ URI");
    Assert(StarwardNotifier.BuildUri("hi3", 2345) == "starward://playtime/bh3_cn?pid=2345", "HI3 URI");
    var transport = new MockTransport { ProtocolAvailable = true }; var notifier = new StarwardNotifier(transport);
    Assert(notifier.Notify("zzz", new(true), identity).State == "sent", "protocol notification sent without claiming recorded");
    Assert(transport.Uris.Single() == StarwardNotifier.BuildUri("zzz", identity.Pid), "real PID in request");
    Assert(notifier.Notify("zzz", new(true), identity).State == "duplicate" && transport.Uris.Count == 1, "PID creation dedup");
    notifier.Notify("zzz", new(true), identity with { CreatedUtc = identity.CreatedUtc.AddSeconds(1) }); Assert(transport.Uris.Count == 2, "reused PID with new creation is separate");
    var absent = new MockTransport(); notifier = new(absent);
    Assert(notifier.Notify("hi3", new(true), identity).State == "unavailable", "missing protocol soft failure");
    notifier = new(absent); Assert(notifier.Notify("hi3", new(true, wrapper), identity).State == "sent" && absent.Executables.Single() == wrapper, "manual EXE fallback");
    var priority = new MockTransport { ProtocolAvailable = true }; new StarwardNotifier(priority).Notify("zzz", new(true, wrapper), identity); Assert(priority.Executables.Count == 0, "protocol preferred");
    foreach (bool fails in new[] { false, true })
    {
        ClearOutput(); var mock = new MockTransport { ProtocolAvailable = fails, Fail = fails }; var statuses = new List<LaunchStatus>();
        Spec(request with { PresetId = "zzz", Starward = new(true) });
        Assert(NativeLauncher.Run(command, mock, statuses.Add) == 7, "Starward unavailable/failed keeps game alive: " + fails);
        Assert(statuses.Any(x => x.State == "unavailable") && File.Exists(output + ".exited"), "soft failure visible, normal game exit");
    }
    ClearOutput(); transport = new() { ProtocolAvailable = true }; var chainStatuses = new List<LaunchStatus>();
    Spec(request with { PresetId = "zzz", Starward = new(true), LaunchMode = LaunchMode.ExistingWrapper, LauncherExe = wrapper, LauncherArguments = "--spawn {game} {gameArgs}", WaitSeconds = 5 });
    Assert(NativeLauncher.Run(command, transport, chainStatuses.Add) == 7, "chain Starward handoff completes");
    using (var child = JsonDocument.Parse(File.ReadAllText(output))) Assert(transport.Uris.Single() == StarwardNotifier.BuildUri("zzz", child.RootElement.GetProperty("ProcessId").GetInt32()), "chain Starward gets actual child PID");
    string hi3 = Path.Combine(fixture, "BH3.exe"); File.Copy(fake, hi3); File.WriteAllText(Path.Combine(fixture, "config.ini"), "game_biz=bh3_cn");
    ClearOutput(); transport = new() { ProtocolAvailable = true }; Spec(request with { NativeExe = hi3, PresetId = "hi3", Starward = new(true) });
    Assert(NativeLauncher.Run(command, transport) == 7 && transport.Uris.Single().Contains("bh3_cn?pid="), "HI3 actual process handoff");
    Spec(request with { PresetId = "zzz", Starward = new(true) }); Refuses(() => NativeLauncher.Run(command), "地区", "wrong local region rejected for Starward");
    File.WriteAllText(Path.Combine(fixture, "config.ini"), "game_biz=nap_cn"); Spec(request);
    // Different Steam entries must not race to start the same local EXE while a launcher waits.
    Spec(request with { LaunchMode = LaunchMode.OfficialLauncher, LauncherExe = wrapper, LauncherArguments = "--no-game", WaitSeconds = 2 });
    var waiting = Task.Run(() => { try { return NativeLauncher.Run(command); } catch (IOException) { return -1; } });
    var deadline = Stopwatch.StartNew(); while (!NativeLauncher.BridgeRunning("4162040") && !waiting.IsCompleted && deadline.ElapsedMilliseconds < 1000) Thread.Sleep(10);
    Thread.Sleep(100);
    string otherSpec = Path.Combine(fixture, "other-launch.json");
    File.WriteAllText(otherSpec, JsonSerializer.Serialize(new LaunchSpec("1234567", request)));
    Steam("1234567"); Refuses(() => NativeLauncher.Run(["--profile", otherSpec, "--steam-command", "unused.exe"]), "本地游戏", "same local EXE protected across AppIDs");
    Assert(waiting.GetAwaiter().GetResult() == -1, "waiting launcher times out normally"); Steam("4162040");
    if (args.Length == 3)
    {
        string published = Path.GetFullPath(args[2]);
        foreach (var mode in new[] { LaunchMode.Direct, LaunchMode.OfficialLauncher, LaunchMode.ExistingWrapper })
        {
            ClearOutput(); Spec(request with { LaunchMode = mode, LauncherExe = wrapper, LauncherArguments = "--spawn {game} {gameArgs}", WaitSeconds = 5 });
            var package = new ProcessStartInfo(published) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture };
            foreach (var value in command) package.ArgumentList.Add(value);
            using var process = Process.Start(package)!;
            if (!process.WaitForExit(15000)) { process.Kill(false); throw new Exception("Only the exact test bridge was terminated after fixture timeout."); }
            Assert(process.ExitCode == 7, "published " + mode + " exit code"); Assert(File.Exists(output + ".exited"), "published " + mode + " waits actual game");
            using var doc = JsonDocument.Parse(File.ReadAllText(output)); Assert(doc.RootElement.GetProperty("Arguments").EnumerateArray().Select(x => x.GetString()).SequenceEqual(native), "published " + mode + " exact args");
        }
    }
    File.WriteAllText(Path.Combine(fixture, "launcher-test-results.json"), JsonSerializer.Serialize(new { Passed = passed, RealSteamLaunched = false, RealGameLaunched = false, StarwardLaunched = false, LiveConfigurationWritten = false, NativeSteamFeaturesVerified = false, PublishedCliExercised = args.Length == 3 }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"RESULT {passed} launcher checks passed. Isolated fixtures only."); return 0;
}
finally { Environment.SetEnvironmentVariable("SteamAppId", oldApp); Environment.SetEnvironmentVariable("SteamGameId", oldGame); }

sealed class MockTransport : IStarwardTransport
{
    public bool ProtocolAvailable { get; init; }
    public bool Fail { get; init; }
    public List<string> Uris { get; } = []; public List<string> Executables { get; } = [];
    public void SendUri(string uri) { if (Fail) throw new IOException("fixture failure"); Uris.Add(uri); }
    public void SendExecutable(string exe, string uri) { if (Fail) throw new IOException("fixture failure"); Executables.Add(exe); Uris.Add(uri); }
}
