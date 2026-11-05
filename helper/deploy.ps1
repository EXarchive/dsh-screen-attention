# Deploy SaHelper: full sync from build output to run dir.
# Lesson: whitelist copying misses dlls -- System.Speech root copy is a 310KB facade,
# the real implementation lives in runtimes\win\lib\net9.0\ (685KB), same name different thing.
# Also: a single Stop-Process is not enough; retry kill inside the copy loop.
param([string]$Src, [string]$Dst)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot   # helper/ itself (script sits next to the csproj)
if (-not $Src) { $Src = Join-Path $root 'bin\Release\next' }
if (-not $Dst) { $Dst = Join-Path $root 'bin\Release\net9.0-windows10.0.19041.0' }
if (-not (Test-Path $Src)) { throw "src not found: $Src" }

function Stop-Helper {
    Get-Process SaHelper -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 40; $i++) {
        if (-not (Get-Process SaHelper -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

$ok = $false
for ($i = 0; $i -lt 20; $i++) {
    if (-not (Stop-Helper)) { Start-Sleep -Milliseconds 200; continue }
    try {
        Copy-Item (Join-Path $Src '*') $Dst -Recurse -Force -ErrorAction Stop
        $ok = $true; break
    } catch {
        Start-Sleep -Milliseconds 250
    }
}
if (-not $ok) { throw "deploy failed: files locked (SaHelper still alive?)" }

# runtimes impl dll -> root (probe order prefers app dir)
$rt = Join-Path $Src 'runtimes\win\lib\net9.0'
if (Test-Path $rt) {
    Get-ChildItem $rt -Filter *.dll | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $Dst $_.Name) -Force
    }
}

# verify (skip System.Speech -- root is intentionally replaced by the runtimes impl build)
$bad = @()
Get-ChildItem $Src -Filter *.dll | Where-Object { $_.Name -ne 'System.Speech.dll' } | ForEach-Object {
    $t = Join-Path $Dst $_.Name
    if (-not (Test-Path $t)) { $bad += "$($_.Name) missing" }
    elseif ((Get-FileHash $_.FullName -Algorithm MD5).Hash -ne (Get-FileHash $t -Algorithm MD5).Hash) { $bad += "$($_.Name) mismatch" }
}
if (-not (Test-Path (Join-Path $Dst 'System.Speech.dll'))) { $bad += 'System.Speech.dll missing' }
if ($bad.Count -gt 0) { throw ("deploy verify failed: " + ($bad -join ', ')) }
"deploy OK -> $Dst  ($((Get-ChildItem $Src -Filter *.dll).Count) dll verified)"
