# Launches the packaged Myra.exe on a Windows desktop and checks that it works:
# the main window opens, a video given on the command line plays embedded in the player window,
# fullscreen and an options menu work, screenshots are saved, and closing the main window
# ends the app cleanly and leaves no aria2c.exe from this build behind.
param(
    [string]$App = 'dist\Myra-win-x64\Myra.exe',
    [string]$Video = 'http://127.0.0.1:8765/Tracks/Long%20Clip%202020.mp4',
    [string]$Output = 'artifacts\smoke'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
New-Item -ItemType Directory -Force $Output | Out-Null
$App = (Resolve-Path $App).Path
$appDir = Split-Path $App -Parent
# An isolated data folder keeps the user's library, settings and player state out of the test.
$env:MYRA_DATA_DIR = Join-Path $env:TEMP "myra-smoke-$([guid]::NewGuid().ToString('N'))"
Write-Host "Data folder: $env:MYRA_DATA_DIR"

Add-Type -Namespace MyraSmoke -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
'@

$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

function Save-Screen([string]$name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save((Join-Path $Output $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
    Write-Host "saved $name"
}

# Top-level windows of the process (the player overlay is a top-level window too).
function Get-TopWindows([int]$processId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $processId)
    @($AE::RootElement.FindAll($Scope::Children, $condition))
}

function Find-Window([int]$processId, [scriptblock]$match) {
    Get-TopWindows $processId | Where-Object { & $match $_.Current.Name } | Select-Object -First 1
}

# Finds a control by its accessible name in any window of the process.
function Find-Control([int]$processId, [string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    foreach ($window in Get-TopWindows $processId) {
        $found = $window.FindFirst($Scope::Descendants, $condition)
        if ($found) { return $found }
    }
    $null
}

function Get-OwnAria2 {
    @(Get-Process aria2c -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase) })
}

$failures = @()
$process = Start-Process -FilePath $App -ArgumentList "`"$Video`"" -PassThru
$deadline = (Get-Date).AddSeconds(40)
do {
    Start-Sleep -Seconds 1
    $titles = @(Get-TopWindows $process.Id | ForEach-Object { $_.Current.Name })
} until ((($titles -contains 'Myra') -and ($titles | Where-Object { $_ -like 'Long Clip*' })) -or $process.HasExited -or (Get-Date) -gt $deadline)

Write-Host "Windows: $($titles -join ' | ')"
if ($process.HasExited) { $failures += "Myra.exe exited early with code $($process.ExitCode)" }
if ($titles -notcontains 'Myra') { $failures += 'main window did not open' }
if (-not ($titles | Where-Object { $_ -like 'Long Clip*' })) { $failures += 'player window did not open' }

if (-not $process.HasExited) {
    Start-Sleep -Seconds 6
    Save-Screen 'windows-desktop.png'
    $titles = @(Get-TopWindows $process.Id | ForEach-Object { $_.Current.Name })
    # libVLC opens its own "VLC (Direct3D11 output)" window when it has no surface to draw into.
    if ($titles | Where-Object { $_ -like 'VLC*' }) { $failures += 'video opened in a separate VLC window' }
    # The controls must show live values: elapsed and total time, and no "cannot seek" notice for this source.
    $texts = @(Get-TopWindows $process.Id | ForEach-Object {
        $_.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
            $AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) } | ForEach-Object { $_.Current.Name })
    $times = @($texts | Where-Object { $_ -match '^\d+:\d\d$' })
    Write-Host "Player times: $($times -join ' / ')"
    if (-not ($times | Where-Object { $_ -ne '0:00' })) { $failures += 'player controls do not show the playback time' }
    if ($texts -contains 'This source does not support seeking.') { $failures += 'player reports a seekable source as not seekable' }

    # Fullscreen: F on the player window, then the controls overlay with live values.
    $player = Find-Window $process.Id { param($n) $n -like 'Long Clip*' }
    if ($player) {
        [MyraSmoke.Native]::SetForegroundWindow([IntPtr]$player.Current.NativeWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 500
        [System.Windows.Forms.SendKeys]::SendWait('f')
        Start-Sleep -Seconds 1
        Save-Screen 'windows-fullscreen.png'
        $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $rect = $player.Current.BoundingRectangle
        if ($rect.Width -lt $screen.Width -or $rect.Height -lt $screen.Height) { $failures += "F did not make the player fullscreen ($rect)" }

        # An options menu opens over the video.
        $audio = Find-Control $process.Id 'Audio'
        if ($audio) {
            $audio.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Milliseconds 800
            Save-Screen 'windows-menu.png'
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
            Start-Sleep -Milliseconds 300
        } else { $failures += 'Audio menu button not found' }
        [System.Windows.Forms.SendKeys]::SendWait('f')
        Start-Sleep -Milliseconds 800
    } else { $failures += 'player window not found for fullscreen' }

    # Close the main 'Myra' window: Process.CloseMainWindow may target the player window instead.
    $main = Find-Window $process.Id { param($n) $n -eq 'Myra' }
    if ($main) {
        $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        if (-not $process.WaitForExit(30000)) { $failures += 'Myra.exe did not exit after closing the main window'; $process.Kill() }
    } else {
        $failures += 'main window not found for closing'; $process.Kill()
    }
}
Start-Sleep -Seconds 2
$leftover = Get-OwnAria2
if ($leftover.Count -gt 0) { $failures += 'aria2c.exe is still running'; $leftover | Stop-Process -Force }

if ($failures.Count -gt 0) { $failures | ForEach-Object { Write-Host "FAIL $_" }; exit 1 }
Write-Host 'SMOKE TEST PASSED'
