# Creates only owned test windows in the guarded VM. Never use on the host.
param([Parameter(Mandatory=$true)]$Grid,[int]$TimeoutSeconds=120)
$ErrorActionPreference='Stop'
$root='C:\DesktopSplitterLab'
if($env:COMPUTERNAME-ne'DSWIN10LAB' -or -not(Test-Path "$root\lab-id.txt") -or (Get-CimInstance Win32_ComputerSystem).Model-ne'Virtual Machine'){throw 'VM-only composition test'}
if(Get-Process dscomp -ErrorAction SilentlyContinue){throw 'Unexpected existing compositor in test VM'}
$Grid | ConvertTo-Json -Depth 6 | Set-Content "$root\Results\nine-active-topology.json"
$segments=@(foreach($slot in $Grid.Slots){@{virtualDevice=$slot.Device;width=640;height=360;physRect=@{x=(($slot.Slot-1)%3)*640;y=[int][math]::Floor(($slot.Slot-1)/3)*360;w=640;h=360}}})
@{physicalDevice=$Grid.Source;refreshMillihertz=60000;segments=$segments}|ConvertTo-Json -Depth 6|Set-Content "$root\Results\nine-compositor-config.json" -Encoding UTF8
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
$colors=@('#D04040','#40D040','#4040D0','#D0D040','#40D0D0','#D040D0','#D08040','#80D040','#4080D0')
$forms=@();$process=$null;$ready=$null;$stop=$null;$bitmap=$null;$graphics=$null;$stdout=$null;$stderr=$null
$result=[ordered]@{Success=$false;Ready=$false;MatchingColors=0;Error=$null;ExitCode=$null}
try {
    foreach($slot in $Grid.Slots){
        $form=New-Object Windows.Forms.Form
        $form.FormBorderStyle='None';$form.StartPosition='Manual';$form.ShowInTaskbar=$false;$form.TopMost=$true
        $form.Location=New-Object Drawing.Point($slot.X,$slot.Y)
        $form.ClientSize=New-Object Drawing.Size(640,360)
        $form.BackColor=[Drawing.ColorTranslator]::FromHtml($colors[$slot.Slot-1])
        $forms+=$form;$form.Show()
    }
    [Windows.Forms.Application]::DoEvents()
    $stop=New-Object Threading.EventWaitHandle($false,[Threading.EventResetMode]::ManualReset,'Global\DeskSplitCompositorStop')
    $stop.Reset()|Out-Null
    $process=New-Object Diagnostics.Process
    $process.StartInfo.FileName="$root\Product\dscomp.exe"
    $process.StartInfo.Arguments="--config $root\Results\nine-compositor-config.json --verbose --no-confine --no-rescue"
    $process.StartInfo.UseShellExecute=$false;$process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardOutput=$true;$process.StartInfo.RedirectStandardError=$true
    $process.Start()|Out-Null
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    $ready=New-Object Threading.EventWaitHandle($false,[Threading.EventResetMode]::ManualReset,"Local\DeskSplitCompositorReady.$($process.Id)")
    $deadline=(Get-Date).AddSeconds($TimeoutSeconds)
    $bitmap=New-Object Drawing.Bitmap(1920,1080)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    do {
        [Windows.Forms.Application]::DoEvents()
        if($process.HasExited){throw "Nine-source compositor exited: $($process.ExitCode)"}
        if($ready.WaitOne(0)){$result.Ready=$true}
        if($result.Ready){
            $graphics.CopyFromScreen(0,0,0,0,$bitmap.Size)
            $matching=0
            for($i=0;$i-lt9;$i++){
                $actual=$bitmap.GetPixel(($i%3)*640+320,[int][math]::Floor($i/3)*360+180)
                $expected=[Drawing.ColorTranslator]::FromHtml($colors[$i])
                if([math]::Abs([int]$actual.R-$expected.R)-le16 -and [math]::Abs([int]$actual.G-$expected.G)-le16 -and [math]::Abs([int]$actual.B-$expected.B)-le16){$matching++}
            }
            $result.MatchingColors=$matching
            if($matching-eq9){$bitmap.Save("$root\Results\nine-composited.png");$result.Success=$true;break}
        }
        if((Get-Date)-ge$deadline){throw "Nine-color presentation deadline; matching=$($result.MatchingColors)"}
        Start-Sleep -Milliseconds 100
    }while($true)
}catch{$result.Error=$_.Exception.Message}
finally{
    if($process){
        if(-not$process.HasExited){if($stop){$stop.Set()|Out-Null};if(-not$process.WaitForExit($TimeoutSeconds*1000)){$process.Kill()}}
        $process.WaitForExit();$result.ExitCode=$process.ExitCode
        if($stdout){$stdout.GetAwaiter().GetResult()|Set-Content "$root\Results\nine-compositor.stdout.txt"}
        if($stderr){$stderr.GetAwaiter().GetResult()|Set-Content "$root\Results\nine-compositor.stderr.txt"}
        if($result.Success -and $process.ExitCode-ne0){$result.Success=$false;$result.Error="Compositor shutdown exit $($process.ExitCode)"}
        $process.Dispose()
    }
    foreach($form in $forms){$form.Dispose()}
    if($graphics){$graphics.Dispose()};if($bitmap){$bitmap.Dispose()};if($ready){$ready.Dispose()};if($stop){$stop.Dispose()}
    $result|ConvertTo-Json|Set-Content "$root\Results\nine-composition-result.json"
}
if(-not$result.Success){throw $result.Error}
