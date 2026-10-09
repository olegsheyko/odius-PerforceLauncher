# Собирает один PerforceLauncher.exe в dist\ (нужен установленный .NET 8 Desktop Runtime).
$ErrorActionPreference = 'Stop'
Get-Process PerforceLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish "$PSScriptRoot\src\Launcher.App\Launcher.App.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o "$PSScriptRoot\dist" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Get-Item "$PSScriptRoot\dist\PerforceLauncher.exe" | Select-Object FullName, Length, LastWriteTime
