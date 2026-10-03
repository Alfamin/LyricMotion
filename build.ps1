# Builds Lyric Motion against the Noctis that is installed on this PC and packs the
# zip that Settings -> Plugins -> "Install from file..." takes.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-Noctis C:\path\to\Noctis] [-Compiler C:\path\to\csc.exe]
#
# No .NET SDK needed: any Roslyn csc.exe will do, and the reference assemblies are
# Noctis' own. By default the compiler is looked for in tools\roslyn (the NuGet package
# microsoft.net.compilers.toolset 5.0.0 from nuget.org, unpacked there).
param(
    [string]$Noctis = "$env:LOCALAPPDATA\Programs\Noctis",
    [string]$Version = "1.4.0",
    [string]$Compiler = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csc = if ($Compiler) { $Compiler } else { Join-Path $root "tools\roslyn\tasks\net472\csc.exe" }
$out = Join-Path $root "dist\LyricMotion"
$zip = Join-Path $root "dist\lyric-motion-$Version.zip"

if (-not (Test-Path $csc)) { throw "Compiler not found: $csc" }
if (-not (Test-Path (Join-Path $Noctis "Noctis.Plugins.Abstractions.dll"))) { throw "Noctis not found in $Noctis" }

# A plugin built against a newer plugin kit than the one plugin.json names is refused by
# every Noctis that came with the older kit, so the kit has to be the one that apiVersion
# names. Noctis updates itself: when it has moved on, point -Noctis at a copy of an older
# release (1.5.8 has kit 1.1).
$api = (Get-Content (Join-Path $root "src\plugin.json") -Raw | ConvertFrom-Json).apiVersion
$kit = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $Noctis "Noctis.Plugins.Abstractions.dll")).Version
if ("$($kit.Major).$($kit.Minor)" -ne $api) {
    throw "The Noctis in $Noctis has plugin kit $($kit.Major).$($kit.Minor), but plugin.json says apiVersion $api. Use -Noctis with a Noctis release that has kit $api."
}

# Everything the app supplies at run time: the .NET libraries, Avalonia and the plugin kit.
$refs = Get-ChildItem $Noctis -Filter *.dll | Where-Object {
    ($_.Name -like "System.*" -and $_.Name -notlike "*Native*") -or
    $_.Name -in "netstandard.dll", "mscorlib.dll", "Avalonia.Base.dll", "Avalonia.Controls.dll", "Noctis.Plugins.Abstractions.dll"
} | ForEach-Object { "-r:`"$($_.FullName)`"" }

New-Item -ItemType Directory -Force $out | Out-Null
$rsp = Join-Path $root "dist\build.rsp"
@(
    "-nologo", "-nostdlib+", "-noconfig", "-target:library", "-optimize+", "-deterministic+",
    "-langversion:latest", "-nullable:enable", "-debug-", "-warnaserror-",
    "-out:`"$out\LyricMotion.dll`""
) + $refs + (Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { "`"$($_.FullName)`"" }) |
    Set-Content -Encoding utf8 $rsp

& $csc "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Compilation failed." }

Copy-Item (Join-Path $root "src\plugin.json") $out -Force
if (Test-Path $zip) { Remove-Item $zip -Confirm:$false }
Compress-Archive -Path (Join-Path $out "LyricMotion.dll"), (Join-Path $out "plugin.json") -DestinationPath $zip
# The same zip under a name that stays the same from version to version (the one the README links to).
Copy-Item $zip (Join-Path $root "LyricMotion-for-Noctis.zip") -Force
"Built $zip"
