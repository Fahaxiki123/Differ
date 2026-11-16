#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$CompilerPath,
    [string]$LogDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'Differ.sln') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'AGENTS.md') -PathType Leaf)) {
    throw 'The script must run from the Differ repository scripts directory.'
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
                throw "Refusing a reparse point inside the staging tree: $($entry.FullName)"
            }
        }
    }
}

function Assert-InstallerBuildPath {
    $resolvedBuild = [IO.Path]::GetFullPath($buildDirectory)
    $expectedBuild = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\packaging\2026-09-25\tooling\build'))
    if (-not [string]::Equals($resolvedBuild, $expectedBuild, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected installer build directory: $resolvedBuild"
    }
    Assert-NoReparsePath -Path $resolvedBuild -IncludeChildren
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
$runName = 'installer-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$logPath = Join-Path $LogDirectory ($runName + '.log')
$manifestLogPath = Join-Path $LogDirectory ($runName + '.manifest.json')
Start-Transcript -LiteralPath $logPath | Out-Null
$cleanupBuild = $false
$lockKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($repositoryRoot.ToUpperInvariant()))).Substring(0, 32)
$maintenanceMutex = [Threading.Mutex]::new($false, 'Local\Differ.WorkspaceMaintenance.' + $lockKey)
$ownsMutex = $false
try {
    try { $ownsMutex = $maintenanceMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another publication, packaging or cleanup operation is using this repository. Retry after it finishes.' }
    $publishDirectory = Join-Path $repositoryRoot 'artifacts\publish\Differ-win-x64'
    $toolingDirectory = Join-Path $repositoryRoot 'artifacts\packaging\2026-09-25\tooling'
    $buildDirectory = Join-Path $toolingDirectory 'build'
    $payloadDirectory = Join-Path $buildDirectory 'payload'
    $installerDirectory = Join-Path $repositoryRoot 'artifacts\installers'
    $stagedInstaller = Join-Path $buildDirectory 'Differ-Setup.exe'
    $outputInstaller = Join-Path $installerDirectory 'Differ-Setup.exe'
    $iconPath = Join-Path $repositoryRoot 'src\Differ.App\Assets\Differ.ico'
    $installerScript = Join-Path $repositoryRoot 'installer\Differ.nsi'
    $webViewInstaller = Join-Path $repositoryRoot 'artifacts\packaging\2026-10-24\webview2\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
    $webViewSha256 = '9B0142E22E8DAA92855D746AE4D1E9EEEF561CCAF67D1DD452165C4AF3DF3191'
    Assert-NoReparsePath -Path $publishDirectory -IncludeChildren
    Assert-NoReparsePath -Path $installerDirectory
    Assert-NoReparsePath -Path $outputInstaller
    if (-not (Test-Path -LiteralPath $webViewInstaller) -or
        (Get-Item -LiteralPath $webViewInstaller).Length -lt 100MB -or
        (Get-FileHash -LiteralPath $webViewInstaller).Hash -ne $webViewSha256 -or
        (Get-AuthenticodeSignature -LiteralPath $webViewInstaller).Status -ne 'Valid') {
        throw 'The reviewed, Microsoft-signed x64 WebView2 Standalone offline installer is missing or changed. See installer/README.md.'
    }

    if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
        $CompilerPath = Join-Path $toolingDirectory 'nsis-3.12\Bin\makensis.exe'
    }
    $CompilerPath = [IO.Path]::GetFullPath($CompilerPath)
    if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
        throw 'NSIS compiler not found. Extract the official NSIS 3.12 ZIP to the tooling directory or pass -CompilerPath. See installer\README.md.'
    }
    $compilerVersion = (& $CompilerPath /VERSION | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $compilerVersion -ne 'v3.12') {
        throw "Expected the reviewed NSIS v3.12 compiler, got: $compilerVersion"
    }

    foreach ($required in @('Differ.App.exe', 'Differ.App.dll', 'Differ.App.deps.json',
            'Differ.App.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
            'PresentationFramework.dll', 'THIRD-PARTY-NOTICES.txt', '新电脑安装与SVN说明.txt',
            'Editor\index.html', 'Editor\editor.js', 'Editor\editor.css', 'Editor\vs\loader.js',
            'Editor\merge.html', 'Editor\merge.js', 'Editor\merge.css',
            'Editor\vs\editor\editor.main.js', 'Editor\vs\base\worker\workerMain.js',
            'Editor\vs\base\browser\ui\codicons\codicon\codicon.ttf',
            'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'WebView2Loader.dll',
            'SharpSvn.dll', 'Ijwhost.dll', 'vcruntime140.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $required) -PathType Leaf)) {
            throw "The self-contained release payload is incomplete: $required. Run scripts\publish.ps1 first."
        }
    }
    if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
        throw "Application icon is missing: $iconPath"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'licenses') -PathType Container)) {
        throw 'The published licenses directory is missing.'
    }
    $nativeRuntime = Join-Path $publishDirectory 'vcruntime140.dll'
    if ((Get-FileHash -LiteralPath $nativeRuntime).Hash -ne 'D1F4225DF2CD877DBF130D5668A021DCE3F94118455FF5EC952061C30AFC9CE7' -or
        (Get-AuthenticodeSignature -LiteralPath $nativeRuntime).Status -ne 'Valid') {
        throw 'The app-local Visual C++ runtime does not match the reviewed signed redistributable.'
    }

    $project = [xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\Differ.App\Differ.App.csproj') -Raw)
    $appVersion = [string]($project.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishDirectory 'Differ.App.exe')).FileVersion
    if ([version]$fileVersion -ne [version]"$appVersion.0") {
        throw "Published executable version $fileVersion does not match project version $appVersion. Publish the current release before packaging."
    }
    # Reject a damaged .NET startup manifest before packaging an unusable app.
    $null = Get-Content -LiteralPath (Join-Path $publishDirectory 'Differ.App.deps.json') -Raw | ConvertFrom-Json
    $runtime = Get-Content -LiteralPath (Join-Path $publishDirectory 'Differ.App.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (-not $runtime.runtimeOptions.PSObject.Properties['includedFrameworks']) {
        throw 'The runtime configuration is not self-contained.'
    }

    # Only the exact disposable staging tree is eligible; the compiler and offline
    # Runtime next to it remain build inputs. Recheck before the finally cleanup.
    Assert-InstallerBuildPath
    if (Test-Path -LiteralPath $buildDirectory) {
        Remove-Item -LiteralPath $buildDirectory -Recurse -Force
    }
    $cleanupBuild = $true
    New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null

    $payloadFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Sort-Object FullName)
    foreach ($file in $payloadFiles) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse points cannot enter the installer: $($file.FullName)"
        }
        $relative = [IO.Path]::GetRelativePath($publishDirectory, $file.FullName)
        if ($file.Extension -eq '.pdb') { continue }
        # Known public SDK IntelliSense documents are not part of the runtime payload.
        if ($relative -in @('Microsoft.Web.WebView2.Core.xml', 'Microsoft.Web.WebView2.Wpf.xml', 'Microsoft.Web.WebView2.WinForms.xml')) { continue }
        if ($file.Name -match '(?i)(\.Tests\.|testhost|testadapter|\.coverage)') {
            throw "Development test binaries cannot enter the installer: $relative"
        }
        $runtimeFile = $file.Extension -in @('.exe', '.dll') -or
            $file.Name -in @('SharpPlink-x64.svnExe', 'SharpSvn-DB44-20-x64.svnDll')
        $runtimeJson = $relative -in @('Differ.App.deps.json', 'Differ.App.runtimeconfig.json')
        $noticeFile = $relative -in @('THIRD-PARTY-NOTICES.txt', '新电脑安装与SVN说明.txt') -or
            $relative.StartsWith('licenses\', [StringComparison]::OrdinalIgnoreCase)
        $editorAsset = $relative.StartsWith('Editor\', [StringComparison]::OrdinalIgnoreCase) -and
            ($file.Extension -in @('.js', '.css', '.html', '.ttf', '.json', '.txt') -or $file.Name -eq 'LICENSE')
        if (-not ($runtimeFile -or $runtimeJson -or $noticeFile -or $editorAsset)) {
            throw "Unexpected file in the publish payload; refusing to package private or development data: $relative"
        }
        if ($relative -match '[\r\n"$]' -or $relative.StartsWith('..')) {
            throw "Payload path cannot be safely represented in NSIS: $relative"
        }
        $destination = Join-Path $payloadDirectory $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'installer\NSIS-COPYING.txt') -Destination (Join-Path $payloadDirectory 'licenses\NSIS-COPYING.txt')

    $manifest = @(Get-ChildItem -LiteralPath $payloadDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            Path = [IO.Path]::GetRelativePath($payloadDirectory, $_.FullName)
            Size = $_.Length
            SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    [ordered]@{ Product = 'Differ.Desktop.x64'; Version = $appVersion; Files = $manifest } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payloadDirectory 'Differ.install-files.json') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $payloadDirectory 'Differ.install-files.json') -Destination $manifestLogPath

    $allFiles = @(Get-ChildItem -LiteralPath $payloadDirectory -Recurse -File | Sort-Object FullName)
    $relativeFiles = @($allFiles | ForEach-Object { [IO.Path]::GetRelativePath($payloadDirectory, $_.FullName) })
    $include = [Collections.Generic.List[string]]::new()
    $include.Add('; Generated by scripts/build-installer.ps1. Do not edit.')
    $include.Add('!macro InstallPayload')
    foreach ($relative in $relativeFiles) {
        $directory = [IO.Path]::GetDirectoryName($relative)
        $include.Add(('  SetOutPath "$INSTDIR\{0}"' -f $directory))
        $include.Add(('  File "${{PAYLOAD_DIR}}\{0}"' -f $relative))
        $include.Add('  IfErrors install_failed')
    }
    $include.Add('!macroend')
    $include.Add('!macro CheckPayloadLocks Prefix')
    foreach ($relative in $relativeFiles) {
        $include.Add(('  Push "$INSTDIR\{0}"' -f $relative))
        $include.Add('  Call ${Prefix}CheckFileUnlocked')
    }
    $include.Add('!macroend')
    $include.Add('!macro RemovePayload')
    foreach ($relative in $relativeFiles) {
        $include.Add(('  ${{If}} ${{FileExists}} "$INSTDIR\{0}"' -f $relative))
        $include.Add(('    Delete "$INSTDIR\{0}"' -f $relative))
        $include.Add('    IfErrors uninstall_failed')
        $include.Add('  ${EndIf}')
    }
    $directories = @(Get-ChildItem -LiteralPath $payloadDirectory -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending)
    foreach ($directory in $directories) {
        $relative = [IO.Path]::GetRelativePath($payloadDirectory, $directory.FullName)
        $include.Add(('  RMDir "$INSTDIR\{0}"' -f $relative))
    }
    $include.Add('!macroend')
    $payloadInclude = Join-Path $buildDirectory 'Payload.nsh'
    [IO.File]::WriteAllLines($payloadInclude, $include, [Text.UTF8Encoding]::new($true))
    $installedSize = [int][Math]::Ceiling(($allFiles | Measure-Object -Property Length -Sum).Sum / 1024)

    & $CompilerPath /V3 /WX /INPUTCHARSET UTF8 "/DAPP_VERSION=$appVersion" "/DAPP_FILE_VERSION=$fileVersion" `
        "/DPAYLOAD_DIR=$payloadDirectory" "/DPAYLOAD_INCLUDE=$payloadInclude" `
        "/DICON_FILE=$iconPath" "/DOUTPUT_FILE=$stagedInstaller" "/DINSTALLED_SIZE=$installedSize" `
        "/DWEBVIEW_INSTALLER=$webViewInstaller" $installerScript
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $stagedInstaller -PathType Leaf)) {
        throw "NSIS compilation failed (exit $LASTEXITCODE). The previous installer is preserved."
    }
    New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null
    Assert-NoReparsePath -Path $stagedInstaller
    Assert-NoReparsePath -Path $outputInstaller
    Move-Item -LiteralPath $stagedInstaller -Destination $outputInstaller -Force
    $installerHash = (Get-FileHash -LiteralPath $outputInstaller -Algorithm SHA256).Hash
    $checksumPath = $outputInstaller + '.sha256'
    Assert-NoReparsePath -Path $checksumPath
    Set-Content -LiteralPath $checksumPath -Value ($installerHash + '  ' + [IO.Path]::GetFileName($outputInstaller)) -Encoding ascii
    $result = [pscustomobject]@{
        Installer = $outputInstaller
        Version = $appVersion
        Compiler = $compilerVersion
        RuntimeFileCount = $relativeFiles.Count
        SHA256 = $installerHash
        DigitallySigned = $false
        Log = $logPath
        Manifest = $manifestLogPath
    }
}
catch {
    Write-Host ('Installer build failed: ' + $_.Exception.Message)
    throw
}
finally {
    try {
        if ($cleanupBuild -and (Test-Path -LiteralPath $buildDirectory)) {
            Assert-InstallerBuildPath
            Remove-Item -LiteralPath $buildDirectory -Recurse -Force
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
