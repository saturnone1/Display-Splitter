<#
.SYNOPSIS
    Signs and installs the local x64 development driver, without applying a split.
.DESCRIPTION
    Run elevated. Creates a dedicated code-signing certificate and trusts its
    public certificate on this machine. Signs a COPY of the DLL, regenerates
    its catalog, signs the catalog, verifies both package members, stages the
    package and creates the driver's root device. Refuses existing devices to
    avoid replaying saved monitor configuration during an upgrade. Does not
    run the app, send SET_CONFIG, change boot policy, or request a reboot.
#>
[CmdletBinding()]
param(
    [string]$SourcePackage,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $SourcePackage) { $SourcePackage = Join-Path $repo 'driver\x64\Release\DesktopSplitterVdd' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo ('artifacts\driver-install\' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$resultPath = Join-Path $OutputDirectory 'result.json'
$result = [ordered]@{ Success = $false; Started = (Get-Date).ToString('o'); Phase = 'Preflight'; Error = $null }
Start-Transcript -Path (Join-Path $OutputDirectory 'install.log') -Force | Out-Null
try {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell process.'
    }
    $devcreate = Join-Path $repo 'installer\devcreate\bin\x64\Release\devcreate.exe'
    $signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
    $inf2cat = Join-Path $repo 'artifacts\toolchain\packages\Microsoft.Windows.WDK.x64.10.0.26100.6584\c\bin\10.0.26100.0\x86\Inf2Cat.exe'
    foreach ($path in @($devcreate, $signtool, $inf2cat, "$SourcePackage\DesktopSplitterVdd.dll", "$SourcePackage\DesktopSplitterVdd.inf")) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Required file missing: $path" }
    }
    & $devcreate status
    if ($LASTEXITCODE -ne 2) { throw 'Expected no existing DesktopSplitter device. Stop to avoid an upgrade replaying saved display settings.' }
    $existing = Get-CimInstance Win32_PnPEntity | Where-Object { $_.HardwareID -contains 'Root\DesktopSplitterVdd' }
    if ($existing) { throw 'An existing device was found; installation was stopped before changing it.' }

    $package = Join-Path $OutputDirectory 'package'
    $null = New-Item -ItemType Directory -Path $package
    foreach ($name in @('DesktopSplitterVdd.dll', 'DesktopSplitterVdd.inf')) {
        Copy-Item -LiteralPath (Join-Path $SourcePackage $name) -Destination $package
    }
    $result.Package = $package
    $result.Phase = 'Certificate'
    $subject = 'CN=DesktopSplitter Local Development'
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) -and
        ($_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3')
    } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
            -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable `
            -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(1)
    }
    $result.CertificateThumbprint = $cert.Thumbprint
    $publicCert = Join-Path $OutputDirectory 'DesktopSplitterLocalDevelopment.cer'
    Export-Certificate -Cert $cert -FilePath $publicCert -Force | Out-Null
    foreach ($store in @('Root', 'TrustedPublisher')) {
        if (-not (Test-Path "Cert:\LocalMachine\$store\$($cert.Thumbprint)")) {
            Import-Certificate -FilePath $publicCert -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
        }
    }

    $result.Phase = 'SignAndVerify'
    & $signtool sign /fd sha256 /sha1 $cert.Thumbprint /s My "$package\DesktopSplitterVdd.dll"
    if ($LASTEXITCODE -ne 0) { throw "DLL signing failed: $LASTEXITCODE" }
    # Catalog hashes MUST be calculated after the DLL's embedded signature is added.
    & $inf2cat "/driver:$package" /os:10_VB_X64,10_CO_X64,10_NI_X64,10_GE_X64 /uselocaltime
    if ($LASTEXITCODE -ne 0) { throw "Catalog generation failed: $LASTEXITCODE" }
    & $signtool sign /fd sha256 /sha1 $cert.Thumbprint /s My "$package\DesktopSplitterVdd.cat"
    if ($LASTEXITCODE -ne 0) { throw "Catalog signing failed: $LASTEXITCODE" }
    foreach ($member in @('DesktopSplitterVdd.dll', 'DesktopSplitterVdd.inf')) {
        & $signtool verify /pa /v /c "$package\DesktopSplitterVdd.cat" "$package\$member"
        if ($LASTEXITCODE -ne 0) { throw "Catalog membership/signature verification failed for $member" }
    }

    $result.Phase = 'StagePackage'
    & "$env:SystemRoot\System32\pnputil.exe" /add-driver "$package\DesktopSplitterVdd.inf"
    $stageExit = $LASTEXITCODE
    $result.StageExitCode = $stageExit
    if ($stageExit -notin @(0, 3010)) { throw "Driver-store staging failed: $stageExit" }
    $result.Phase = 'CreateDevice'
    & $devcreate create
    if ($LASTEXITCODE -ne 0) { throw "Device creation failed: $LASTEXITCODE" }

    $result.Phase = 'VerifyDevice'
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $device = Get-CimInstance Win32_PnPEntity | Where-Object { $_.HardwareID -contains 'Root\DesktopSplitterVdd' } | Select-Object -First 1
        if ($device -and $device.ConfigManagerErrorCode -eq 0) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    if (-not $device) { throw 'Device was not enumerated after installation.' }
    $result.DeviceInstanceId = $device.PNPDeviceID
    $result.DeviceProblemCode = $device.ConfigManagerErrorCode
    if ($device.ConfigManagerErrorCode -ne 0) {
        throw "Driver did not start (device problem code $($device.ConfigManagerErrorCode)). Boot policy and display settings were not changed."
    }
    $signed = Get-CimInstance Win32_PnPSignedDriver | Where-Object DeviceID -eq $device.PNPDeviceID | Select-Object -First 1
    $result.PublishedInf = $signed.InfName
    $result.DriverVersion = $signed.DriverVersion
    $result.RebootRequested = $false
    $result.Success = $true
    $result.Phase = 'Complete'
    Write-Host 'Driver installed. No split was applied. No display SET operation was sent.'
} catch {
    $result.Error = $_.Exception.Message
    Write-Host "INSTALL FAILED: $($result.Error)"
} finally {
    $result.Finished = (Get-Date).ToString('o')
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    Stop-Transcript | Out-Null
}
if (-not $result.Success) { exit 1 }
