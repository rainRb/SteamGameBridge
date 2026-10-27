using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SteamGameBridge.Core;

var runner = new Runner();
var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "appinfo.json"));
SteamAppMetadata Metadata() => SteamAppMetadata.ParseApiSteamCmdJson(raw, "4162040");
void InvalidMetadata(string name, Action<JsonObject> mutate) => runner.Test(name, () => { var json = JsonNode.Parse(raw)!.AsObject(); mutate(json); Runner.Throws(() => SteamAppMetadata.ParseApiSteamCmdJson(json.ToJsonString(), "4162040")); });

runner.Test("Real-shape metadata parses exact public build and depot", () => { var m = Metadata(); Runner.Equal("25596035", m.BuildId); Runner.Equal("HYP.exe", m.Executable); Runner.Equal("354184710998767755", m.Depots.Single().ManifestId); });
InvalidMetadata("API error rejected", j => j["status"] = "error");
InvalidMetadata("Missing selected AppID rejected", j => j["data"]!.AsObject().Remove("4162040"));
InvalidMetadata("Missing build rejected", j => j["data"]!["4162040"]!["depots"]!["branches"]!["public"]!.AsObject().Remove("buildid"));
InvalidMetadata("Zero build rejected", j => j["data"]!["4162040"]!["depots"]!["branches"]!["public"]!["buildid"] = "0");
InvalidMetadata("Overflow build rejected", j => j["data"]!["4162040"]!["depots"]!["branches"]!["public"]!["buildid"] = "99999999999999999999");
InvalidMetadata("Zero manifest rejected", j => j["data"]!["4162040"]!["depots"]!["4162041"]!["manifests"]!["public"]!["gid"] = "0");
InvalidMetadata("Missing public depot rejected", j => j["data"]!["4162040"]!["depots"]!.AsObject().Remove("4162041"));
InvalidMetadata("Install traversal rejected", j => j["data"]!["4162040"]!["config"]!["installdir"] = "..\\native");
InvalidMetadata("Reserved path rejected", j => j["data"]!["4162040"]!["config"]!["installdir"] = "NUL");
InvalidMetadata("Executable traversal rejected", j => j["data"]!["4162040"]!["config"]!["launch"]!["0"]!["executable"] = "..\\game.exe");
InvalidMetadata("Ambiguous Windows launch rejected", j => j["data"]!["4162040"]!["config"]!["launch"]!["1"] = j["data"]!["4162040"]!["config"]!["launch"]!["0"]!.DeepClone());
runner.Test("Malformed JSON rejected", () => Runner.Throws(() => SteamAppMetadata.ParseApiSteamCmdJson("{", "4162040")));

const string original = "// preserved comment\r\n\"UserLocalConfigStore\"\r\n{\r\n\"friends\" { \"name\" \"中文好友\" }\r\n\"Software\" { \"Valve\" { \"Steam\" { \"apps\" { \"123\" { \"LaunchOptions\" \"keep\" } \"4162040\" { \"LaunchOptions\" \"-screen-fullscreen 0\" \"LastPlayed\" \"9\" } } } } }\r\n}\r\n";
runner.Test("Targeted VDF edit preserves all unrelated characters", () => { var edited = Vdf.SetLaunchOptions(original, "new \\\" value"); Runner.Equal(original.Replace("\"-screen-fullscreen 0\"", ValveKeyValues.Quote("new \\\" value"), StringComparison.Ordinal), edited); Runner.Equal("new \\\" value", Vdf.ReadLaunchOptions(edited).Value); });
runner.Test("VDF quoted backslash and unicode roundtrip", () => { var v = "\"D:\\路径\\client.exe\" -- --steam-command %command%"; Runner.Equal(v, Vdf.ReadLaunchOptions(Vdf.SetLaunchOptions(original, v)).Value); });
runner.Test("VDF missing path created without touching original text", () => { var before = "//hi\n\"UserLocalConfigStore\" { \"existing\" \"value\" }"; var edited = Vdf.SetLaunchOptions(before, "bridge"); Runner.Equal("bridge", Vdf.ReadLaunchOptions(edited).Value); Runner.True(edited.Contains("\"existing\" \"value\"")); });
runner.Test("VDF duplicate targeted leaf rejected", () => Runner.Throws(() => Vdf.ReadLaunchOptions(original.Replace("\"LastPlayed\" \"9\"", "\"LaunchOptions\" \"duplicate\""))));
runner.Test("VDF malformed nesting rejected", () => Runner.Throws(() => Vdf.SetLaunchOptions("\"UserLocalConfigStore\" {", "x")));
runner.Test("VDF removing absent target leaves text intact", () => Runner.Equal("\"UserLocalConfigStore\" {}", Vdf.SetLaunchOptions("\"UserLocalConfigStore\" {}", null, false)));

