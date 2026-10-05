[CmdletBinding()]
param(
    [ValidateSet('Docker', 'Portable')]
    [string] $Mode = 'Docker',
    [string] $DataRoot = (Join-Path $env:LOCALAPPDATA 'Codex\manufacturing-quote-test-postgres')
)

$ErrorActionPreference = 'Stop'
if ($Mode -eq 'Docker') {
    & docker compose -f (Join-Path $PSScriptRoot '..\docker-compose.yml') stop postgres
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop the project PostgreSQL container.' }
    exit 0
}

$pgCtl = Join-Path $DataRoot 'binaries-16.15-1\pgsql\bin\pg_ctl.exe'
$dataDirectory = Join-Path $DataRoot 'data'
if (-not (Test-Path -LiteralPath $pgCtl)) { throw 'Portable PostgreSQL is not installed at the specified DataRoot.' }
& $pgCtl stop -D $dataDirectory -m fast -w
if ($LASTEXITCODE -ne 0) { throw 'Could not stop the project PostgreSQL cluster.' }
# Data is intentionally preserved so the database can be restarted.
