param(
    [Parameter(Mandatory=$true)][string]$StandaloneExe,
    [Parameter(Mandatory=$true)][string]$AppHost
)
$ErrorActionPreference = 'Stop'
# Only isolated copies are replaced. This tests the helper, native launcher identity,
# retained backup, atomic file replacement and restarted GUI on an actual Windows runner.
$fixture = Join-Path $PWD 'out/update-smoke'
if (Test-Path $fixture) { Remove-Item -Recurse -Force $fixture }
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$fixture = (Resolve-Path $fixture).Path
$target = Join-Path $fixture 'SWBI-fixture.exe'
$stage = Join-Path $fixture '.swbi-update-smoke.exe'
$backup = Join-Path $fixture '.swbi-backup-smoke.exe'
$report = Join-Path $fixture 'restarted.json'
Copy-Item -LiteralPath $StandaloneExe -Destination $target
Copy-Item -LiteralPath $StandaloneExe -Destination $stage
# Change only the legacy DOS last-page byte; the PE and embedded SWBI payload remain valid.
$bytes = [IO.File]::ReadAllBytes($stage)
$bytes[2] = $bytes[2] -bxor 1
[IO.File]::WriteAllBytes($stage, $bytes)
$oldHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
$newHash = (Get-FileHash -LiteralPath $stage -Algorithm SHA256).Hash
if ($oldHash -eq $newHash) { throw 'Update smoke fixture hashes did not differ.' }
$quote = { param($s) '"' + $s.Replace('"', '\"') + '"' }
$helperArgs = @('--swbi-apply-update', '2147483647', '1', (& $quote $target), (& $quote $stage),
    (& $quote $backup), $oldHash, $newHash, 'X64', '--smoke-test', '--smoke-report', (& $quote $report))
$proc = Start-Process -FilePath (Resolve-Path $AppHost).Path -ArgumentList $helperArgs -PassThru
try {
    if (-not $proc.WaitForExit(90000)) { throw 'Updater helper timed out.' }
    if ($proc.ExitCode -ne 0) { throw "Updater helper failed with exit code $($proc.ExitCode)." }
    for ($i = 0; $i -lt 90 -and -not (Test-Path $report); $i++) { Start-Sleep -Seconds 1 }
    if (-not (Test-Path $report)) { throw 'The updated standalone EXE did not produce a restarted smoke report.' }
    $json = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $json.Passed -or $json.BindingErrorCount -ne 0 -or $json.RuntimeErrorCount -ne 0) {
        throw "The updated EXE failed its GUI smoke test: $($json | ConvertTo-Json -Compress)"
    }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $newHash -or
        (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $oldHash -or (Test-Path $stage)) {
        throw 'Update replacement or retained backup was incorrect.'
    }
    Write-Host 'Standalone update handoff, replacement, backup and restart verified.'
} finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