runner.Test("One MostRecent account is suggested unambiguously", () => { using var f = new Fixture(Metadata(), original); var a = SteamDiscovery.ReadAccounts(f.Steam); Runner.Equal("12345", a.SuggestedAccount!.AccountId); Runner.Equal(f.Config, a.Accounts.Single().LocalConfigPath); });
runner.Test("Ambiguous recent accounts are never auto-selected", () => { using var f = new Fixture(Metadata(), original); var text = Fixture.LoginUsers + ""; text = text[..text.LastIndexOf('}')] + "\"76561197960278074\" { \"PersonaName\" \"second\" \"MostRecent\" \"1\" } }"; var a = SteamDiscovery.ParseLoginUsers(text, f.Steam); Runner.Equal(2, a.Accounts.Count); Runner.True(a.SuggestedAccount is null); });
runner.Test("No recent account is never auto-selected", () => { using var f = new Fixture(Metadata(), original); Runner.True(SteamDiscovery.ParseLoginUsers(Fixture.LoginUsers.Replace("\"MostRecent\" \"1\"", "\"MostRecent\" \"0\""), f.Steam).SuggestedAccount is null); });
runner.Test("Modern registered library discovery", () => { using var f = new Fixture(Metadata(), original); var a = SteamDiscovery.ReadLibraries(f.Steam); Runner.Equal(2, a.Count); Runner.True(a.Contains(f.Library)); });
runner.Test("Legacy registered library discovery", () => { using var f = new Fixture(Metadata(), original); File.WriteAllText(Path.Combine(f.Steam, "steamapps", "libraryfolders.vdf"), "\"LibraryFolders\" { \"1\" " + ValveKeyValues.Quote(f.Library) + " }"); Runner.True(SteamDiscovery.ReadLibraries(f.Steam).Contains(f.Library)); });

runner.Test("Preview is read-only with exact command contract", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Preview(f.Request with { GameArguments = "-popupwindow -screen-fullscreen 0" }, f.Metadata); Runner.Equal($"\"{f.Helper}\" --profile \"{Path.Combine(f.State, "launch.json")}\" --steam-command %command%", p.LaunchOptions); Runner.True(!Directory.Exists(f.State)); Runner.True(!File.Exists(p.ManifestPath)); Runner.Equal(original, File.ReadAllText(f.Config)); });
runner.Test("Apply and restore preserve native files and selected options", () => { using var f = new Fixture(Metadata(), original); var nativeBytes = File.ReadAllBytes(f.Native); var result = f.Service.Apply(f.Request, f.Metadata); Runner.True(f.Service.Inspect().Conflicts.Count == 0); Runner.Equal(result.Preview!.LaunchOptions, Vdf.ReadLaunchOptions(File.ReadAllText(f.Config)).Value); Runner.Equal(f.Metadata.BuildId, f.Service.ReadReceipt()!.Metadata.BuildId); Runner.True(File.ReadAllText(result.Preview.ManifestPath).Contains(f.Metadata.Depots.Single().ManifestId)); Runner.Equal(2, Directory.GetFiles(Path.GetDirectoryName(f.Native)!).Length); Runner.True(File.ReadAllBytes(f.Native).SequenceEqual(nativeBytes)); f.Service.Restore(); Runner.Equal(original, File.ReadAllText(f.Config)); Runner.True(!Directory.Exists(result.Preview.PlaceholderDirectory)); Runner.True(!File.Exists(result.Preview.ManifestPath)); Runner.True(!f.Service.Inspect().Installed); });
runner.Test("Restore preserves unrelated config edits made after apply", () => { using var f = new Fixture(Metadata(), original); f.Service.Apply(f.Request, f.Metadata); File.WriteAllText(f.Config, File.ReadAllText(f.Config).Replace("中文好友", "new friend")); f.Service.Restore(); Runner.Equal(original.Replace("中文好友", "new friend"), File.ReadAllText(f.Config)); });
runner.Test("Restore preserves user replacement LaunchOptions", () => { using var f = new Fixture(Metadata(), original); f.Service.Apply(f.Request, f.Metadata); File.WriteAllText(f.Config, Vdf.SetLaunchOptions(File.ReadAllText(f.Config), "-my-custom-option")); f.Service.Restore(); Runner.Equal("-my-custom-option", Vdf.ReadLaunchOptions(File.ReadAllText(f.Config)).Value); });
runner.Test("Restore removes only newly added LaunchOptions when originally absent", () => { using var f = new Fixture(Metadata(), Vdf.SetLaunchOptions(original, null, false)); f.Service.Apply(f.Request, f.Metadata); f.Service.Restore(); Runner.True(!Vdf.ReadLaunchOptions(File.ReadAllText(f.Config)).Exists); });
runner.Test("UTF8 BOM survives apply and restore", () => { using var f = new Fixture(Metadata(), original); File.WriteAllText(f.Config, original, new UTF8Encoding(true)); f.Service.Apply(f.Request, f.Metadata); Runner.True(File.ReadAllBytes(f.Config).AsSpan().StartsWith(new byte[] { 239, 187, 191 })); f.Service.Restore(); Runner.True(File.ReadAllBytes(f.Config).AsSpan().StartsWith(new byte[] { 239, 187, 191 })); });
runner.Test("UTF16LE encoding survives apply and restore", () => { using var f = new Fixture(Metadata(), original); File.WriteAllText(f.Config, original, Encoding.Unicode); f.Service.Apply(f.Request, f.Metadata); f.Service.Restore(); Runner.Equal(original, File.ReadAllText(f.Config)); Runner.True(File.ReadAllBytes(f.Config).AsSpan().StartsWith(new byte[] { 255, 254 })); });
runner.Test("Busy Steam blocks apply before any persistent changes", () => { using var f = new Fixture(Metadata(), original); f.Busy = true; Runner.Throws(() => f.Service.Apply(f.Request, f.Metadata)); Runner.True(!Directory.Exists(f.State)); Runner.Equal(original, File.ReadAllText(f.Config)); });
runner.Test("Existing foreign ACF is never overwritten", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Preview(f.Request, f.Metadata); File.WriteAllText(p.ManifestPath, "foreign"); Runner.Throws(() => f.Service.Apply(f.Request, f.Metadata)); Runner.Equal("foreign", File.ReadAllText(p.ManifestPath)); });
runner.Test("Existing game directory is never overwritten even when empty", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Preview(f.Request, f.Metadata); Directory.CreateDirectory(p.PlaceholderDirectory); Runner.Throws(() => f.Service.Apply(f.Request, f.Metadata)); Runner.True(Directory.Exists(p.PlaceholderDirectory)); });
runner.Test("Existing download in another registered library blocks apply", () => { using var f = new Fixture(Metadata(), original); Directory.CreateDirectory(Path.Combine(f.Steam, "steamapps", "downloading", "4162040")); Runner.Throws(() => f.Service.Apply(f.Request, f.Metadata)); });
runner.Test("Unregistered game library rejected", () => { using var f = new Fixture(Metadata(), original); Runner.Throws(() => f.Service.Preview(f.Request with { LibraryRoot = Path.Combine(f.Root, "other") }, f.Metadata)); });
runner.Test("Unregistered user configuration rejected", () => { using var f = new Fixture(Metadata(), original); Runner.Throws(() => f.Service.Preview(f.Request with { AccountConfigPath = Path.Combine(f.Root, "otherconfig.vdf") }, f.Metadata)); });
runner.Test("Non EXE native file rejected", () => { using var f = new Fixture(Metadata(), original); Runner.Throws(() => f.Service.Preview(f.Request with { NativeExe = Path.Combine(f.Root, "not-executable.txt") }, f.Metadata)); });
runner.Test("Old metadata rejected", () => { using var f = new Fixture(Metadata(), original); Runner.Throws(() => f.Service.Preview(f.Request, f.Metadata with { FetchedAt = DateTimeOffset.UtcNow.AddHours(-1) })); });
runner.Test("Future metadata rejected", () => { using var f = new Fixture(Metadata(), original); Runner.Throws(() => f.Service.Preview(f.Request, f.Metadata with { FetchedAt = DateTimeOffset.UtcNow.AddHours(1) })); });
foreach (var arg in new[] { "%command%", "--steam-command fake", "login_auth_ticket=secret", "x\ny", "x\0y" })
    runner.Test("Reserved game argument blocked: " + arg.Replace('\0', '?').Replace('\n', '?'), () => { using var f = new Fixture(Metadata(), original); Runner.Throws(() => f.Service.Preview(f.Request with { GameArguments = arg }, f.Metadata)); });
