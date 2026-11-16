#requires -Version 7.0
<#
.SYNOPSIS
Prepares, promotes, accepts, rolls back, or discards a development preview candidate.
.DESCRIPTION
Prepare uses publish.ps1 and leaves the active preview untouched. Promote retains
both the active preview and an existing older rollback until Finalize. Finalize
requires an explicit acceptance JSON for the complete promoted payload, including
Version, AppDllSha256, and PayloadSha256. No stage closes a
process or changes SVN settings. The JSON records the authorized task's completed
validation; it does not claim a separate user approval click. Keep
EvidenceDirectory for every stage of a run.
#>
[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Promote', 'Finalize', 'Rollback', 'DiscardCandidate')]
    [string]$Stage,
    [string]$EvidenceDirectory,
    [string]$PreviewRoot = 'D:\项目归档\Differ',
    [string]$WorkspaceRoot = (Join-Path $PSScriptRoot '..'),
    [string]$AcceptanceReport
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:PreviewPathComparison = [StringComparison]::OrdinalIgnoreCase

function Get-PreviewNormalPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathFullyQualified($Path)) {
        throw "An absolute path is required: $Path"
    }
    return [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
}

function Test-PreviewUnderPath([string]$Path, [string]$Parent) {
    return $Path.StartsWith($Parent + [IO.Path]::DirectorySeparatorChar, $script:PreviewPathComparison)
}

function Assert-PreviewNoLinks([string]$Path) {
    for ($ancestor = [IO.Path]::GetFullPath($Path); $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if ((Test-Path -LiteralPath $ancestor) -and
            ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a reparse point or linked ancestor: $ancestor"
        }
    }
}

function Get-PreviewFiles([string]$Path, [switch]$SourceTree) {
    Assert-PreviewNoLinks $Path
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    while ($pending.Count) {
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing a linked descendant: $($entry.FullName)"
            }
            if ($entry.PSIsContainer) {
                if ($entry.Name -in @('.svn', '.git')) { throw "Version-control data is not a preview payload: $($entry.FullName)" }
                if ($SourceTree -and $entry.Name -in @('bin', 'obj', '__pycache__')) { continue }
                $pending.Push($entry.FullName)
            }
            else { $entry }
        }
    }
}

function New-PreviewContext([string]$Repository, [string]$Root, [string]$Evidence) {
    $repositoryPath = Get-PreviewNormalPath $Repository
    $rootPath = Get-PreviewNormalPath $Root
    $evidencePath = Get-PreviewNormalPath $Evidence
    foreach ($path in @($repositoryPath, $rootPath, $evidencePath)) { Assert-PreviewNoLinks $path }
    if ($rootPath -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetPathRoot($rootPath))) {
        throw 'The preview parent cannot be a drive root.'
    }
    if ($rootPath -eq $repositoryPath -or (Test-PreviewUnderPath $rootPath $repositoryPath) -or
        (Test-PreviewUnderPath $repositoryPath $rootPath)) { throw 'Preview and repository trees must be separate.' }
    if ($rootPath -eq 'D:\Differ' -or (Test-PreviewUnderPath $rootPath 'D:\Differ')) {
        throw 'The installed SVN application is not a development preview parent.'
    }
    if ($evidencePath -eq $repositoryPath -or (Test-PreviewUnderPath $evidencePath $repositoryPath)) {
        throw 'Preview evidence must be outside the repository.'
    }
    foreach ($marker in @('Differ.sln', 'AGENTS.md', 'src\Differ.App\Differ.App.csproj', 'scripts\publish.ps1')) {
        $path = Join-Path $repositoryPath $marker
        Assert-PreviewNoLinks $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Repository marker missing: $path" }
    }
    $paths = [ordered]@{
        Current = Join-Path $rootPath '开发预览'
        Next = Join-Path $rootPath '.开发预览-next'
        Previous = Join-Path $rootPath '.开发预览-previous'
        Retired = Join-Path $rootPath '.开发预览-retired'
    }
    foreach ($path in $paths.Values) {
        if ($evidencePath -eq $path -or (Test-PreviewUnderPath $evidencePath $path) -or
            (Test-PreviewUnderPath $path $evidencePath)) { throw 'Evidence cannot contain or be inside a preview payload.' }
    }
    return [pscustomobject]@{
        Repository = $repositoryPath
        Root = $rootPath
        Evidence = $evidencePath
        Paths = $paths
        StateFile = Join-Path $evidencePath 'preview-state.json'
    }
}

