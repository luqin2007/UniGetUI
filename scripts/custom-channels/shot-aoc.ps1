Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# 0 = 主屏，1 = 副屏（AOC）
$screens = [System.Windows.Forms.Screen]::AllScreens
$screens | ForEach-Object { Write-Output "monitor: $($_.Bounds)" }
if ($screens.Count -lt 2) { Write-Output "only one monitor"; exit 1 }
$target = $screens[1].Bounds
$w = $target.Width
$h = $target.Height

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($target.X, $target.Y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()
$bmp.Save('C:\Dev\projects\UniGetUI\cc-aoc.png', [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved"
