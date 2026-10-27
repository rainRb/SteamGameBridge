[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$All,
    [switch]$RunTests,
    [switch]$Package,
    [string]$Version,
    [string]$OutputDirectory = 'artifacts'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Build.ps1 requires Windows and the .NET 10 SDK.' }
if ($All -and $SelfContained) { throw 'Use either -All or -SelfContained, not both.' }

Push-Location -LiteralPath $PSScriptRoot
try {
    if ([IO.Path]::IsPathRooted($OutputDirectory)) { throw 'OutputDirectory must be relative to the checkout.' }
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
    if (-not $artifactRoot.StartsWith($PSScriptRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory must stay inside the checkout.' }
    $logRoot = Join-Path $artifactRoot 'test-results'
    New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot 'assets') | Out-Null

    function Invoke-DotNet {
        param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$LogName)
        & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $logRoot $LogName)
        if ($LASTEXITCODE -ne 0) { throw "dotnet operation failed; see artifacts/test-results/$LogName." }
    }

    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.0\.') { throw 'Install the stable .NET 10 SDK selected by global.json.' }
    $sdkLine = @(& dotnet --list-sdks) | Where-Object { $_ -match ('^' + [regex]::Escape($sdkVersion) + ' \[') } | Select-Object -First 1
    if (-not $sdkLine -or $sdkLine -notmatch '\[(.+)\]$') { throw 'Could not find the selected .NET SDK compiler directory.' }
    $compiler = Join-Path (Join-Path $Matches[1] $sdkVersion) 'Roslyn\bincore\csc.dll'
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The selected SDK has no Roslyn C# compiler.' }

    # Compile the fail-closed placeholder from source against Windows Framework
    # references. It needs no NuGet dependency and is embedded in the main app.
    $referenceRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
    if (-not (Test-Path -LiteralPath (Join-Path $referenceRoot 'mscorlib.dll'))) {
        $referenceRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    }
    $references = @('mscorlib.dll', 'System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll')
    $compilerArgs = @($compiler, '/nologo', '/noconfig', '/nostdlib+', '/target:winexe', '/platform:anycpu', '/optimize+', '/deterministic+', '/langversion:5', '/codepage:65001', '/utf8output', "/pathmap:$PSScriptRoot=/_/SteamGameBridge", '/out:assets\SteamPlaceholder.exe')
    foreach ($reference in $references) {
        $referencePath = Join-Path $referenceRoot $reference
        if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) { throw "Missing Windows Framework reference: $reference" }
        $compilerArgs += "/reference:$referencePath"
    }
    $compilerArgs += '.\src\SteamPlaceholder\Program.cs'
    Invoke-DotNet -Arguments $compilerArgs -LogName 'placeholder-build.txt'

    if ([string]::IsNullOrWhiteSpace($Version)) {
        [xml]$project = Get-Content -LiteralPath 'src\SteamGameBridge.App\SteamGameBridge.App.csproj' -Raw -Encoding UTF8
        $Version = [string]$project.Project.PropertyGroup.Version
    }
    if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
        throw 'Version must have three numeric parts and an optional prerelease suffix, for example 0.2.0-dev.'
    }

    $modes = @()
    if ($All -or -not $SelfContained -or $RunTests) { $modes += 'small' }
    if ($All -or $SelfContained) { $modes += 'self-contained' }
    foreach ($mode in $modes) {
        $selfContainedValue = if ($mode -eq 'self-contained') { 'true' } else { 'false' }
        $publishArgs = @('publish', 'src/SteamGameBridge.App/SteamGameBridge.App.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', $selfContainedValue, '-p:PublishSingleFile=true', "-p:Version=$Version", '-p:ContinuousIntegrationBuild=true', '-o', (Join-Path $artifactRoot $mode), '--nologo')
        Invoke-DotNet -Arguments $publishArgs -LogName "publish-$mode.txt"
        if (-not (Test-Path -LiteralPath (Join-Path $artifactRoot "$mode\SteamGameBridge.exe") -PathType Leaf)) { throw "Published executable missing for $mode." }
        $noticeStart = New-Object System.Diagnostics.ProcessStartInfo
        $noticeStart.FileName = Join-Path $artifactRoot "$mode\SteamGameBridge.exe"
        $noticeStart.Arguments = '--licenses'
        $noticeStart.UseShellExecute = $false; $noticeStart.CreateNoWindow = $true
        $noticeStart.RedirectStandardOutput = $true; $noticeStart.RedirectStandardError = $true
        $noticeProcess = [Diagnostics.Process]::Start($noticeStart)
        try {
            $noticeOutput = $noticeProcess.StandardOutput.ReadToEndAsync()
            $noticeError = $noticeProcess.StandardError.ReadToEndAsync()
            if (-not $noticeProcess.WaitForExit(30000)) { $noticeProcess.Kill(); throw 'License inspection timed out.' }
            $noticeText = $noticeOutput.GetAwaiter().GetResult()
            if ($noticeProcess.ExitCode -ne 0 -or -not $noticeText.Contains('MIT License')) { throw 'Embedded project license missing.' }
            foreach ($name in @('Licenses.NETCore.txt', 'Licenses.NETCore.ThirdParty.txt', 'Licenses.WindowsDesktop.txt')) {
                if ($mode -eq 'self-contained' -and -not $noticeText.Contains($name)) { throw "Embedded runtime notice missing: $name" }
                if ($mode -eq 'small' -and $noticeText.Contains($name)) { throw 'Small build contains stale runtime notices.' }
            }
            [IO.File]::WriteAllText((Join-Path $logRoot "licenses-$mode.txt"), $noticeText, [Text.UTF8Encoding]::new($false))
        } finally { $noticeProcess.Dispose() }

    }

    if ($RunTests) {
        # Render isolated forms offscreen, without controlling desktop windows.
        $uiArguments = @('run', '--project', 'tests/SteamGameBridge.Ui.Tests/SteamGameBridge.Ui.Tests.csproj', '-c', 'Release', '-r', 'win-x64', '-p:SelfContained=false')
        Invoke-DotNet -Arguments $uiArguments -LogName 'ui.txt'
        # Fixtures remain beneath this checkout. No real Steam or game process
        # is started, and no live Steam or game configuration is modified.
        Invoke-DotNet -Arguments @('run', '--project', 'tests/SteamGameBridge.Core.Tests/SteamGameBridge.Core.Tests.csproj', '-c', 'Release') -LogName 'core.txt'
        Invoke-DotNet -Arguments @('build', 'tests/SteamGameBridge.FakeGame/SteamGameBridge.FakeGame.csproj', '-c', 'Release', '--nologo') -LogName 'fakegame-build.txt'
        $fakeGame = Join-Path $PSScriptRoot 'tests\SteamGameBridge.FakeGame\bin\Release\net10.0\ZenlessZoneZero.exe'
        foreach ($mode in $modes) {
            $fixtureDirectory = Join-Path $artifactRoot "test-fixtures\$mode"
            $publishedExe = Join-Path $artifactRoot "$mode\SteamGameBridge.exe"
            Invoke-DotNet -Arguments @('run', '--project', 'tests/SteamGameBridge.Launcher.Tests/SteamGameBridge.Launcher.Tests.csproj', '-c', 'Release', '--', $fakeGame, $fixtureDirectory, $publishedExe) -LogName "launcher-$mode.txt"
        }
    }

    if ($Package) {
        $packageRoot = Join-Path $artifactRoot 'packages'
        New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
        $checksums = @()
        foreach ($mode in $modes) {
            $fileName = if ($mode -eq 'small') { 'SteamGameBridge-small.exe' } else { 'SteamGameBridge.exe' }
            $target = Join-Path $packageRoot $fileName
            Copy-Item -LiteralPath (Join-Path $artifactRoot "$mode\SteamGameBridge.exe") -Destination $target -Force
            $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
            $checksums += "$hash  $fileName"
        }
        [IO.File]::WriteAllLines((Join-Path $packageRoot 'SHA256SUMS.txt'), $checksums, [Text.UTF8Encoding]::new($false))
        Write-Host "Packages and SHA256SUMS.txt: $packageRoot"
    }

    Write-Host "Build finished. SDK $sdkVersion; version $Version; variants: $($modes -join ', ')."
}
finally { Pop-Location }
