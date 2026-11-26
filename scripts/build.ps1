param([switch]$SkipExtract)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
$image = 'docker.io/linuxserver/lidarr:nightly-4.0.0.5236-ls228@sha256:656a4d008a9a60ba32aba95c85b0fd19a06af8e2680f6748ef693956aa84ae33'
$assembly = 'Lidarr.Plugin.SpotifyNavidrome'
$artifacts = Join-Path $root '.artifacts'
$references = Join-Path $artifacts 'lidarr'

if (!$SkipExtract) {
    New-Item -ItemType Directory -Force $references | Out-Null
    $container = docker create $image
    try {
        docker cp "${container}:/app/lidarr/bin/." $references
    } finally {
        docker rm -v $container | Out-Null
    }
}

$publish = Join-Path $artifacts 'publish'
dotnet publish (Join-Path $root "src/Plugin/$assembly.csproj") -c Release -o $publish
$files = @("$assembly.dll", "$assembly.deps.json") |
    ForEach-Object { Join-Path $publish $_ }
$archive = Join-Path $artifacts "$assembly.net10.0.zip"
Compress-Archive -LiteralPath $files -DestinationPath $archive -Force
Get-FileHash -Algorithm SHA256 $archive
