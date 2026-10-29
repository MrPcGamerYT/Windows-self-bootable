param(
    [string]$Configuration = "Release",
    [ValidateSet("win-x64", "win-arm64", "win-x86")]
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [string]$Version = "1.0.1",
    [string]$OutputExe = ""
)

$ErrorActionPreference = "Stop"
$asmVer = "$Version.0"
$scFlag = if ($SelfContained) { "true" } else { "false" }
$flavorSuffix = if (-not $SelfContained) { "lightweight" } elseif ($Runtime -eq "win-x86") { "32bit" } else { "standalone" }

if ([string]::IsNullOrWhiteSpace($OutputExe)) {
    $OutputExe = "publish/SWBI-SelfWindowsBootableInstaller-v$Version-$Runtime-$flavorSuffix.exe"
}

Write-Host "==> [1/2] Publishing unbundled SWBI WPF runtime ($Runtime, SelfContained=$scFlag)..." -ForegroundColor Cyan
$appStageDir = Join-Path $PWD "artifacts/stage-app-$Runtime-$flavorSuffix"
if (Test-Path $appStageDir) { Remove-Item -Recurse -Force $appStageDir }

dotnet publish src/UsbFreeInstaller.App/UsbFreeInstaller.App.csproj `
  --configuration $Configuration `
  --runtime $Runtime `
  --self-contained $scFlag `
  -p:PublishSingleFile=false `
  -p:DebugType=embedded `
  -p:Version=$Version `
  -p:AssemblyVersion=$asmVer `
  -p:FileVersion=$asmVer `
  --output $appStageDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish UsbFreeInstaller.App failed ($LASTEXITCODE)" }

$targetArch = switch ($Runtime) {
    "win-x64"   { "x64" }
    "win-arm64" { "arm64" }
    "win-x86"   { "x86" }
}
$flavorTag = switch ("$Runtime-$flavorSuffix") {
    "win-x64-standalone"  { "x64s" }
    "win-x64-lightweight" { "x64l" }
    "win-arm64-standalone"{ "arm64" }
    "win-x86-32bit"       { "x86s" }
    default               { $targetArch }
}

Write-Host "==> [2/2] Building %TEMP%-immune single-file native Win32 EXE ($Runtime -> $OutputExe)..." -ForegroundColor Cyan
& "$PSScriptRoot/scripts/Build-SwbiNativeExe.ps1" `
  -AppFolder $appStageDir `
  -TargetArch $targetArch `
  -FlavorTag $flavorTag `
  -Version $Version `
  -OutputExe $OutputExe
