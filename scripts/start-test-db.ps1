[CmdletBinding()]
param(
    [ValidateSet('Docker', 'Portable')]
    [string] $Mode = 'Docker',
    [ValidateRange(1024, 65535)]
    [int] $Port = 55432,
    [string] $DataRoot = (Join-Path $env:LOCALAPPDATA 'Codex\manufacturing-quote-test-postgres')
)

$ErrorActionPreference = 'Stop'
$databaseName = 'manufacturing_quote_test'
$databaseUser = 'quote_test'
$databasePassword = 'quote_test_local_only'
$connectionString = "Host=127.0.0.1;Port=$Port;Database=$databaseName;Username=$databaseUser;Password=$databasePassword"

if ($Mode -eq 'Docker') {
    if ($Port -ne 55432) { throw 'Docker mode uses the port 55432 defined in docker-compose.yml.' }
    $composeFile = Join-Path $PSScriptRoot '..\docker-compose.yml'
    & docker compose -f $composeFile up -d --wait postgres | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Docker PostgreSQL failed to start. On Windows you can use -Mode Portable.' }
    Write-Output $connectionString
    exit 0
}

if ($env:OS -ne 'Windows_NT') { throw 'Portable mode currently supports Windows only. Use Docker on other platforms.' }

$binaryVersion = '16.15-1'
$archiveName = "postgresql-$binaryVersion-windows-x64-binaries.zip"
$archivePath = Join-Path $DataRoot $archiveName
$binaryRoot = Join-Path $DataRoot "binaries-$binaryVersion"
$pgBin = Join-Path $binaryRoot 'pgsql\bin'
$dataDirectory = Join-Path $DataRoot 'data'
$logPath = Join-Path $DataRoot 'postgres.log'
$extractionMarker = Join-Path $binaryRoot '.extraction-complete'
New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $extractionMarker)) {
    if (-not (Test-Path -LiteralPath $archivePath)) {
        Write-Host "Downloading PostgreSQL $binaryVersion binaries from EDB..."
        & curl.exe --fail --location --silent --show-error --output $archivePath "https://get.enterprisedb.com/postgresql/$archiveName"
        if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL binary download failed.' }
    }
    Write-Host 'Extracting PostgreSQL binaries...'
    New-Item -ItemType Directory -Path $binaryRoot -Force | Out-Null
    # PostgreSQL only needs these directories; pgAdmin is unnecessary for tests.
    & tar.exe -xf $archivePath -C $binaryRoot pgsql/bin pgsql/lib pgsql/share pgsql/server_license.txt pgsql/commandlinetools_3rd_party_licenses.txt
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL binary extraction failed.' }
    [IO.File]::WriteAllText($extractionMarker, $binaryVersion)
}

if (-not (Test-Path -LiteralPath (Join-Path $dataDirectory 'PG_VERSION'))) {
    $passwordFile = Join-Path $DataRoot 'initdb-password.tmp'
    try {
        [IO.File]::WriteAllText($passwordFile, $databasePassword, [Text.UTF8Encoding]::new($false))
        & (Join-Path $pgBin 'initdb.exe') -D $dataDirectory -U $databaseUser --encoding=UTF8 --locale=C --auth=scram-sha-256 --pwfile=$passwordFile | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL cluster initialization failed.' }
    }
    finally {
        if (Test-Path -LiteralPath $passwordFile) { Remove-Item -LiteralPath $passwordFile -Force }
    }
}

& (Join-Path $pgBin 'pg_ctl.exe') status -D $dataDirectory *> $null
if ($LASTEXITCODE -ne 0) {
    $serverOptions = "-h 127.0.0.1 -p $Port -c timezone=UTC"
    # Use a hidden process; pg_ctl exits after starting the dedicated local cluster.
    $startInfo = @{
        FilePath = (Join-Path $pgBin 'pg_ctl.exe')
        ArgumentList = @('start', '-D', ('"' + $dataDirectory + '"'), '-l', ('"' + $logPath + '"'), '-o', ('"' + $serverOptions + '"'), '-w', '-t', '60')
        WindowStyle = 'Hidden'
        PassThru = $true
    }
    $startResult = Start-Process @startInfo
    # Start-Process -Wait also waits for postgres descendants on Windows.
    if (-not $startResult.WaitForExit(65000)) { throw "PostgreSQL startup timed out. See $logPath" }
    if ($startResult.ExitCode -ne 0) { throw "PostgreSQL failed to start. See $logPath" }
}

$previousPgPassword = $env:PGPASSWORD
try {
    $env:PGPASSWORD = $databasePassword
    $commonArguments = @('-h', '127.0.0.1', '-p', "$Port", '-U', $databaseUser, '-v', 'ON_ERROR_STOP=1')
    $exists = & (Join-Path $pgBin 'psql.exe') @commonArguments -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$databaseName'"
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL authentication/readiness check failed.' }
    if ($exists -ne '1') {
        & (Join-Path $pgBin 'createdb.exe') -h 127.0.0.1 -p $Port -U $databaseUser $databaseName | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Test database creation failed.' }
    }
    & (Join-Path $pgBin 'psql.exe') @commonArguments -d $databaseName -tAc 'SELECT version();' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Test database validation failed.' }
}
finally {
    $env:PGPASSWORD = $previousPgPassword
}

Write-Output $connectionString
