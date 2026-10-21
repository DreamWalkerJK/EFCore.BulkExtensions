[CmdletBinding()]
param(
    [string]$FipsRoot = 'D:\Documents\Code\FIPS\FIPS2022',
    [string]$MonicaRoot = 'D:\Documents\Code\FIPS\MoLibrary',
    [switch]$SharedSuite
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:GAUSSDB_TEST_CONNECTION)) {
    throw 'Set GAUSSDB_TEST_CONNECTION before running.'
}
$fipsPath = (Resolve-Path -LiteralPath $FipsRoot).Path
$monicaPath = (Resolve-Path -LiteralPath $MonicaRoot).Path
$bulkPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$properties = @('-p:GeneratePackageOnBuild=false', '-p:NuGetAudit=false',
    "-p:DirectoryBuildTargetsPath=$PSScriptRoot/Integration.targets", "-p:FipsRoot=$fipsPath",
    "-p:MonicaRoot=$monicaPath", "-p:BulkExtensionsRoot=$bulkPath")
Push-Location -LiteralPath $PSScriptRoot
try {
    # Restore must run with the same replacement properties to exclude official Core assets.
    & dotnet run --project CurrentFips.csproj -c Release @properties
    if ($LASTEXITCODE -ne 0) { throw 'FIPS production model validation failed. See results.json.' }
    if ($SharedSuite) {
        if ([string]::IsNullOrWhiteSpace($env:FIPS_EFCORE_TEST_GAUSS)) {
            throw 'Set FIPS_EFCORE_TEST_GAUSS to a disposable database for the original shared suite.'
        }
        & dotnet test (Join-Path $fipsPath 'src/Tests/Test.Platform.Shared/Test.Platform.Shared.csproj') -c Release @properties --logger 'trx;LogFileName=fips-shared.trx'
        if ($LASTEXITCODE -ne 0) { throw 'The FIPS shared suite reported failures. Review its TRX, including the recorded baseline defects.' }
    }
}
finally { Pop-Location }
