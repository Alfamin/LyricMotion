param(
    [string]$Noctis = "$env:LOCALAPPDATA\Programs\Noctis",
    [string]$Dotnet = "$env:USERPROFILE\dotnet-sdk\dotnet.exe"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root 'dist\tests'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$headless = Join-Path $env:USERPROFILE '.nuget\packages\avalonia.headless\12.1.3\lib\net10.0\Avalonia.Headless.dll'
if (-not (Test-Path -LiteralPath $headless)) { throw 'Cached Avalonia.Headless is required; no downloads are performed.' }
$refs = Get-ChildItem -LiteralPath $Noctis -Filter '*.dll' | Where-Object {
    ($_.Name -like 'System.*' -and $_.Name -notlike '*Native*') -or
    $_.Name -in @('netstandard.dll','mscorlib.dll','Avalonia.dll','Avalonia.Base.dll','Avalonia.Controls.dll','Avalonia.Skia.dll','Avalonia.HarfBuzz.dll','SkiaSharp.dll','Noctis.Plugins.Abstractions.dll')
} | ForEach-Object { '-r:"' + $_.FullName + '"' }
$sources = Get-ChildItem -LiteralPath (Join-Path $root 'src'),$PSScriptRoot -Filter '*.cs' | ForEach-Object { '"' + $_.FullName + '"' }
$rsp = Join-Path $out 'tests.rsp'
@('-nologo','-nostdlib+','-target:exe','-optimize+','-langversion:latest','-nullable:enable',('-out:"' + (Join-Path $out 'MotionTests.dll') + '"'),('-r:"' + $headless + '"')) + $refs + $sources | Set-Content -LiteralPath $rsp -Encoding utf8
& (Join-Path $root 'tools\roslyn\tasks\net472\csc.exe') -noconfig "@$rsp"
if ($LASTEXITCODE -ne 0) { throw 'Motion test compilation failed.' }
Copy-Item -LiteralPath $headless -Destination $out -Force
Get-ChildItem -LiteralPath $Noctis -Filter 'Avalonia*.dll' | Copy-Item -Destination $out -Force
foreach ($name in @('Noctis.Plugins.Abstractions.dll','Avalonia.dll','Avalonia.Base.dll','Avalonia.Controls.dll','Avalonia.Markup.dll','Avalonia.Markup.Xaml.dll','Avalonia.Skia.dll','Avalonia.HarfBuzz.dll','SkiaSharp.dll','SkiaSharp.Resources.dll','SkiaSharp.SceneGraph.dll','SkiaSharp.Skottie.dll','libSkiaSharp.dll','HarfBuzzSharp.dll','libHarfBuzzSharp.dll','MicroCom.Runtime.dll')) {
    if (Test-Path -LiteralPath (Join-Path $Noctis $name)) { Copy-Item -LiteralPath (Join-Path $Noctis $name) -Destination $out -Force }
}
'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"},"rollForward":"LatestMinor"}}' | Set-Content -LiteralPath (Join-Path $out 'MotionTests.runtimeconfig.json') -Encoding utf8
& $Dotnet (Join-Path $out 'MotionTests.dll')
if ($LASTEXITCODE -ne 0) { throw 'Motion tests failed.' }
