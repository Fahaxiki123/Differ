#requires -Version 7.0
[CmdletBinding()]
param([string]$ReportDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$cleaner = Join-Path $PSScriptRoot 'clean-workspace.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('Differ-cleaner-tests-' + [Guid]::NewGuid().ToString('N'))
$temporary = [IO.Path]::GetFullPath($temporary)
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Differ\WorkspaceMaintenance\tests-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'))
}
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $temporary, $ReportDirectory -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
$heldProcess = $null
$links = [Collections.Generic.List[string]]::new()
$sequence = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Write-FixtureFile([string]$Root, [string]$Relative, [string]$Content = 'preserve') {
    $path = Join-Path $Root $Relative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($path)) -Force | Out-Null
    [IO.File]::WriteAllText($path, $Content)
}

function New-Workspace([string]$Name) {
    $root = Join-Path $temporary $Name
    Write-FixtureFile $root 'Differ.sln' 'Microsoft Visual Studio Solution File, Format Version 12.00'
    Write-FixtureFile $root 'AGENTS.md'
    Write-FixtureFile $root 'docs\DEVELOPMENT_SPEC.md'
    Write-FixtureFile $root 'src\Example\Example.csproj' '<Project Sdk="Microsoft.NET.Sdk" />'
    return $root
}

function Invoke-Cleaner([string]$Root, [string[]]$Options = @(), [string]$OutputPath) {
    $script:sequence++
    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $OutputPath = Join-Path $ReportDirectory ('case-' + $script:sequence + '.json')
    }
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($value in @('-NoProfile', '-File', $cleaner, '-WorkspaceRoot', $Root, '-ReportPath', $OutputPath) + $Options) {
        $start.ArgumentList.Add($value)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        Assert-True ($process.WaitForExit(30000)) 'Cleaner did not terminate within 30 seconds.'
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout.Result; Stderr = $stderr.Result; Report = $OutputPath }
    }
    finally { $process.Dispose() }
}

function Record-Pass([string]$Name) {
    $results.Add([pscustomobject]@{ Name = $Name; Passed = $true })
}

