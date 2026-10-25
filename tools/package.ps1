# Builds both halves of KerbalCraft and packs a release into dist\:
#   KerbalCraft-<version>.zip          drop its GameData folder into your KSP install:
#                                      GameData\KerbalCraft\{Plugins\KerbalCraft.dll, Shaders\, KerbalCraft.cfg, Minecraft\KerbalCraft-Minecraft.zip}
#   kerbalcraft-fabric-<version>.jar   the Minecraft mod on its own (for your own launcher)
#
# KerbalCraft-Minecraft.zip holds a portable Prism Launcher with a ready "KerbalCraft" instance (Minecraft
# 26.3, Fabric, Fabric API, e4mc, KerbalCraft). On first start the plugin unpacks it to
# %LOCALAPPDATA%\KerbalCraft and starts it; Prism asks the player to sign in once, then
# downloads Minecraft and Java itself.
#
#   powershell -ExecutionPolicy Bypass -File tools\package.ps1 -KspDir "C:\Games\KSP" [-NoBuild] [-ShaderBundle path\to\kerbalcraft.shaders]
param([string]$KspDir = $env:KSP_DIR, [switch]$NoBuild, [string]$ShaderBundle = "")
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$root = Split-Path -Parent $PSScriptRoot
$version = (Select-String -Path "$root\fabric\gradle.properties" -Pattern '^version=(.+)$').Matches[0].Groups[1].Value.Trim()

# Pinned downloads (checked against these hashes); same pins as SkyCraft.
$prismVersion = "11.1.1"
$prismZip = "PrismLauncher-Windows-MSVC-Portable-$prismVersion.zip"
$prismUrl = "https://github.com/PrismLauncher/PrismLauncher/releases/download/$prismVersion/$prismZip"
$prismSha256 = "ab35a770fb06d89d2ccc098079db5db329fb4e68f42b72babd8b095efde3d2d7"
$prismLicenseUrl = "https://raw.githubusercontent.com/PrismLauncher/PrismLauncher/$prismVersion/LICENSE"
$fabricApiJar = "fabric-api-0.161.0+26.3.jar"
$fabricApiUrl = "https://cdn.modrinth.com/data/P7dR8mSH/versions/bNnaTiuM/fabric-api-0.161.0%2B26.3.jar"
$e4mcJar = "e4mc-fabric-6.2.2-modern.jar"
$e4mcUrl = "https://cdn.modrinth.com/data/qANg5Jrr/versions/AouleFRY/e4mc-fabric-6.2.2-modern.jar"
$e4mcSha512 = "01ef0a8c5b76e2cb0effd337bad3350d8807d100d0ec661e01b2ffb20af7b652f756c5eaa11bee233c37905bfd7b573f7a85f3d15bfd2833962c76f02cd59a86"
$fabricApiSha512 = "ed6b2586d6fde11fde8472f5a527c51e99b67026e46f94d4bfd85e7e28ce5ee299173ee16ad576ceb51f39f98d30a811086a6deb1a86a524859cc16e12da109d"

