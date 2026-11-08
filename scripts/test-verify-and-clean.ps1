#requires -Version 7.0
[CmdletBinding()]
param([string]$ReportDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$temporary = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('Differ-verifier-tests-' + [Guid]::NewGuid().ToString('N'))))
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Differ\WorkspaceMaintenance\verifier-tests-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'))
}
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $temporary, $ReportDirectory -Force | Out-Null
$results = [Collections.Generic.List[string]]::new()
$lock = $null
$lockHeld = $false

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-Fixture([string]$Name, [string]$FailedStep = '') {
    $root = Join-Path $temporary $Name
    foreach ($directory in @('docs', 'scripts', 'src\Example\bin', 'fake-tools')) {
        New-Item -ItemType Directory -Path (Join-Path $root $directory) -Force | Out-Null
    }
    foreach ($file in @('Differ.sln', 'AGENTS.md', 'docs\DEVELOPMENT_SPEC.md', 'src\Example\Example.csproj', 'src\Example\bin\generated.dat')) {
        [IO.File]::WriteAllText((Join-Path $root $file), 'fixture')
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'clean-workspace.ps1') -Destination (Join-Path $root 'scripts\clean-workspace.ps1')
    $commands = @('@echo off', 'echo fake-dotnet %*')
    if ($FailedStep.Length -gt 0) { $commands += 'if "%1"=="' + $FailedStep + '" exit /b 7' }
    $commands += 'exit /b 0'
    [IO.File]::WriteAllLines((Join-Path $root 'fake-tools\dotnet.cmd'), $commands, [Text.Encoding]::ASCII)
    return $root
}

function Invoke-Verifier([string]$Root, [string]$Logs) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['PATH'] = (Join-Path $Root 'fake-tools') + ';' + $env:PATH
    foreach ($value in @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'verify-and-clean.ps1'), '-WorkspaceRoot', $Root, '-LogDirectory', $Logs)) {
        $start.ArgumentList.Add($value)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        Assert-True ($process.WaitForExit(30000)) 'Verification fixture exceeded 30 seconds.'
        return [pscustomobject]@{ Code = $process.ExitCode; Stdout = $stdout.Result; Stderr = $stderr.Result }
    }
    finally { $process.Dispose() }
}

try {
    $root = New-Fixture 'success'
    $logs = Join-Path $ReportDirectory 'success'
    $result = Invoke-Verifier $root $logs
    Assert-True ($result.Code -eq 0) "Successful verification fixture failed: $($result.Stderr)"
    $summary = Get-Content (Join-Path $logs 'summary.json') -Raw | ConvertFrom-Json
    Assert-True (($summary.Steps.Name -join ',') -eq 'build,test,format,cleanup-apply,cleanup-check') 'Required step order changed.'
    Assert-True ($summary.Success -and -not (Test-Path (Join-Path $root 'src\Example\bin'))) 'Successful run did not clean outputs.'
    Assert-True ((Get-Content (Join-Path $logs 'test.log') -Raw).Contains('--results-directory')) 'Tests did not receive external results path.'
    $results.Add('Success executes all required commands and reentrant-lock cleanup in order')

    foreach ($step in @('build', 'test', 'format')) {
        $root = New-Fixture ('fail-' + $step) $step
        $logs = Join-Path $ReportDirectory ('fail-' + $step)
        $result = Invoke-Verifier $root $logs
        Assert-True ($result.Code -eq 1) "Failed $step was reported successful."
        $summary = Get-Content (Join-Path $logs 'summary.json') -Raw | ConvertFrom-Json
        Assert-True (-not $summary.Success) 'Failure lost in summary.'
        Assert-True (-not (Test-Path (Join-Path $root 'src\Example\bin'))) "Failed $step skipped cleanup."
        Assert-True (($summary.Steps.Name | Select-Object -Last 2) -join ',' -eq 'cleanup-apply,cleanup-check') 'Failure skipped cleanup gate.'
        $results.Add("Failed $step still cleans and checks, preserves failed exit and external logs")
    }

    $root = New-Fixture 'invalid-logs'
    $result = Invoke-Verifier $root (Join-Path $root 'logs')
    Assert-True ($result.Code -eq 1 -and $result.Stderr.Contains('outside the workspace')) 'In-workspace log directory accepted.'
    Assert-True (Test-Path (Join-Path $root 'src\Example\bin')) 'Invalid log path mutated the fixture.'
    $results.Add('Invalid in-workspace log directory fails before any build or cleanup')

    $root = New-Fixture 'busy-lock'
    $identity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant()))).Substring(0, 32)
    $lock = [Threading.Mutex]::new($false, ('Local\Differ.WorkspaceMaintenance.' + $identity))
    $lockHeld = $lock.WaitOne(0)
    Assert-True $lockHeld 'Could not own isolated fixture lock.'
    $result = Invoke-Verifier $root (Join-Path $ReportDirectory 'busy-lock')
    Assert-True ($result.Code -eq 1 -and $result.Stderr.Contains('owns this workspace')) 'Active maintenance lock was not respected.'
    Assert-True (Test-Path (Join-Path $root 'src\Example\bin')) 'Busy lock allowed mutation.'
    $lock.ReleaseMutex()
    $lockHeld = $false
    $lock.Dispose()
    $lock = $null
    $results.Add('Shared maintenance lock blocks concurrent mutation before output creation')

    [pscustomobject]@{ RecordedUtc = [DateTime]::UtcNow; Passed = $results.Count; Method = 'Isolated fake-dotnet fixtures verify orchestration, not product build/test results'; Results = $results } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'summary.json') -Encoding utf8
    [pscustomobject]@{ Passed = $results.Count; ReportDirectory = $ReportDirectory }
}
finally {
    if ($lockHeld) { $lock.ReleaseMutex() }
    if ($null -ne $lock) { $lock.Dispose() }
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($temporary) -ne $parent -or [IO.Path]::GetFileName($temporary) -notlike 'Differ-verifier-tests-*') {
        throw "Refusing unexpected fixture cleanup path: $temporary"
    }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
