param(
    [switch]$Benchmark,
    [int]$Repeats = 20,
    [string]$BenchmarkOutput = '',
    [string]$Framework = 'net8.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$engineRoot = Join-Path $repoRoot 'src/Lighting.Engine'
$manifest = Get-Content -LiteralPath (Join-Path $engineRoot 'upstream.lock.json') -Raw | ConvertFrom-Json
$expectedPaths = @($manifest.files | ForEach-Object { $_.local.Replace('\','/') } | Sort-Object)
$actualPaths = @(Get-ChildItem -LiteralPath (Join-Path $engineRoot 'Upstream') -Recurse -File -Filter '*.cs' |
    ForEach-Object { [IO.Path]::GetRelativePath($engineRoot, $_.FullName).Replace('\','/') } | Sort-Object)
if (Compare-Object $expectedPaths $actualPaths) { throw 'Upstream source file set differs from the pinned manifest.' }
foreach ($entry in $manifest.files) {
    if ($entry.imagingPatchSha256) {
        if ((Get-FileHash -LiteralPath (Join-Path $engineRoot $entry.local) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.imagingPatchSha256) { throw "Imaging overlay changed: $($entry.local)" }
        continue
    }
    $actual = git -C $repoRoot hash-object --no-filters (Join-Path $engineRoot $entry.local)
    if ($LASTEXITCODE -ne 0 -or $actual -ne $entry.gitBlob) { throw "Upstream source changed: $($entry.local)" }
}
if ($manifest.imaging -and (Get-FileHash -LiteralPath (Join-Path $engineRoot $manifest.imaging.file) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.imaging.sha256) { throw 'Calculated renderer differs from its shared manifest.' }
Write-Output "Verified $($manifest.files.Count) pinned sources and the calculated-imaging overlay."
$env:MSBuildEnableWorkloadResolver = 'false'
$project = Join-Path $PSScriptRoot 'Lighting.Baseline/Lighting.Baseline.csproj'
# One MSBuild node avoids sandbox worker-start failures. Restore uses the normal configured feeds/cache.
dotnet build $project -c Release -f $Framework --ignore-failed-sources --disable-build-servers -m:1 -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Baseline build failed.' }
$runner = Join-Path $PSScriptRoot "Lighting.Baseline/bin/Release/$Framework/Lighting.Baseline.dll"
$corpus = Join-Path $repoRoot 'tests/Lighting.Corpus'
if ($Benchmark) {
    if (!$BenchmarkOutput) { throw 'Provide -BenchmarkOutput for a new benchmark artifact; saved measurements are never overwritten implicitly.' }
    if (Test-Path -LiteralPath $BenchmarkOutput) { throw 'Benchmark output already exists. Choose a new output path.' }
    dotnet $runner benchmark $corpus $BenchmarkOutput $Repeats
} else {
    dotnet $runner verify $corpus
}
if ($LASTEXITCODE -ne 0) { throw 'Lighting characterization failed.' }
