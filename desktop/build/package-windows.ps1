# Builds dist\Myra-win-x64.zip on Windows: a self-contained build with libVLC and aria2c.exe.
# Needs the .NET 10 SDK. Run: powershell -ExecutionPolicy Bypass -File build\package-windows.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$Aria2Version = '1.37.0'
$Aria2Zip = "aria2-$Aria2Version-win-64bit-build1.zip"
$Aria2Sha256 = '67d015301eef0b612191212d564c5bb0a14b5b9c4796b76454276a4d28d9b288'
$Out = 'dist\Myra-win-x64'

New-Item -ItemType Directory -Force build\cache, dist | Out-Null
$cached = "build\cache\$Aria2Zip"
if (-not (Test-Path $cached)) {
    Invoke-WebRequest -Uri "https://github.com/aria2/aria2/releases/download/release-$Aria2Version/$Aria2Zip" -OutFile $cached
}
if ((Get-FileHash $cached -Algorithm SHA256).Hash.ToLower() -ne $Aria2Sha256) { throw "aria2 download checksum mismatch" }

Remove-Item -Recurse -Force $Out, "$Out.zip" -ErrorAction SilentlyContinue
dotnet publish src\Myra.App\Myra.App.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -o $Out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force "$Out\tools", "$Out\licenses" | Out-Null
$extract = Join-Path $env:TEMP "myra-aria2"
Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
Expand-Archive $cached -DestinationPath $extract
Copy-Item (Get-ChildItem $extract -Recurse -Filter aria2c.exe).FullName "$Out\tools\aria2c.exe"
Copy-Item (Get-ChildItem $extract -Recurse -Filter COPYING).FullName "$Out\licenses\aria2-COPYING.txt"
Copy-Item LICENSE-VLC.txt "$Out\licenses\VLC-LGPL-2.1.txt"
Copy-Item build\README-Windows.txt "$Out\README.txt"

Compress-Archive -Path $Out -DestinationPath "$Out.zip"
Get-Item "$Out\Myra.exe", "$Out\tools\aria2c.exe", "$Out\libvlc\win-x64\libvlc.dll", "$Out.zip" | Format-Table Name, Length
