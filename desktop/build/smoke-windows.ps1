# Launches the packaged Myra.exe on a Windows desktop and checks that it works:
# the main window opens, a video given on the command line opens the player window,
# screenshots are saved, and closing the app leaves no aria2c.exe behind.
param(
    [string]$App = 'dist\Myra-win-x64\Myra.exe',
    [string]$Video = 'http://127.0.0.1:8765/Tracks/Long%20Clip%202020.mp4',
    [string]$Output = 'artifacts\smoke'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
New-Item -ItemType Directory -Force $Output | Out-Null
$env:MYRA_DATA_DIR = Join-Path $env:TEMP "myra-smoke-$([guid]::NewGuid().ToString('N'))"

function Save-Screen([string]$name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save((Join-Path $Output $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}

function Get-Windows([int]$processId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition) | ForEach-Object { $_.Current.Name }
}

$failures = @()
$process = Start-Process -FilePath $App -ArgumentList "`"$Video`"" -PassThru
$deadline = (Get-Date).AddSeconds(40)
do {
    Start-Sleep -Seconds 1
    $titles = @(Get-Windows $process.Id)
} until ($titles.Count -ge 2 -or $process.HasExited -or (Get-Date) -gt $deadline)

Write-Host "Windows: $($titles -join ' | ')"
if ($process.HasExited) { $failures += "Myra.exe exited early with code $($process.ExitCode)" }
if ($titles -notcontains 'Myra') { $failures += 'main window did not open' }
if (-not ($titles | Where-Object { $_ -like 'Long Clip*' })) { $failures += 'player window did not open' }

Start-Sleep -Seconds 6
Save-Screen 'windows-desktop.png'

$process.CloseMainWindow() | Out-Null
if (-not $process.WaitForExit(20000)) { $failures += 'Myra.exe did not exit after closing'; $process.Kill() }
Start-Sleep -Seconds 2
if (Get-Process aria2c -ErrorAction SilentlyContinue) { $failures += 'aria2c.exe is still running' }

if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Host "FAIL $_" }; exit 1 }
Write-Host 'SMOKE TEST PASSED'
