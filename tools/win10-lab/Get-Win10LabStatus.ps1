# Read-only Hyper-V/guest status. Run elevated; no VM console or host UI action.
[CmdletBinding()]
param([string]$LabRoot='F:\DesktopSplitter-Win10Lab',[string]$VmName='DesktopSplitter-Win10-21H2-Lab')
$ErrorActionPreference='Stop'
$vm=Get-VM -Name $VmName
$report=[ordered]@{Name=$vm.Name;Id=$vm.Id;State=$vm.State.ToString();Status=$vm.Status;Uptime=$vm.Uptime.ToString();CPUUsage=$vm.CPUUsage;MemoryAssigned=$vm.MemoryAssigned;Guest=$null;GuestError=$null}
$report.SecureBoot=($vm | Get-VMFirmware).SecureBoot.ToString()
$report.NetworkAdapterCount=@($vm | Get-VMNetworkAdapter).Count
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $LabRoot 'vm-status.json')
if($vm.State -eq 'Running') {
    try {
        $credential=Import-Clixml -LiteralPath (Join-Path $LabRoot 'guest-credential.clixml')
        $report.Guest=Invoke-Command -VMId $vm.Id -Credential $credential -ScriptBlock {
            [pscustomobject]@{
                ComputerName=$env:COMPUTERNAME
                Build=(Get-CimInstance Win32_OperatingSystem).BuildNumber
                TestLog=[string]::Join("`n",[string[]](Get-Content 'C:\DesktopSplitterLab\Results\guest.log' -Tail 12 -ErrorAction SilentlyContinue))
                Summary=if(Test-Path 'C:\DesktopSplitterLab\Results\summary.json'){[IO.File]::ReadAllText('C:\DesktopSplitterLab\Results\summary.json')}else{$null}
                Results=@(Get-ChildItem 'C:\DesktopSplitterLab\Results' -File | Select-Object Name,Length,LastWriteTimeUtc)
            }
        } -ErrorAction Stop
    } catch { $report.GuestError=$_.Exception.Message }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $LabRoot 'vm-status.json')
}
$report | ConvertTo-Json -Depth 6
