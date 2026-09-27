<#
.SYNOPSIS
  Regenerates every app-produced image in docs/ so the GitHub docs stay in sync with the build.
  Run this before cutting a release (see docs/DEVELOPING.md).

.DESCRIPTION
  Produces, from the current build:
    - docs/images/app-*.png        the app window screenshots  (via --docs)
    - docs/images/hero-*.png       hero card renders           (via --docs)
    - docs/QUICKSTART.md           illustrated quickstart      (via --docs)
    - docs/images/frames/*.png     one sample card per frame   (via --frames)
    - docs/images/gallery/*.png    the GALLERY.md tour, rendered from the committed card
                                   sources in docs/gallery-src/ (JSON + art)

  The gallery is fully reproducible: its card definitions live in docs/gallery-src/*.json and their
  art in docs/gallery-src/art/ (doc-only; NOT embedded in the exe).

.PARAMETER Exe
  Path to a built Cardinator.exe. Defaults to the Debug win-x64 build.
#>
param(
  [string]$Exe = "src/Cardinator/bin/Debug/net8.0-windows/win-x64/Cardinator.exe"
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not (Test-Path $Exe)) {
  Write-Host "Building Debug (no exe at $Exe)..." -ForegroundColor Yellow
  dotnet build src/Cardinator/Cardinator.csproj -c Debug -v quiet -nologo
}

function Run($argList) {
  $p = Start-Process $Exe -ArgumentList $argList -PassThru -NoNewWindow -Wait
  if ($p.ExitCode -ne 0) { throw "Cardinator $($argList -join ' ') exited $($p.ExitCode)" }
}

Write-Host "App screenshots + hero renders + QUICKSTART..." -ForegroundColor Cyan
Run @('--docs','docs')

Write-Host "Per-frame images..." -ForegroundColor Cyan
Run @('--frames','docs\images\frames')

Write-Host "Gallery (from docs/gallery-src)..." -ForegroundColor Cyan
$gOut = 'docs\images\gallery'
New-Item -ItemType Directory -Force $gOut | Out-Null
foreach ($jf in Get-ChildItem 'docs\gallery-src' -Filter *.json | Sort-Object Name) {
  $png = Join-Path $gOut ($jf.BaseName + '.png')
  Run @('--render', $jf.FullName, $png, 'scale=2')
  Write-Host "  $($jf.BaseName).png"
}

Write-Host "Done. Review 'git status docs' and commit." -ForegroundColor Green
