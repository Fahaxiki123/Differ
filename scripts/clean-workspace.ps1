#requires -Version 7.0
<#
.SYNOPSIS
Checks or removes only reproducible Differ build output.
.DESCRIPTION
Default: inspect and write an external JSON report. -Check exits 1 when the
workspace is not clean. -Apply removes the explicitly planned output directories;
unknown artifacts are reported for review and are never automatically removed.
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$Check,
    [string]$WorkspaceRoot = (Join-Path $PSScriptRoot '..'),
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$comparison = [StringComparison]::OrdinalIgnoreCase
$planned = [Collections.Generic.List[object]]::new()
$unclassified = [Collections.Generic.List[string]]::new()
$report = $null
$reportFile = $null
$maintenanceLock = $null
$ownsMaintenanceLock = $false

function Get-NormalPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Test-UnderPath([string]$Path, [string]$Parent) {
    return $Path.StartsWith($Parent + [IO.Path]::DirectorySeparatorChar, $comparison)
}

function Assert-NoReparseAncestors([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing a reparse point or linked ancestor: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ([string]::Equals($parent, $current, $comparison)) { break }
        $current = $parent
    }
}

function Get-TreeInventory([string]$Path) {
    Assert-NoReparseAncestors $Path
    $stack = [Collections.Generic.Stack[string]]::new()
    $stack.Push($Path)
    $bytes = 0L
    $count = 0L
    while ($stack.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $stack.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing a linked item within a cleanup target: $($item.FullName)"
            }
            if ($item.PSIsContainer) {
                if ($item.Name -in @('.svn', '.git')) {
                    throw "Refusing version-control metadata within a cleanup target: $($item.FullName)"
                }
                $stack.Push($item.FullName)
            }
            else {
                $bytes += $item.Length
                $count++
            }
        }
    }
    return [pscustomobject]@{ Bytes = $bytes; Files = $count }
}

function Assert-CleanupTarget([string]$Path) {
    $target = Get-NormalPath $Path
    if (-not (Test-UnderPath $target $repositoryRoot)) {
        throw "Cleanup target is outside the exact workspace: $target"
    }
    Assert-NoReparseAncestors $target
    $relative = [IO.Path]::GetRelativePath($repositoryRoot, $target)
    $allowed = $relative -in $fixedStagingPaths
    if ($relative -match '^(src|tests|scripts)\\(?:[^\\]+\\)*(bin|obj)$') {
        $projectDirectory = [IO.Path]::GetDirectoryName($target)
        $allowed = @(Get-ChildItem -LiteralPath $projectDirectory -File -Filter '*.csproj').Count -gt 0
    }
    elseif ($relative -match '^(src|tests|scripts)\\(?:[^\\]+\\)*__pycache__$') {
        $allowed = $true
    }
    if (-not $allowed) { throw "Target is not on the reproducible-output allowlist: $relative" }
    if (-not (Test-Path -LiteralPath $target -PathType Container)) {
        throw "Cleanup target is not an existing directory: $target"
    }
    return $target
}

function Add-CleanupTarget([string]$Path, [string]$Reason) {
    $target = Assert-CleanupTarget $Path
    $inventory = Get-TreeInventory $target
    $planned.Add([pscustomobject]@{
        Path = $target
        RelativePath = [IO.Path]::GetRelativePath($repositoryRoot, $target)
        Reason = $Reason
        Bytes = $inventory.Bytes
        Files = $inventory.Files
        Status = 'Planned'
    })
}

function Find-ProjectOutput([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    Assert-NoReparseAncestors $Path
    $stack = [Collections.Generic.Stack[string]]::new()
    $stack.Push($Path)
    while ($stack.Count -gt 0) {
        $directory = $stack.Pop()
        $hasProject = @(Get-ChildItem -LiteralPath $directory -File -Filter '*.csproj').Count -gt 0
        foreach ($child in Get-ChildItem -LiteralPath $directory -Directory -Force) {
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to traverse linked source/output directory: $($child.FullName)"
            }
            if ($child.Name -in @('.svn', '.git')) { continue }
            if ($child.Name -in @('bin', 'obj')) {
                if ($hasProject) { Add-CleanupTarget $child.FullName 'Project build output' }
                else { $unclassified.Add([IO.Path]::GetRelativePath($repositoryRoot, $child.FullName)) }
            }
            elseif ($child.Name -eq '__pycache__') {
                Add-CleanupTarget $child.FullName 'Python bytecode cache'
            }
            else { $stack.Push($child.FullName) }
        }
    }
}