function Assert-PreviewPayloadPath($Context, [string]$Path) {
    $full = Get-PreviewNormalPath $Path
    if ($full -notin $Context.Paths.Values -or [IO.Path]::GetDirectoryName($full) -ne $Context.Root) {
        throw "Unexpected preview payload path: $full"
    }
    Assert-PreviewNoLinks $full
    if (Test-Path -LiteralPath $full) {
        if (-not (Test-Path -LiteralPath $full -PathType Container)) { throw "Expected a preview directory: $full" }
        $null = @(Get-PreviewFiles $full)
    }
}

function Get-PreviewProcessRecords { return @(Get-CimInstance Win32_Process) }

function Assert-PreviewNotInUse($Context) {
    foreach ($process in Get-PreviewProcessRecords) {
        if ($process.ProcessId -eq $PID) { continue }
        $executable = [string]$process.ExecutablePath
        $command = [string]$process.CommandLine
        foreach ($path in $Context.Paths.Values) {
            if (($executable -and (Test-PreviewUnderPath $executable $path)) -or
                ($process.Name -match '^(dotnet|testhost|MSBuild|pwsh|powershell|node)\.exe$' -and
                    $command -and $command.Replace('/', '\').Contains($path, $script:PreviewPathComparison))) {
                throw "Preview in use by process $($process.ProcessId) ($($process.Name)); no process was stopped."
            }
        }
    }
}

function Get-PreviewProductVersion([string]$Path) { return (Get-Item -LiteralPath $Path).VersionInfo.ProductVersion }

function Get-PreviewPayloadSha256($Snapshot) {
    # Hash a stable, length-delimited manifest rather than locale-dependent JSON.
    # A script-only change must have a new identity even when the app DLL is unchanged.
    $entries = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $Snapshot.Files) {
        $path = ([string]$file.Path).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($path) -or [IO.Path]::IsPathRooted($path) -or $path.Contains(':') -or
            $path -match '[\x00-\x1f]' -or @($path -split '/' | Where-Object { $_ -in @('', '.', '..') }).Count) {
            throw "Invalid payload manifest path: $path"
        }
        if (-not $unique.Add($path)) { throw "Duplicate payload manifest path: $path" }
        $length = [long]$file.Length
        $hash = [string]$file.Sha256
        if ($length -lt 0 -or $hash -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid payload manifest metadata: $path" }
        $entries.Add($path, $length.ToString([Globalization.CultureInfo]::InvariantCulture) + ':' + $hash.ToUpperInvariant())
    }
    $canonical = [Text.StringBuilder]::new('DifferPreviewPayload/v1' + "`n")
    foreach ($entry in $entries.GetEnumerator()) {
        [void]$canonical.Append($entry.Key.Length.ToString([Globalization.CultureInfo]::InvariantCulture))
        [void]$canonical.Append(':').Append($entry.Key).Append(':').Append($entry.Value).Append("`n")
    }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical.ToString())))
}

function Assert-PreviewPayloadIdentity($Snapshot, [switch]$Required) {
    $identity = Get-PreviewPayloadSha256 $Snapshot
    if ($Snapshot.Contains('PayloadSha256')) {
        if (-not $Snapshot.Contains('PayloadHashAlgorithm') -or $Snapshot.PayloadHashAlgorithm -ne 'SHA256-DifferPreviewFiles-v1' -or
            $Snapshot.PayloadSha256 -ne $identity) { throw 'Payload digest does not match its complete file manifest.' }
    }
    elseif ($Required) { throw 'Schema 2 preview state is missing the complete payload digest.' }
    return $identity
}

