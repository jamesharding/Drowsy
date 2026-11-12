# Clean local publish to artifacts/publish (self-contained, trimmed).
# Always starts from an empty folder so stale files can't mask trimming problems.
param([switch]$Aot)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = if ($Aot) { Join-Path $root 'artifacts/aot' } else { Join-Path $root 'artifacts/publish' }

$running = Get-Process Drowsy -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500 # let file handles close
}
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$dotnetArgs = @('publish', (Join-Path $root 'src/Drowsy'), '-c', 'Release', '-o', $out)
if ($Aot) { $dotnetArgs += '-p:PublishAot=true' }
dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$files = Get-ChildItem $out -Recurse -File
"{0} files, {1:N1} MB -> {2}" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), $out
