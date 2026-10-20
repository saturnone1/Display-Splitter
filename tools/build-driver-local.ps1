# Builds only: never signs, installs, deploys, or starts the driver.
# Requires the project-local toolchain described in docs/BUILDING.md.
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$VisualStudioPath
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolchain = Join-Path $repo 'artifacts\toolchain'
if (-not $VisualStudioPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $VisualStudioPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $VisualStudioPath) { throw 'No Visual Studio C++ installation found.' }
$msbuild = Join-Path $VisualStudioPath 'MSBuild\Current\Bin\amd64\MSBuild.exe'
$spectreDesktop = "$toolchain\spectre-x64\Contents\VC\Tools\MSVC\14.44.35207\lib\spectre\x64"
$spectreOneCore = "$toolchain\spectre-onecore-x64\Contents\VC\Tools\MSVC\14.44.35207\lib\spectre\onecore\x64"
foreach ($required in @(
    $msbuild,
    "$VisualStudioPath\VC\Tools\MSVC\14.44.35207",
    "$toolchain\msbuild\v170\Microsoft.Cpp.Default.props",
    "$toolchain\local-wdk.props",
    "$toolchain\local-wdk.targets",
    "$spectreDesktop\libcmt.lib",
    "$spectreOneCore\libcmt.lib"
)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Missing local prerequisite: $required. See docs/BUILDING.md."
    }
}
$buildArgs = @(
    "$repo\driver\DesktopSplitterVdd.vcxproj", '/t:Build', '/m:1',
    '/p:VisualStudioVersion=17.0', "/p:Configuration=$Configuration", '/p:Platform=x64',
    '/p:SignMode=Off', '/p:EnableTestSign=false', '/p:Inf2CatUseLocalTime=true',
    '/p:WindowsTargetPlatformVersion=10.0.26100.0', '/p:_NT_TARGET_VERSION=0xA000008',
    '/p:VCToolsVersion=14.44.35207', '/p:MultiProcessorCompilation=false',
    "/p:VCTargetsPath=$toolchain\msbuild\v170\",
    "/p:DirectoryBuildPropsPath=$toolchain\local-wdk.props",
    "/p:ForceImportAfterCppTargets=$toolchain\local-wdk.targets",
    "/p:VCInstallDir=$VisualStudioPath\VC\",
    "/p:VC_LibraryPath_VC_x64_OneCore_spectre=$spectreOneCore",
    "/p:VC_LibraryPath_VC_x64_Desktop_spectre=$spectreDesktop",
    "/p:VC_LibraryPath_VC_OneCore_CurrentPlatform_spectre=$spectreOneCore",
    "/p:VC_LibraryPath_VC_Desktop_CurrentPlatform_spectre=$spectreDesktop",
    '/v:minimal', '/nologo'
)
$previousCL = $env:CL
try {
    $env:CL = "$previousCL /utf-8"
    & $msbuild @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Driver build failed with exit code $LASTEXITCODE." }
} finally {
    $env:CL = $previousCL
}