runner.Test("State directory overlapping native game rejected", () => { using var f = new Fixture(Metadata(), original); var unsafeService = new SteamBridgeService(new() { StateDirectory = Path.Combine(Path.GetDirectoryName(f.Native)!, "state"), SteamRoot = f.Steam, AppId = "4162040", RegisteredLibraries = [f.Library, f.Steam] }); Runner.Throws(() => unsafeService.Preview(f.Request, f.Metadata)); });
runner.Test("Double apply does not destroy previous ownership", () => { using var f = new Fixture(Metadata(), original); var id = f.Service.Apply(f.Request, f.Metadata).ReceiptId; Runner.Throws(() => f.Service.Apply(f.Request, f.Metadata)); Runner.Equal(id, f.Service.ReadReceipt()!.Id); f.Service.Restore(); });
runner.Test("Interrupted apply retains sufficient receipt for one-click restore", () => { using var f = new Fixture(Metadata(), original); var calls = 0; f.BusyProbe = () => ++calls == 3; Runner.Throws(() => f.Service.Apply(f.Request, f.Metadata)); Runner.Equal("recovery-required", f.Service.ReadReceipt()!.Phase); f.BusyProbe = null; f.Service.Restore(); Runner.True(!f.Service.Inspect().Installed); Runner.Equal(original, File.ReadAllText(f.Config)); });
runner.Test("Unknown payload blocks deletion but own launch option restores", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Apply(f.Request, f.Metadata).Preview!; File.WriteAllText(Path.Combine(p.PlaceholderDirectory, "downloaded.bin"), "foreign payload"); Runner.Throws(() => f.Service.Restore()); Runner.Equal(original, File.ReadAllText(f.Config)); Runner.Equal("foreign payload", File.ReadAllText(Path.Combine(p.PlaceholderDirectory, "downloaded.bin"))); Runner.True(f.Service.Inspect().Installed); });
runner.Test("Changed owned helper is never deleted", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Apply(f.Request, f.Metadata).Preview!; var placeholder = Path.Combine(p.PlaceholderDirectory, f.Metadata.Executable); File.AppendAllText(placeholder, "user change"); Runner.Throws(() => f.Service.Restore()); Runner.True(File.Exists(placeholder)); });
runner.Test("Repair reconstructs missing placeholder and manifest", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Apply(f.Request, f.Metadata).Preview!; File.Delete(p.ManifestPath); File.Delete(Path.Combine(p.PlaceholderDirectory, f.Metadata.Executable)); f.Service.Repair(f.Metadata with { BuildId = "25596036", FetchedAt = DateTimeOffset.UtcNow }); Runner.True(f.Service.Inspect().Conflicts.Count == 0); Runner.True(File.ReadAllText(p.ManifestPath).Contains("25596036")); f.Service.Restore(); });
runner.Test("Runtime LastPlayed LastOwner and unknown fields survive repair", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Apply(f.Request, f.Metadata).Preview!; var acf = File.ReadAllText(p.ManifestPath).Replace("\"LastOwner\"\t\t\"0\"", "\"LastOwner\"\t\t\"76561197960278073\"").Replace("\"AppState\"\n{", "\"AppState\"\n{\n\"LastPlayed\" \"123456\"\n\"FutureSteamField\" \"keep this text\""); File.WriteAllText(p.ManifestPath, acf); Runner.True(f.Service.Inspect().Conflicts.Count == 0); f.Service.Repair(f.Metadata with { BuildId = "25596037" }); var after = File.ReadAllText(p.ManifestPath); Runner.True(after.Contains("\"LastPlayed\" \"123456\"")); Runner.True(after.Contains("\"LastOwner\"\t\t\"76561197960278073\"")); Runner.True(after.Contains("\"FutureSteamField\" \"keep this text\"")); f.Service.Restore(); Runner.True(!File.Exists(p.ManifestPath)); });
runner.Test("Repair restores missing LaunchOptions while preserving unrelated changes", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Apply(f.Request, f.Metadata).Preview!; File.WriteAllText(f.Config, Vdf.SetLaunchOptions(File.ReadAllText(f.Config).Replace("中文好友", "new friend"), null, false)); f.Service.Repair(f.Metadata); Runner.Equal(p.LaunchOptions, Vdf.ReadLaunchOptions(File.ReadAllText(f.Config)).Value); Runner.True(File.ReadAllText(f.Config).Contains("new friend")); f.Service.Restore(); Runner.Equal("-screen-fullscreen 0", Vdf.ReadLaunchOptions(File.ReadAllText(f.Config)).Value); });
runner.Test("Repair restores empty LaunchOptions", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Apply(f.Request, f.Metadata).Preview!; File.WriteAllText(f.Config, Vdf.SetLaunchOptions(File.ReadAllText(f.Config), "")); f.Service.Repair(f.Metadata); Runner.Equal(p.LaunchOptions, Vdf.ReadLaunchOptions(File.ReadAllText(f.Config)).Value); });
runner.Test("Repair refuses nonempty user LaunchOptions", () => { using var f = new Fixture(Metadata(), original); f.Service.Apply(f.Request, f.Metadata); File.WriteAllText(f.Config, Vdf.SetLaunchOptions(File.ReadAllText(f.Config), "custom")); Runner.Throws(() => f.Service.Repair(f.Metadata)); Runner.Equal("custom", Vdf.ReadLaunchOptions(File.ReadAllText(f.Config)).Value); });
runner.Test("Pending download ACF state refused", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Apply(f.Request, f.Metadata).Preview!; File.WriteAllText(p.ManifestPath, File.ReadAllText(p.ManifestPath).Replace("\"BytesToDownload\"\t\t\"0\"", "\"BytesToDownload\"\t\t\"100\"")); Runner.Throws(() => f.Service.Repair(f.Metadata)); Runner.Throws(() => f.Service.Restore()); Runner.True(File.Exists(p.ManifestPath)); });
runner.Test("Tampered receipt cannot make restore write native directory", () => { using var f = new Fixture(Metadata(), original); f.Service.Apply(f.Request, f.Metadata); var receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(f.State, "active.json")))!; receipt["Request"]!["NativeExe"] = Path.Combine(f.Library, "steamapps", "common", f.Metadata.InstallDirectory, "ZenlessZoneZero.exe"); File.WriteAllText(Path.Combine(f.State, "active.json"), receipt.ToJsonString()); Runner.Throws(() => f.Service.Restore()); Runner.True(File.Exists(f.Native)); });
runner.Test("Repair interrupted after accepting runtime ACF still restores safely", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Apply(f.Request, f.Metadata).Preview!; File.WriteAllText(p.ManifestPath, File.ReadAllText(p.ManifestPath).Replace("\"AppState\"\n{", "\"AppState\"\n{\n\"LastPlayed\" \"123456\"")); var calls = 0; f.BusyProbe = () => ++calls == 2; Runner.Throws(() => f.Service.Repair(f.Metadata with { BuildId = "25596038" })); f.BusyProbe = null; Runner.Equal("recovery-required", f.Service.ReadReceipt()!.Phase); f.Service.Restore(); Runner.True(!f.Service.Inspect().Installed); Runner.True(!Directory.Exists(p.PlaceholderDirectory)); });
runner.Test("Repair interrupted after replacing files can restore owned hash alternatives", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Apply(f.Request, f.Metadata).Preview!; File.AppendAllText(f.Stub, "updated helper"); var calls = 0; f.BusyProbe = () => ++calls == 3; Runner.Throws(() => f.Service.Repair(f.Metadata with { BuildId = "25596039" })); f.BusyProbe = null; f.Service.Restore(); Runner.True(!f.Service.Inspect().Installed); Runner.True(!File.Exists(p.ManifestPath)); });
runner.Test("Unknown empty directory is never removed", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Apply(f.Request, f.Metadata).Preview!; var empty = Path.Combine(p.PlaceholderDirectory, "user-created-empty"); Directory.CreateDirectory(empty); Runner.Throws(() => f.Service.Restore()); Runner.True(Directory.Exists(empty)); });
runner.Test("Restore with wrong receipt ID leaves owned files unchanged", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Apply(f.Request, f.Metadata).Preview!; Runner.Throws(() => f.Service.Restore("wrong-id")); Runner.True(File.Exists(p.ManifestPath)); Runner.Equal(p.LaunchOptions, Vdf.ReadLaunchOptions(File.ReadAllText(f.Config)).Value); });
runner.Test("Receipt with explicit null state fails closed in inspect", () => { using var f = new Fixture(Metadata(), original); f.Service.Apply(f.Request, f.Metadata); var receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(f.State, "active.json")))!; receipt["OwnedFiles"] = null; File.WriteAllText(Path.Combine(f.State, "active.json"), receipt.ToJsonString()); var inspection = f.Service.Inspect(); Runner.True(inspection.Conflicts.Count > 0); Runner.Throws(() => f.Service.Restore()); });
runner.Test("Native game directory junction rejected", () => { using var f = new Fixture(Metadata(), original); var link = Path.Combine(f.Root, "NativeJunction"); f.Junction(link, Path.GetDirectoryName(f.Native)!); Runner.Throws(() => f.Service.Preview(f.Request with { NativeExe = Path.Combine(link, "ZenlessZoneZero.exe") }, f.Metadata)); Runner.True(File.Exists(f.Native)); });
runner.Test("State directory junction rejected before writes", () => { using var f = new Fixture(Metadata(), original); var target = Path.Combine(f.Root, "StateTarget"); Directory.CreateDirectory(target); f.Junction(f.StateRoot, target); Runner.Throws(() => f.Service.Preview(f.Request, f.Metadata)); Runner.Equal(0, Directory.GetFiles(target).Length); });
runner.Test("Nested placeholder junction refuses restore without touching target", () => { using var f = new Fixture(Metadata(), original); var p = f.Service.Apply(f.Request, f.Metadata).Preview!; var target = Path.Combine(f.Root, "OtherFiles"); Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "keep.txt"), "must remain"); f.Junction(Path.Combine(p.PlaceholderDirectory, "linked"), target); Runner.Throws(() => f.Service.Restore()); Runner.Equal("must remain", File.ReadAllText(Path.Combine(target, "keep.txt"))); });
runner.Test("Broken directory junction rejected", () => { using var f = new Fixture(Metadata(), original); var target = Path.Combine(f.Root, "RemovedTarget"); Directory.CreateDirectory(target); var link = Path.Combine(f.Root, "BrokenLibrary"); f.Junction(link, target); Directory.Delete(target); Runner.Throws(() => f.Service.Preview(f.Request with { LibraryRoot = link }, f.Metadata)); });


