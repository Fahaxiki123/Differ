#requires -Version 7.0
[CmdletBinding()]
param([string]$ReportDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'update-dev-preview.ps1')
$subjectSource = Join-Path $PSScriptRoot 'update-dev-preview.ps1'
$temporary = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('Differ-preview-update-tests-' + [Guid]::NewGuid().ToString('N'))))
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Differ\WorkspaceMaintenance\preview-tests-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'))
}
$ReportDirectory = Get-PreviewNormalPath $ReportDirectory
Assert-PreviewNoLinks $temporary
Assert-PreviewNoLinks $ReportDirectory
if (Test-Path -LiteralPath $ReportDirectory) { throw 'Use a new external test report directory.' }
if (Test-PreviewUnderPath $ReportDirectory ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')))) {
    throw 'Test reports must be outside the repository.'
}
New-Item -ItemType Directory -Path $temporary, $ReportDirectory | Out-Null
$results = [Collections.Generic.List[string]]::new()
$originalMove = (Get-Item Function:Move-PreviewDirectory).ScriptBlock
$originalProcesses = (Get-Item Function:Get-PreviewProcessRecords).ScriptBlock
$originalVersion = (Get-Item Function:Get-PreviewProductVersion).ScriptBlock
$script:moveFailure = ''
$script:moveCount = 0
# The fixture exercises real transaction/path/manifest code, not a .NET publisher.
# Override only PE metadata reads in a temporary test copy; production never sees this stub.
$versionStub = @'
function Get-PreviewProductVersion([string]$Path) { return [IO.File]::ReadAllText($Path) }
'@
$subject = Join-Path $temporary 'update-dev-preview-under-test.ps1'
$subjectText = [IO.File]::ReadAllText($subjectSource)
$subjectText = $subjectText.Replace('# Dot-sourcing exposes the same guarded transaction functions to isolated fixture tests.', $versionStub + "`r`n# Dot-sourcing exposes the same guarded transaction functions to isolated fixture tests.")
[IO.File]::WriteAllText($subject, $subjectText, [Text.UTF8Encoding]::new($false))
function Get-PreviewProductVersion([string]$Path) { return [IO.File]::ReadAllText($Path) }

function Assert-PreviewTest([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-PreviewThrows([scriptblock]$Action, [string]$Pattern) {
    $failure = $null
    try { & $Action } catch { $failure = $_ }
    Assert-PreviewTest ($null -ne $failure) "Expected failure matching $Pattern"
    Assert-PreviewTest ($failure.Exception.Message -match $Pattern) "Unexpected failure: $failure"
}

function New-FixturePayload([string]$Path, [string]$Version) {
    New-Item -ItemType Directory -Path $Path, (Join-Path $Path 'Editor\vs'), (Join-Path $Path 'licenses') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $Path 'Differ.App.dll'), "$Version-ui-workspace+fixture")
    Copy-Item -LiteralPath (Join-Path $Path 'Differ.App.dll') -Destination (Join-Path $Path 'Differ.Phase1.Preview.exe')
    foreach ($file in @('Differ.Core.dll', 'Differ.UI.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll',
            'WebView2Loader.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll',
            'vcruntime140.dll', 'Editor\index.html', 'Editor\editor.js', 'Editor\vs\loader.js', 'THIRD-PARTY-NOTICES.txt',
            'licenses\test-license.txt')) {
        [IO.File]::WriteAllText((Join-Path $Path $file), 'fixture-' + $file)
    }
    $deps = [ordered]@{
        runtimeTarget = @{ name = '.NETCoreApp,Version=v10.0/win-x64' }
        targets = @{ '.NETCoreApp,Version=v10.0/win-x64' = @{
                "Differ.App/$Version" = @{ runtime = @{ 'Differ.App.dll' = @{}; 'lib/net10.0/Differ.Core.dll' = @{} }; native = @{ 'WebView2Loader.dll' = @{} } }
            } }
    }
    $deps | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Path 'Differ.App.deps.json') -Encoding utf8
    @{ runtimeOptions = @{ includedFrameworks = @(@{ name = 'Microsoft.NETCore.App'; version = '10.0.10' }, @{ name = 'Microsoft.WindowsDesktop.App'; version = '10.0.10' }) } } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Path 'Differ.App.runtimeconfig.json') -Encoding utf8
}