function Assert-PreviewDependencyFiles([string]$Path, $Dependencies) {
    $targetName = [string]$Dependencies.runtimeTarget.name
    if (-not $targetName.EndsWith('/win-x64', [StringComparison]::OrdinalIgnoreCase) -or
        -not $Dependencies.targets.Contains($targetName)) { throw 'Expected a win-x64 dependency target.' }
    foreach ($library in $Dependencies.targets[$targetName].Values) {
        foreach ($kind in @('runtime', 'native', 'resources')) {
            if (-not $library.Contains($kind)) { continue }
            foreach ($asset in $library[$kind].GetEnumerator()) {
                $relative = [string]$asset.Key
                if ([IO.Path]::IsPathRooted($relative) -or ($relative -split '[\\/]') -contains '..') {
                    throw "Invalid dependency asset path: $relative"
                }
                if ([IO.Path]::GetFileName($relative) -eq '_._') { continue }
                $file = [IO.Path]::GetFileName($relative)
                if ($kind -eq 'resources') {
                    $locale = [string]$asset.Value.locale
                    if ($locale -notmatch '^[a-zA-Z0-9-]+$') { throw "Invalid resource locale for $relative" }
                    $file = Join-Path $locale $file
                }
                if (-not (Test-Path -LiteralPath (Join-Path $Path $relative) -PathType Leaf) -and
                    -not (Test-Path -LiteralPath (Join-Path $Path $file) -PathType Leaf)) {
                    throw "Published dependency is missing: $relative"
                }
            }
        }
    }
}

function Get-PreviewSnapshot($Context, [string]$Path, [string]$ExpectedVersion = '', [switch]$MatchSource) {
    Assert-PreviewPayloadPath $Context $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "Preview is missing: $Path" }
    foreach ($file in @('Differ.Phase1.Preview.exe', 'Differ.App.dll', 'Differ.App.deps.json', 'Differ.App.runtimeconfig.json',
            'Differ.Core.dll', 'Differ.UI.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll',
            'WebView2Loader.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll',
            'vcruntime140.dll', 'Editor\index.html', 'Editor\editor.js', 'Editor\vs\loader.js', 'THIRD-PARTY-NOTICES.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $file) -PathType Leaf)) { throw "Preview dependency missing: $file" }
    }
    foreach ($file in @('Uninstall.exe', 'Differ.installation.ini')) {
        if (Test-Path -LiteralPath (Join-Path $Path $file)) { throw 'Installed application data is not an isolated preview.' }
    }
    $version = Get-PreviewProductVersion (Join-Path $Path 'Differ.Phase1.Preview.exe')
    $assemblyVersion = Get-PreviewProductVersion (Join-Path $Path 'Differ.App.dll')
    if ($version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?-ui-workspace(?:\+.*)?$' -or $version -ne $assemblyVersion) {
        throw "Not a consistent isolated preview version: $version / $assemblyVersion"
    }
    if ($ExpectedVersion -and $version -notmatch ('^' + [regex]::Escape($ExpectedVersion) + '-ui-workspace(?:\+.*)?$')) {
        throw "Unexpected candidate version: $version"
    }
    $dependencies = Get-Content -LiteralPath (Join-Path $Path 'Differ.App.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $runtime = Get-Content -LiteralPath (Join-Path $Path 'Differ.App.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
    if (-not $runtime.runtimeOptions.Contains('includedFrameworks') -or
        @($runtime.runtimeOptions.includedFrameworks).Count -lt 2) { throw 'The preview must be self-contained.' }
    Assert-PreviewDependencyFiles $Path $dependencies
    if ($MatchSource) {
        $editorRoot = Join-Path $Context.Repository 'src\Differ.App\Editor'
        foreach ($file in Get-PreviewFiles $editorRoot) {
            $relative = [IO.Path]::GetRelativePath($editorRoot, $file.FullName)
            $copy = Join-Path (Join-Path $Path 'Editor') $relative
            if (-not (Test-Path -LiteralPath $copy -PathType Leaf) -or
                (Get-FileHash -LiteralPath $copy).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                throw "Candidate Editor differs from source: $relative"
            }
        }
        $licenseRoot = Join-Path $Context.Repository 'docs\third-party'
        $licenses = Get-Content -LiteralPath (Join-Path $licenseRoot 'manifest.json') -Raw | ConvertFrom-Json
        foreach ($entry in $licenses) {
            foreach ($file in $entry.files) {
                $copy = [IO.Path]::GetFullPath((Join-Path (Join-Path $Path 'licenses') $file.file))
                if (-not (Test-PreviewUnderPath $copy (Join-Path $Path 'licenses')) -or
                    -not (Test-Path -LiteralPath $copy -PathType Leaf) -or
                    (Get-FileHash -LiteralPath $copy).Hash -ne $file.sha256) { throw "Candidate license mismatch: $($file.file)" }
            }
        }
    }
    $files = @(Get-PreviewFiles $Path | Sort-Object FullName | ForEach-Object {
        [ordered]@{ Path = [IO.Path]::GetRelativePath($Path, $_.FullName); Length = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    $snapshot = [ordered]@{
        Version = $version
        AppDllSha256 = (Get-FileHash -LiteralPath (Join-Path $Path 'Differ.App.dll')).Hash
        Files = $files
    }
    $snapshot['PayloadHashAlgorithm'] = 'SHA256-DifferPreviewFiles-v1'
    $snapshot['PayloadSha256'] = Get-PreviewPayloadSha256 $snapshot
    return $snapshot
}

function Assert-PreviewSnapshot($Context, [string]$Path, $Snapshot) {
    Assert-PreviewPayloadPath $Context $Path
    $null = Assert-PreviewPayloadIdentity $Snapshot
    $actual = @(Get-PreviewFiles $Path)
    if ($actual.Count -ne @($Snapshot.Files).Count) { throw "Preview contents changed: $Path" }
    foreach ($file in $Snapshot.Files) {
        $full = [IO.Path]::GetFullPath((Join-Path $Path $file.Path))
        if (-not (Test-PreviewUnderPath $full $Path) -or -not (Test-Path -LiteralPath $full -PathType Leaf) -or
            (Get-Item -LiteralPath $full).Length -ne $file.Length -or
            (Get-FileHash -LiteralPath $full).Hash -ne $file.Sha256) { throw "Preview file changed: $full" }
    }
}

function Get-PreviewSourceIdentity($Context) {
    $files = [Collections.Generic.List[object]]::new()
    foreach ($relative in @('src', 'docs\third-party')) {
        foreach ($file in Get-PreviewFiles (Join-Path $Context.Repository $relative) -SourceTree) { $files.Add($file) }
    }
    foreach ($relative in @('Directory.Build.props', 'Directory.Packages.props', 'scripts\publish.ps1', 'scripts\collect-third-party-notices.ps1')) {
        $path = Join-Path $Context.Repository $relative
        Assert-PreviewNoLinks $path
        $files.Add((Get-Item -LiteralPath $path))
    }
    $records = @($files | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($Context.Repository, $_.FullName) + '|' + (Get-FileHash -LiteralPath $_.FullName).Hash
    })
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($records -join "`n"))))
}