foreach (var preset in GamePreset.All.Where(p => p.Id != "custom"))
{
    runner.Test("Preset metadata independent mapping: " + preset.Id, () =>
    {
        var json = JsonNode.Parse(raw)!.AsObject(); var app = json["data"]!["4162040"]!.DeepClone(); json["data"]!.AsObject().Clear(); json["data"]![preset.AppId] = app;
        app["common"]!["name"] = preset.Name; app["config"]!["installdir"] = "Fixture-" + preset.Id;
        var m = SteamAppMetadata.ParseApiSteamCmdJson(json.ToJsonString(), preset.AppId);
        Runner.Equal(preset.AppId, m.AppId); Runner.Equal(preset.Name, m.Name);
    });
}
runner.Test("Custom AppID accepts actual title, not inferred local identity", () =>
{
    var json = JsonNode.Parse(raw)!.AsObject(); var app = json["data"]!["4162040"]!.DeepClone(); json["data"]!.AsObject().Clear(); json["data"]!["1234567"] = app; app["common"]!["name"] = "Actual custom title";
    Runner.Equal("Actual custom title", SteamAppMetadata.ParseApiSteamCmdJson(json.ToJsonString(), "1234567").Name);
    Runner.Equal("nap_cn", GamePreset.Find("zzz").StarwardBiz);
});
foreach (var id in new[] { "0", "-1", "01", "4294967296", "123/4", "" }) runner.Test("Invalid AppID " + id, () => Runner.Throws(() => SteamAppMetadata.ValidateAppId(id)));
runner.Test("Multiple Windows launches require choice then use selected executable", () =>
{
    var json = JsonNode.Parse(raw)!; var launch = json["data"]!["4162040"]!["config"]!["launch"]!.AsObject();
    launch["1"] = launch["0"]!.DeepClone(); launch["1"]!["executable"] = "alternate.exe";
    Runner.Equal(2, SteamAppMetadata.ReadLaunchChoices(json.ToJsonString(), "4162040").Count);
    Runner.Equal("alternate.exe", SteamAppMetadata.ParseApiSteamCmdJson(json.ToJsonString(), "4162040", "1").Executable);
    Runner.Throws(() => SteamAppMetadata.ParseApiSteamCmdJson(json.ToJsonString(), "4162040", "missing"));
});
runner.Test("Dynamic VDF edits isolate different apps in same account", () =>
{
    string text = ValveKeyValues.SetLaunchOptions(original, "1671200", "HI3 flags");
    Runner.Equal("-screen-fullscreen 0", Vdf.ReadLaunchOptions(text).Value); Runner.Equal("HI3 flags", ValveKeyValues.ReadLaunchOptions(text, "1671200").Value);
    text = ValveKeyValues.SetLaunchOptions(text, "1671200", null, false); Runner.Equal("-screen-fullscreen 0", Vdf.ReadLaunchOptions(text).Value);
});
runner.Test("Shared account concurrent apply repair restore preserve other game", () =>
{
    using var f = new Fixture(Metadata(), original);
    var secondMetadata = f.Metadata with { AppId = "1671200", Name = "Fixture HI3", InstallDirectory = "FixtureHI3" };
    var second = new SteamBridgeService(new() { StateDirectory = f.StateRoot, SteamRoot = f.Steam, AppId = "1671200", RegisteredLibraries = [f.Library, f.Steam] });
    var request2 = f.Request with { PresetId = "hi3", ExpectedOriginalLaunchOptions = null };
    Task.WaitAll(Task.Run(() => f.Service.Apply(f.Request, f.Metadata)), Task.Run(() => second.Apply(request2, secondMetadata)));
    Runner.Equal(0, f.Service.Inspect().Conflicts.Count); Runner.Equal(0, second.Inspect().Conflicts.Count);
    string command2 = second.ReadReceipt()!.Preview.LaunchOptions;
    File.WriteAllText(f.Config, Vdf.SetLaunchOptions(File.ReadAllText(f.Config), "")); f.Service.Repair(f.Metadata);
    Runner.Equal(command2, ValveKeyValues.ReadLaunchOptions(File.ReadAllText(f.Config), "1671200").Value);
    f.Service.Restore(); Runner.Equal(command2, ValveKeyValues.ReadLaunchOptions(File.ReadAllText(f.Config), "1671200").Value);
    second.Restore(); Runner.Equal("-screen-fullscreen 0", Vdf.ReadLaunchOptions(File.ReadAllText(f.Config)).Value); Runner.True(!ValveKeyValues.ReadLaunchOptions(File.ReadAllText(f.Config), "1671200").Exists); Runner.True(File.ReadAllText(f.Config).Contains("中文好友"));
});
runner.Test("Active identity keyed by install plus AppID even if manifest missing", () =>
{
    using var f = new Fixture(Metadata(), original); var preview = f.Service.Apply(f.Request, f.Metadata).Preview!; File.Delete(preview.ManifestPath);
    var other = new SteamBridgeService(new() { StateDirectory = f.StateRoot, SteamRoot = f.Steam, AppId = "4162040", RegisteredLibraries = [f.Library, f.Steam] });
    Runner.Throws(() => other.Apply(f.Request, f.Metadata)); Runner.Equal(f.Service.State, other.State);
});
runner.Test("Keep existing launch options does not change Steam or create receipt", () =>
{
    using var f = new Fixture(Metadata(), original); var result = f.Service.Apply(f.Request with { ExistingOptionsAction = ExistingLaunchOptionsAction.Keep }, f.Metadata);
    Runner.Equal("", result.ReceiptId); Runner.Equal(original, File.ReadAllText(f.Config)); Runner.True(!File.Exists(Path.Combine(f.State, "active.json")));
});
runner.Test("Unreviewed replacement and changed original rejected", () =>
{
    using var f = new Fixture(Metadata(), original);
    Runner.Throws(() => f.Service.Apply(f.Request with { OriginalOptionsReviewed = false }, f.Metadata));
    Runner.Throws(() => f.Service.Apply(f.Request with { ExpectedOriginalLaunchOptions = "stale" }, f.Metadata)); Runner.Equal(original, File.ReadAllText(f.Config));
});
runner.Test("Preserve arguments keeps order and restores exact original", () =>
{
    using var f = new Fixture(Metadata(), original);
    string combined = WindowsArguments.Join(WindowsArguments.Parse("--dx11 --label \"中文 空格\" --dx12").Concat(WindowsArguments.Parse("--dx11")));
    f.Service.Apply(f.Request with { ExistingOptionsAction = ExistingLaunchOptionsAction.PreserveArguments, GameArguments = combined }, f.Metadata);
    var spec = JsonSerializer.Deserialize<LaunchSpec>(File.ReadAllText(Path.Combine(f.State, "launch.json")))!;
    Runner.True(WindowsArguments.Parse(spec.Request.GameArguments).SequenceEqual(new[] { "--dx11", "--label", "中文 空格", "--dx12", "--dx11" }));
    f.Service.Restore(); Runner.Equal(original, File.ReadAllText(f.Config));
});
runner.Test("Windows argument roundtrip including empty quotes slash and Unicode", () =>
{
    string[] args = ["", "plain", "a b", "中文", "a\"b", @"C:\trailing\", @"C:\space folder\", "--dx11", "--dx12"];
    Runner.True(WindowsArguments.Parse(WindowsArguments.Join(args)).SequenceEqual(args));
    Runner.Throws(() => WindowsArguments.Parse("\"unclosed"));
});
runner.Test("Complete old commands detected without command nesting", () =>
{
    Runner.True(LaunchConfiguration.IsComplexCommand("\"C:\\工具\\wrapper.exe\" --game foo %command%"));
    Runner.True(LaunchConfiguration.IsComplexCommand("echo hello & exit")); Runner.True(!LaunchConfiguration.IsComplexCommand("--dx11 --label \"中文 空格\""));
});
runner.Test("Wrapper placeholders expand as argument arrays", () =>
{
    using var f = new Fixture(Metadata(), original);
    var values = LaunchConfiguration.ExpandLauncherArguments(f.Request with { LauncherArguments = "--game {game} -- {gameArgs} --steam-command {steamCommand}", GameArguments = "--dx11 \"中文 空格\"" }, ["C:\\Steam library\\HYP.exe", "--flag"]);
    Runner.True(values.SequenceEqual(new[] { "--game", f.Native, "--", "--dx11", "中文 空格", "--steam-command", "C:\\Steam library\\HYP.exe", "--flag" }));
    Runner.Throws(() => LaunchConfiguration.ExpandLauncherArguments(f.Request with { LauncherArguments = "--game={game}" }, []));
});
runner.Test("New profile store preserves multiple mappings, refuses duplicate install/AppID", () =>
{
    using var f = new Fixture(Metadata(), original); var store = new ProfileStore(f.StateRoot);
    var one = new GameProfile { Name = "一", AppId = "4162040", Request = f.Request }; store.Save(one);
    var two = new GameProfile { Name = "二", AppId = "1671200", Request = f.Request with { PresetId = "hi3" } }; store.Save(two);
    Runner.Equal(2, store.Load().Count); Runner.Throws(() => store.Save(new() { Name = "duplicate", AppId = "4162040", Request = f.Request }));
    store.Delete(one.Id); Runner.Equal(two.Id, store.Load().Single().Id);
});
runner.Test("Metadata for another AppID cannot be applied to current integration", () => { using var f = new Fixture(Metadata(), original); Runner.Throws(() => f.Service.Preview(f.Request, f.Metadata with { AppId = "1671200" })); });
runner.Test("Starward mapping follows preset independent of Steam AppID", () => { Runner.Equal("nap_cn", GamePreset.Find("zzz").StarwardBiz); Runner.Equal("bh3_cn", GamePreset.Find("hi3").StarwardBiz); Runner.True(GamePreset.Find("custom").StarwardBiz is null); });

foreach (var preset in GamePreset.All.Where(p => p.Id != "custom")) runner.Test("Public metadata snapshot 2026-10-23: " + preset.Id, () =>
{
    var snapshot = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", preset.AppId + ".json"));
    var entries = SteamAppMetadata.ReadLaunchChoices(snapshot, preset.AppId);
    var metadata = SteamAppMetadata.ParseApiSteamCmdJson(snapshot, preset.AppId, "0");
    Runner.Equal(preset.AppId, metadata.AppId); Runner.True(metadata.Depots.Count > 0);
    if (preset.Id == "endfield") Runner.Equal(2, entries.Count);
});

runner.Test("Starward mode preserves existing enum values", () => { Runner.Equal(0, (int)LaunchMode.Direct); Runner.Equal(1, (int)LaunchMode.OfficialLauncher); Runner.Equal(2, (int)LaunchMode.ExistingWrapper); Runner.Equal(3, (int)LaunchMode.Starward); });
foreach (string id in new[] { "zzz", "hi3" }) runner.Test("Starward start URI uses selected local path: " + id, () =>
{
    using var f = new Fixture(Metadata(), original);
    var request = f.Request with { PresetId = id, LaunchMode = LaunchMode.Starward };
    LaunchConfiguration.Validate(request);
    var uri = new Uri(LaunchConfiguration.BuildStarwardStartUri(request));
    Runner.Equal("startgame", uri.Host); Runner.Equal("/" + GamePreset.Find(id).StarwardBiz, uri.AbsolutePath);
    Runner.Equal(Path.GetDirectoryName(f.Native), Uri.UnescapeDataString(uri.Query.Split('=', 2)[1]));
});
runner.Test("Starward start rejects unsupported game and unsupported arguments", () =>
{
    using var f = new Fixture(Metadata(), original);
    Runner.Throws(() => LaunchConfiguration.Validate(f.Request with { PresetId = "endfield", LaunchMode = LaunchMode.Starward }));
    Runner.Throws(() => LaunchConfiguration.Validate(f.Request with { PresetId = "zzz", LaunchMode = LaunchMode.Starward, GameArguments = "--dx11" }));
});
runner.Test("Search finds executable in common game tree and companion program", () =>
{
    using var f = new Fixture(Metadata(), original);
    string folder = Path.Combine(f.Root, "games", "米哈游", "绝区零"); Directory.CreateDirectory(folder);
    string game = Path.Combine(folder, "ZenlessZoneZero.exe"); File.Copy(f.Native, game);
    string programs = Path.Combine(f.Root, "Program Files", "Starward"); Directory.CreateDirectory(programs); File.Copy(f.Helper, Path.Combine(programs, "Starward.exe"));
    var result = GameFileSearch.Find([Path.Combine(f.Root, "games"), Path.Combine(f.Root, "Program Files")], GameFileSearch.TargetNames("zzz"), true);
    Runner.Equal(game, result.Games.Single()); Runner.Equal(Path.Combine(programs, "Starward.exe"), result.StarwardExecutables.Single());
});
runner.Test("Search ignores own placeholder installation, wrong region and non-EXE files", () =>
{
    using var f = new Fixture(Metadata(), original);
    foreach (string name in new[] { "placeholder", "global", "text" }) Directory.CreateDirectory(Path.Combine(f.Root, "games", name));
    string placeholder = Path.Combine(f.Root, "games", "placeholder"); File.Copy(f.Native, Path.Combine(placeholder, "ZenlessZoneZero.exe")); File.WriteAllText(Path.Combine(placeholder, ".steam-game-bridge-owner.json"), "{}");
    string global = Path.Combine(f.Root, "games", "global"); File.Copy(f.Native, Path.Combine(global, "ZenlessZoneZero.exe")); File.WriteAllText(Path.Combine(global, "config.ini"), "game_biz=nap_global");
    File.WriteAllText(Path.Combine(f.Root, "games", "text", "ZenlessZoneZero.exe"), "not executable");
    Runner.Equal(0, GameFileSearch.Find([Path.Combine(f.Root, "games")], GameFileSearch.TargetNames("zzz"), gameBiz: "nap_cn").Games.Count);
});
runner.Test("Search returns all copies without choosing one", () =>
{
    using var f = new Fixture(Metadata(), original); string second = Path.Combine(f.Root, "another"); Directory.CreateDirectory(second); File.Copy(f.Native, Path.Combine(second, "ZenlessZoneZero.exe"));
    Runner.Equal(2, GameFileSearch.Find([Path.GetDirectoryName(f.Native)!, second, second], GameFileSearch.TargetNames("zzz")).Games.Count);
});
runner.Test("Search respects depth, directory and time limits", () =>
{
    using var f = new Fixture(Metadata(), original);
    Runner.Equal(0, GameFileSearch.Find([f.Root], GameFileSearch.TargetNames("zzz"), maxDepth: 0).Games.Count);
    Runner.True(GameFileSearch.Find([f.Root], GameFileSearch.TargetNames("zzz"), maxDirectories: 1).Limited);
    Runner.True(GameFileSearch.Find([f.Root], GameFileSearch.TargetNames("zzz"), timeLimit: TimeSpan.Zero).Limited);
});
runner.Test("Search skips directory junctions", () =>
{
    using var f = new Fixture(Metadata(), original); string root = Path.Combine(f.Root, "scan"); Directory.CreateDirectory(root); f.Junction(Path.Combine(root, "linked-game"), Path.GetDirectoryName(f.Native)!);
    Runner.Equal(0, GameFileSearch.Find([root], GameFileSearch.TargetNames("zzz")).Games.Count);
});
runner.Test("Search cancellation and custom target names", () =>
{
    using var f = new Fixture(Metadata(), original); using var token = new CancellationTokenSource(); token.Cancel();
    bool cancelled = false; try { GameFileSearch.Find([f.Root], GameFileSearch.TargetNames("zzz"), cancellation: token.Token); } catch (OperationCanceledException) { cancelled = true; }
    Runner.True(cancelled); Runner.Equal("Custom.exe", GameFileSearch.TargetNames("custom", Path.Combine(f.Root, "Custom.exe")).Single());
    Runner.Throws(() => GameFileSearch.TargetNames("custom"));
});

runner.Report();
return runner.Failed == 0 ? 0 : 1;

sealed class Runner
{
    public int Passed { get; private set; } public int Failed { get; private set; }
    public void Test(string name, Action body) { try { body(); Passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { Failed++; Console.WriteLine("FAIL " + name + ": " + ex); } }
    public static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
    public static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
    public static void Throws(Action action) { try { action(); } catch (BridgeException) { return; } throw new Exception("Expected BridgeException"); }
    public void Report() => Console.WriteLine($"\nRESULT {Passed} passed; {Failed} failed. All Steam/account/game fixtures were isolated under test output.");
}

sealed class Fixture : IDisposable
{
    public const string LoginUsers = "\"users\" { \"76561197960278073\" { \"PersonaName\" \"Fixture user\" \"MostRecent\" \"1\" } }";
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "isolated-fixtures", Guid.NewGuid().ToString("N"));
    public string Steam => Path.Combine(Root, "Steam"); public string Library => Path.Combine(Root, "Library"); public string StateRoot => Path.Combine(Root, "ToolState"); public string State => BridgeIdentity.IntegrationDirectory(StateRoot, Steam, "4162040");
    public string Config => Path.Combine(Steam, "userdata", "12345", "config", "localconfig.vdf");
    public string Native => Path.Combine(Root, "NativeCN", "ZenlessZoneZero.exe"); public string Helper => Path.Combine(Root, "Tool", "SteamGameBridge.exe");
    public string Stub => Path.Combine(Root, "Tool", "Placeholder.exe"); public SetupRequest Request => new(Steam, Library, Config, Native, Helper, Stub) { ExistingOptionsAction = ExistingLaunchOptionsAction.Replace, OriginalOptionsReviewed = true, ExpectedOriginalLaunchOptions = Vdf.ReadLaunchOptions(File.ReadAllText(Config)).Value };
    public SteamAppMetadata Metadata { get; } public SteamBridgeService Service { get; } public bool Busy { get; set; } public Func<bool>? BusyProbe { get; set; }
    private readonly List<string> junctions = [];
    public Fixture(SteamAppMetadata metadata, string config)
    {
        Metadata = metadata;
        foreach (var dir in new[] { Path.Combine(Steam, "steamapps"), Path.Combine(Steam, "config"), Path.GetDirectoryName(Config)!, Path.Combine(Library, "steamapps"), Path.GetDirectoryName(Native)!, Path.GetDirectoryName(Helper)! }) Directory.CreateDirectory(dir);
        foreach (var path in new[] { Path.Combine(Steam, "steam.exe"), Native, Helper, Stub }) File.WriteAllBytes(path, [(byte)'M', (byte)'Z', 1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Native)!, "config.ini"), "[General]\ngame_biz=nap_cn\n");
        File.WriteAllText(Config, config, new UTF8Encoding(false)); File.WriteAllText(Path.Combine(Steam, "config", "loginusers.vdf"), LoginUsers);
        File.WriteAllText(Path.Combine(Steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"0\" { \"path\" " + ValveKeyValues.Quote(Steam) + " } \"1\" { \"path\" " + ValveKeyValues.Quote(Library) + " } }");
        Service = new(new() { StateDirectory = StateRoot, SteamRoot = Steam, AppId = "4162040", RegisteredLibraries = [Steam, Library], IsBusy = () => Busy || BusyProbe?.Invoke() == true });
    }
    public void Dispose()
    {
        var allowedRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "isolated-fixtures")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture cleanup path");
        foreach (var link in junctions)
        {
            try { Directory.Delete(link, false); }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) when (!Directory.Exists(link)) { }
        }
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
    public void Junction(string link, string target)
    {
        var allowed = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(link).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !Path.GetFullPath(target).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe junction fixture path");
        var script = "New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null";
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi)!;
        if (!process.WaitForExit(10000)) { process.Kill(); throw new Exception("Junction fixture creation timed out"); }
        if (process.ExitCode != 0) throw new Exception("Junction fixture creation failed: " + process.StandardError.ReadToEnd());
        junctions.Add(link);
    }
}

static class Vdf
{
 public static (bool Exists, string? Value) ReadLaunchOptions(string text) => ValveKeyValues.ReadLaunchOptions(text, "4162040");
 public static string SetLaunchOptions(string text, string? value, bool exists = true) => ValveKeyValues.SetLaunchOptions(text, "4162040", value, exists);
}