function Find-BlockingProcesses {
    if (-not $IsWindows) { throw 'Apply requires Windows process inspection.' }
    $blocks = [Collections.Generic.List[object]]::new()
    foreach ($process in Get-CimInstance Win32_Process) {
        if ($process.ProcessId -eq $PID) { continue }
        $executable = [string]$process.ExecutablePath
        $command = [string]$process.CommandLine
        $reason = $null
        foreach ($target in $planned) {
            if (-not [string]::IsNullOrEmpty($executable) -and (Test-UnderPath $executable $target.Path)) {
                $reason = 'An executable is running from a cleanup target'
                break
            }
        }
        $mentionsWorkspace = $command.Replace('/', '\') -match ('(?i)' + [regex]::Escape($repositoryRoot) + '(?=[\\"\s]|$)')
        if ($null -eq $reason -and $process.Name -in @('pwsh.exe', 'powershell.exe') -and $mentionsWorkspace -and
            $command -match '(?i)(publish|build-installer|verify-and-clean)\.ps1') {
            $reason = 'A workspace publish/package/verification script is active'
        }
        if ($null -eq $reason -and $process.Name -in @('dotnet.exe', 'testhost.exe', 'testhost.x86.exe', 'MSBuild.exe', 'makensis.exe')) {
            if ($mentionsWorkspace) {
                $reason = 'A build/test/package process references this workspace'
            }
            elseif ($process.Name -eq 'dotnet.exe' -and $command -match '\s(build|test|run|publish|restore|format)\b') {
                # Win32_Process does not expose current directory. A relative build
                # command cannot safely be assigned to a different repository.
                if ($command -notmatch '(?i)(?:^|\s)(?:"[A-Z]:[\\/][^"]+\.(?:slnx?|[cfv]sproj)"|[A-Z]:[\\/][^\s"]+\.(?:slnx?|[cfv]sproj))(?=\s|$)') {
                    $reason = 'A relative dotnet operation has an unknown working directory'
                }
            }
        }
        if ($null -ne $reason) {
            $blocks.Add([pscustomobject]@{ Id = $process.ProcessId; Name = $process.Name; Reason = $reason })
        }
    }
    return $blocks.ToArray()
}

function Save-CleanupReport {
    if ($null -ne $report -and $null -ne $reportFile) {
        $report.CompletedUtc = [DateTime]::UtcNow
        $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportFile -Encoding utf8
    }
}

try {
    if ($Apply -and $Check) { throw 'Use -Apply or -Check, not both.' }
    $repositoryRoot = Get-NormalPath $WorkspaceRoot
    $driveRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($WorkspaceRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([string]::Equals($repositoryRoot, $driveRoot, $comparison)) {
        throw 'A drive root cannot be a Differ workspace.'
    }
    Assert-NoReparseAncestors $repositoryRoot
    foreach ($marker in @('Differ.sln', 'AGENTS.md', 'docs\DEVELOPMENT_SPEC.md')) {
        $path = Join-Path $repositoryRoot $marker
        Assert-NoReparseAncestors $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Workspace marker missing: $path" }
    }
    if ([string]::IsNullOrWhiteSpace($ReportPath)) {
        $reportDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Differ\WorkspaceMaintenance'
        $ReportPath = Join-Path $reportDirectory ('cleanup-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff') + '.json')
    }
    $reportFile = [IO.Path]::GetFullPath($ReportPath)
    if ([string]::Equals($reportFile, $repositoryRoot, $comparison) -or (Test-UnderPath $reportFile $repositoryRoot)) {
        throw 'Cleanup reports must be outside the workspace.'
    }
    Assert-NoReparseAncestors $reportFile
    if (Test-Path -LiteralPath $reportFile) { throw "Refusing to overwrite an existing report: $reportFile" }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($reportFile)) -Force | Out-Null
    $report = [ordered]@{
        Workspace = $repositoryRoot
        StartedUtc = [DateTime]::UtcNow
        CompletedUtc = $null
        Mode = $(if ($Apply) { 'Apply' } elseif ($Check) { 'Check' } else { 'Inspect' })
        Targets = $planned
        Unclassified = $unclassified
        BlockingProcesses = @()
        PlannedBytes = 0L
        RemovedBytes = 0L
        Clean = $false
        Error = $null
    }
    if ($Apply) {
        $identity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($repositoryRoot.ToUpperInvariant()))).Substring(0, 32)
        $maintenanceLock = [Threading.Mutex]::new($false, ('Local\Differ.WorkspaceMaintenance.' + $identity))
        try { $ownsMaintenanceLock = $maintenanceLock.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $ownsMaintenanceLock = $true }
        if (-not $ownsMaintenanceLock) { throw 'Another publish/package/cleanup operation owns this workspace.' }
    }
    $fixedStagingPaths = @('artifacts\packaging\2026-09-25\tooling\build', 'artifacts\Differ-win-x64-staging')
    foreach ($rootName in @('src', 'tests', 'scripts')) {
        Find-ProjectOutput (Join-Path $repositoryRoot $rootName)
    }
    foreach ($relative in $fixedStagingPaths) {
        $path = Join-Path $repositoryRoot $relative
        Assert-NoReparseAncestors $path
        if (Test-Path -LiteralPath $path) { Add-CleanupTarget $path 'Reproducible packaging/publish staging' }
    }
    # These tool-owned locations can contain useful diagnostics or settings.
    # Report files for review; do not add them to the deletion allowlist.
    foreach ($relative in @('TestResults', '.vs', '.codex\audit')) {
        $path = Join-Path $repositoryRoot $relative
        Assert-NoReparseAncestors $path
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $unclassified.Add($relative)
        }
        elseif (Test-Path -LiteralPath $path -PathType Container) {
            $pending = [Collections.Generic.Stack[string]]::new()
            $pending.Push($path)
            $hasFiles = $false
            while ($pending.Count -gt 0 -and -not $hasFiles) {
                foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
                    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                        throw "Refusing linked diagnostic directory entry: $($item.FullName)"
                    }
                    if ($item.PSIsContainer) { $pending.Push($item.FullName) }
                    else { $hasFiles = $true; break }
                }
            }
            if ($hasFiles) { $unclassified.Add($relative) }
        }
    }
    $artifacts = Join-Path $repositoryRoot 'artifacts'
    Assert-NoReparseAncestors $artifacts
    if (Test-Path -LiteralPath $artifacts -PathType Container) {
        foreach ($item in Get-ChildItem -LiteralPath $artifacts -Force) {
            if ($item.Name -notin @('publish', 'installers', 'packaging', 'Differ-win-x64-staging')) {
                $unclassified.Add([IO.Path]::GetRelativePath($repositoryRoot, $item.FullName))
            }
        }
        foreach ($entry in @(
                @{ Path = 'artifacts\publish'; Allowed = @('Differ-win-x64') },
                @{ Path = 'artifacts\installers'; Allowed = @('Differ-Setup.exe', 'Differ-Setup.exe.sha256', '新电脑安装与SVN说明.txt') },
                @{ Path = 'artifacts\packaging'; Allowed = @('2026-09-25', '2026-10-24') },
                @{ Path = 'artifacts\packaging\2026-09-25'; Allowed = @('tooling') },
                @{ Path = 'artifacts\packaging\2026-09-25\tooling'; Allowed = @('nsis-3.12', 'toolchain.json', 'build') },
                @{ Path = 'artifacts\packaging\2026-10-24'; Allowed = @('webview2') },
                @{ Path = 'artifacts\packaging\2026-10-24\webview2'; Allowed = @('MicrosoftEdgeWebView2RuntimeInstallerX64.exe', 'manifest.json') })) {
            $directory = Join-Path $repositoryRoot $entry.Path
            Assert-NoReparseAncestors $directory
            if (Test-Path -LiteralPath $directory -PathType Container) {
                foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
                    if ($item.Name -notin $entry.Allowed) {
                        $unclassified.Add([IO.Path]::GetRelativePath($repositoryRoot, $item.FullName))
                    }
                }
            }
        }
    }
    foreach ($target in $planned) { $report.PlannedBytes += $target.Bytes }
    Save-CleanupReport
    if ($Apply) {
        $report.BlockingProcesses = @(Find-BlockingProcesses)
        if ($report.BlockingProcesses.Count -gt 0) {
            throw 'Cleanup blocked by active build/test/package processes; see report. No process was stopped.'
        }
        # Validate the complete plan again before the first deletion; a late link
        # must not allow a partial cleanup before a later unsafe target is noticed.
        foreach ($target in $planned) {
            $null = Assert-CleanupTarget $target.Path
            $null = Get-TreeInventory $target.Path
        }
        foreach ($target in $planned) {
            $null = Assert-CleanupTarget $target.Path
            $null = Get-TreeInventory $target.Path
            Remove-Item -LiteralPath $target.Path -Recurse -Force
            if (Test-Path -LiteralPath $target.Path) { throw "Cleanup target still exists: $($target.Path)" }
            $target.Status = 'Removed'
            $report.RemovedBytes += $target.Bytes
            Save-CleanupReport
        }
    }
    $report.Clean = $unclassified.Count -eq 0 -and ($Apply -or $planned.Count -eq 0)
    Save-CleanupReport
    [pscustomobject]@{
        Mode = $report.Mode
        Targets = $planned.Count
        PlannedMiB = [Math]::Round($report.PlannedBytes / 1MB, 2)
        RemovedMiB = [Math]::Round($report.RemovedBytes / 1MB, 2)
        Unclassified = $unclassified.Count
        Clean = $report.Clean
        Report = $reportFile
    }
    if (($Check -or $Apply) -and -not $report.Clean) { exit 1 }
    exit 0
}
catch {
    if ($null -ne $report) {
        $report.Error = $_.Exception.Message
        Save-CleanupReport
    }
    Write-Error -ErrorAction Continue $_
    exit 2
}
finally {
    if ($ownsMaintenanceLock) { $maintenanceLock.ReleaseMutex() }
    if ($null -ne $maintenanceLock) { $maintenanceLock.Dispose() }
}