function Write-PreviewState($Context, $State) {
    Assert-PreviewNoLinks $Context.StateFile
    $temporary = $Context.StateFile + '.tmp'
    Assert-PreviewNoLinks $temporary
    if (Test-Path -LiteralPath $temporary) { throw 'A preview state write needs recovery; refusing to overwrite it.' }
    $State.UpdatedUtc = [DateTime]::UtcNow.ToString('o')
    $State | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporary -Encoding utf8
    [IO.File]::Move($temporary, $Context.StateFile, $true)
}

function Read-PreviewState($Context) {
    Assert-PreviewNoLinks $Context.StateFile
    $state = Get-Content -LiteralPath $Context.StateFile -Raw | ConvertFrom-Json -AsHashtable
    if ($state.SchemaVersion -notin @(1, 2) -or $state.Repository -ne $Context.Repository -or $state.PreviewRoot -ne $Context.Root) {
        throw 'Preview state belongs to a different repository or preview root.'
    }
    # Historical schema 1 records remain readable without rewriting/migrating them.
    foreach ($snapshot in @($state.Candidate, $state.CurrentBefore, $state.PreviousBefore)) {
        if ($null -ne $snapshot) { $null = Assert-PreviewPayloadIdentity $snapshot -Required:($state.SchemaVersion -eq 2) }
    }
    return $state
}

function Move-PreviewDirectory([string]$Source, [string]$Destination) {
    Move-Item -LiteralPath $Source -Destination $Destination
}

function Move-CheckedPreview($Context, [string]$Source, [string]$Destination) {
    Assert-PreviewPayloadPath $Context $Source
    Assert-PreviewPayloadPath $Context $Destination
    Assert-PreviewNotInUse $Context
    if (Test-Path -LiteralPath $Destination) { throw "Destination is occupied: $Destination" }
    Move-PreviewDirectory $Source $Destination
}

