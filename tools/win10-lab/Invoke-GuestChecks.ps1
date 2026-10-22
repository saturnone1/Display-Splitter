# Mutating tests are restricted to this freshly provisioned, offline Win10 VM.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$LabId,[int]$TestTimeoutSeconds=120,[switch]$FreshDevice)
$ErrorActionPreference = 'Stop'
$root = 'C:\DesktopSplitterLab'
$os = Get-CimInstance Win32_OperatingSystem
$computer = Get-CimInstance Win32_ComputerSystem
if ($os.BuildNumber -ne '19044' -or $computer.Model -ne 'Virtual Machine' -or $computer.Manufacturer -ne 'Microsoft Corporation' -or $env:COMPUTERNAME -ne 'DSWIN10LAB' -or (Get-Content "$root\lab-id.txt" -Raw).Trim() -ne $LabId) { throw 'Not the authorized Windows 10 lab VM' }
Start-Transcript -Path "$root\Results\guest.log" -Append | Out-Null
$summary = [ordered]@{ OS=$os.Caption; Build=$os.BuildNumber; LabId=$LabId; Steps=@(); Success=$false; Error=$null }
function Run-Check($name,$file,$arguments) {
    # Own the process handle; PS 5.1 Start-Process without -Wait can lose ExitCode.
    $process = New-Object Diagnostics.Process
    $process.StartInfo.FileName = $file
    $process.StartInfo.Arguments = $arguments -join ' ' # All lab-generated arguments are space-free.
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    try {
        $process.Start() | Out-Null
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TestTimeoutSeconds*1000)) { $process.Kill(); throw "Test deadline exceeded: $name" }
        $process.WaitForExit()
        $stdout.GetAwaiter().GetResult() | Set-Content "$root\Results\$name.stdout.txt"
        $stderr.GetAwaiter().GetResult() | Set-Content "$root\Results\$name.stderr.txt"
        $summary.Steps += [ordered]@{Name=$name;ExitCode=$process.ExitCode}
        if ($process.ExitCode -ne 0) { throw "$name failed: exit $($process.ExitCode)" }
    } finally { $process.Dispose() }
}
try {
    $os | Select-Object Caption,Version,BuildNumber,OSArchitecture | ConvertTo-Json | Set-Content "$root\Results\os.json"
    & "$root\Test-DriverConnection.ps1" -OutputPath "$root\Results\driver-before-install.json" | Out-Null
    Run-Check 'installer-selftest' "$root\Setup.exe" @('/selftest')
    if ($FreshDevice) {
        $before=Get-Content "$root\Results\driver-before-install.json" -Raw|ConvertFrom-Json
        if(@($before.Interfaces|Where-Object {$_.ActiveMonitorCount -ne 0}).Count){throw 'Fresh-device test requires zero active virtual monitors'}
        Run-Check 'uninstall-idle-driver' "$root\Setup.exe" @('/uninstall','/quiet')
    }
    # Fresh installation and idempotent reinstallation use the real shipping EXE.
    Run-Check 'install' "$root\Setup.exe" @('/quiet','/noshortcut','/keeptemp','/dir',"$root\Product")
    & "$root\Test-DriverConnection.ps1" -OutputPath "$root\Results\driver-after-install.json"
    $probe = Get-Content "$root\Results\driver-after-install.json" -Raw | ConvertFrom-Json
    if (-not @($probe.Interfaces | Where-Object { $_.Connected -and $_.Protocol -eq 3 }).Count) { throw 'No protocol 3 driver connection after install' }
    Run-Check 'reinstall' "$root\Setup.exe" @('/quiet','/noshortcut','/keeptemp','/dir',"$root\Product")
    # WMI package metadata can remain stale after reinstall. Query the live
    # device property as well; protocol 3 alone does not prove package binding.
    $productVersion=[regex]::Match((Get-Item "$root\Setup.exe").VersionInfo.ProductVersion,'^\d+\.\d+\.\d+').Value
    if(-not $productVersion){throw 'Installer product version missing'}
    $expectedDriverVersion="$productVersion.0"
    $bindings=@(foreach($device in $probe.Devices){
        $version=(Get-PnpDeviceProperty -InstanceId $device.PNPDeviceID -KeyName 'DEVPKEY_Device_DriverVersion' -ErrorAction Stop).Data
        $inf=(Get-PnpDeviceProperty -InstanceId $device.PNPDeviceID -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction Stop).Data
        [pscustomobject]@{Device=$device.PNPDeviceID;DriverVersion=$version;Inf=$inf;ExpectedVersion=$expectedDriverVersion}
    })
    $bindings|ConvertTo-Json -Depth 3|Set-Content "$root\Results\live-driver-binding.json"
    if(-not $bindings.Count -or @($bindings|Where-Object {$_.DriverVersion -ne $expectedDriverVersion}).Count){throw 'Installed device is not bound to the selected product-version driver'}
    $summary.Steps += [ordered]@{Name='product-driver-binding';ExitCode=0}
    Run-Check 'installer-verify' "$root\Setup.exe" @('/verify')
    Run-Check 'compositor-load' "$root\Product\dscomp.exe" @('--help')
    Run-Check 'device-helper-status' "$root\Product\devcreate.exe" @('status')
    Add-Type -Path "$root\DriverRoundTrip.cs","$root\VmDisplayTopology.cs" -ReferencedAssemblies 'System.dll','System.Core.dll'
    $roundTripError=$null
    try {
        $verifyComposition=[Action[object]]{param($grid) & "$root\Test-NineComposition.ps1" -Grid $grid -TimeoutSeconds $TestTimeoutSeconds}
        [DeskSplitVmRoundTrip]::Run($TestTimeoutSeconds,$verifyComposition) | ConvertTo-Json -Depth 6 | Set-Content "$root\Results\fhd-roundtrip.json"
        $summary.Steps += [ordered]@{Name='fhd-driver-roundtrip';ExitCode=0}
        $summary.Steps += [ordered]@{Name='nine-compositor-nine-colors';ExitCode=0}
    } catch {
        $roundTripError=$_.Exception.Message
        $roundTripError | Set-Content "$root\Results\fhd-roundtrip-error.txt"
        $summary.Steps += [ordered]@{Name='fhd-driver-roundtrip';ExitCode=1}
    }
    foreach ($name in @('driver-status','free-layout','win10-fixes','matrix','matrix-ui','live-sync','shortcuts','refactor','stability','confirmation-ui','state-checks','hiding-ui','appearance','error-reporter','lifecycle-close','lifecycle-exit','lifecycle-preview','lifecycle-manual')) {
        Run-Check $name "$root\smoke\ui-smoke.exe" @("--$name")
    }
    # The actual self-contained shipping app is opened/closed inside the VM.
    # Manual review suppresses startup apply, recovery and exit restoration.
    $settingsDir = Join-Path $env:APPDATA 'DesktopSplitter'
    New-Item -ItemType Directory -Path $settingsDir -Force | Out-Null
    @{minimizeToTrayOnClose=$false} | ConvertTo-Json | Set-Content (Join-Path $settingsDir 'settings.json')
    $app = New-Object Diagnostics.Process
    $app.StartInfo.FileName = "$root\Product\DesktopSplitter.exe"
    $app.StartInfo.Arguments = '--manual-review'
    $app.StartInfo.UseShellExecute = $false
    $app.Start() | Out-Null
    try {
        if (-not $app.WaitForInputIdle($TestTimeoutSeconds*1000)) { throw 'Shipping app did not reach input idle' }
        $uiDeadline = (Get-Date).AddSeconds($TestTimeoutSeconds)
        do {
            $app.Refresh()
            if ($app.HasExited) { throw "Shipping app exited before showing its window: $($app.ExitCode)" }
            if ($app.MainWindowHandle -ne 0) { break }
            Start-Sleep -Milliseconds 200
        } while ((Get-Date) -lt $uiDeadline)
        if ($app.MainWindowHandle -eq 0) { throw 'Shipping app did not show its window' }
        # Closing through the tray is deliberately avoided: there is no host UI action.
        if (-not $app.CloseMainWindow()) { throw 'Shipping app rejected close' }
        if (-not $app.WaitForExit($TestTimeoutSeconds*1000)) { throw 'Shipping app did not close' }
        if ($app.ExitCode -ne 0) { throw "Shipping app close failed: $($app.ExitCode)" }
        $summary.Steps += [ordered]@{Name='shipping-app-open-close';ExitCode=$app.ExitCode}
    } finally { if (-not $app.HasExited) { $app.Kill() }; $app.Dispose() }
    & "$root\Test-DriverConnection.ps1" -OutputPath "$root\Results\driver-after-tests.json"
    $summary.Success = $null -eq $roundTripError
    $summary.Error = $roundTripError
} catch {
    $summary.Error = $_.Exception.Message
    $_ | Out-String | Set-Content "$root\Results\error.txt"
} finally {
    try { & "$root\Test-DriverConnection.ps1" -OutputPath "$root\Results\driver-finally.json" | Out-Null }
    catch { $_ | Out-String | Set-Content "$root\Results\driver-finally-error.txt" }
    foreach ($channel in @('Microsoft-Windows-CodeIntegrity/Operational','Microsoft-Windows-Kernel-PnP/Configuration','Microsoft-Windows-DriverFrameworks-UserMode/Operational')) {
        $name=($channel -replace '[^a-zA-Z0-9-]','_')
        Get-WinEvent -LogName $channel -MaxEvents 60 -ErrorAction SilentlyContinue | Select-Object TimeCreated,ProviderName,Id,Message | ConvertTo-Json -Depth 3 | Set-Content "$root\Results\$name.json"
    }
    Get-CimInstance Win32_Process -Filter "Name='WUDFHost.exe' OR Name='dwm.exe'" -ErrorAction SilentlyContinue | Select-Object Name,ProcessId,SessionId,CommandLine | ConvertTo-Json -Depth 3 | Set-Content "$root\Results\graphics-processes.json"
    Get-FileHash "$env:SystemRoot\System32\drivers\UMDF\DesktopSplitterVdd.dll" -Algorithm SHA256 -ErrorAction SilentlyContinue | Select-Object Path,Hash | ConvertTo-Json | Set-Content "$root\Results\installed-driver-hash.json"
    Copy-Item "$env:SystemRoot\INF\setupapi.dev.log" "$root\Results\setupapi.dev.log" -ErrorAction SilentlyContinue
    Get-WinEvent -FilterHashtable @{LogName='System';Level=1,2,3} -MaxEvents 80 -ErrorAction SilentlyContinue | Select-Object TimeCreated,ProviderName,Id,Message | ConvertTo-Json -Depth 4 | Set-Content "$root\Results\system-events.json"
    $summary | ConvertTo-Json -Depth 6 | Set-Content "$root\Results\summary.json"
    Unregister-ScheduledTask -TaskName 'DesktopSplitterLabChecks' -Confirm:$false -ErrorAction SilentlyContinue
    Stop-Transcript | Out-Null
    # Only this guard-verified guest is shut down. The host is never restarted.
    Stop-Computer -Force
}
