#requires -Version 7.0
<#
.SYNOPSIS
Runs the required Release checks, then cleans and checks the workspace even on failure.
#>
[CmdletBinding()]
param(
    [string]$WorkspaceRoot = (Join-Path $PSScriptRoot '..'),
    [string]$LogDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$comparison = [StringComparison]::OrdinalIgnoreCase
$repositoryRoot = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$maintenanceLock = $null
$ownsMaintenanceLock = $false
$locationPushed = $false
$started = $false
$failed = $false
$summary = [ordered]@{ StartedUtc = [DateTime]::UtcNow; CompletedUtc = $null; Workspace = $repositoryRoot; Steps = @(); Error = $null; Success = $false }

function Assert-NoReparseAncestors([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing linked workspace or log ancestor: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::Equals($parent, $current, $comparison)) { break }
        $current = $parent
    }
}

function Invoke-RequiredStep([string]$Name, [string[]]$Arguments) {
    $log = Join-Path $LogDirectory ($Name + '.log')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $dotnet @Arguments *> $log
    $code = $LASTEXITCODE
    $summary.Steps += [pscustomobject]@{ Name = $Name; ExitCode = $code; Seconds = $watch.Elapsed.TotalSeconds; Log = $log }
    Write-Host "$Name finished with exit code $code."
    if ($code -ne 0) { throw "Required $Name failed. See $log" }
}

try {
    Assert-NoReparseAncestors $repositoryRoot
    foreach ($marker in @('Differ.sln', 'AGENTS.md', 'docs\DEVELOPMENT_SPEC.md', 'scripts\clean-workspace.ps1')) {
        $path = Join-Path $repositoryRoot $marker
        Assert-NoReparseAncestors $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Workspace marker missing: $path" }
    }
    if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
        $LogDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Differ\WorkspaceMaintenance\verify-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'))
    }
    $LogDirectory = [IO.Path]::GetFullPath($LogDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([string]::Equals($LogDirectory, $repositoryRoot, $comparison) -or $LogDirectory.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, $comparison)) {
        throw 'Verification logs must be outside the workspace.'
    }
    Assert-NoReparseAncestors $LogDirectory
    if (Test-Path -LiteralPath $LogDirectory) { throw 'Use a new external log directory to preserve previous evidence.' }
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $identity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($repositoryRoot.ToUpperInvariant()))).Substring(0, 32)
    $maintenanceLock = [Threading.Mutex]::new($false, ('Local\Differ.WorkspaceMaintenance.' + $identity))
    try { $ownsMaintenanceLock = $maintenanceLock.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMaintenanceLock = $true }
    if (-not $ownsMaintenanceLock) { throw 'Another publish/package/cleanup operation owns this workspace.' }
    New-Item -ItemType Directory -Path $LogDirectory | Out-Null
    $started = $true
    Push-Location -LiteralPath $repositoryRoot
    $locationPushed = $true
    Invoke-RequiredStep 'build' @('build', '-c', 'Release')
    Invoke-RequiredStep 'test' @('test', '-c', 'Release', '--no-build', '--logger', 'trx;LogFilePrefix=verify', '--results-directory', (Join-Path $LogDirectory 'test-results'))
    Invoke-RequiredStep 'format' @('format', 'Differ.sln', '--verify-no-changes', '--no-restore')
}
catch {
    $failed = $true
    $summary.Error = $_.Exception.Message
    Write-Error -ErrorAction Continue $_
}
finally {
    if ($started) {
        $cleaner = Join-Path $repositoryRoot 'scripts\clean-workspace.ps1'
        foreach ($mode in @('Apply', 'Check')) {
            $path = Join-Path $LogDirectory ('cleanup-' + $mode.ToLowerInvariant() + '.json')
            try {
                # Invoked synchronously in this runspace: the shared mutex is
                # reentrant on this thread and remains held across verification.
                $parameters = @{ WorkspaceRoot = $repositoryRoot; ReportPath = $path }
                $parameters[$mode] = $true
                & $cleaner @parameters *> (Join-Path $LogDirectory ('cleanup-' + $mode.ToLowerInvariant() + '.log'))
                $code = $LASTEXITCODE
                $summary.Steps += [pscustomobject]@{ Name = ('cleanup-' + $mode.ToLowerInvariant()); ExitCode = $code; Log = $path }
                if ($code -ne 0) { $failed = $true }
            }
            catch {
                $failed = $true
                $summary.Steps += [pscustomobject]@{ Name = ('cleanup-' + $mode.ToLowerInvariant()); ExitCode = 2; Error = $_.Exception.Message; Log = $path }
            }
        }
        $summary.CompletedUtc = [DateTime]::UtcNow
        $summary.Success = -not $failed
        $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $LogDirectory 'summary.json') -Encoding utf8
    }
    if ($locationPushed) { Pop-Location }
    if ($ownsMaintenanceLock) { $maintenanceLock.ReleaseMutex() }
    if ($null -ne $maintenanceLock) { $maintenanceLock.Dispose() }
}

if ($started) { Write-Host "Verification and cleanup report: $LogDirectory" }
if ($failed) { exit 1 }
exit 0
