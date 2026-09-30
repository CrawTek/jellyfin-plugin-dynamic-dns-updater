param(
    [string]$DownloadUrl,
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
& $Dotnet build src/Jellyfin.Plugin.DynDns.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& $Dotnet run --project tests/Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
$meta = Get-Content meta.json -Raw | ConvertFrom-Json
$out = Join-Path $PSScriptRoot 'release'
New-Item -ItemType Directory -Force $out | Out-Null
$zip = Join-Path $out "Dynamic-DNS-Updater-$($meta.version).zip"
# Explicit allowlist: never include local installers, credentials, configuration or SDK caches.
Compress-Archive -Path src/bin/Release/net10.0/Jellyfin.Plugin.DynDns.dll,meta.json,assets/icon.png,LICENSE,README.md -DestinationPath $zip -Force
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $out 'SHA256SUMS'), "$sha  $([IO.Path]::GetFileName($zip))`n")
$sourceZip = Join-Path $out "Dynamic-DNS-Updater-$($meta.version)-source.zip"
$stream = [IO.File]::Open($sourceZip, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @('README.md','LICENSE','meta.json','package.ps1','publish-manifest.ps1','PUBLISHING.md','RELEASE-NOTES.md','.gitignore','.github/workflows/build.yml','assets/icon.png','src/Jellyfin.Plugin.DynDns.csproj','src/UpdateEngine.cs','src/Plugin.cs','src/DynDnsController.cs','src/settings.html','tests/Tests.csproj','tests/Program.cs','tests/ControllerChecks.cs','tests/DuckDnsChecks.cs','tests/FreeDnsChecks.cs')
    foreach ($file in $files) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $PSScriptRoot $file),$file) | Out-Null }
} finally { $archive.Dispose(); $stream.Dispose() }
$sourceHash = (Get-FileHash $sourceZip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::AppendAllText((Join-Path $out 'SHA256SUMS'), "$sourceHash  $([IO.Path]::GetFileName($sourceZip))`n")
if ($DownloadUrl) {
    $uri = [Uri]$DownloadUrl
    if (!$uri.IsAbsoluteUri -or $uri.Scheme -ne 'https') { throw 'DownloadUrl must be a public HTTPS ZIP URL' }
    $entry = [ordered]@{
        guid = $meta.guid; name = $meta.name; description = $meta.description
        overview = $meta.overview; owner = $meta.owner; category = $meta.category
        versions = @([ordered]@{
            version = $meta.version; targetAbi = $meta.targetAbi
            changelog = 'Five DDNS providers, 10-minute checks, and settings-based error recovery.'
            sourceUrl = $DownloadUrl
            checksum = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()
            timestamp = [DateTime]::UtcNow.ToString('o')
        })
    }
    $json = ConvertTo-Json -InputObject @($entry) -Depth 8
    [IO.File]::WriteAllText((Join-Path $out 'manifest.json'), $json)
}
Write-Host "Release package: $zip"
if (!$DownloadUrl) { Write-Host 'Manifest not generated: supply -DownloadUrl once the public release URL is known.' }
