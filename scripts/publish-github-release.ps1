#requires -Version 7.4
<#
.SYNOPSIS
Validates a versioned Differ package, prepares a GitHub draft, and optionally publishes it.
.DESCRIPTION
Uses the existing Git Credential Manager credential in memory only. Never creates or
changes tags, repository visibility, published assets, or existing release contents.
DryRun performs authenticated reads and produces a concrete plan without remote writes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$')][string]$Repository,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$ExpectedCommit,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidateSet('Stable', 'Preview')][string]$Channel,
    [Parameter(Mandatory)][string]$AssetDirectory,
    [string]$NotesFile,
    [bool]$ExpectedPrivate = $true,
    [switch]$Publish,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$expectedAccount = 'Fahaxiki123'
$apiVersion = '2022-11-28'
$assetStreams = [Collections.Generic.List[IO.FileStream]]::new()
$apiClient = $null
$downloadClient = $null
$credentialToken = $null
$apiHandler = $null
$downloadHandler = $null

function Invoke-CapturedGit {
    param([Parameter(Mandatory)][string[]]$Arguments, [string]$InputText)
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.WorkingDirectory = $repositoryRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['GCM_INTERACTIVE'] = 'Never'
    $start.Environment['GIT_TERMINAL_PROMPT'] = '0'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Could not start the local Git validation process.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($null -ne $InputText) { $process.StandardInput.Write($InputText) }
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw 'The noninteractive Git operation timed out; no credential output was logged.'
        }
        # Credential helper diagnostics can contain account material. Never print either stream on failure.
        if ($process.ExitCode -ne 0) { throw 'The local Git operation failed. Verify origin, source commit, and existing Git Credential Manager sign-in.' }
        $null = $stderr.GetAwaiter().GetResult()
        return $stdout.GetAwaiter().GetResult()
    }
    finally { $process.Dispose() }
}

function Get-RepositoryFromRemote {
    param([string]$Remote)
    $value = $Remote.Trim()
    if ($value -match '^https://github\.com/([^/]+/[^/]+?)(?:\.git)?/?$') { return $Matches[1] }
    if ($value -match '^git@github\.com:([^/]+/[^/]+?)(?:\.git)?$') { return $Matches[1] }
    throw 'Origin must be a GitHub HTTPS or git@github.com remote without embedded credentials.'
}

function Assert-NoReparsePath {
    param([Parameter(Mandatory)][string]$Path)
    for ($current = [IO.Path]::GetFullPath($Path); -not [string]::IsNullOrEmpty($current); $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Release inputs must not use symbolic links or reparse-point ancestors.'
        }
    }
}

function Assert-RequiredValue {
    param([System.Collections.IDictionary]$Object, [string]$Name, [string]$Expected)
    if (-not $Object.Contains($Name) -or [string]$Object[$Name] -cne $Expected) {
        throw "Release manifest field '$Name' does not match the requested publication."
    }
}