function Invoke-PreviewPromotion($Context, $State) {
    if ($State.Phase -ne 'Prepared') { throw "Cannot promote preview state $($State.Phase)." }
    if (Test-Path -LiteralPath $Context.Paths.Retired) { throw 'Existing retired preview requires recovery.' }
    Assert-PreviewNotInUse $Context
    Assert-PreviewSnapshot $Context $Context.Paths.Current $State.CurrentBefore
    Assert-PreviewSnapshot $Context $Context.Paths.Next $State.Candidate
    if ($null -ne $State.PreviousBefore) { Assert-PreviewSnapshot $Context $Context.Paths.Previous $State.PreviousBefore }
    elseif (Test-Path -LiteralPath $Context.Paths.Previous) { throw 'An unrecorded previous preview appeared.' }
    if ((Get-PreviewSourceIdentity $Context) -ne $State.SourceIdentity) { throw 'Source changed after candidate preparation.' }
    $retiredMoved = $false
    $currentMoved = $false
    $candidateMoved = $false
    $State.Phase = 'Promoting'
    Write-PreviewState $Context $State
    try {
        if ($null -ne $State.PreviousBefore) {
            Move-CheckedPreview $Context $Context.Paths.Previous $Context.Paths.Retired
            $retiredMoved = $true
        }
        Move-CheckedPreview $Context $Context.Paths.Current $Context.Paths.Previous
        $currentMoved = $true
        Move-CheckedPreview $Context $Context.Paths.Next $Context.Paths.Current
        $candidateMoved = $true
        Assert-PreviewSnapshot $Context $Context.Paths.Current $State.Candidate
        $State.Phase = 'Promoted'
        Write-PreviewState $Context $State
    }
    catch {
        $originalFailure = $_
        try {
            if ($candidateMoved) { Move-CheckedPreview $Context $Context.Paths.Current $Context.Paths.Next }
            if ($currentMoved) { Move-CheckedPreview $Context $Context.Paths.Previous $Context.Paths.Current }
            if ($retiredMoved) { Move-CheckedPreview $Context $Context.Paths.Retired $Context.Paths.Previous }
            Assert-PreviewSnapshot $Context $Context.Paths.Current $State.CurrentBefore
            $State.Phase = 'Prepared'
            Write-PreviewState $Context $State
        }
        catch {
            $State.Phase = 'RecoveryRequired'
            $State.RecoveryError = $_.Exception.Message
            Write-PreviewState $Context $State
            throw "Preview promotion failed: $originalFailure Rollback needs recovery: $_. No payload was deleted."
        }
        throw $originalFailure
    }
}

function Invoke-PreviewRollback($Context, $State) {
    if ($State.Phase -ne 'Promoted') { throw 'Automatic rollback requires a fully promoted state; interrupted recovery needs inspection.' }
    Assert-PreviewNotInUse $Context
    Assert-PreviewSnapshot $Context $Context.Paths.Current $State.Candidate
    Assert-PreviewSnapshot $Context $Context.Paths.Previous $State.CurrentBefore
    if ($null -ne $State.PreviousBefore) { Assert-PreviewSnapshot $Context $Context.Paths.Retired $State.PreviousBefore }
    if (Test-Path -LiteralPath $Context.Paths.Next) { throw 'Cannot roll back into an occupied candidate directory.' }
    $candidateMoved = $false
    $previousMoved = $false
    $retiredMoved = $false
    try {
        Move-CheckedPreview $Context $Context.Paths.Current $Context.Paths.Next
        $candidateMoved = $true
        Move-CheckedPreview $Context $Context.Paths.Previous $Context.Paths.Current
        $previousMoved = $true
        if ($null -ne $State.PreviousBefore) {
            Move-CheckedPreview $Context $Context.Paths.Retired $Context.Paths.Previous
            $retiredMoved = $true
        }
        Assert-PreviewSnapshot $Context $Context.Paths.Current $State.CurrentBefore
        $State.Phase = 'Prepared'
        Write-PreviewState $Context $State
    }
    catch {
        $originalFailure = $_
        try {
            if ($retiredMoved) { Move-CheckedPreview $Context $Context.Paths.Previous $Context.Paths.Retired }
            if ($previousMoved) { Move-CheckedPreview $Context $Context.Paths.Current $Context.Paths.Previous }
            if ($candidateMoved) { Move-CheckedPreview $Context $Context.Paths.Next $Context.Paths.Current }
        }
        catch {
            $State.Phase = 'RecoveryRequired'
            $State.RecoveryError = $_.Exception.Message
            Write-PreviewState $Context $State
            throw "Preview rollback failed: $originalFailure Recovery failed: $_. No payload was deleted."
        }
        throw $originalFailure
    }
}

