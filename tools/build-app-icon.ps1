# Generate one multi-resolution ICO from the original tray's split-monitor design.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $PSScriptRoot '..\app\Assets\App.ico'
$frames = @()
foreach ($size in @(16, 20, 24, 32, 48, 64, 128, 256)) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.ScaleTransform(($size / 32.0), ($size / 32.0))
    $framePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(230,235,245), 2)
    $leftBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(76,141,255))
    $rightBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(120,200,160))
    $graphics.FillRectangle($leftBrush, 4, 7, 11, 18)
    $graphics.FillRectangle($rightBrush, 17, 7, 11, 18)
    $graphics.DrawRectangle($framePen, 3, 6, 26, 20)
    $buffer = [System.IO.MemoryStream]::new()
    $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += @{ Size = $size; Bytes = $buffer.ToArray() }
    $buffer.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    $framePen.Dispose(); $leftBrush.Dispose(); $rightBrush.Dispose()
}
$stream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Output "Created $iconPath"
