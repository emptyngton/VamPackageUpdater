$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $MyInvocation.MyCommand.Path)

$project = "VamPackageUpdater/VamPackageUpdater.csproj"
$output  = "publish"

if (Test-Path $output) { Remove-Item $output -Recurse -Force }

Write-Host "Publishing single-file self-contained win-x64 build..." -ForegroundColor Cyan
dotnet publish $project -c Release -r win-x64 -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Get-ChildItem $output -Filter "VamPackageUpdater.exe" | Select-Object -First 1
if (-not $exe) { throw "Expected VamPackageUpdater.exe not found in $output." }

$sizeMb = [math]::Round($exe.Length / 1MB, 1)
Write-Host ""
Write-Host "Done. Output:" -ForegroundColor Green
Write-Host ("  {0}  ({1} MB)" -f $exe.FullName, $sizeMb)
