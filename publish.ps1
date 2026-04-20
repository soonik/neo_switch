# Builds self-contained, single-file Windows x64 releases of NeoSwitch.
# Output goes to .\publish\   (inside the repo root).
#
# Usage:
#   pwsh ./publish.ps1                      # clean publish
#   pwsh ./publish.ps1 -Rid win-arm64       # ARM64 build
#   pwsh ./publish.ps1 -Configuration Debug # debug publish

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Rid = 'win-x64',
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'publish')
)

$ErrorActionPreference = 'Stop'

function Publish-Project {
    param([string]$Project, [string]$OutDir)
    Write-Host "==> publishing $Project -> $OutDir" -ForegroundColor Cyan
    dotnet publish $Project `
        -c $Configuration `
        -r $Rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=embedded `
        -o $OutDir `
    || throw "publish failed for $Project"
}

if (Test-Path $OutputRoot) {
    Remove-Item -Recurse -Force $OutputRoot
}
New-Item -ItemType Directory -Path $OutputRoot | Out-Null

Publish-Project -Project (Join-Path $PSScriptRoot 'src/NeoSwitch.App/NeoSwitch.App.csproj') `
                -OutDir  (Join-Path $OutputRoot  'app')

Publish-Project -Project (Join-Path $PSScriptRoot 'src/NeoSwitch.Cli/NeoSwitch.Cli.csproj') `
                -OutDir  (Join-Path $OutputRoot  'cli')

# Zip each artifact so they upload cleanly.
$appZip = Join-Path $OutputRoot "NeoSwitch-$Rid.zip"
$cliZip = Join-Path $OutputRoot "neoswitch-cli-$Rid.zip"
Compress-Archive -Path (Join-Path $OutputRoot 'app/*') -DestinationPath $appZip -Force
Compress-Archive -Path (Join-Path $OutputRoot 'cli/*') -DestinationPath $cliZip -Force

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Get-ChildItem $OutputRoot -Filter '*.zip' | ForEach-Object {
    $sizeMb = [math]::Round($_.Length / 1MB, 1)
    Write-Host ("  {0,6} MB  {1}" -f $sizeMb, $_.FullName)
}
Write-Host ""
Write-Host "Tray app:   $(Join-Path $OutputRoot 'app/NeoSwitch.exe')"
Write-Host "CLI:        $(Join-Path $OutputRoot 'cli/neoswitch.exe')"
