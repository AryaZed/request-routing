<#
.SYNOPSIS
    Proves requirement 2 with two real OS processes: 20 concurrent requests for an expert with capacity 5.

.DESCRIPTION
    Starts two instances of the API on one throwaway database, sends the requests to both at once,
    prints the outcome from the database, then stops the instances and drops the database.
#>
param(
    [string] $Server = '.',
    [int] $PortA = 5101,
    [int] $PortB = 5102,
    [string] $ApiDll = ''
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Net.Http

$database = "RequestRoutingDemo_$([guid]::NewGuid().ToString('N'))"
$connectionString = "Server=$Server;Database=$database;Trusted_Connection=True;TrustServerCertificate=True"
$masterConnectionString = "Server=$Server;Database=master;Trusted_Connection=True;TrustServerCertificate=True"
$instances = @()
$http = [System.Net.Http.HttpClient]::new()

function Invoke-Scalar([string] $connection, [string] $sql) {
    $db = [System.Data.SqlClient.SqlConnection]::new($connection)
    $db.Open()
    try {
        $command = $db.CreateCommand()
        $command.CommandText = $sql
        return $command.ExecuteScalar()
    }
    finally {
        $db.Dispose()
    }
}

function Start-Instance([int] $port) {
    $env:ConnectionStrings__Default = $connectionString
    $env:Database__MigrateOnStartup = 'true'
    $env:ASPNETCORE_URLS = "http://localhost:$port"
    $env:ASPNETCORE_ENVIRONMENT = 'Production'

    if ($ApiDll) {
        return Start-Process dotnet -ArgumentList "`"$ApiDll`"" -PassThru -WindowStyle Hidden
    }

    $project = Join-Path $PSScriptRoot '..\src\RequestRouting.Api'
    return Start-Process dotnet -ArgumentList 'run', '--no-launch-profile', '--project', "`"$project`"" -PassThru -WindowStyle Hidden
}

function Wait-Until-Ready([int] $port) {
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        try {
            if ($http.GetAsync("http://localhost:$port/health/ready").GetAwaiter().GetResult().IsSuccessStatusCode) {
                return
            }
        }
        catch {
            # The instance is still starting.
        }
        Start-Sleep -Milliseconds 300
    }
    throw "The instance on port $port did not become ready."
}

function New-JsonContent([string] $json) {
    return [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
}

try {
    Write-Host "Database: $database"
    # The first instance creates and migrates the database, so it must be ready before the second one starts.
    $instances += Start-Instance $PortA
    Wait-Until-Ready $PortA
    $instances += Start-Instance $PortB
    Wait-Until-Ready $PortB
    Write-Host "Two instances are running: process ids $($instances.Id -join ', ')"

    $expertJson = '{"name":"Only expert","capacity":5,"topics":["finance"]}'
    $created = $http.PostAsync("http://localhost:$PortA/api/experts", (New-JsonContent $expertJson)).GetAwaiter().GetResult()
    if (-not $created.IsSuccessStatusCode) { throw "Could not create the expert: $($created.StatusCode)" }

    $tasks = 0..19 | ForEach-Object {
        $port = if ($_ % 2 -eq 0) { $PortA } else { $PortB }
        $body = "{`"requestKey`":`"demo-$_`",`"topic`":`"finance`",`"title`":`"Request $_`",`"priority`":`"Normal`"}"
        $http.PostAsync("http://localhost:$port/api/requests", (New-JsonContent $body))
    }
    [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]] $tasks)

    # Every key is sent a second time, to the other instance: these retries must create nothing.
    $retries = 0..19 | ForEach-Object {
        $port = if ($_ % 2 -eq 0) { $PortB } else { $PortA }
        $body = "{`"requestKey`":`"demo-$_`",`"topic`":`"finance`",`"title`":`"Request $_`",`"priority`":`"Normal`"}"
        $http.PostAsync("http://localhost:$port/api/requests", (New-JsonContent $body))
    }
    [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]] $retries)

    $failed = @($tasks + $retries | Where-Object { -not $_.Result.IsSuccessStatusCode }).Count
    $total = Invoke-Scalar $connectionString 'SELECT COUNT(*) FROM ServiceRequests'
    $assigned = Invoke-Scalar $connectionString 'SELECT COUNT(*) FROM ServiceRequests WHERE Status = 2'
    $pending = Invoke-Scalar $connectionString 'SELECT COUNT(*) FROM ServiceRequests WHERE Status = 1'
    $openCount = Invoke-Scalar $connectionString 'SELECT OpenCount FROM Experts'

    Write-Host ''
    Write-Host "HTTP failures:      $failed"
    Write-Host "Requests stored:    $total (expected 20)"
    Write-Host "Assigned:           $assigned (expected 5)"
    Write-Host "Pending:            $pending (expected 15)"
    Write-Host "Expert open count:  $openCount (expected 5)"

    if ($failed -ne 0 -or $total -ne 20 -or $assigned -ne 5 -or $pending -ne 15 -or $openCount -ne 5) {
        throw 'The demo did not produce the expected result.'
    }

    Write-Host ''
    Write-Host 'PASS'
}
finally {
    foreach ($instance in $instances) {
        Get-CimInstance Win32_Process -Filter "ParentProcessId = $($instance.Id)" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Stop-Process -Id $instance.Id -Force -ErrorAction SilentlyContinue
    }
    $http.Dispose()
    Start-Sleep -Seconds 1
    [System.Data.SqlClient.SqlConnection]::ClearAllPools()
    Invoke-Scalar $masterConnectionString "IF DB_ID('$database') IS NOT NULL BEGIN ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]; END" | Out-Null
}
