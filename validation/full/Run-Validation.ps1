[CmdletBinding()]
param(
    [string]$VolumeCounts = '10000,20000,50000,100000'
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:GAUSSDB_TEST_CONNECTION)) {
    throw 'Set GAUSSDB_TEST_CONNECTION to the test server connection string before running.'
}
$env:GAUSSDB_BULK_VOLUME_COUNTS = $VolumeCounts
$env:GAUSSDB_RESULTS_DIRECTORY = $PSScriptRoot
Push-Location -LiteralPath $PSScriptRoot
try {
    & dotnet run --project CurrentFull.csproj -c Release -p:GeneratePackageOnBuild=false
    if ($LASTEXITCODE -ne 0) { throw 'Full integration validation failed. See results.json.' }
}
finally {
    Pop-Location
}
