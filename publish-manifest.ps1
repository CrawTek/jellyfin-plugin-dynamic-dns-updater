param(
    [Parameter(Mandatory)][string]$DownloadUrl,
    [Parameter(Mandatory)][string]$ImageUrl,
    [string]$ReleaseDirectory = "$PSScriptRoot/release"
)
$ErrorActionPreference = 'Stop'
foreach ($url in @($DownloadUrl, $ImageUrl)) {
    $uri = [Uri]$url
    if (!$uri.IsAbsoluteUri -or $uri.Scheme -ne 'https') { throw 'Public HTTPS URLs are required.' }
}
$meta = Get-Content "$PSScriptRoot/meta.json" -Raw | ConvertFrom-Json
$zip = Join-Path $ReleaseDirectory "Dynamic-DNS-Updater-$($meta.version).zip"
if (!(Test-Path $zip)) { throw 'Build the release package first.' }
$entry = [ordered]@{
    guid = $meta.guid; name = $meta.name; description = $meta.description
    overview = $meta.overview; owner = $meta.owner; category = $meta.category
    imageUrl = $ImageUrl
    versions = @([ordered]@{
        version = $meta.version; targetAbi = $meta.targetAbi
        changelog = 'Five DDNS providers, 10-minute checks, and settings-based error recovery.'
        sourceUrl = $DownloadUrl
        checksum = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()
        timestamp = [DateTime]::UtcNow.ToString('o')
    })
}
[IO.File]::WriteAllText((Join-Path $ReleaseDirectory 'manifest.json'), (ConvertTo-Json -InputObject @($entry) -Depth 8))
Write-Host 'Manifest created for the existing ZIP; no rebuild performed.'
