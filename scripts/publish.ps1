#requires -Version 7.0
[CmdletBinding()]
param([string]$LogDirectory, [string]$PreviewDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'Differ.sln') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'AGENTS.md') -PathType Leaf)) {
    throw 'The script must run from the Differ repository scripts directory.'
}
$publishRoot = Join-Path $repositoryRoot 'artifacts\publish'
$outputDirectory = Join-Path $publishRoot 'Differ-win-x64'
$stagingDirectory = Join-Path $repositoryRoot 'artifacts\Differ-win-x64-staging'
$previousDirectory = Join-Path $repositoryRoot 'artifacts\Differ-win-x64-previous'
$projectPath = Join-Path $repositoryRoot 'src\Differ.App\Differ.App.csproj'
$nugetSource = 'https://api.nuget.org/v3/index.json'
$preview = $PSBoundParameters.ContainsKey('PreviewDirectory')
$previewRuntime = '10.0.10'

if ($preview) {
    if ([string]::IsNullOrWhiteSpace($PreviewDirectory) -or -not [IO.Path]::IsPathFullyQualified($PreviewDirectory)) {
        throw 'PreviewDirectory must be a new absolute directory outside the repository.'
    }
    $outputDirectory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($PreviewDirectory))
    $publishRoot = [IO.Path]::GetDirectoryName($outputDirectory)
    if (-not $publishRoot -or
        $outputDirectory.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $outputDirectory.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Test-Path -LiteralPath $outputDirectory)) {
        throw 'PreviewDirectory must be a new absolute directory outside the repository.'
    }
    $stagingDirectory = Join-Path $publishRoot ('.differ-preview-staging-' + [Guid]::NewGuid().ToString('N'))
}

function Assert-NoReparsePath {
    param([Parameter(Mandatory)][string]$Path, [switch]$IncludeChildren)

    $current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($current)) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a reparse-point path: $current"
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    if ($IncludeChildren -and (Test-Path -LiteralPath $Path)) {
        foreach ($entry in Get-ChildItem -LiteralPath $Path -Force -Recurse) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing a reparse point inside an artifact tree: $($entry.FullName)"
            }
        }
    }
}

function Assert-ArtifactPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$RelativePath)

    $resolved = [IO.Path]::GetFullPath($Path)
    $expected = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $RelativePath))
    if (-not [string]::Equals($resolved, $expected, [StringComparison]::OrdinalIgnoreCase) -or
        -not $resolved.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected artifact target: $resolved"
    }
    if ((Test-Path -LiteralPath $resolved) -and -not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "Expected an artifact directory: $resolved"
    }
    Assert-NoReparsePath -Path $resolved -IncludeChildren
}

function Assert-StagingPath {
    if ($preview) {
        if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stagingDirectory)) -ne $publishRoot -or
            [IO.Path]::GetFileName($stagingDirectory) -notmatch '^\.differ-preview-staging-[0-9a-f]{32}$') {
            throw "Unexpected preview staging directory: $stagingDirectory"
        }
        Assert-NoReparsePath -Path $stagingDirectory -IncludeChildren
    } else {
        Assert-ArtifactPath -Path $stagingDirectory -RelativePath 'artifacts\Differ-win-x64-staging'
    }
}

