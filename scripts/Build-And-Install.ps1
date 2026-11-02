param(
    [Parameter(Mandatory=$true)][string]$GameRoot,
    [string]$ServerRoot = '',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# Handle the accidentally nested folder layout produced when an older ZIP was
# extracted into a directory with the same name.
$nestedRoot = Join-Path $root 'ScavLoadoutReroller'
if (-not (Test-Path -LiteralPath (Join-Path $root 'src/ScavReroller.Server/ScavReroller.Server.csproj')) -and
    (Test-Path -LiteralPath (Join-Path $nestedRoot 'src/ScavReroller.Server/ScavReroller.Server.csproj'))) {
    Write-Warning 'Detected a nested ScavLoadoutReroller folder; using the nested project.'
    $root = $nestedRoot
}
$GameRoot = (Resolve-Path $GameRoot).Path
if ([string]::IsNullOrWhiteSpace($ServerRoot)) { $ServerRoot = $GameRoot }
$ServerRoot = (Resolve-Path $ServerRoot).Path

$serverRefs = Join-Path $root '.refs/server'
$clientRefs = Join-Path $root '.refs/client'
New-Item -ItemType Directory -Force $serverRefs, $clientRefs | Out-Null

function Copy-Ref([string]$lookupRoot, [string]$fileName, [string]$destination) {
    $found = Get-ChildItem -LiteralPath $lookupRoot -Recurse -File -Filter $fileName -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '[\\/](user[\\/]mods|obj|bin)[\\/]' } |
        Select-Object -First 1
    if ($null -eq $found) {
        throw "Cannot find $fileName under $lookupRoot. Check SPT 4.1.5 installation."
    }
    Copy-Item -LiteralPath $found.FullName -Destination (Join-Path $destination $fileName) -Force
    Write-Host "Reference: $fileName"
}

$serverRuntime = Join-Path $ServerRoot 'SPT_Runtime'
if (-not (Test-Path -LiteralPath $serverRuntime)) { throw "Missing SPT_Runtime folder: $serverRuntime" }

foreach ($file in @('SPTarkov.Server.Core.dll', 'SPTarkov.DI.dll', 'SPTarkov.Common.dll', 'SemanticVersioning.dll')) {
    Copy-Ref $serverRuntime $file $serverRefs
}

$bep = Join-Path $GameRoot 'BepInEx/core/BepInEx.dll'
if (-not (Test-Path -LiteralPath $bep)) { throw "Missing BepInEx.dll: $bep" }
Copy-Item -LiteralPath $bep -Destination $clientRefs -Force

$sptCommon = Join-Path $GameRoot 'BepInEx/plugins/spt/spt-common.dll'
if (-not (Test-Path -LiteralPath $sptCommon)) { throw "Missing SPT client library: $sptCommon" }
Copy-Item -LiteralPath $sptCommon -Destination $clientRefs -Force

$harmony = Join-Path $GameRoot 'BepInEx/core/0Harmony.dll'
if (-not (Test-Path -LiteralPath $harmony)) { throw "Missing Harmony library: $harmony" }
Copy-Item -LiteralPath $harmony -Destination $clientRefs -Force

$managed = Join-Path $GameRoot 'EscapeFromTarkov_Data/Managed'
foreach ($file in @('Assembly-CSharp.dll', 'Comfort.dll', 'Comfort.Unity.dll', 'Sirenix.Serialization.dll', 'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll', 'UnityEngine.TextRenderingModule.dll', 'UnityEngine.JSONSerializeModule.dll')) {
    $source = Join-Path $managed $file
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing game DLL: $source" }
    Copy-Item -LiteralPath $source -Destination $clientRefs -Force
}

$serverProj = Join-Path $root 'src/ScavReroller.Server/ScavReroller.Server.csproj'
$clientProj = Join-Path $root 'src/ScavReroller.Client/ScavReroller.Client.csproj'
foreach ($project in @($serverProj, $clientProj)) {
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Project file not found: $project. Extract the full v0.1.3 archive into a NEW empty folder."
    }
}

& dotnet build $serverProj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }
& dotnet build $clientProj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Client build failed.' }

$serverDll = Join-Path $root 'src/ScavReroller.Server/bin/Release/net10.0/ScavReroller.Server.dll'
$clientDll = Join-Path $root 'src/ScavReroller.Client/bin/Release/net472/ScavReroller.Client.dll'
if (-not (Test-Path -LiteralPath $serverDll) -or -not (Test-Path -LiteralPath $clientDll)) {
    throw 'Missing output DLL after a successful build.'
}

Write-Host "`nBuild complete:"
Write-Host $serverDll
Write-Host $clientDll

if ($Install) {
    $serverTarget = Join-Path $serverRuntime 'user/mods/ScavReroller'
    $clientTarget = Join-Path $GameRoot 'BepInEx/plugins/ScavReroller'
    New-Item -ItemType Directory -Force $serverTarget, $clientTarget | Out-Null
    Copy-Item -LiteralPath $serverDll -Destination $serverTarget -Force
    Copy-Item -LiteralPath $clientDll -Destination $clientTarget -Force
    Write-Host "`nInstalled the server mod and client plugin. Restart SPT server and EFT."
}
