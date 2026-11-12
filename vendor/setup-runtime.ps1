# Setup embedded VLM runtime for dsh-screen-attention.
# Unzips vendor/ollama/ollama.exe and pulls the vision model into vendor/models.
# Run once after cloning. Pure ASCII on purpose (cmd/PowerShell encoding safety).
$ErrorActionPreference = 'Stop'
# script lives in vendor/ itself, so its own dir IS the vendor dir
$vendor = Split-Path -Parent $MyInvocation.MyCommand.Path
$ollamaDir = Join-Path $vendor 'ollama'
$modelsDir = Join-Path $vendor 'models'
New-Item -ItemType Directory -Force -Path $ollamaDir, $modelsDir | Out-Null

$zip = Join-Path $env:TEMP 'ollama-win.zip'
if (-not (Test-Path (Join-Path $ollamaDir 'ollama.exe'))) {
  if (-not (Test-Path $zip)) { throw "missing $zip - download ollama-windows-amd64.zip first" }
  Write-Host 'Unzipping ollama ...'
  Expand-Archive -Path $zip -DestinationPath $ollamaDir -Force
}
$exe = Join-Path $ollamaDir 'ollama.exe'
if (-not (Test-Path $exe)) { throw 'ollama.exe not found after unzip' }
Write-Host "ollama: $exe"

$env:OLLAMA_MODELS = $modelsDir
$env:OLLAMA_HOST = '127.0.0.1:11434'
$alive = $false
try { Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 2 | Out-Null; $alive = $true } catch {}
if (-not $alive) {
  Write-Host 'Starting ollama serve ...'
  $q = 'set OLLAMA_MODELS=' + $modelsDir + ' && start "" /B "' + $exe + '" serve'
  Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = 'cmd /c ' + $q } | Out-Null
  for ($i = 0; $i -lt 20; $i++) {
    Start-Sleep -Milliseconds 700
    try { Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 2 | Out-Null; $alive = $true; break } catch {}
  }
}
if (-not $alive) { throw 'ollama did not become ready' }

$model = 'qwen2.5vl:3b'
Write-Host "Pulling model $model into $modelsDir ..."
& $exe pull $model
Write-Host 'Done.'
Write-Host "models: $modelsDir"