function New-PreviewFixture([string]$Name, [switch]$WithoutPrevious) {
    $root = Join-Path $temporary $Name
    $repository = Join-Path $root 'repo'
    $previewRoot = Join-Path $root 'previews'
    $evidence = Join-Path $ReportDirectory $Name
    foreach ($relative in @('docs\third-party', 'scripts', 'src\Differ.App\Editor\vs')) {
        New-Item -ItemType Directory -Path (Join-Path $repository $relative) -Force | Out-Null
    }
    New-Item -ItemType Directory -Path $previewRoot -Force | Out-Null
    foreach ($file in @('Differ.sln', 'AGENTS.md', 'Directory.Build.props', 'Directory.Packages.props', 'scripts\collect-third-party-notices.ps1')) {
        [IO.File]::WriteAllText((Join-Path $repository $file), 'fixture')
    }
    [IO.File]::WriteAllText((Join-Path $repository 'src\Differ.App\Differ.App.csproj'), '<Project><PropertyGroup><Version>9.0.2</Version></PropertyGroup></Project>')
    foreach ($file in @('index.html', 'editor.js', 'vs\loader.js')) {
        [IO.File]::WriteAllText((Join-Path $repository ('src\Differ.App\Editor\' + $file)), 'fixture-Editor\' + $file)
    }
    $license = Join-Path $repository 'docs\third-party\test-license.txt'
    [IO.File]::WriteAllText($license, 'fixture-licenses\test-license.txt')
    @(@{ package = 'fixture'; files = @(@{ file = 'test-license.txt'; sha256 = (Get-FileHash -LiteralPath $license).Hash }) }) |
        ConvertTo-Json -Depth 5 -AsArray | Set-Content -LiteralPath (Join-Path $repository 'docs\third-party\manifest.json') -Encoding utf8
    $publisher = @'
param([string]$PreviewDirectory, [string]$LogDirectory)
$ErrorActionPreference='Stop'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\candidate-source') -Destination $PreviewDirectory -Recurse
$global:LASTEXITCODE=0
[pscustomobject]@{Log='fixture';Version='9.0.2-ui-workspace+fixture'}
'@
    [IO.File]::WriteAllText((Join-Path $repository 'scripts\publish.ps1'), $publisher)
    New-FixturePayload (Join-Path $repository 'candidate-source') '9.0.2'
    New-FixturePayload (Join-Path $previewRoot '开发预览') '9.0.1'
    if (-not $WithoutPrevious) { New-FixturePayload (Join-Path $previewRoot '.开发预览-previous') '9.0.0' }
    return New-PreviewContext $repository $previewRoot $evidence
}

function Invoke-PreviewCli($Context, [string]$Action, [string]$Acceptance = '') {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', $subject, '-Stage', $Action, '-WorkspaceRoot', $Context.Repository,
            '-PreviewRoot', $Context.Root, '-EvidenceDirectory', $Context.Evidence)) { $start.ArgumentList.Add($argument) }
    if ($Acceptance) { $start.ArgumentList.Add('-AcceptanceReport'); $start.ArgumentList.Add($Acceptance) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        Assert-PreviewTest ($process.WaitForExit(60000)) 'Preview fixture exceeded 60 seconds.'
        return [pscustomobject]@{ Code = $process.ExitCode; Output = $stdout.Result; Error = $stderr.Result }
    }
    finally { $process.Dispose() }
}

function Prepare-Fixture($Context) {
    $result = Invoke-PreviewCli $Context 'Prepare'
    Assert-PreviewTest ($result.Code -eq 0) "Fixture prepare failed: $($result.Error)"
    return Read-PreviewState $Context
}

function New-Acceptance($Context, $State, [string]$Hash = '') {
    if (-not $Hash) { $Hash = $State.Candidate.AppDllSha256 }
    $path = Join-Path $Context.Evidence 'acceptance.json'
    @{ Accepted = $true; Version = $State.Candidate.Version; AppDllSha256 = $Hash; PayloadSha256 = (Get-PreviewPayloadSha256 $State.Candidate) } |
        ConvertTo-Json | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}

try {
    $context = New-PreviewFixture 'complete'
    $state = Prepare-Fixture $context
    Assert-PreviewSnapshot $context $context.Paths.Current $state.CurrentBefore
    Assert-PreviewSnapshot $context $context.Paths.Previous $state.PreviousBefore
    $result = Invoke-PreviewCli $context 'Promote'
    Assert-PreviewTest ($result.Code -eq 0) "Promote failed: $($result.Error)"
    Assert-PreviewSnapshot $context $context.Paths.Current $state.Candidate
    Assert-PreviewSnapshot $context $context.Paths.Previous $state.CurrentBefore
    Assert-PreviewSnapshot $context $context.Paths.Retired $state.PreviousBefore
    $acceptance = New-Acceptance $context $state
    $result = Invoke-PreviewCli $context 'Finalize' $acceptance
    Assert-PreviewTest ($result.Code -eq 0) "Finalize failed: $($result.Error)"
    foreach ($name in @('Next', 'Previous', 'Retired')) { Assert-PreviewTest (-not (Test-Path -LiteralPath $context.Paths[$name])) "Temporary $name remains." }
    Assert-PreviewSnapshot $context $context.Paths.Current $state.Candidate
    $results.Add('Prepare leaves both old previews intact; promotion preserves both; accepted finalization leaves exactly one current preview')

    $context = New-PreviewFixture 'rollback'
    $state = Prepare-Fixture $context
    Invoke-PreviewPromotion $context $state
    Invoke-PreviewRollback $context $state
    Assert-PreviewSnapshot $context $context.Paths.Current $state.CurrentBefore
    Assert-PreviewSnapshot $context $context.Paths.Previous $state.PreviousBefore
    Assert-PreviewSnapshot $context $context.Paths.Next $state.Candidate
    Assert-PreviewTest (-not (Test-Path -LiteralPath $context.Paths.Retired)) 'Explicit rollback retained the retired slot.'
    $results.Add('Explicit rollback restores both original versions and retains the candidate for investigation')
    $result = Invoke-PreviewCli $context 'DiscardCandidate'
    Assert-PreviewTest ($result.Code -eq 0) "Discard candidate failed: $($result.Error)"
    Assert-PreviewTest (-not (Test-Path -LiteralPath $context.Paths.Next)) 'Rejected candidate was not removed.'
    Assert-PreviewSnapshot $context $context.Paths.Current $state.CurrentBefore
    Assert-PreviewSnapshot $context $context.Paths.Previous $state.PreviousBefore
    $results.Add('Discarding a recorded rejected candidate preserves both original previews')

    function Move-PreviewDirectory([string]$Source, [string]$Destination) {
        $script:moveCount++
        if (($script:moveFailure -eq 'candidate' -and $script:moveCount -eq 3) -or
            ($script:moveFailure -eq 'rollback' -and $script:moveCount -in @(3, 4))) { throw 'Injected move failure' }
        Move-Item -LiteralPath $Source -Destination $Destination
    }
    $context = New-PreviewFixture 'failed-promotion'
    $state = Prepare-Fixture $context
    $script:moveCount = 0
    $script:moveFailure = 'candidate'
    Assert-PreviewThrows { Invoke-PreviewPromotion $context $state } 'Injected move failure'
    Assert-PreviewSnapshot $context $context.Paths.Current $state.CurrentBefore
    Assert-PreviewSnapshot $context $context.Paths.Previous $state.PreviousBefore
    Assert-PreviewSnapshot $context $context.Paths.Next $state.Candidate
    Assert-PreviewTest ((Read-PreviewState $context).Phase -eq 'Prepared') 'Successful automatic rollback did not restore state.'
    $results.Add('Failed candidate move restores the active preview and the earlier rollback without deleting payloads')

    $context = New-PreviewFixture 'failed-recovery'
    $state = Prepare-Fixture $context
    $script:moveCount = 0
    $script:moveFailure = 'rollback'
    Assert-PreviewThrows { Invoke-PreviewPromotion $context $state } 'Rollback needs recovery'
    Assert-PreviewSnapshot $context $context.Paths.Previous $state.CurrentBefore
    Assert-PreviewSnapshot $context $context.Paths.Retired $state.PreviousBefore
    Assert-PreviewSnapshot $context $context.Paths.Next $state.Candidate
    Assert-PreviewTest ((Read-PreviewState $context).Phase -eq 'RecoveryRequired') 'Failed recovery was not recorded.'
    $results.Add('Failed rollback preserves all recoverable versions and records RecoveryRequired')
    Set-Item Function:Move-PreviewDirectory $originalMove

    $context = New-PreviewFixture 'modified-candidate'
    $state = Prepare-Fixture $context
    [IO.File]::AppendAllText((Join-Path $context.Paths.Next 'Editor\editor.js'), 'tamper')
    Assert-PreviewThrows { Invoke-PreviewPromotion $context $state } 'file changed'
    Assert-PreviewSnapshot $context $context.Paths.Current $state.CurrentBefore
    $results.Add('Changed candidate files are rejected before moving the current preview')

    $context = New-PreviewFixture 'missing-dependency'
    $candidateSource = Join-Path $context.Repository 'candidate-source'
    Remove-Item -LiteralPath (Join-Path $candidateSource 'WebView2Loader.dll')
    $result = Invoke-PreviewCli $context 'Prepare'
    Assert-PreviewTest ($result.Code -ne 0 -and $result.Error.Contains('dependency missing')) 'Missing WebView2 dependency was accepted.'
    Assert-PreviewTest ((Get-PreviewProductVersion (Join-Path $context.Paths.Current 'Differ.App.dll')) -like '9.0.1-*') 'Failed prepare changed the active preview.'
    $results.Add('A missing native dependency fails preparation while leaving current and previous intact')

    $context = New-PreviewFixture 'source-drift'
    $state = Prepare-Fixture $context
    [IO.File]::AppendAllText((Join-Path $context.Repository 'src\Differ.App\Editor\editor.js'), 'new source')
    Assert-PreviewThrows { Invoke-PreviewPromotion $context $state } 'Source changed'
    Assert-PreviewSnapshot $context $context.Paths.Current $state.CurrentBefore
    $results.Add('Source drift after publication blocks promotion of an outdated candidate')

    $context = New-PreviewFixture 'acceptance-mismatch' -WithoutPrevious
    $state = Prepare-Fixture $context
    Invoke-PreviewPromotion $context $state
    $acceptance = New-Acceptance $context $state 'wrong-hash'
    Assert-PreviewThrows { Invoke-PreviewFinalize $context $state $acceptance } 'Acceptance does not identify'
    Assert-PreviewSnapshot $context $context.Paths.Previous $state.CurrentBefore
    $results.Add('Acceptance for another binary cannot delete the rollback version')

    $context = New-PreviewFixture 'editor-only-change'
    $oldState = Prepare-Fixture $context
    $oldAcceptance = New-Acceptance $context $oldState
    $result = Invoke-PreviewCli $context 'DiscardCandidate'
    Assert-PreviewTest ($result.Code -eq 0) "Could not discard old fixture candidate: $($result.Error)"
    $oldStateHash = (Get-FileHash -LiteralPath $context.StateFile).Hash
    foreach ($editor in @(
            (Join-Path $context.Repository 'candidate-source\Editor\editor.js'),
            (Join-Path $context.Repository 'src\Differ.App\Editor\editor.js'))) {
        [IO.File]::AppendAllText($editor, 'new-js-only-fix')
    }
    $newContext = New-PreviewContext $context.Repository $context.Root (Join-Path $ReportDirectory 'editor-only-change-new')
    $newState = Prepare-Fixture $newContext
    Assert-PreviewTest ($newState.Candidate.Version -eq $oldState.Candidate.Version) 'Fixture changed the product version.'
    Assert-PreviewTest ($newState.Candidate.AppDllSha256 -eq $oldState.Candidate.AppDllSha256) 'Fixture changed the application binary.'
    Assert-PreviewTest ($newState.Candidate.PayloadSha256 -ne $oldState.Candidate.PayloadSha256) 'JS-only change did not change payload identity.'
    Invoke-PreviewPromotion $newContext $newState
    Assert-PreviewThrows { Invoke-PreviewFinalize $newContext $newState $oldAcceptance } 'Acceptance does not identify'
    Assert-PreviewSnapshot $newContext $newContext.Paths.Previous $newState.CurrentBefore
    Assert-PreviewSnapshot $newContext $newContext.Paths.Retired $newState.PreviousBefore
    Assert-PreviewTest ((Get-FileHash -LiteralPath $context.StateFile).Hash -eq $oldStateHash) 'New transaction rewrote the historical discarded record.'
    $newAcceptance = New-Acceptance $newContext $newState
    Invoke-PreviewFinalize $newContext $newState $newAcceptance
    $results.Add('Stale acceptance is rejected for identical version/App DLL but changed Editor; fresh full-payload acceptance succeeds')

    $context = New-PreviewFixture 'payload-normalization'
    $state = Prepare-Fixture $context
    $normal = $state.Candidate.PayloadSha256
    $snapshot = $state.Candidate | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
    [array]::Reverse($snapshot.Files)
    foreach ($file in $snapshot.Files) { $file.Path = $file.Path.Replace('\', '/') }
    Assert-PreviewTest ((Get-PreviewPayloadSha256 $snapshot) -eq $normal) 'File ordering or separators changed the canonical digest.'
    $snapshot.Files[0].Length = [long]$snapshot.Files[0].Length + 1
    Assert-PreviewTest ((Get-PreviewPayloadSha256 $snapshot) -ne $normal) 'Manifest length changes were not bound to payload identity.'
    $results.Add('Payload digest canonicalizes file order/separators while binding names, lengths, and hashes')

    $context = New-PreviewFixture 'legacy-read-only'
    $state = Prepare-Fixture $context
    $state.SchemaVersion = 1
    foreach ($snapshot in @($state.Candidate, $state.CurrentBefore, $state.PreviousBefore)) {
        if ($null -ne $snapshot) {
            $null = $snapshot.Remove('PayloadSha256')
            $null = $snapshot.Remove('PayloadHashAlgorithm')
        }
    }
    foreach ($phase in @('Prepared', 'Discarded')) {
        $state.Phase = $phase
        Write-PreviewState $context $state
        $beforeHash = (Get-FileHash -LiteralPath $context.StateFile).Hash
        $read = Read-PreviewState $context
        Assert-PreviewTest ($read.SchemaVersion -eq 1 -and -not $read.Candidate.Contains('PayloadSha256')) 'Reading migrated a historical schema 1 state.'
        Assert-PreviewTest ((Get-FileHash -LiteralPath $context.StateFile).Hash -eq $beforeHash) 'Reading rewrote historical state bytes.'
    }
    $results.Add('Legacy schema 1 Prepared/Discarded history is readable without migration or file modification')

    $context = New-PreviewFixture 'missing-payload-acceptance' -WithoutPrevious
    $state = Prepare-Fixture $context
    Invoke-PreviewPromotion $context $state
    $acceptance = Join-Path $context.Evidence 'old-style-acceptance.json'
    @{ Accepted = $true; Version = $state.Candidate.Version; AppDllSha256 = $state.Candidate.AppDllSha256 } |
        ConvertTo-Json | Set-Content -LiteralPath $acceptance -Encoding utf8
    Assert-PreviewThrows { Invoke-PreviewFinalize $context $state $acceptance } 'Acceptance does not identify'
    Assert-PreviewSnapshot $context $context.Paths.Previous $state.CurrentBefore
    $results.Add('Old acceptance containing only version and App DLL cannot finalize a payload')

    $context = New-PreviewFixture 'occupied'
    $state = Prepare-Fixture $context
    $script:occupiedPath = Join-Path $context.Paths.Current 'Differ.Phase1.Preview.exe'
    function Get-PreviewProcessRecords {
        return @([pscustomobject]@{ ProcessId = 999999; Name = 'Differ.Phase1.Preview.exe'; ExecutablePath = $script:occupiedPath; CommandLine = $script:occupiedPath })
    }
    Assert-PreviewThrows { Invoke-PreviewPromotion $context $state } 'Preview in use'
    Assert-PreviewSnapshot $context $context.Paths.Current $state.CurrentBefore
    Set-Item Function:Get-PreviewProcessRecords $originalProcesses
    $results.Add('Process ownership blocks promotion before any move without killing the process')

    $context = New-PreviewFixture 'linked-descendant'
    $state = Prepare-Fixture $context
    $outside = Join-Path $temporary 'outside-sentinel'
    New-Item -ItemType Directory -Path $outside | Out-Null
    [IO.File]::WriteAllText((Join-Path $outside 'keep.txt'), 'must survive')
    $link = Join-Path $context.Paths.Next 'linked'
    New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
    Assert-PreviewThrows { Invoke-PreviewPromotion $context $state } 'linked descendant'
    Assert-PreviewTest ([IO.File]::ReadAllText((Join-Path $outside 'keep.txt')) -eq 'must survive') 'Linked target was touched.'
    Remove-Item -LiteralPath $link -Force
    Assert-PreviewSnapshot $context $context.Paths.Current $state.CurrentBefore
    $results.Add('A reparse point anywhere in a payload is rejected without traversing its target')

    $context = New-PreviewFixture 'path-boundary'
    Assert-PreviewThrows { Assert-PreviewPayloadPath $context (Join-Path $context.Root 'unrelated') } 'Unexpected preview payload path'
    Assert-PreviewThrows { New-PreviewContext $context.Repository ([IO.Path]::GetPathRoot($context.Root)) $context.Evidence } 'drive root'
    Assert-PreviewThrows { New-PreviewContext $context.Repository (Join-Path $context.Repository 'preview') $context.Evidence } 'must be separate'
    $results.Add('Unexpected leaves, drive roots, and in-repository preview paths are rejected')

    $context = New-PreviewFixture 'linked-ancestor'
    $link = Join-Path $temporary 'linked-preview-parent'
    New-Item -ItemType Junction -Path $link -Target $context.Root | Out-Null
    Assert-PreviewThrows { New-PreviewContext $context.Repository $link $context.Evidence } 'linked ancestor'
    Remove-Item -LiteralPath $link -Force
    $results.Add('A linked preview parent is rejected before any payload operation')

    $context = New-PreviewFixture 'busy-lock'
    $identity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($context.Root.ToUpperInvariant()))).Substring(0, 32)
    $mutex = [Threading.Mutex]::new($false, 'Local\Differ.DevPreview.' + $identity)
    $held = $mutex.WaitOne(0)
    Assert-PreviewTest $held 'Could not own fixture preview lock.'
    try {
        $result = Invoke-PreviewCli $context 'Prepare'
        Assert-PreviewTest ($result.Code -ne 0 -and $result.Error.Contains('owns this workspace or preview')) 'Concurrent preview preparation ignored the mutex.'
        Assert-PreviewTest (-not (Test-Path -LiteralPath $context.Paths.Next)) 'Busy lock still prepared a candidate.'
    }
    finally { if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
    $results.Add('A second update process is rejected by the preview transaction lock')

    [ordered]@{ RecordedUtc = [DateTime]::UtcNow.ToString('o'); Passed = $results.Count;
        Method = 'Synthetic isolated payloads with PE-version metadata stub; real transaction/path/dependency/manifest code, fake publisher and injected move/process failures; no real preview, app launch, or SVN configuration touched'; Results = $results } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'summary.json') -Encoding utf8
    [pscustomobject]@{ Passed = $results.Count; ReportDirectory = $ReportDirectory }
}
finally {
    Set-Item Function:Move-PreviewDirectory $originalMove
    Set-Item Function:Get-PreviewProcessRecords $originalProcesses
    Set-Item Function:Get-PreviewProductVersion $originalVersion
    $parent = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
    if ([IO.Path]::GetDirectoryName($temporary) -ne $parent -or [IO.Path]::GetFileName($temporary) -notmatch '^Differ-preview-update-tests-[0-9a-f]{32}$') {
        throw "Unexpected fixture cleanup target: $temporary"
    }
    Assert-PreviewNoLinks $temporary
    if (Test-Path -LiteralPath $temporary) {
        # Only unlink test-created junctions; never follow them during fixture cleanup.
        $pending = [Collections.Generic.Stack[string]]::new()
        $pending.Push($temporary)
        while ($pending.Count) {
            foreach ($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
                if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { Remove-Item -LiteralPath $entry.FullName -Force }
                elseif ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
            }
        }
        $null = @(Get-PreviewFiles $temporary)
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
