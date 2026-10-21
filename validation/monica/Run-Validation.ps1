[CmdletBinding()]
param(
    [string]$MonicaRoot = 'D:\Documents\Code\FIPS\MoLibrary'
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:GAUSSDB_TEST_CONNECTION)) {
    throw 'Set GAUSSDB_TEST_CONNECTION to the test server connection string before running.'
}
$sourceRoot = (Resolve-Path -LiteralPath $MonicaRoot).Path
$snapshotRoot = Join-Path $PSScriptRoot 'vendor\Monica'
if (Test-Path -LiteralPath $snapshotRoot) {
    throw "Snapshot already exists at $snapshotRoot. Use a fresh validation checkout to capture the latest Monica source."
}
New-Item -ItemType Directory -Path $snapshotRoot -Force | Out-Null
$projects = @('Monica.Tool', 'Monica.Core', 'Monica.DependencyInjection', 'Monica.Authority', 'Monica.EventBus', 'Monica.Repository')
foreach ($project in $projects) {
    & robocopy (Join-Path $sourceRoot $project) (Join-Path $snapshotRoot $project) /E /XD bin obj .git /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "Source snapshot failed for $project." }
}
Copy-Item -LiteralPath (Join-Path $sourceRoot 'Directory.Build.props') -Destination $snapshotRoot
Copy-Item -LiteralPath (Join-Path $sourceRoot '.editorconfig') -Destination $snapshotRoot
$sourceCommit = (& git -C $sourceRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the Monica source commit.' }
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'source-commit.txt') -Value $sourceCommit
$manifest = Get-ChildItem -LiteralPath $snapshotRoot -Recurse -File | ForEach-Object {
    [PSCustomObject]@{ Path = $_.FullName.Substring($snapshotRoot.Length + 1); SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'source-manifest.json')

& dotnet build (Join-Path $PSScriptRoot 'MonicaCurrentValidation.csproj') -c Release -m -p:IsPackable=false
if ($LASTEXITCODE -ne 0) { throw 'Monica integration validation did not build.' }
& dotnet run --no-build -c Release --project (Join-Path $PSScriptRoot 'MonicaCurrentValidation.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Monica integration validation failed. See bin/Release/net10.0/monica-results.json.' }
