# Static regression guard for the Windows 10 21H2 installation contract.
[CmdletBinding()]
param([string]$InfPath,[string]$ExpectedDriverVersion)
$ErrorActionPreference = 'Stop'
if (-not $InfPath) { $InfPath = Join-Path $PSScriptRoot '..\driver\x64\Release\DesktopSplitterVdd\DesktopSplitterVdd.inf' }
if (-not $ExpectedDriverVersion) {
    $generated=Join-Path $PSScriptRoot '..\build\generated\Version.props'
    if(Test-Path -LiteralPath $generated) { $productVersion=([xml](Get-Content -LiteralPath $generated -Raw)).Project.PropertyGroup.DeskSplitVersion }
    else { $productVersion=(Get-Content (Join-Path $PSScriptRoot '..\VERSION') | Where-Object {$_ -match '^\d+\.\d+\.\d+\s*$'} | Select-Object -Last 1).Trim() }
    $ExpectedDriverVersion="$productVersion.0"
}
function Find-Incompatibilities([string]$text) {
    $issues = @()
    $active = (($text -split '\r?\n' | ForEach-Object { ($_ -split ';',2)[0].Trim() }) -join "`n")
    $driverVersion=[regex]::Match($active,'(?im)^DriverVer\s*=\s*\d+/\d+/\d+,\s*(\d+\.\d+\.\d+\.\d+)\s*$')
    if(-not $driverVersion.Success -or $driverVersion.Groups[1].Value -ne $ExpectedDriverVersion) { $issues += "DriverVer must match the product version $ExpectedDriverVersion, not the WDK wall-clock default" }
    if ($active -match '(?im)^Include\s*=.*\bWUDFRD\.inf\b') { $issues += 'WUDFRD.inf is supplied only on Windows 11' }
    if ($active -notmatch '(?im)^AddService\s*=\s*WUDFRd\s*,\s*0x0*1fa\s*,') { $issues += 'Missing Windows 10 reflector function service registration' }
    $framework = [regex]::Match($active,'(?im)^UmdfLibraryVersion\s*=\s*(\d+)\.(\d+)\.\d+\s*$')
    if (-not $framework.Success -or [int]$framework.Groups[1].Value -ne 2 -or [int]$framework.Groups[2].Value -gt 31) { $issues += 'Built INF must use a UMDF runtime available on Windows 10 21H2 (2.31 or earlier)' }
    $models = [regex]::Matches($active,'(?im)^\[Standard\.NTamd64\.10\.0\.\.\.(\d+)\]$')
    if (-not @($models | Where-Object { [int]$_.Groups[1].Value -le 19044 }).Count) { $issues += 'No x64 model section applicable to Windows 10 build 19044' }
    return $issues
}
$text = Get-Content -LiteralPath $InfPath -Raw
$issues = @(Find-Incompatibilities $text)
if ($issues.Count) { throw ($issues -join '; ') }
# Reproduce the previous Windows 11-only dependency, and a too-new UMDF baseline.
$win11Only = $text + "`n[RegressionFixture]`nInclude=WUDFRD.inf`n"
if (-not @(Find-Incompatibilities $win11Only).Count) { throw 'Regression guard missed the previous installation defect' }
$newFramework = $text -replace '(?im)(^UmdfLibraryVersion\s*=).*','${1}2.33.0'
if (-not @(Find-Incompatibilities $newFramework).Count) { throw 'Regression guard missed Windows 11-only UMDF' }
$clockVersion=$text -replace '(?im)(^DriverVer\s*=\s*[^,]+,).*','${1}22.31.0.1'
if (-not @(Find-Incompatibilities $clockVersion).Count) { throw 'Regression guard missed the IddCx-stalling clock version' }
Write-Output 'PASS: Windows 10 21H2 INF baseline and incompatible-package regressions'