function Get-MemoryCredential {
    $raw = $null
    $credential = @{}
    try {
        $raw = Invoke-CapturedGit -Arguments @('-c', 'credential.interactive=never', 'credential', 'fill') `
            -InputText "protocol=https`nhost=github.com`nusername=$expectedAccount`n`n"
        foreach ($line in $raw -split "`r?`n") {
            $separator = $line.IndexOf('=')
            if ($separator -gt 0) { $credential[$line.Substring(0, $separator)] = $line.Substring($separator + 1) }
        }
        if (-not $credential.ContainsKey('username') -or $credential.username -ine $expectedAccount -or
            -not $credential.ContainsKey('password') -or [string]::IsNullOrWhiteSpace($credential.password)) {
            throw 'The existing Git credential does not belong to the expected account or contains no token.'
        }
        if ($credential.password.Length -gt 2048 -or $credential.password -notmatch '^[A-Za-z0-9._~+/-]+={0,2}$') {
            throw 'The credential token has an unexpected format; its value was not logged.'
        }
        return [string]$credential.password
    }
    finally { $raw = $null; $credential.Clear() }
}

function Invoke-GitHubJson {
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path,
        [object]$Body, [switch]$AllowNotFound)
    if (-not $Path.StartsWith('/') -or $Path.StartsWith('//')) { throw 'Invalid GitHub API path.' }
    $uri = [Uri]::new('https://api.github.com' + $Path)
    if ($uri.Host -cne 'api.github.com' -or $uri.Scheme -cne 'https' -or $uri.Port -ne 443) { throw 'Invalid GitHub API destination.' }
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $uri)
    $response = $null
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(90))
    try {
        $request.Headers.Accept.ParseAdd('application/vnd.github+json')
        if ($null -ne $Body) {
            $request.Content = [Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 12 -Compress), [Text.Encoding]::UTF8, 'application/json')
        }
        try { $response = $apiClient.SendAsync($request, $timeout.Token).GetAwaiter().GetResult() }
        catch { throw "GitHub $Method request failed or timed out; credentials and response contents were not logged." }
        if ($AllowNotFound -and [int]$response.StatusCode -eq 404) { return $null }
        if (-not $response.IsSuccessStatusCode) { throw "GitHub $Method returned HTTP $([int]$response.StatusCode). No existing release content was replaced." }
        $json = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($json.Length -gt 10MB) { throw 'GitHub metadata response exceeded the bounded response size.' }
        if (-not [string]::IsNullOrWhiteSpace($json)) { return ($json | ConvertFrom-Json -AsHashtable) }
    }
    finally { if ($null -ne $response) { $response.Dispose() }; $request.Dispose(); $timeout.Dispose() }
}

function Get-RemoteTagCommit {
    $tagPath = [Uri]::EscapeDataString($Tag)
    $reference = Invoke-GitHubJson -Method GET -Path "/repos/$Repository/git/ref/tags/$tagPath"
    if ($reference.ref -cne "refs/tags/$Tag") { throw 'The remote tag reference does not match the requested tag.' }
    $target = $reference.object
    for ($depth = 0; $depth -lt 8; $depth++) {
        if ([string]$target.sha -notmatch '^[a-fA-F0-9]{40}$') { throw 'The remote tag has an invalid object identity.' }
        if ($target.type -eq 'commit') { return [string]$target.sha }
        if ($target.type -ne 'tag') { throw 'The remote tag does not target a commit.' }
        $annotated = Invoke-GitHubJson -Method GET -Path "/repos/$Repository/git/tags/$($target.sha)"
        $target = $annotated.object
    }
    throw 'The remote annotated tag chain is unexpectedly deep.'
}

function Assert-RemoteRepository {
    param([long]$ExpectedId = 0)
    $repo = Invoke-GitHubJson -Method GET -Path "/repos/$Repository"
    if ($repo.full_name -ine $Repository -or ($ExpectedId -gt 0 -and [long]$repo.id -ne $ExpectedId)) {
        throw 'GitHub repository identity changed or does not match origin.'
    }
    if ([bool]$repo.private -ne $ExpectedPrivate) { throw 'GitHub repository visibility differs from the expected visibility; it will not be changed.' }
    if (-not $repo.ContainsKey('permissions') -or -not $repo.permissions.push) { throw 'The authenticated account does not have push permission for this repository.' }
    return $repo
}

function Find-Release {
    $found = @()
    for ($page = 1; $page -le 100; $page++) {
        $items = @(Invoke-GitHubJson -Method GET -Path "/repos/$Repository/releases?per_page=100&page=$page")
        $found += @($items | Where-Object { $_.tag_name -ceq $Tag })
        if ($items.Count -lt 100) {
            if ($found.Count -gt 1) { throw 'Multiple releases claim the same tag; manual review is required.' }
            if ($found.Count -eq 1) { return $found[0] }
            return $null
        }
    }
    throw 'Release pagination exceeded its safety limit.'
}

function Get-ReleaseAssets {
    param([long]$ReleaseId)
    for ($page = 1; $page -le 100; $page++) {
        $items = @(Invoke-GitHubJson -Method GET -Path "/repos/$Repository/releases/$ReleaseId/assets?per_page=100&page=$page")
        foreach ($item in $items) { $item }
        if ($items.Count -lt 100) { return }
    }
    throw 'Release asset pagination exceeded its safety limit.'
}

function Get-DownloadedAssetHash {
    param([long]$AssetId)
    $uri = [Uri]::new("https://api.github.com/repos/$Repository/releases/assets/$AssetId")
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(1800))
    try {
        for ($redirect = 0; $redirect -lt 5; $redirect++) {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $uri)
            $request.Headers.Accept.ParseAdd('application/octet-stream')
            $response = $null
            try {
                # Only the GitHub API receives the token. Signed asset URLs never do.
                $client = if ($uri.Host -ceq 'api.github.com') { $apiClient } else { $downloadClient }
                try { $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $timeout.Token).GetAwaiter().GetResult() }
                catch { throw 'Remote asset verification download failed or timed out.' }
                if ([int]$response.StatusCode -in @(301, 302, 303, 307, 308)) {
                    $location = $response.Headers.Location
                    if ($null -eq $location) { throw 'Asset download redirect did not include a location.' }
                    $next = [Uri]::new($uri, $location)
                    if ($next.Scheme -cne 'https' -or $next.Port -ne 443 -or $next.UserInfo.Length -gt 0 -or
                        $next.Host -notin @('release-assets.githubusercontent.com', 'objects.githubusercontent.com', 'github-releases.githubusercontent.com')) {
                        throw 'Asset verification refused an unexpected redirect host.'
                    }
                    $uri = $next
                    continue
                }
                if (-not $response.IsSuccessStatusCode) { throw "Asset verification download returned HTTP $([int]$response.StatusCode)." }
                $stream = $response.Content.ReadAsStreamAsync($timeout.Token).GetAwaiter().GetResult()
                try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashDataAsync($stream, $timeout.Token).AsTask().GetAwaiter().GetResult()) }
                finally { $stream.Dispose() }
            }
            finally { if ($null -ne $response) { $response.Dispose() }; $request.Dispose() }
        }
        throw 'Asset verification redirect limit exceeded.'
    }
    finally { $timeout.Dispose() }
}

function Assert-RemoteAsset {
    param([System.Collections.IDictionary]$RemoteAsset, [System.Collections.IDictionary]$LocalAsset)
    if ($RemoteAsset.name -cne $LocalAsset.Name -or $RemoteAsset.state -ne 'uploaded' -or
        [long]$RemoteAsset.size -ne $LocalAsset.Size) {
        throw "Existing asset '$($LocalAsset.Name)' is incomplete or has different metadata; refusing replacement."
    }
    $digest = if ($RemoteAsset.Contains('digest')) { [string]$RemoteAsset.digest } else { '' }
    if ($digest -match '^sha256:([a-fA-F0-9]{64})$') { $remoteHash = $Matches[1] }
    else {
        Write-Host "Verifying $($LocalAsset.Name) by streamed download (server SHA-256 unavailable)."
        $remoteHash = Get-DownloadedAssetHash -AssetId ([long]$RemoteAsset.id)
    }
    if ($remoteHash -ine $LocalAsset.Sha256) { throw "Existing asset '$($LocalAsset.Name)' has different SHA-256; refusing replacement. Use a new version." }
}

function Assert-ReleaseMetadata {
    param([System.Collections.IDictionary]$Release)
    if ($Release.tag_name -cne $Tag -or $Release.name -cne $releaseName -or $Release.body -cne $releaseNotes -or
        [bool]$Release.prerelease -ne $isPreview -or $Release.target_commitish -ine $ExpectedCommit) {
        throw 'Existing release metadata differs from this package. No release or asset will be overwritten.'
    }
}

function Send-ReleaseAsset {
    param([long]$ReleaseId, [System.Collections.IDictionary]$Asset)
    $uri = [Uri]::new("https://uploads.github.com/repos/$Repository/releases/$ReleaseId/assets?name=$([Uri]::EscapeDataString($Asset.Name))")
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $uri)
    $response = $null
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(1800))
    # A dedicated read handle makes disposing StreamContent independent of the
    # publication-wide source lock that prevents local package replacement.
    $stream = [IO.FileStream]::new($Asset.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $request.Content = [Net.Http.StreamContent]::new($stream, 1048576)
        $request.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
        $request.Content.Headers.ContentLength = $Asset.Size
        Write-Host "Uploading $($Asset.Name) ($($Asset.Size) bytes)."
        try { $response = $apiClient.SendAsync($request, $timeout.Token).GetAwaiter().GetResult() }
        catch { throw "Upload outcome is uncertain for '$($Asset.Name)'. Rerun DryRun to inspect the matching draft; no asset was deleted." }
        if (-not $response.IsSuccessStatusCode) { throw "Upload returned HTTP $([int]$response.StatusCode) for '$($Asset.Name)'; the draft was retained." }
        $uploaded = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json -AsHashtable
        Assert-RemoteAsset -RemoteAsset $uploaded -LocalAsset $Asset
        Write-Host "Verified $($Asset.Name)."
    }
    finally { if ($null -ne $response) { $response.Dispose() }; $request.Dispose(); $stream.Dispose(); $timeout.Dispose() }
}

try {
    if ($Publish -and $DryRun) { throw 'Use -Publish or -DryRun, not both.' }
    if ($Tag -cne "v$Version") { throw 'The release tag must be exactly v<Version>.' }
    $ExpectedCommit = $ExpectedCommit.ToLowerInvariant()
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'Differ.sln') -PathType Leaf)) { throw 'Run the script from the Differ checkout scripts directory.' }
    foreach ($arguments in @(@('remote', 'get-url', 'origin'), @('remote', 'get-url', '--push', 'origin'))) {
        $remoteRepository = Get-RepositoryFromRemote (Invoke-CapturedGit -Arguments $arguments)
        if ($remoteRepository -ine $Repository) { throw 'The requested repository does not match the checkout origin and push destination.' }
    }
    $sourceProject = Invoke-CapturedGit -Arguments @('show', "${ExpectedCommit}:src/Differ.App/Differ.App.csproj")
    [xml]$sourceXml = $sourceProject
    $sourceVersions = @($sourceXml.SelectNodes('/Project/PropertyGroup/Version') | ForEach-Object { $_.InnerText })
    if ($sourceVersions.Count -ne 1 -or $sourceVersions[0] -cne $Version) { throw 'The expected source commit does not declare this application version.' }

    $AssetDirectory = [IO.Path]::GetFullPath($AssetDirectory)
    Assert-NoReparsePath $AssetDirectory
    if (-not (Test-Path -LiteralPath $AssetDirectory -PathType Container)) { throw 'The versioned asset directory does not exist.' }
    $installerName = "Differ-$Version-win-x64-Setup.exe"
    $names = @($installerName, "$installerName.sha256", 'release-manifest.json', 'RELEASE-NOTES.txt')
    $assets = [ordered]@{}
    foreach ($name in $names) {
        $path = Join-Path $AssetDirectory $name
        Assert-NoReparsePath $path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release asset is missing: $name" }
        $stream = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $assetStreams.Add($stream)
        if ($stream.Length -le 0 -or $stream.Length -ge 2GB) { throw "Invalid release asset size: $name" }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
        $stream.Position = 0
        $assets[$name] = @{ Name = $name; Path = $path; Size = $stream.Length; Sha256 = $hash }
    }
    if ($assets['release-manifest.json'].Size -gt 1MB -or $assets['RELEASE-NOTES.txt'].Size -gt 64KB -or
        $assets["$installerName.sha256"].Size -gt 4096) { throw 'Release metadata exceeds the bounded size limits.' }
    $notesPath = $assets['RELEASE-NOTES.txt'].Path
    if (-not [string]::IsNullOrWhiteSpace($NotesFile) -and
        -not [string]::Equals([IO.Path]::GetFullPath($NotesFile), $notesPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw '-NotesFile must identify the same RELEASE-NOTES.txt that will be uploaded.'
    }
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $manifestJson = [IO.File]::ReadAllText($assets['release-manifest.json'].Path, $strictUtf8)
    $manifest = $manifestJson | ConvertFrom-Json -AsHashtable
    Assert-RequiredValue $manifest 'repository' $Repository
    Assert-RequiredValue $manifest 'tag' $Tag
    Assert-RequiredValue $manifest 'version' $Version
    Assert-RequiredValue $manifest 'channel' $Channel
    Assert-RequiredValue $manifest 'sourceCommit' $ExpectedCommit
    if (-not $manifest.ContainsKey('installer') -or $manifest.installer -isnot [System.Collections.IDictionary]) { throw 'Release manifest installer section is missing.' }
    Assert-RequiredValue $manifest.installer 'fileName' $installerName
    Assert-RequiredValue $manifest.installer 'sha256' $assets[$installerName].Sha256
    Assert-RequiredValue $manifest.installer 'fileVersion' "$Version.0"
    if (-not $manifest.ContainsKey('rebuilt') -or $manifest.rebuilt -isnot [bool]) { throw 'Release manifest rebuilt must be a boolean.' }
    # PowerShell 7.5+ may materialize JSON dates as DateTime. Inspect the original
    # JSON spelling so local culture cannot obscure a non-UTC manifest timestamp.
    $manifestDocument = [Text.Json.JsonDocument]::Parse($manifestJson)
    try {
        $createdUtc = $manifestDocument.RootElement.GetProperty('createdUtc').GetString()
        if ($createdUtc -notmatch '(Z|\+00:00)$') { throw 'Release manifest requires a UTC createdUtc timestamp.' }
        $null = [DateTimeOffset]::Parse($createdUtc, [Globalization.CultureInfo]::InvariantCulture)
    }
    finally { $manifestDocument.Dispose() }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($assets[$installerName].Path).FileVersion
    if ([version]$fileVersion -ne [version]"$Version.0") { throw 'The actual installer PE version does not match the package version.' }
    $checksum = [IO.File]::ReadAllText($assets["$installerName.sha256"].Path, $strictUtf8).Trim()
    if ($checksum -notmatch ('^([a-fA-F0-9]{64})[ \t]+\*?' + [regex]::Escape($installerName) + '$') -or
        $Matches[1] -ine $assets[$installerName].Sha256) { throw 'The SHA-256 sidecar does not match the installer bytes and exact file name.' }
    $releaseNotes = [IO.File]::ReadAllText($notesPath, $strictUtf8)
    if ([string]::IsNullOrWhiteSpace($releaseNotes)) { throw 'Release notes must not be empty.' }
    $isPreview = $Channel -eq 'Preview'
    $releaseName = if ($isPreview) { "Differ $Version (Preview)" } else { "Differ $Version" }

    $credentialToken = Get-MemoryCredential
    $apiHandler = [Net.Http.HttpClientHandler]::new()
    $apiHandler.AllowAutoRedirect = $false
    $apiClient = [Net.Http.HttpClient]::new($apiHandler)
    $apiClient.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $apiClient.DefaultRequestHeaders.UserAgent.ParseAdd('Differ-Release-Publisher/1.0')
    $apiClient.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $credentialToken)
    $apiClient.DefaultRequestHeaders.Add('X-GitHub-Api-Version', $apiVersion)
    $downloadHandler = [Net.Http.HttpClientHandler]::new()
    $downloadHandler.AllowAutoRedirect = $false
    $downloadClient = [Net.Http.HttpClient]::new($downloadHandler)
    $downloadClient.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd('Differ-Release-Publisher/1.0')
    $credentialToken = $null

    $account = Invoke-GitHubJson -Method GET -Path '/user'
    if ($account.login -ine $expectedAccount) { throw 'The GitHub API authenticated as an unexpected account.' }
    $repo = Assert-RemoteRepository
    $repositoryId = [long]$repo.id
    if ((Get-RemoteTagCommit) -ine $ExpectedCommit) { throw 'The existing remote tag does not match ExpectedCommit. Tags will not be created or moved.' }
    $release = Find-Release
    $remoteAssets = @()
    if ($null -ne $release) {
        Assert-ReleaseMetadata $release
        $remoteAssets = @(Get-ReleaseAssets -ReleaseId ([long]$release.id))
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($remoteAsset in $remoteAssets) {
        if (-not $assets.Contains([string]$remoteAsset.name) -or -not $seen.Add([string]$remoteAsset.name)) {
            throw 'The existing release has unexpected or duplicate assets; no assets will be removed.'
        }
        Assert-RemoteAsset $remoteAsset $assets[[string]$remoteAsset.name]
    }
    $missing = @($names | Where-Object { -not $seen.Contains($_) })
    if ($null -ne $release -and -not $release.draft -and $missing.Count -gt 0) { throw 'The published release is incomplete or differs from this package; published assets are immutable in this workflow.' }
    Write-Host "Repository: $Repository; private=$($repo.private); channel=$Channel; tag=$Tag; source=$ExpectedCommit"
    foreach ($asset in $assets.Values) { Write-Host "$($asset.Name): $($asset.Size) bytes; SHA256=$($asset.Sha256)" }
    $action = if ($null -ne $release -and -not $release.draft) { 'AlreadyPublishedIdentical' }
        elseif ($DryRun) { 'WouldPrepareDraft' }
        elseif ($Publish) { 'PublishAfterVerification' } else { 'PrepareDraft' }
    Write-Host "Plan: $action; missing assets=$($missing.Count); prerelease=$isPreview; publish makes latest=$(-not $isPreview)."
    if ($DryRun -or ($null -ne $release -and -not $release.draft)) {
        [pscustomobject]@{ Repository = $Repository; Tag = $Tag; Channel = $Channel; Private = [bool]$repo.private; DryRun = [bool]$DryRun; Action = $action; MissingAssets = $missing; Url = "https://github.com/$Repository/releases/tag/$Tag" }
        return
    }

    $null = Assert-RemoteRepository -ExpectedId $repositoryId
    if ((Get-RemoteTagCommit) -ine $ExpectedCommit) { throw 'The remote tag changed during validation; publication stopped.' }
    if ($null -eq $release) {
        $release = Invoke-GitHubJson -Method POST -Path "/repos/$Repository/releases" -Body @{
            tag_name = $Tag; target_commitish = $ExpectedCommit; name = $releaseName; body = $releaseNotes
            draft = $true; prerelease = $isPreview; make_latest = 'false'
        }
        Assert-ReleaseMetadata $release
        if (-not $release.draft) { throw 'GitHub did not create the requested draft; publication stopped.' }
        Write-Host "Created draft release for $Tag."
    }
    foreach ($name in $missing) { Send-ReleaseAsset -ReleaseId ([long]$release.id) -Asset $assets[$name] }
    $release = Invoke-GitHubJson -Method GET -Path "/repos/$Repository/releases/$($release.id)"
    Assert-ReleaseMetadata $release
    if (-not $release.draft) { throw 'The draft was published by another process; no metadata was changed.' }
    $finalAssets = @(Get-ReleaseAssets -ReleaseId ([long]$release.id))
    if ($finalAssets.Count -ne $names.Count) { throw 'The draft does not contain exactly the four verified release assets.' }
    $verifiedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($asset in $finalAssets) {
        if (-not $assets.Contains([string]$asset.name) -or -not $verifiedNames.Add([string]$asset.name)) { throw 'Unexpected draft asset detected; publication stopped.' }
        Assert-RemoteAsset $asset $assets[[string]$asset.name]
    }
    $null = Assert-RemoteRepository -ExpectedId $repositoryId
    if ((Get-RemoteTagCommit) -ine $ExpectedCommit) { throw 'The remote tag changed before publication; the draft was retained.' }
    if ($Publish) {
        $release = Invoke-GitHubJson -Method PATCH -Path "/repos/$Repository/releases/$($release.id)" -Body @{
            draft = $false; prerelease = $isPreview; make_latest = $(if ($isPreview) { 'false' } else { 'true' })
        }
        Assert-ReleaseMetadata $release
        if ($release.draft) { throw 'GitHub still reports a draft; publication was not confirmed.' }
        $null = Assert-RemoteRepository -ExpectedId $repositoryId
        $latest = Invoke-GitHubJson -Method GET -Path "/repos/$Repository/releases/latest" -AllowNotFound
        if ($isPreview -and $null -ne $latest -and [long]$latest.id -eq [long]$release.id) { throw 'Published preview unexpectedly became Latest; manual review is required.' }
        if (-not $isPreview -and ($null -eq $latest -or [long]$latest.id -ne [long]$release.id)) { throw 'Stable publication succeeded but Latest was not confirmed; no other releases were changed.' }
    }
    [pscustomobject]@{ Repository = $Repository; Tag = $Tag; Channel = $Channel; Private = $ExpectedPrivate; Draft = [bool]$release.draft; AssetsVerified = $finalAssets.Count; Url = "https://github.com/$Repository/releases/tag/$Tag" }
}
finally {
    $credentialToken = $null
    if ($null -ne $apiClient) { $apiClient.DefaultRequestHeaders.Authorization = $null; $apiClient.Dispose() }
    if ($null -ne $downloadClient) { $downloadClient.Dispose() }
    if ($null -ne $apiHandler) { $apiHandler.Dispose() }
    if ($null -ne $downloadHandler) { $downloadHandler.Dispose() }
    foreach ($stream in $assetStreams) { $stream.Dispose() }
}
