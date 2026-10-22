# Read PE imports only. Does not run the product, install anything or change displays.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer/vswhere is required.' }
$dumpbin = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\dumpbin.exe' | Select-Object -First 1
if (-not $dumpbin) { throw 'dumpbin.exe was not found in the C++ tools.' }

$files = @(
    'compositor\build\x64\Release\dscomp.exe',
    'installer\edid-override\build\x64\Release\edidoverride.exe',
    'installer\devcreate\bin\x64\Release\devcreate.exe'
)
foreach ($file in $files) {
    $path = Join-Path $repoRoot $file
    if (-not (Test-Path -LiteralPath $path)) { throw "Build the Release binary first: $file" }
    $imports = & $dumpbin /nologo /dependents $path 2>&1
    if ($LASTEXITCODE -ne 0) { throw "dumpbin failed: $file" }
    $redist = @($imports | Where-Object { $_ -match '(?i)^\s*(msvcp\d+[^\s]*|vcruntime\d+[^\s]*|concrt\d+[^\s]*)\.dll\s*$' })
    if ($redist.Count -gt 0) {
        throw "$file requires undeployed VC++ redist DLLs: $($redist -join ', ')"
    }
    Write-Host "PASS $file : no external VC++ redistributable dependency"
}

# Check the actual published PE, not just the MSBuild property: single-file
# publishing uses a different host from the ordinary build output.
$managedHost = Join-Path $repoRoot 'installer\setup\payload\DesktopSplitter.exe'
if (Test-Path -LiteralPath $managedHost) {
    $headers = & $dumpbin /nologo /headers $managedHost 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the published managed host' }
    if ($headers -match 'CET compatible') {
        throw 'Published .NET host enables CET and can abort before Main on early Windows 10 21H2. Publish with CETCompat=false.'
    }
    Write-Host 'PASS published .NET host: no CET requirement on early Windows 10 21H2'
}
