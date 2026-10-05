[CmdletBinding()]
param(
    [ValidateSet('Docker', 'Portable')]
    [string] $DatabaseMode = 'Docker',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$previousTestConnection = $env:QUOTEENGINE_TEST_DATABASE
try {
    if ([string]::IsNullOrWhiteSpace($env:QUOTEENGINE_TEST_DATABASE)) {
        $env:QUOTEENGINE_TEST_DATABASE = & (Join-Path $PSScriptRoot 'start-test-db.ps1') -Mode $DatabaseMode
    }
    $solution = Join-Path $PSScriptRoot '..\QuoteEngine.sln'
    & dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
    & dotnet test $solution --configuration $Configuration --no-restore --logger 'trx;LogFilePrefix=stage1' --results-directory (Join-Path $PSScriptRoot '..\artifacts\test-results')
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
}
finally {
    $env:QUOTEENGINE_TEST_DATABASE = $previousTestConnection
}