function Invoke-PreviewFinalize($Context, $State, [string]$Acceptance) {
    if ($State.Phase -notin @('Promoted', 'Finalizing')) { throw 'Only a promoted preview can be finalized.' }
    $acceptancePath = Get-PreviewNormalPath $Acceptance
    Assert-PreviewNoLinks $acceptancePath
    $accepted = Get-Content -LiteralPath $acceptancePath -Raw | ConvertFrom-Json -AsHashtable
    $payloadIdentity = Assert-PreviewPayloadIdentity $State.Candidate
    if ($accepted.Accepted -isnot [bool] -or -not $accepted.Accepted -or $accepted.Version -ne $State.Candidate.Version -or
        $accepted.AppDllSha256 -ne $State.Candidate.AppDllSha256 -or -not $accepted.Contains('PayloadSha256') -or
        $accepted.PayloadSha256 -ne $payloadIdentity) { throw 'Acceptance does not identify the complete promoted preview payload.' }
    Assert-PreviewNotInUse $Context
    Assert-PreviewSnapshot $Context $Context.Paths.Current $State.Candidate
    if (Test-Path -LiteralPath $Context.Paths.Next) { throw 'Unexpected candidate remains before finalization.' }
    $deletions = @(
        @{ Path = $Context.Paths.Previous; Snapshot = $State.CurrentBefore },
        @{ Path = $Context.Paths.Retired; Snapshot = $State.PreviousBefore }
    )
    foreach ($item in $deletions) {
        if (Test-Path -LiteralPath $item.Path) {
            if ($null -eq $item.Snapshot) { throw 'An unrecorded rollback directory appeared.' }
            Assert-PreviewSnapshot $Context $item.Path $item.Snapshot
        }
        elseif ($State.Phase -eq 'Promoted' -and $null -ne $item.Snapshot) { throw 'A recorded rollback directory is missing.' }
    }
    $State.Phase = 'Finalizing'
    $State.Acceptance = $acceptancePath
    Write-PreviewState $Context $State
    foreach ($item in $deletions) {
        if (Test-Path -LiteralPath $item.Path) {
            Assert-PreviewNotInUse $Context
            Assert-PreviewSnapshot $Context $Context.Paths.Current $State.Candidate
            Assert-PreviewSnapshot $Context $item.Path $item.Snapshot
            Remove-Item -LiteralPath $item.Path -Recurse -Force
        }
    }
    $State.Phase = 'Finalized'
    Write-PreviewState $Context $State
}

function Invoke-PreviewDiscardCandidate($Context, $State) {
    if ($State.Phase -ne 'Prepared') { throw 'Only a prepared candidate may be discarded; roll back a promoted candidate first.' }
    Assert-PreviewNotInUse $Context
    Assert-PreviewSnapshot $Context $Context.Paths.Current $State.CurrentBefore
    Assert-PreviewSnapshot $Context $Context.Paths.Next $State.Candidate
    if ($null -ne $State.PreviousBefore) { Assert-PreviewSnapshot $Context $Context.Paths.Previous $State.PreviousBefore }
    elseif (Test-Path -LiteralPath $Context.Paths.Previous) { throw 'An unrecorded previous preview appeared.' }
    if (Test-Path -LiteralPath $Context.Paths.Retired) { throw 'A retired preview still needs recovery.' }
    Remove-Item -LiteralPath $Context.Paths.Next -Recurse -Force
    $State.Phase = 'Discarded'
    Write-PreviewState $Context $State
}

# Dot-sourcing exposes the same guarded transaction functions to isolated fixture tests.
if ($MyInvocation.InvocationName -eq '.') { return }

