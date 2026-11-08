[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceDirectory = Join-Path $repositoryRoot 'docs\third-party'
$manifest = @(Get-Content -LiteralPath (Join-Path $sourceDirectory 'manifest.json') -Raw | ConvertFrom-Json)
$publishPath = [IO.Path]::GetFullPath($PublishDirectory)
$dependencies = Get-Content -LiteralPath (Join-Path $publishPath 'Differ.App.deps.json') -Raw | ConvertFrom-Json
$runtime = Get-Content -LiteralPath (Join-Path $publishPath 'Differ.App.runtimeconfig.json') -Raw | ConvertFrom-Json
$identities = @($dependencies.libraries.PSObject.Properties |
    Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Name })
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    $identities += $framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version
}

foreach ($identity in $identities) {
    $entry = @($manifest | Where-Object { $_.package -eq $identity })
    if ($entry.Count -ne 1 -or @($entry[0].files | Where-Object { $_.file -match '(?i)licen[cs]e' }).Count -eq 0) {
        throw "Missing pinned license record for $identity. Update docs/third-party before publishing."
    }
}

$licenseDirectory = Join-Path $publishPath 'licenses'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
$notice = [Text.StringBuilder]::new()
[void]$notice.AppendLine('Differ - Third-party components and notices')
[void]$notice.AppendLine('Licenses apply to their respective components. See the complete texts in licenses/.')
[void]$notice.AppendLine('No user files, settings, or SVN credentials are included in this distribution.')
[void]$notice.AppendLine()
$copied = @{}
foreach ($entry in $manifest) {
    [void]$notice.AppendLine($entry.package)
    [void]$notice.AppendLine('License: ' + $entry.license)
    # These fields are optional in the pinned manifest. The publishing caller
    # enables StrictMode, so read their presence before accessing their values.
    $copyright = $entry.PSObject.Properties['copyright']
    $authors = $entry.PSObject.Properties['authors']
    $modifications = $entry.PSObject.Properties['modifications']
    if ($copyright -and $copyright.Value) { [void]$notice.AppendLine($copyright.Value) }
    if ($authors -and $authors.Value) { [void]$notice.AppendLine('Authors: ' + $authors.Value) }
    if ($modifications -and $modifications.Value) { [void]$notice.AppendLine($modifications.Value) }
    foreach ($file in $entry.files) {
        $source = [IO.Path]::GetFullPath((Join-Path $sourceDirectory $file.file))
        if (-not $source.StartsWith($sourceDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "License path must stay within docs/third-party: $($file.file)"
        }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.sha256) {
            throw "License source changed without a manifest update: $($file.file)"
        }
        if (-not $copied.ContainsKey($file.file)) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $licenseDirectory $file.file)
            $copied[$file.file] = $true
        }
        [void]$notice.AppendLine('  Text: licenses/' + $file.file)
        [void]$notice.AppendLine('  Source: ' + $file.source)
    }
    [void]$notice.AppendLine()
}
[IO.File]::WriteAllText((Join-Path $publishPath 'THIRD-PARTY-NOTICES.txt'), $notice.ToString(), [Text.UTF8Encoding]::new($false))
Write-Output "Third-party notices: $($manifest.Count) component records, $($copied.Count) license/notice files."