try {
    $root = New-Workspace 'allowlist'
    Write-FixtureFile $root 'samples\Gallery\Gallery.csproj' '<Project Sdk="Microsoft.NET.Sdk" />'
    $generated = @('src\Example\bin', 'src\Example\obj', 'scripts\__pycache__',
        'samples\Gallery\bin', 'samples\Gallery\obj',
        'artifacts\packaging\2026-09-25\tooling\build', 'artifacts\Differ-win-x64-staging')
    foreach ($directory in $generated) { Write-FixtureFile $root ($directory + '\generated.dat') 'rebuildable' }
    $protected = @('src\Example\Main.cs', 'samples\Gallery\MainWindow.xaml', '.svn\pristine\original', '测试用例\original.docx',
        'artifacts\publish\Differ-win-x64\keep.dll', 'artifacts\installers\Differ-Setup.exe',
        'artifacts\packaging\2026-10-24\webview2\MicrosoftEdgeWebView2RuntimeInstallerX64.exe')
    foreach ($path in $protected) { Write-FixtureFile $root $path }
    $inspect = Invoke-Cleaner $root
    Assert-True ($inspect.ExitCode -eq 0) "Inspect failed: $($inspect.Stderr)"
    foreach ($path in $generated) { Assert-True (Test-Path -LiteralPath (Join-Path $root $path)) 'Inspect changed a target.' }
    $inspection = Get-Content -LiteralPath $inspect.Report -Raw | ConvertFrom-Json
    Assert-True ($inspection.Targets.Count -eq $generated.Count -and $inspection.RemovedBytes -eq 0) 'Inspect did not report the exact plan.'
    Record-Pass 'Default inspection is read-only and reports exact allowlist'

    $check = Invoke-Cleaner $root @('-Check')
    Assert-True ($check.ExitCode -eq 1) 'Dirty Check must fail.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root $generated[0])) 'Check changed a target.'
    Record-Pass 'Check fails dirty without deleting'

    $apply = Invoke-Cleaner $root @('-Apply')
    Assert-True ($apply.ExitCode -eq 0) "Apply failed: $($apply.Stderr)"
    foreach ($path in $generated) { Assert-True (-not (Test-Path -LiteralPath (Join-Path $root $path))) "Target survived: $path" }
    foreach ($path in $protected) {
        Assert-True ((Get-Content -LiteralPath (Join-Path $root $path) -Raw) -eq 'preserve') "Protected content changed: $path"
    }
    $applied = Get-Content -LiteralPath $apply.Report -Raw | ConvertFrom-Json
    Assert-True ($applied.RemovedBytes -eq $applied.PlannedBytes -and $applied.RemovedBytes -gt 0) 'Deletion accounting mismatch.'
    Assert-True ((Invoke-Cleaner $root @('-Check')).ExitCode -eq 0) 'Clean Check failed.'
    Record-Pass 'Apply removes only allowed generated paths, preserves source/runtime/SVN/fixtures, and clean Check passes'

    Write-FixtureFile $root 'artifacts\unclassified-proof\keep.txt'
    Write-FixtureFile $root 'src\NotAProject\bin\keep.txt'
    Write-FixtureFile $root 'artifacts\packaging\2026-09-25\tooling\old-probe\keep.txt'
    $unknown = Invoke-Cleaner $root @('-Apply')
    Assert-True ($unknown.ExitCode -eq 1) 'Unclassified artifacts must keep the gate failing.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'artifacts\unclassified-proof\keep.txt')) 'Unknown artifact was removed.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'src\NotAProject\bin\keep.txt')) 'Unmarked source folder was removed.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'artifacts\packaging\2026-09-25\tooling\old-probe\keep.txt')) 'Unknown packaging data was removed.'
    $unknownReport = Get-Content -LiteralPath $unknown.Report -Raw | ConvertFrom-Json
    Assert-True ($unknownReport.Unclassified.Count -eq 3) 'Nested packaging residue was not reported.'
    Record-Pass 'Unknown artifacts, nested packaging and bin folders without project markers are reported but never deleted'

    $root = New-Workspace 'root-tool-output'
    foreach ($directory in @('.codex', '.codex\audit\empty', '.vs', 'TestResults')) {
        New-Item -ItemType Directory -Path (Join-Path $root $directory) -Force | Out-Null
    }
    Assert-True ((Invoke-Cleaner $root @('-Check')).ExitCode -eq 0) 'Empty tool directories must not fail the gate.'
    foreach ($path in @('.codex\audit\nested\evidence.json', '.vs\state.dat', 'TestResults\run\result.trx')) {
        Write-FixtureFile $root $path
    }
    $toolOutput = Invoke-Cleaner $root @('-Apply')
    Assert-True ($toolOutput.ExitCode -eq 1) 'Nonempty root tool output must fail the gate.'
    $toolReport = Get-Content -LiteralPath $toolOutput.Report -Raw | ConvertFrom-Json
    Assert-True ($toolReport.Targets.Count -eq 0 -and $toolReport.Unclassified.Count -eq 3) 'Tool output was not classified only as unknown.'
    foreach ($path in @('.codex\audit\nested\evidence.json', '.vs\state.dat', 'TestResults\run\result.trx')) {
        Assert-True ((Get-Content -LiteralPath (Join-Path $root $path) -Raw) -eq 'preserve') 'Root tool evidence was changed.'
    }
    Record-Pass 'Nonempty root TestResults, .vs and .codex/audit are reported but preserved; empty tool directories are allowed'

    $root = New-Workspace 'report-safety'
    Write-FixtureFile $root 'src\Example\bin\generated.dat'
    $inside = Invoke-Cleaner $root @('-Apply') (Join-Path $root 'report.json')
    Assert-True ($inside.ExitCode -eq 2) 'In-workspace report was accepted.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'src\Example\bin\generated.dat')) 'Unsafe report deleted files.'
    $existingReport = Join-Path $ReportDirectory 'existing.json'
    [IO.File]::WriteAllText($existingReport, 'do not overwrite')
    Assert-True ((Invoke-Cleaner $root @('-Apply') $existingReport).ExitCode -eq 2) 'Existing report was overwritten.'
    Assert-True ((Get-Content $existingReport -Raw) -eq 'do not overwrite') 'Existing report content changed.'
    Record-Pass 'Reports must be external and cannot overwrite previous evidence'

    $missing = Join-Path $temporary 'missing-markers'
    Write-FixtureFile $missing 'src\Example\bin\keep.txt'
    Assert-True ((Invoke-Cleaner $missing @('-Apply')).ExitCode -eq 2) 'Unmarked workspace was accepted.'
    $driveAttempt = Invoke-Cleaner ([IO.Path]::GetPathRoot($temporary)) @('-Apply')
    Assert-True ($driveAttempt.ExitCode -eq 2 -and $driveAttempt.Stderr.Contains('drive root cannot')) 'Drive-root guard did not run.'
    Assert-True ((Invoke-Cleaner $root @('-Apply', '-Check')).ExitCode -eq 2) 'Conflicting modes were accepted.'
    Record-Pass 'Root markers, drive-root rejection and exclusive modes are enforced'

    $outside = Join-Path $temporary 'outside'
    Write-FixtureFile $outside 'sentinel.txt'
    $root = New-Workspace 'linked-target'
    Write-FixtureFile $root 'src\Example\obj\early.dat'
    $link = Join-Path $root 'src\Example\bin'
    New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
    $links.Add($link)
    Assert-True ((Invoke-Cleaner $root @('-Apply')).ExitCode -eq 2) 'Linked target was accepted.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'src\Example\obj\early.dat')) 'Preflight deleted an earlier target.'
    Assert-True ((Get-Content (Join-Path $outside 'sentinel.txt') -Raw) -eq 'preserve') 'Linked external data changed.'
    Record-Pass 'Target junctions abort the whole plan before deletion'

    $root = New-Workspace 'linked-child'
    Write-FixtureFile $root 'src\Example\bin\generated.dat'
    $link = Join-Path $root 'src\Example\bin\escape'
    New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
    $links.Add($link)
    Assert-True ((Invoke-Cleaner $root @('-Apply')).ExitCode -eq 2) 'Nested junction was accepted.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'src\Example\bin\generated.dat')) 'Nested-link preflight changed files.'
    $rootLink = Join-Path $temporary 'workspace-link'
    New-Item -ItemType Junction -Path $rootLink -Target $root | Out-Null
    $links.Add($rootLink)
    Assert-True ((Invoke-Cleaner $rootLink @('-Apply')).ExitCode -eq 2) 'Linked workspace ancestor was accepted.'
    Record-Pass 'Nested links and linked workspace ancestors are rejected'

    $root = New-Workspace 'nested-metadata'
    Write-FixtureFile $root 'src\Example\bin\.svn\sentinel'
    Assert-True ((Invoke-Cleaner $root @('-Apply')).ExitCode -eq 2) 'Nested SVN metadata was accepted.'
    Assert-True (Test-Path -LiteralPath (Join-Path $root 'src\Example\bin\.svn\sentinel')) 'Nested SVN metadata was removed.'
    Record-Pass 'Unexpected nested version-control metadata prevents deletion'

    $root = New-Workspace 'active-process'
    Write-FixtureFile $root 'src\Example\bin\generated.dat'
    $holdExe = Join-Path $root 'src\Example\bin\hold-output.exe'
    Copy-Item -LiteralPath (Join-Path $env:WINDIR 'System32\ping.exe') -Destination $holdExe
    $start = [Diagnostics.ProcessStartInfo]::new($holdExe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.ArgumentList.Add('-t')
    $start.ArgumentList.Add('127.0.0.1')
    $heldProcess = [Diagnostics.Process]::Start($start)
    Assert-True (-not $heldProcess.HasExited) 'Process fixture did not start.'
    $active = Invoke-Cleaner $root @('-Apply')
    Assert-True ($active.ExitCode -eq 2) 'Active output executable was not blocked.'
    Assert-True (Test-Path -LiteralPath $holdExe) 'Running executable was removed.'
    $heldProcess.Kill()
    $heldProcess.WaitForExit()
    $heldProcess.Dispose()
    $heldProcess = $null
    Assert-True ((Invoke-Cleaner $root @('-Apply')).ExitCode -eq 0) 'Cleanup failed after fixture process ended.'
    Record-Pass 'Running output processes block Apply without being killed'

    [pscustomobject]@{ RecordedUtc = [DateTime]::UtcNow; Passed = $results.Count; Results = $results } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'summary.json') -Encoding utf8
    [pscustomobject]@{ Passed = $results.Count; ReportDirectory = $ReportDirectory }
}
finally {
    if ($null -ne $heldProcess) {
        if (-not $heldProcess.HasExited) { $heldProcess.Kill(); $heldProcess.WaitForExit() }
        $heldProcess.Dispose()
    }
    # Remove only the fixture junction itself, never recursively through it.
    foreach ($link in $links) {
        if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link) }
    }
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($temporary) -ne $parent -or [IO.Path]::GetFileName($temporary) -notlike 'Differ-cleaner-tests-*') {
        throw "Refusing unexpected fixture cleanup path: $temporary"
    }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
