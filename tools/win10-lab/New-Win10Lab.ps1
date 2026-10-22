# Run elevated. Only a new, lab-owned VHDX is partitioned/formatted.
# No host drivers, display settings, network switches or boot entries are changed.
[CmdletBinding()]
param(
    [string]$LabRoot = 'F:\DesktopSplitter-Win10Lab',
    [string]$IsoPath = 'F:\DesktopSplitter-Win10Lab\Windows10-21H2-LTSC-eval-x64.iso',
    [string]$VmName = 'DesktopSplitter-Win10-21H2-Lab',
    [string]$InstallerPath,
    [ValidateRange(2GB,16GB)][long]$MemoryStartupBytes=4GB,
    [ValidateRange(1,100)][int]$ProcessorMaximum=50,
    [uint16]$VideoWidth=1920,
    [uint16]$VideoHeight=1080,
    [int]$SetupTimeoutMinutes = 25,
    [switch]$ResumePreparedDisk
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$lab = [IO.Path]::GetFullPath($LabRoot).TrimEnd('\')
if ($lab -eq [IO.Path]::GetPathRoot($lab).TrimEnd('\')) { throw 'Lab must be a dedicated subdirectory' }
$ancestor = $lab
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse point: $ancestor" }
    $ancestor = Split-Path -Parent $ancestor
}
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator access required for Hyper-V' }
New-Item -ItemType Directory -Path $lab -Force | Out-Null
Start-Transcript -Path (Join-Path $lab 'host-setup.log') -Append | Out-Null
$vhd = Join-Path $lab 'Windows10-21H2.vhdx'
$mounted = $false
$isoMounted = $false
function Phase($message) { $message | Set-Content -LiteralPath (Join-Path $lab 'phase.txt'); Write-Host $message }
try {
    Phase 'Verifying official ISO and prerequisites'
    if ((Get-FileHash -LiteralPath $IsoPath -Algorithm SHA256).Hash -ne 'E4AB2E3535BE5748252A8D5D57539A6E59BE8D6726345EE10E7AFD2CB89FEFB5') { throw 'ISO SHA256 differs from Microsoft published 21H2 LTSC evaluation hash' }
    if (Get-VM -Name $VmName -ErrorAction SilentlyContinue) { throw 'VM already exists; refusing to replace it' }
    # Check before applying an image, then check again immediately before boot.
    if ((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1KB -lt $MemoryStartupBytes + 4GB) { throw 'Insufficient free host memory to preserve a 4 GiB reserve' }
    if ((Test-Path -LiteralPath $vhd) -and -not $ResumePreparedDisk) { throw 'VHD already exists; refusing to overwrite it' }
    if ($ResumePreparedDisk -and -not (Test-Path -LiteralPath $vhd)) { throw 'No prepared lab VHD exists' }
    if ((Get-Volume -DriveLetter ([IO.Path]::GetPathRoot($lab).Substring(0,1))).SizeRemaining -lt 45GB) { throw 'At least 45 GiB of free lab space required' }
    if (-not $InstallerPath) { $InstallerPath = Join-Path $repo 'artifacts\installer\DesktopSplitter-0.5.6-FreeLayout9-win-x64-Setup.exe' }
    $setup = (Resolve-Path -LiteralPath $InstallerPath -ErrorAction Stop).Path
    $smoke = Join-Path $repo 'artifacts\win10-lab-smoke\ui-smoke.exe'
    foreach ($file in @($setup,$smoke)) { if (-not (Test-Path -LiteralPath $file)) { throw "Missing lab input: $file" } }
    $iso = Get-DiskImage -ImagePath $IsoPath
    if (-not $iso.Attached) { $iso = Mount-DiskImage -ImagePath $IsoPath -StorageType ISO -Access ReadOnly -PassThru; $isoMounted = $true }
    $isoVolume = $iso | Get-Volume
    $wim = "$($isoVolume.DriveLetter):\sources\install.wim"
    $images = @(Get-WindowsImage -ImagePath $wim)
    $image = $images | Where-Object ImageName -Match 'Enterprise.*LTSC' | Select-Object -First 1
    if (-not $image) { throw 'No Enterprise LTSC image found' }
    Phase 'Creating isolated virtual disk'
    if (-not $ResumePreparedDisk) { New-VHD -Path $vhd -SizeBytes 64GB -Dynamic | Out-Null }
    $disk = Mount-VHD -Path $vhd -NoDriveLetter -PassThru | Get-Disk
    $mounted = $true
    $identity = Get-VHD -Path $vhd
    # Never select a disk by a guessed disk number or drive letter.
    if ([IO.Path]::GetFullPath($identity.Path) -ne $vhd -or -not $identity.Attached -or $identity.DiskNumber -ne $disk.Number -or $disk.IsBoot -or $disk.IsSystem -or $disk.Size -ne 64GB) { throw 'Lab VHD identity check failed' }
    if (-not $ResumePreparedDisk) {
    if ($disk.PartitionStyle -ne 'RAW') { throw 'New disk is not RAW' }
    $disk | Initialize-Disk -PartitionStyle GPT
    $efi = New-Partition -DiskNumber $disk.Number -Size 260MB -GptType '{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}' -AssignDriveLetter
    $efi | Format-Volume -FileSystem FAT32 -NewFileSystemLabel 'DSLabEFI' -Confirm:$false | Out-Null
    # Initialize-Disk already creates the MSR on this host.
    $os = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
    $os | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'DSLabWin10' -Confirm:$false | Out-Null
    $target = "$($os.DriveLetter):"
    Phase 'Applying Windows 10 image to lab VHD only'
    New-Item -ItemType Directory -Path (Join-Path $lab 'scratch') -Force | Out-Null
    & dism.exe /English /Apply-Image "/ImageFile:$wim" "/Index:$($image.ImageIndex)" "/ApplyDir:$target\" "/ScratchDir:$lab\scratch" /CheckIntegrity *> (Join-Path $lab 'dism-apply.log')
    if ($LASTEXITCODE) { throw "DISM apply failed: $LASTEXITCODE" }
    } else {
        Phase 'Resuming only the previously prepared lab disk; no format or image apply'
        if ($disk.PartitionStyle -ne 'GPT') { throw 'Prepared disk is not GPT' }
        $parts = @(Get-Partition -DiskNumber $disk.Number)
        $efi = $parts | Where-Object GptType -eq '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' | Select-Object -First 1
        $os = $parts | Where-Object { ($_ | Get-Volume -ErrorAction SilentlyContinue).FileSystemLabel -eq 'DSLabWin10' } | Select-Object -First 1
        if (-not $efi -or -not $os) { throw 'Prepared lab partition identities differ' }
        foreach ($partition in @($efi,$os)) { if (-not $partition.DriveLetter) { $partition | Add-PartitionAccessPath -AssignDriveLetter } }
        $efi = Get-Partition -DiskNumber $disk.Number -PartitionNumber $efi.PartitionNumber
        $os = Get-Partition -DiskNumber $disk.Number -PartitionNumber $os.PartitionNumber
        $target = "$($os.DriveLetter):"
        if (-not (Test-Path "$target\Windows\System32\bcdboot.exe")) { throw 'Prepared Windows image is missing' }
    }
    # /s is mandatory: explicitly targets VM EFI and prevents host NVRAM changes.
    $boot = Start-Process -FilePath "$target\Windows\System32\bcdboot.exe" -ArgumentList @("$target\Windows",'/s',"$($efi.DriveLetter):",'/f','UEFI','/c') -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $lab 'bcdboot.log') -RedirectStandardError (Join-Path $lab 'bcdboot-error.log')
    if ($boot.ExitCode) {
        # Some hosts fail BCDBoot's store creation with STATUS_OBJECT_NAME_COLLISION.
        # Build only the new VM's store from the signed image's template; every
        # BCDEdit call explicitly names that offline store, never the host store.
        Phase 'Preparing explicit offline VM boot store'
        $efiRoot = "$($efi.DriveLetter):"
        $bootRoot = "$efiRoot\EFI\Microsoft\Boot"
        New-Item -ItemType Directory -Path $bootRoot,"$efiRoot\EFI\Boot" -Force | Out-Null
        Copy-Item "$target\Windows\Boot\EFI\*" $bootRoot -Recurse -Force
        foreach ($folder in @('Fonts','Resources')) { Copy-Item "$target\Windows\Boot\$folder" $bootRoot -Recurse -Force }
        Copy-Item "$target\Windows\Boot\EFI\bootmgfw.efi" "$efiRoot\EFI\Boot\bootx64.efi" -Force
        $store = "$bootRoot\BCD"
        Copy-Item "$target\Windows\System32\Config\BCD-Template" $store -Force
        $operations = @(
            @('/set','{bootmgr}','device',"partition=$efiRoot"),
            @('/set','{default}','device','locate=\Windows\system32\winload.efi'),
            @('/set','{default}','osdevice','locate=\Windows'),
            @('/set','{default}','path','\Windows\system32\winload.efi'),
            @('/set','{default}','systemroot','\Windows'),
            @('/set','{default}','winpe','No'),
            @('/set','{default}','detecthal','Yes'),
            @('/set','{default}','description','Windows10-21H2-Lab'),
            @('/displayorder','{default}')
        )
        foreach ($operation in $operations) {
            $bcd = Start-Process "$env:SystemRoot\System32\bcdedit.exe" -ArgumentList (@('/store',$store) + $operation) -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $lab 'bcd-edit.log') -RedirectStandardError (Join-Path $lab 'bcd-edit-error.log')
            if ($bcd.ExitCode) { throw "Explicit VM boot store edit failed: $($operation -join ' ') ($($bcd.ExitCode))" }
        }
        # BCD-Template is not a system store. These flags are required by the
        # guest's Specialize_Bcd action. Set them after explicit BCDEdit edits,
        # using a private registry handle so host HKLM/BCD is never modified.
        Add-Type -Path (Join-Path $PSScriptRoot 'BcdStoreMetadata.cs')
        [LabBcdStoreMetadata]::MarkSystemStore($store)
    }
    $guest = "$target\DesktopSplitterLab"
    New-Item -ItemType Directory -Path "$guest\smoke","$guest\Results" -Force | Out-Null
    $labId = [guid]::NewGuid().ToString()
    $labId | Set-Content "$guest\lab-id.txt"
    Copy-Item -LiteralPath $setup -Destination "$guest\Setup.exe"
    Copy-Item "$repo\artifacts\win10-lab-smoke\*" -Destination "$guest\smoke" -Recurse
    Copy-Item -LiteralPath "$repo\tools\Test-DriverConnection.ps1" -Destination $guest
    Copy-Item -LiteralPath "$PSScriptRoot\Invoke-GuestChecks.ps1","$PSScriptRoot\DriverRoundTrip.cs","$PSScriptRoot\VmDisplayTopology.cs","$PSScriptRoot\Test-NineComposition.ps1" -Destination $guest
    $password = 'DsLab!' + [guid]::NewGuid().ToString('N')
    $guestCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\DesktopSplitterLab\Invoke-GuestChecks.ps1 -LabId $labId"
    $unattend = @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend">
 <settings pass="specialize">
  <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><ComputerName>DSWIN10LAB</ComputerName><TimeZone>UTC</TimeZone></component>
 </settings>
 <settings pass="oobeSystem">
  <component name="Microsoft-Windows-International-Core" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"><InputLocale>en-US</InputLocale><SystemLocale>en-US</SystemLocale><UILanguage>en-US</UILanguage><UserLocale>en-US</UserLocale></component>
  <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">
   <OOBE><HideEULAPage>true</HideEULAPage><HideOnlineAccountScreens>true</HideOnlineAccountScreens><HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE><ProtectYourPC>3</ProtectYourPC></OOBE>
   <UserAccounts><LocalAccounts><LocalAccount xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" wcm:action="add"><Name>LabAdmin</Name><Group>Administrators</Group><Password><Value>$password</Value><PlainText>true</PlainText></Password></LocalAccount></LocalAccounts></UserAccounts>
   <AutoLogon><Username>LabAdmin</Username><Enabled>true</Enabled><LogonCount>1</LogonCount><Password><Value>$password</Value><PlainText>true</PlainText></Password></AutoLogon>
   <FirstLogonCommands><SynchronousCommand xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" wcm:action="add"><Order>1</Order><CommandLine>$guestCommand</CommandLine><Description>Isolated DesktopSplitter compatibility checks</Description></SynchronousCommand></FirstLogonCommands>
  </component>
 </settings>
</unattend>
"@
    New-Item -ItemType Directory -Path "$target\Windows\Panther" -Force | Out-Null
    $unattend | Set-Content -LiteralPath "$target\Windows\Panther\unattend.xml" -Encoding UTF8
    [pscredential]::new('LabAdmin',(ConvertTo-SecureString $password -AsPlainText -Force)) | Export-Clixml -LiteralPath (Join-Path $lab 'guest-credential.clixml')
    $password = $null
    Dismount-VHD -Path $vhd
    $mounted = $false
    if($isoMounted) { Dismount-DiskImage -ImagePath $IsoPath | Out-Null; $isoMounted = $false }
    Phase 'Starting offline Windows 10 VM; waiting for guest checks'
    if((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory*1KB -lt $MemoryStartupBytes+4GB) { throw 'Insufficient free host memory to preserve a 4 GiB reserve' }
    $vm = New-VM -Name $VmName -Generation 2 -MemoryStartupBytes $MemoryStartupBytes -VHDPath $vhd -Path $lab
    $vm | Set-VM -AutomaticStartAction Nothing -AutomaticStopAction ShutDown -CheckpointType Disabled
    $vm | Set-VMProcessor -Count 2 -Maximum $ProcessorMaximum
    $vm | Set-VMMemory -DynamicMemoryEnabled $false
    $vm | Get-VMNetworkAdapter | Remove-VMNetworkAdapter
    $vm | Set-VMFirmware -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
    $vm | Set-VMVideo -ResolutionType Single -HorizontalResolution $VideoWidth -VerticalResolution $VideoHeight
    $vm | Start-VM
    $deadline = (Get-Date).AddMinutes($SetupTimeoutMinutes)
    while ((Get-VM -Id $vm.Id).State -ne 'Off') {
        if ((Get-Date) -gt $deadline) { throw 'Guest setup/test deadline reached; VM retained for investigation' }
        Start-Sleep -Seconds 3
    }
    Phase 'Collecting guest results from powered-off VM disk'
    $guestDisk = Mount-VHD -Path $vhd -ReadOnly -PassThru | Get-Disk
    $mounted = $true
    $guestPartition = $guestDisk | Get-Partition | Where-Object Type -eq 'Basic' | Select-Object -Last 1
    if (-not $guestPartition.DriveLetter) { $guestPartition | Add-PartitionAccessPath -AssignDriveLetter; $guestPartition = Get-Partition -DiskNumber $guestPartition.DiskNumber -PartitionNumber $guestPartition.PartitionNumber }
    $results = "$($guestPartition.DriveLetter):\DesktopSplitterLab\Results"
    Copy-Item -LiteralPath $results -Destination (Join-Path $lab 'Results') -Recurse
    Dismount-VHD -Path $vhd
    $mounted = $false
    $summaryPath = Join-Path $lab 'Results\summary.json'
    if (-not (Test-Path -LiteralPath $summaryPath)) { throw 'Guest stopped before producing a test report; inspect Windows setup logs' }
    $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
    if (-not $summary.Success) { throw ('Guest checks failed: ' + $summary.Error) }
    Phase 'Guest checks passed; inspect Results\summary.json'
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $lab 'host-error.txt')
    Phase ('Failed: ' + $_.Exception.Message)
    throw
} finally {
    if ($mounted) { Dismount-VHD -Path $vhd -ErrorAction Continue }
    if ($isoMounted) { Dismount-DiskImage -ImagePath $IsoPath -ErrorAction Continue | Out-Null }
    Stop-Transcript | Out-Null
}