if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $LogDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Differ\WorkspaceMaintenance'
}
$LogDirectory = [IO.Path]::GetFullPath($LogDirectory)
if ([string]::Equals($LogDirectory, $repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $LogDirectory.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Build logs must be stored outside the repository.'
}
Assert-NoReparsePath -Path $LogDirectory
New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
$logPath = Join-Path $LogDirectory ('publish-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N') + '.log')
Start-Transcript -LiteralPath $logPath | Out-Null
$cleanupStaging = $false
$lockKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($repositoryRoot.ToUpperInvariant()))).Substring(0, 32)
$maintenanceMutex = [Threading.Mutex]::new($false, 'Local\Differ.WorkspaceMaintenance.' + $lockKey)
$ownsMutex = $false
try {
    try { $ownsMutex = $maintenanceMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another publication, packaging or cleanup operation is using this repository. Retry after it finishes.' }
    Assert-StagingPath
    if ($preview) {
        Assert-NoReparsePath -Path $outputDirectory
        if (Test-Path -LiteralPath $stagingDirectory) { throw 'Unexpected occupied preview staging path.' }
    } else {
        Assert-ArtifactPath -Path $outputDirectory -RelativePath 'artifacts\publish\Differ-win-x64'
        Assert-ArtifactPath -Path $previousDirectory -RelativePath 'artifacts\Differ-win-x64-previous'
        if (Test-Path -LiteralPath $previousDirectory) {
            throw "A previous publication needs recovery before retrying: $previousDirectory. It has not been deleted."
        }
        foreach ($entry in @(Get-ChildItem -LiteralPath $publishRoot -Force -ErrorAction SilentlyContinue)) {
            if (-not [string]::Equals($entry.FullName, $outputDirectory, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Unexpected item in the publish directory; classify it before publishing: $($entry.FullName)"
            }
        }
        if ($null -ne (Get-Process -Name 'Differ.App' -ErrorAction SilentlyContinue)) {
            throw 'Close all Differ windows before publishing.'
        }
    }

    # The staging path is disposable even if restore fails; the current release
    # remains untouched until the complete replacement has passed validation.
    $cleanupStaging = $true
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
    $previewProperties = @()
    if ($preview) {
        $previewProperties = @('-p:DifferIsolatedPreview=true', "-p:RuntimeFrameworkVersion=$previewRuntime", '-p:SelfContained=true')
    }
    dotnet restore $projectPath -r win-x64 --source $nugetSource -p:NuGetAudit=false @previewProperties
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
    dotnet publish $projectPath -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false @previewProperties -o $stagingDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    Assert-StagingPath
    $stagedExecutablePath = Join-Path $stagingDirectory 'Differ.App.exe'
    if (-not (Test-Path -LiteralPath $stagedExecutablePath -PathType Leaf)) {
        throw "Staged executable was not created: $stagedExecutablePath"
    }
    foreach ($json in @('Differ.App.deps.json', 'Differ.App.runtimeconfig.json')) {
        $null = Get-Content -LiteralPath (Join-Path $stagingDirectory $json) -Raw | ConvertFrom-Json
    }
    & (Join-Path $PSScriptRoot 'collect-third-party-notices.ps1') -PublishDirectory $stagingDirectory
    if ($preview) {
        $previewExeName = 'Differ.Phase1.Preview.exe'
        Rename-Item -LiteralPath $stagedExecutablePath -NewName $previewExeName
        Assert-StagingPath
        Assert-NoReparsePath -Path $outputDirectory
        [IO.Directory]::Move($stagingDirectory, $outputDirectory)
        $executablePath = Join-Path $outputDirectory $previewExeName
        [pscustomobject]@{
            Executable = $executablePath
            Version = (Get-Item -LiteralPath $executablePath).VersionInfo.ProductVersion
            Log = $logPath
        }
        return
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\INSTALLATION.md') -Destination (Join-Path $stagingDirectory '新电脑安装与SVN说明.txt')

    $previousMoved = $false
    $replacementMoved = $false
    try {
        Assert-ArtifactPath -Path $outputDirectory -RelativePath 'artifacts\publish\Differ-win-x64'
        Assert-ArtifactPath -Path $previousDirectory -RelativePath 'artifacts\Differ-win-x64-previous'
        Assert-ArtifactPath -Path $stagingDirectory -RelativePath 'artifacts\Differ-win-x64-staging'
        if (Test-Path -LiteralPath $outputDirectory) {
            Move-Item -LiteralPath $outputDirectory -Destination $previousDirectory
            $previousMoved = $true
        }
        Move-Item -LiteralPath $stagingDirectory -Destination $outputDirectory
        $replacementMoved = $true
        $executablePath = Join-Path $outputDirectory 'Differ.App.exe'
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw "Published executable was not created: $executablePath"
        }
    }
    catch {
        $publishFailure = $_
        if ($previousMoved) {
            try {
                Assert-ArtifactPath -Path $outputDirectory -RelativePath 'artifacts\publish\Differ-win-x64'
                Assert-ArtifactPath -Path $previousDirectory -RelativePath 'artifacts\Differ-win-x64-previous'
                if ($replacementMoved -and (Test-Path -LiteralPath $outputDirectory)) {
                    Remove-Item -LiteralPath $outputDirectory -Recurse -Force
                }
                if (Test-Path -LiteralPath $outputDirectory) {
                    throw 'The output path is occupied; the previous release remains in the recovery directory.'
                }
                Move-Item -LiteralPath $previousDirectory -Destination $outputDirectory
            }
            catch {
                throw "Publication failed: $publishFailure Rollback failed: $_ Previous release preserved at $previousDirectory."
            }
        }
        throw $publishFailure
    }
    if (Test-Path -LiteralPath $previousDirectory) {
        Assert-ArtifactPath -Path $previousDirectory -RelativePath 'artifacts\Differ-win-x64-previous'
        Remove-Item -LiteralPath $previousDirectory -Recurse -Force
    }
    $result = [pscustomobject]@{
        Executable = $executablePath
        Version = (Get-Item -LiteralPath $executablePath).VersionInfo.FileVersion
        Log = $logPath
    }
}
catch {
    Write-Host ('Publication failed: ' + $_.Exception.Message)
    throw
}
finally {
    try {
        if ($cleanupStaging -and (Test-Path -LiteralPath $stagingDirectory)) {
            Assert-StagingPath
            Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
        }
    }
    finally {
        try {
            if ($ownsMutex) { $maintenanceMutex.ReleaseMutex() }
        }
        finally {
            $maintenanceMutex.Dispose()
            Stop-Transcript | Out-Null
        }
    }
}
$result
