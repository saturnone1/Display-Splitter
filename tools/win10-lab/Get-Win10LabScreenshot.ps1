# Reads only the VM framebuffer; does not open a host window or send any input.
[CmdletBinding()]
param([string]$LabRoot='F:\DesktopSplitter-Win10Lab',[string]$VmName='DesktopSplitter-Win10-21H2-Lab')
$ErrorActionPreference='Stop'
try {
    $vm=Get-WmiObject -Namespace root/virtualization/v2 -Class Msvm_ComputerSystem -Filter "ElementName='$VmName'"
    if(-not $vm){throw 'VM not found'}
    $settings=$vm.GetRelated('Msvm_VirtualSystemSettingData','Msvm_SettingsDefineState',$null,$null,'SettingData','ManagedElement',$false,$null) | Select-Object -First 1
    $service=Get-WmiObject -Namespace root/virtualization/v2 -Class Msvm_VirtualSystemManagementService
    $parameters=$service.GetMethodParameters('GetVirtualSystemThumbnailImage')
    $parameters.TargetSystem=$settings.Path.Path
    $parameters.WidthPixels=[uint16]320
    $parameters.HeightPixels=[uint16]200
    Get-VM -Name $VmName | Select-Object Name,State,Uptime | ConvertTo-Json | Set-Content (Join-Path $LabRoot 'screenshot-vm-state.json')
    $settings | Select-Object InstanceID,VirtualSystemType | ConvertTo-Json | Set-Content (Join-Path $LabRoot 'screenshot-setting.json')
    $image=$service.InvokeMethod('GetVirtualSystemThumbnailImage',$parameters,$null)
    if($image.ReturnValue -ne 0){throw "Thumbnail error: $($image.ReturnValue)"}
    Add-Type -AssemblyName System.Drawing
    $bitmap=New-Object Drawing.Bitmap(320,200,[Drawing.Imaging.PixelFormat]::Format16bppRgb565)
    try {
        $bits=$bitmap.LockBits([Drawing.Rectangle]::new(0,0,320,200),[Drawing.Imaging.ImageLockMode]::WriteOnly,[Drawing.Imaging.PixelFormat]::Format16bppRgb565)
        try{[Runtime.InteropServices.Marshal]::Copy([byte[]]$image.ImageData,0,$bits.Scan0,$image.ImageData.Length)}finally{$bitmap.UnlockBits($bits)}
        $bitmap.Save((Join-Path $LabRoot 'vm-screen.png'),[Drawing.Imaging.ImageFormat]::Png)
    }finally{$bitmap.Dispose()}
}catch{$_ | Out-String | Set-Content -LiteralPath (Join-Path $LabRoot 'screenshot-error.txt');throw}
