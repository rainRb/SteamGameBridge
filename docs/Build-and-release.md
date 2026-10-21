# Build and release

Use Windows x64 and the stable .NET 10 SDK selected by `global.json`. The script uses that SDK's Roslyn compiler and Windows .NET Framework reference assemblies to generate `assets/SteamPlaceholder.exe` from `src/SteamPlaceholder/Program.cs`. The generated placeholder is embedded in the main app and is intentionally not committed. No game assets or Steam installation are needed to build.

From a clean checkout in PowerShell:

```powershell
./Build.ps1                         # Small single executable
./Build.ps1 -SelfContained          # Single executable including the runtime
./Build.ps1 -All -RunTests -Package # Both variants, fixture tests, ZIPs and hashes
```

`-RunTests` builds the isolated fake game, runs Core fixtures and tests every published launcher variant against that fake process. It checks arguments, Steam-context inheritance, process lifetime and exit status without starting real Steam or a real game, or editing their configuration. It does not validate actual Steam recognition, playtime, friends status, Overlay, login, anti-cheat acceptance or update persistence. Test counts are reported by the suites rather than used as fixed pass criteria, so adding meaningful tests does not require editing the build script.

Executables are `artifacts/small/ZZZSteamBridge.exe` and `artifacts/self-contained/ZZZSteamBridge.exe`. Packages are in `artifacts/packages/`; `SHA256SUMS.txt` covers both ZIPs. Packages include the executable, README, MIT license and documentation. The self-contained package also includes the .NET runtime and Windows Desktop redistribution licenses and third-party notices under `third-party/`, selected from the versions actually resolved during publish. PDB files and raw test logs are excluded. Staging and test files remain in ignored `artifacts/` directories. The script stages known deliverables rather than arbitrary files remaining from earlier builds and does not recursively clean user directories. The small variant requires the .NET 10 Desktop Runtime x64 on the target computer. Outputs are unsigned experimental builds.

An explicit prerelease version can be supplied:

```powershell
./Build.ps1 -All -RunTests -Package -Version 0.1.0-preview.1
```

GitHub Actions runs the same build, fixture tests and packaging on pushes, pull requests and manual runs, then uploads ZIPs/checksums and test logs as workflow artifacts. A pushed `v*` tag also creates an **experimental GitHub prerelease**, never a stable release, after the build passes. For example, `v0.1.0-preview.1` produces version `0.1.0-preview.1`. Creating and pushing a tag is an external publication action and should happen only when the repository owner intends to publish that version. The workflow uses only GitHub's automatic `GITHUB_TOKEN`, passed as the `GH_TOKEN` environment variable to `gh`; no personal token belongs in source files. Re-running a tag workflow can replace assets only on an existing prerelease; an existing stable release is left unchanged.

The workflow is `.github/workflows/build.yml`. Release text comes from `docs/ReleaseNotes.md`; update it before tagging a new version. The release contains two ZIP variants and SHA256 checksums; GitHub supplies source ZIP/tar archives for the tag automatically. A passed workflow demonstrates fixture/build coverage only. Keep the prerelease designation until real Steam scenarios have been explicitly verified and documented.
