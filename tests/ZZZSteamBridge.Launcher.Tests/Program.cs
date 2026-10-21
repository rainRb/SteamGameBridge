using System.Diagnostics;
using System.Text.Json;
using ZZZSteamBridge.App;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage: launcher-tests <built fake ZenlessZoneZero.exe> <isolated fixture directory> [published ZZZSteamBridge.exe]");
    return 2;
}
string builtFake = Path.GetFullPath(args[0]);
string fixtureRoot = Path.GetFullPath(args[1]);
if (!File.Exists(builtFake) || Path.GetFileName(builtFake) != "ZenlessZoneZero.exe")
    throw new InvalidOperationException("Fake game must be built before running this test.");
if (LocalEnvironment.GameRunning() || NativeLauncher.BridgeRunning())
    throw new InvalidOperationException("Stop existing game/bridge sessions before these isolated tests; nothing was launched.");
Directory.CreateDirectory(fixtureRoot);
string fixture = Path.Combine(fixtureRoot, "native-launch-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
foreach (string input in Directory.GetFiles(Path.GetDirectoryName(builtFake)!))
    File.Copy(input, Path.Combine(fixture, Path.GetFileName(input)), false);
string fake = Path.Combine(fixture, "ZenlessZoneZero.exe");
string config = Path.Combine(fixture, "config.ini");
File.WriteAllText(config, "[General]\ngame_biz=nap_cn\nchannel=1\n");
string output = Path.Combine(fixture, "received.json");
string[] nativeArguments = [output, "--delay-ms", "1300", "--label", "中文 with spaces, \"quotes\", C:\\fixture\\"];
string[] launch = ["--game", fake, "--", .. nativeArguments, "--steam-command", "X:\\never-run\\HYP.exe", "--international-only", "must-not-reach-game"];

int passed = 0;
void Assert(bool success, string name)
{
    if (!success) throw new InvalidOperationException("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}
void Refuses(Action action, string message, string name)
{
    try { action(); }
    catch (Exception error)
    {
        Assert(error.Message.Contains(message, StringComparison.OrdinalIgnoreCase), name + " (expected rejection)");
        return;
    }
    throw new InvalidOperationException("FAIL: " + name + " (did not reject)");
}

string? previousAppId = Environment.GetEnvironmentVariable("SteamAppId");
string? previousGameId = Environment.GetEnvironmentVariable("SteamGameId");
try
{
    Environment.SetEnvironmentVariable("SteamAppId", null);
    Environment.SetEnvironmentVariable("SteamGameId", null);
    Refuses(() => NativeLauncher.Run(launch), "Steam", "missing Steam context");
    Assert(!File.Exists(output), "missing context does not create a process");
    Environment.SetEnvironmentVariable("SteamAppId", "4162040");
    Environment.SetEnvironmentVariable("SteamGameId", "123");
    Refuses(() => NativeLauncher.Run(launch), "Steam", "wrong SteamGameId");
    Environment.SetEnvironmentVariable("SteamGameId", "4162040");
    Refuses(() => NativeLauncher.Run(["--game", fake, "--"]), "启动命令", "incomplete generated command");
    Refuses(() => NativeLauncher.Run(["--game", fake, "--", output, "--steam-command"]), "缺少", "missing original Steam command");
    File.WriteAllText(config, "[General]\ngame_biz=nap_global\n");
    Refuses(() => NativeLauncher.Run(launch), "nap_cn", "international client marker rejected");
    Assert(!File.Exists(output), "invalid channel does not create a process");
    File.WriteAllText(config, "[General]\ngame_biz=nap_cn\n");
    Refuses(() => NativeLauncher.Run(["--game", fake, "--", "--login_auth_ticket", "not-a-real-ticket", "--steam-command", "HYP.exe"]), "票据", "login ticket refused in persisted native arguments");
    Assert(!NativeLauncher.BridgeRunning(), "rejected commands do not hold session mutex");

    var timer = Stopwatch.StartNew();
    Task<int> running = Task.Run(() => NativeLauncher.Run(launch));
    var deadline = Stopwatch.StartNew();
    while (!File.Exists(output) && !running.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(25);
    Assert(File.Exists(output), "fake game received launch");
    Assert(!running.IsCompleted, "launcher waits while its child is running");
    Assert(NativeLauncher.BridgeRunning(), "running session visible across threads");
    Refuses(() => NativeLauncher.Run(launch), "已有", "concurrent Steam session rejected");
    int exit = running.GetAwaiter().GetResult();
    timer.Stop();
    Assert(exit == 7, "child exit code propagated");
    Assert(timer.Elapsed >= TimeSpan.FromMilliseconds(1200), "wait covers real child lifetime");
    Assert(File.Exists(output + ".exited"), "game wrote exit marker before launcher returned");
    Assert(!NativeLauncher.BridgeRunning(), "mutex released after child exit");
    using (var report = JsonDocument.Parse(File.ReadAllText(output)))
    {
        var root = report.RootElement;
        string[] received = root.GetProperty("Arguments").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert(received.SequenceEqual(nativeArguments), "exact configured native arguments survive quotes and Unicode");
        Assert(!received.Contains("--steam-command") && !received.Contains("--international-only"), "original Steam executable and arguments never forwarded");
        Assert(root.GetProperty("WorkingDirectory").GetString() == fixture, "child cwd is native directory");
        Assert(root.GetProperty("SteamAppId").GetString() == "4162040" && root.GetProperty("SteamGameId").GetString() == "4162040", "Steam context inherited without fabrication");
    }
    // An independently running game is a conflict, not a process the bridge may adopt.
    string independentOutput = Path.Combine(fixture, "independent.json");
    var independentStart = new ProcessStartInfo(fake) { UseShellExecute = false, WorkingDirectory = fixture };
    independentStart.ArgumentList.Add(independentOutput);
    independentStart.ArgumentList.Add("--delay-ms");
    independentStart.ArgumentList.Add("1300");
    using (var independent = Process.Start(independentStart)!)
    {
        Refuses(() => NativeLauncher.Run(launch), "已经运行", "existing native session rejected without adoption");
        independent.WaitForExit();
        Assert(independent.ExitCode == 7, "independent fake game exited normally");
    }
    Assert(!NativeLauncher.BridgeRunning(), "failed duplicate process check releases mutex");

    if (args.Length == 3)
    {
        // Packaged CLI exercises only the successful fake-game path. The package's
        // error UI is deliberately not invoked by any negative packaged test.
        string published = Path.GetFullPath(args[2]);
        if (!File.Exists(published) || Path.GetFileName(published) != "ZZZSteamBridge.exe")
            throw new InvalidOperationException("The optional package must be a built ZZZSteamBridge.exe.");
        string publishedOutput = Path.Combine(fixture, "published-received.json");
        string[] publishedNative = [publishedOutput, "--delay-ms", "1300", "--label", "打包版本 with \"quotes\" and 中文"];
        var publishedStart = new ProcessStartInfo(published)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(published),
            CreateNoWindow = true
        };
        publishedStart.Environment["SteamAppId"] = "4162040";
        publishedStart.Environment["SteamGameId"] = "4162040";
        string[] publishedArguments = ["--game", fake, "--", .. publishedNative, "--steam-command", "X:\\never-run\\HYP.exe", "--ignored-international-switch"];
        foreach (string argument in publishedArguments)
            publishedStart.ArgumentList.Add(argument);
        var packagedTimer = Stopwatch.StartNew();
        using var packaged = Process.Start(publishedStart)!;
        if (!packaged.WaitForExit(15000))
        {
            // Terminate only the exact test helper started above, never any Steam
            // or real game process. A failing fixture cannot leave modal UI behind.
            packaged.Kill(entireProcessTree: false);
            packaged.WaitForExit();
            throw new InvalidOperationException("FAIL: packaged fixture timed out; only its own test helper was terminated.");
        }
        Assert(packaged.ExitCode == 7, "published CLI propagates fake game exit code");
        Assert(packagedTimer.Elapsed >= TimeSpan.FromMilliseconds(1200), "published CLI waits for child lifetime");
        Assert(File.Exists(publishedOutput + ".exited"), "published game completed before packaged helper returned");
        using var publishedReport = JsonDocument.Parse(File.ReadAllText(publishedOutput));
        var packagedRoot = publishedReport.RootElement;
        string[] received = packagedRoot.GetProperty("Arguments").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert(received.SequenceEqual(publishedNative), "published CLI preserves only configured native arguments");
        Assert(packagedRoot.GetProperty("SteamAppId").GetString() == "4162040" && packagedRoot.GetProperty("SteamGameId").GetString() == "4162040", "published child inherits fixture Steam context");
        Assert(packagedRoot.GetProperty("WorkingDirectory").GetString() == fixture, "published child uses native cwd");
        Assert(!NativeLauncher.BridgeRunning(), "published helper releases session after child exit");
    }

    File.WriteAllText(Path.Combine(fixture, "launcher-test-results.json"), JsonSerializer.Serialize(new
    {
        Passed = passed,
        RealSteamLaunched = false,
        RealGameLaunched = false,
        LiveSteamOrGameConfigurationWritten = false,
        NativeSteamFeaturesVerified = false,
        PublishedCliExercised = args.Length == 3,
        Fixture = fixture
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{passed} launcher checks passed. Test-only fixture: {fixture}");
    return 0;
}
finally
{
    Environment.SetEnvironmentVariable("SteamAppId", previousAppId);
    Environment.SetEnvironmentVariable("SteamGameId", previousGameId);
}