$context = $null
$record = $null
$operationLog = $null
$locks = [Collections.Generic.List[Threading.Mutex]]::new()
try {
    if (-not $Stage) { throw 'Specify Prepare, Promote, Finalize, Rollback, or DiscardCandidate.' }
    $context = New-PreviewContext (Get-PreviewNormalPath $WorkspaceRoot) $PreviewRoot $EvidenceDirectory
    foreach ($path in $context.Paths.Values) { Assert-PreviewPayloadPath $context $path }
    foreach ($identity in @(
            @{ Prefix = 'Local\Differ.DevPreview.'; Path = $context.Root },
            @{ Prefix = 'Local\Differ.WorkspaceMaintenance.'; Path = $context.Repository })) {
        $key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity.Path.ToUpperInvariant()))).Substring(0, 32)
        $mutex = [Threading.Mutex]::new($false, $identity.Prefix + $key)
        try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
        if (-not $held) { $mutex.Dispose(); throw 'Another preview/publication/cleanup operation owns this workspace or preview.' }
        $locks.Add($mutex)
    }
    New-Item -ItemType Directory -Path $context.Evidence -Force | Out-Null
    $operationLog = Join-Path $context.Evidence ($Stage.ToLowerInvariant() + '-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff') + '.json')
    $record = [ordered]@{ Stage = $Stage; StartedUtc = [DateTime]::UtcNow.ToString('o'); CompletedUtc = $null; Success = $false; Error = $null }
    if ($Stage -eq 'Prepare') {
        if (Test-Path -LiteralPath $context.StateFile) { throw 'Use a new evidence directory; do not overwrite an earlier transaction.' }
        if ((Test-Path -LiteralPath $context.Paths.Next) -or (Test-Path -LiteralPath $context.Paths.Retired)) { throw 'Existing candidate/retired directory requires inspection before preparation.' }
        $current = Get-PreviewSnapshot $context $context.Paths.Current
        $previous = if (Test-Path -LiteralPath $context.Paths.Previous) { Get-PreviewSnapshot $context $context.Paths.Previous } else { $null }
        [xml]$project = Get-Content -LiteralPath (Join-Path $context.Repository 'src\Differ.App\Differ.App.csproj') -Raw
        $versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
        if ($null -eq $versionNode -or $versionNode.InnerText -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
            throw 'The application project must declare one numeric development version.'
        }
        $version = $versionNode.InnerText
        $sourceIdentity = Get-PreviewSourceIdentity $context
        & (Join-Path $context.Repository 'scripts\publish.ps1') -PreviewDirectory $context.Paths.Next -LogDirectory $context.Evidence
        if ($LASTEXITCODE -ne 0) { throw "Preview publication returned exit code $LASTEXITCODE." }
        $candidate = Get-PreviewSnapshot $context $context.Paths.Next $version -MatchSource
        if ((Get-PreviewSourceIdentity $context) -ne $sourceIdentity) { throw 'Source changed during preview publication.' }
        $state = [ordered]@{ SchemaVersion = 2; Repository = $context.Repository; PreviewRoot = $context.Root; UpdatedUtc = $null;
            Phase = 'Prepared'; SourceIdentity = $sourceIdentity; Candidate = $candidate; CurrentBefore = $current; PreviousBefore = $previous }
        Write-PreviewState $context $state
    }
    else {
        $state = Read-PreviewState $context
        switch ($Stage) {
            'Promote' { Invoke-PreviewPromotion $context $state }
            'Finalize' { Invoke-PreviewFinalize $context $state $AcceptanceReport }
            'Rollback' { Invoke-PreviewRollback $context $state }
            'DiscardCandidate' { Invoke-PreviewDiscardCandidate $context $state }
        }
    }
    $record.Success = $true
    [pscustomobject]@{ Stage = $Stage; Phase = $state.Phase; Version = $state.Candidate.Version; Current = $context.Paths.Current; StateFile = $context.StateFile }
}
catch {
    if ($null -ne $record) { $record.Error = $_.Exception.Message }
    throw
}
finally {
    try {
        if ($null -ne $record) {
            $record.CompletedUtc = [DateTime]::UtcNow.ToString('o')
            Assert-PreviewNoLinks $operationLog
            $record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $operationLog -Encoding utf8
        }
    }
    finally {
        for ($index = $locks.Count - 1; $index -ge 0; $index--) { $locks[$index].ReleaseMutex(); $locks[$index].Dispose() }
    }
}
