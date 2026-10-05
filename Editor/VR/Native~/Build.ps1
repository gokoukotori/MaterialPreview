param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio C++ x64 tools are required to rebuild the bridge.' }
$devCommand = Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
$environment = & $env:ComSpec /d /c "call `"$devCommand`" -no_logo -arch=x64 -host_arch=x64 && set"
if ($LASTEXITCODE -ne 0) { throw 'Visual Studio environment setup failed.' }
foreach ($line in $environment) {
    if ($line -match '^(PATH|INCLUDE|LIB|LIBPATH)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process') }
}
Push-Location $output
try {
    & cl.exe /nologo /std:c++17 /O2 /MT /EHsc /W4 /WX /LD (Join-Path $PSScriptRoot 'VrRenderBridge.cpp') /link /OUT:material_preview_vr_render.dll
    if ($LASTEXITCODE -ne 0) { throw 'Native bridge build failed.' }
    & cl.exe /nologo /std:c++17 /O2 /MT /EHsc /W4 /WX (Join-Path $PSScriptRoot 'VrRenderBridgeTests.cpp') /link d3d11.lib /OUT:VrRenderBridgeTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Native bridge test build failed.' }
    & (Join-Path $output 'VrRenderBridgeTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Native bridge tests failed.' }
    & cl.exe /nologo /std:c++17 /O2 /MT /EHsc /W4 /WX /LD (Join-Path $PSScriptRoot 'VrRenderBridgeFixture.cpp') /link /OUT:material_preview_vr_fixture.dll
    if ($LASTEXITCODE -ne 0) { throw 'Native Unity test fixture build failed.' }
} finally { Pop-Location }
