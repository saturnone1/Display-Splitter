# Rerun the guarded guest suite in an existing lab VM, using PowerShell Direct.
[CmdletBinding()]
param([string]$LabRoot='F:\DesktopSplitter-Win10Lab\Clean',[string]$VmName='DesktopSplitter-Win10-21H2-Clean',[int]$TimeoutMinutes=15,[string]$InstallerPath,[switch]$FreshDevice,[int]$GuestTimeoutSeconds=120)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if(-not $InstallerPath){$InstallerPath=Join-Path $repo 'artifacts\installer\DesktopSplitter-0.5.6-FreeLayout9-win-x64-Setup.exe'}
$InstallerPath=(Resolve-Path -LiteralPath $InstallerPath -ErrorAction Stop).Path
$lab=[IO.Path]::GetFullPath($LabRoot).TrimEnd('\')
Start-Transcript -Path (Join-Path $lab 'rerun.log') -Append | Out-Null
$session=$null
$mounted=$false
$vhd=Join-Path $lab 'Windows10-21H2.vhdx'
try {
    $vm=Get-VM -Name $VmName
    if(@($vm|Get-VMNetworkAdapter).Count-ne0 -or ($vm|Get-VMFirmware).SecureBoot-ne'On'){throw 'Lab checks require an isolated, Secure Boot-enabled VM'}
    $protectedPath=Join-Path $repo 'artifacts\user-active-vms.json'
    if((Test-Path $protectedPath) -and (Get-Content $protectedPath -Raw|ConvertFrom-Json).ProtectedVmIds-contains$vm.Id.ToString()){throw 'User is using this VM; refusing to mutate it'}
    $disks=@($vm | Get-VMHardDiskDrive)
    if($disks.Count -ne 1 -or [IO.Path]::GetFullPath($disks[0].Path) -ne $vhd){throw 'VM is not attached solely to the expected lab VHD'}
    $credential=Import-Clixml -LiteralPath (Join-Path $lab 'guest-credential.clixml')
    if($vm.State -eq 'Off'){
        if((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory*1KB -lt $vm.MemoryStartup+4GB){throw 'Insufficient free host memory to preserve a 4 GiB reserve'}
        $vm | Start-VM
    }
    'Waiting for guest PowerShell Direct' | Set-Content (Join-Path $lab 'phase.txt')
    $deadline=(Get-Date).AddMinutes($TimeoutMinutes)
    while(-not $session){
        try{$session=New-PSSession -VMId $vm.Id -Credential $credential -ErrorAction Stop}catch{if((Get-Date) -gt $deadline){throw};Start-Sleep -Seconds 3}
    }
    Copy-Item -LiteralPath "$PSScriptRoot\Invoke-GuestChecks.ps1","$PSScriptRoot\DriverRoundTrip.cs","$PSScriptRoot\VmDisplayTopology.cs","$PSScriptRoot\Test-NineComposition.ps1" -Destination 'C:\DesktopSplitterLab' -ToSession $session
    Copy-Item -LiteralPath $InstallerPath -Destination 'C:\DesktopSplitterLab\Setup.exe' -ToSession $session
    Copy-Item -LiteralPath "$repo\tools\Test-DriverConnection.ps1" -Destination 'C:\DesktopSplitterLab\Test-DriverConnection.ps1' -ToSession $session
    Copy-Item "$repo\artifacts\win10-lab-smoke\*" -Destination 'C:\DesktopSplitterLab\smoke' -ToSession $session -Recurse -Force
    $password=$credential.GetNetworkCredential().Password
    Invoke-Command -Session $session -ArgumentList $password,([bool]$FreshDevice),$GuestTimeoutSeconds -ScriptBlock {
        param($password,$freshDevice,$guestTimeout)
        if($env:COMPUTERNAME -ne 'DSWIN10LAB' -or (Get-CimInstance Win32_OperatingSystem).BuildNumber -ne '19044'){throw 'Wrong guest'}
        $root='C:\DesktopSplitterLab'
        if(Test-Path "$root\Results\summary.json"){Copy-Item "$root\Results\summary.json" "$root\Results\previous-summary.json" -Force}
        $labId=(Get-Content "$root\lab-id.txt" -Raw).Trim()
        $user="$env:COMPUTERNAME\LabAdmin"
        $freshArgument=if($freshDevice){' -FreshDevice'}else{''}
        $action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File $root\Invoke-GuestChecks.ps1 -LabId $labId$freshArgument -TestTimeoutSeconds $guestTimeout"
        $principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        $trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
        Register-ScheduledTask -TaskName 'DesktopSplitterLabChecks' -Action $action -Principal $principal -Trigger $trigger -Force | Out-Null
        if((Get-CimInstance Win32_ComputerSystem).UserName){Start-ScheduledTask -TaskName 'DesktopSplitterLabChecks'}else{
            $key='HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
            Set-ItemProperty $key AutoAdminLogon '1'
            Set-ItemProperty $key DefaultUserName 'LabAdmin'
            Set-ItemProperty $key DefaultDomainName $env:COMPUTERNAME
            Set-ItemProperty $key DefaultPassword $password
            Set-ItemProperty $key AutoLogonCount 1 -Type DWord
            # Reboot only the guard-verified guest to enter its interactive session.
            Start-Process shutdown.exe -ArgumentList @('/r','/t','0') -WindowStyle Hidden
        }
    }
    $password=$null
    Remove-PSSession $session
    $session=$null
    'Running Windows 10 guest checks' | Set-Content (Join-Path $lab 'phase.txt')
    while((Get-VM -Id $vm.Id).State -ne 'Off'){
        if((Get-Date) -gt $deadline){throw 'Guest test deadline exceeded; VM retained'}
        Start-Sleep -Seconds 3
    }
    $disk=Mount-VHD -Path $vhd -ReadOnly -PassThru | Get-Disk
    $mounted=$true
    $os=Get-Partition -DiskNumber $disk.Number | Where-Object {($_ | Get-Volume -ErrorAction SilentlyContinue).FileSystemLabel -eq 'DSLabWin10'} | Select-Object -First 1
    if(-not $os.DriveLetter){$os | Add-PartitionAccessPath -AssignDriveLetter;$os=Get-Partition -DiskNumber $disk.Number -PartitionNumber $os.PartitionNumber}
    $results="$($os.DriveLetter):\DesktopSplitterLab\Results"
    New-Item -ItemType Directory -Path (Join-Path $lab 'Results') -Force | Out-Null
    Get-ChildItem -LiteralPath $results | Copy-Item -Destination (Join-Path $lab 'Results') -Recurse -Force
    Dismount-VHD -Path $vhd
    $mounted=$false
    $summary=Get-Content (Join-Path $lab 'Results\summary.json') -Raw | ConvertFrom-Json
    if(-not $summary.Success){throw ('Guest checks failed: '+$summary.Error)}
    'Windows 10 guest checks passed' | Set-Content (Join-Path $lab 'phase.txt')
}catch{
    $_ | Out-String | Set-Content (Join-Path $lab 'rerun-error.txt')
    ('Failed: '+$_.Exception.Message) | Set-Content (Join-Path $lab 'phase.txt')
    throw
}finally{
    if($session){Remove-PSSession $session -ErrorAction Continue}
    if($mounted){Dismount-VHD -Path $vhd -ErrorAction Continue}
    Stop-Transcript | Out-Null
}