function Get-Pinned([string]$url, [string]$path, [string]$algorithm, [string]$hash) {
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory (Split-Path $path) -Force | Out-Null
        Invoke-WebRequest -Uri $url -OutFile $path -UseBasicParsing
    }
    if ($hash -and (Get-FileHash $path -Algorithm $algorithm).Hash -ne $hash.ToUpper()) {
        Remove-Item $path
        throw "$path doesn't match its pinned $algorithm hash"
    }
}

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
function New-Zip([string]$path, [System.Collections.IDictionary]$entries) {
    $zip = [System.IO.Compression.ZipFile]::Open($path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $entries.Keys) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entries[$name], $name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
}
function New-ZipFromFolder([string]$path, [string]$folder) {
    $entries = [ordered]@{}
    $base = (Resolve-Path $folder).Path.TrimEnd('\') + '\'
    Get-ChildItem $folder -Recurse -File | Sort-Object FullName | ForEach-Object {
        $entries[$_.FullName.Substring($base.Length).Replace('\', '/')] = $_.FullName
    }
    New-Zip $path $entries
}

if (-not $NoBuild) {
    if (-not $KspDir) { throw "pass -KspDir (your KSP 1.12.x install) so the plugin can be built against its assemblies" }
    python "$root\tools\gen_proto_cs.py"
    dotnet build "$root\ksp\KerbalCraft.csproj" -c Release "-p:KSPDir=$KspDir"
    if ($LASTEXITCODE) { throw "the KSP plugin didn't build" }
    Push-Location "$root\fabric"
    try {
        .\gradlew.bat build --no-configuration-cache
        if ($LASTEXITCODE) { throw "the Fabric mod didn't build" }
    } finally { Pop-Location }
}

$dll = "$root\ksp\GameData\KerbalCraft\Plugins\KerbalCraft.dll"
$jar = "$root\fabric\build\libs\kerbalcraft-$version.jar"
foreach ($f in @($dll, $jar)) { if (-not (Test-Path $f)) { throw "missing $f (build first, or drop -NoBuild)" } }
$cache = "$root\.tools\prism"
Get-Pinned $prismUrl "$cache\$prismZip" SHA256 $prismSha256
Get-Pinned $fabricApiUrl "$cache\$fabricApiJar" SHA512 $fabricApiSha512
Get-Pinned $e4mcUrl "$cache\$e4mcJar" SHA512 $e4mcSha512
Get-Pinned $prismLicenseUrl "$cache\PrismLauncher-$prismVersion-LICENSE.txt" "" ""

$dist = "$root\dist"
New-Item -ItemType Directory $dist -Force | Out-Null
Get-ChildItem $dist | Remove-Item -Recurse -Force

$bundle = "$dist\bundle"
Copy-Item -Recurse "$root\tools\minecraft-bundle" $bundle
Expand-Archive "$cache\$prismZip" "$bundle\Prism" -Force
Copy-Item "$cache\PrismLauncher-$prismVersion-LICENSE.txt" "$bundle\Prism\LICENSE-PrismLauncher.txt"
(Get-Content "$bundle\Prism\THIRD-PARTY.txt" -Raw).Replace("{PRISM_VERSION}", $prismVersion) | Set-Content "$bundle\Prism\THIRD-PARTY.txt" -NoNewline
$mods = "$bundle\Prism\instances\KerbalCraft\.minecraft\mods"
New-Item -ItemType Directory $mods -Force | Out-Null
Copy-Item "$cache\$fabricApiJar" $mods
Copy-Item "$cache\$e4mcJar" $mods
Copy-Item $jar "$mods\kerbalcraft-$version.jar"
Set-Content "$bundle\bundle-version.txt" "KerbalCraft $version, Prism Launcher $prismVersion, $fabricApiJar, $e4mcJar" -NoNewline
New-ZipFromFolder "$dist\KerbalCraft-Minecraft.zip" $bundle
Remove-Item -Recurse -Force $bundle

$entries = [ordered]@{
    "GameData/KerbalCraft/Plugins/KerbalCraft.dll" = $dll
    "GameData/KerbalCraft/KerbalCraft.cfg" = "$root\ksp\GameData\KerbalCraft\KerbalCraft.cfg"
    "GameData/KerbalCraft/Minecraft/KerbalCraft-Minecraft.zip" = "$dist\KerbalCraft-Minecraft.zip"
    "GameData/KerbalCraft/LICENSE.txt" = "$root\LICENSE"
    "GameData/KerbalCraft/THIRD-PARTY-NOTICES.md" = "$root\THIRD-PARTY-NOTICES.md"
}
if ($ShaderBundle -and (Test-Path $ShaderBundle)) { $entries["GameData/KerbalCraft/Shaders/kerbalcraft.shaders"] = $ShaderBundle }
else { Write-Warning "no -ShaderBundle: the release falls back to KSP's own shaders (no tint, ambient occlusion or Minecraft light levels). See ksp/Unity." }
New-Zip "$dist\KerbalCraft-$version.zip" $entries
Copy-Item $jar "$dist\kerbalcraft-fabric-$version.jar"
Remove-Item "$dist\KerbalCraft-Minecraft.zip"

Get-ChildItem $dist | ForEach-Object { "{0,-40} {1,12:N0} bytes" -f $_.Name, $_.Length }
