# Compila o QubitsCast e gera o instalador.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# Sai em instalador\saida\QubitsCast-instalador.exe

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $MyInvocation.MyCommand.Path

# O C: vive cheio; a compilacao vai para o D: quando ele existir.
$trabalho = if (Test-Path 'D:\') { 'D:\qcast-build' } else { Join-Path $env:TEMP 'qcast-build' }
$publicado = Join-Path $trabalho 'publicado'
$destinoApp = Join-Path $raiz 'instalador\app'

$dotnet = if (Test-Path 'D:\dotnet\dotnet.exe') { 'D:\dotnet\dotnet.exe' } else { 'dotnet' }
$iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'

function Passo($texto) { Write-Host "`n=== $texto" -ForegroundColor Cyan }

# --------------------------------------------------------------- espaco em disco
$livre = [math]::Round((Get-PSDrive C).Free / 1GB, 1)
Write-Host "espaco livre no C: $livre GB"
if ($livre -lt 1.5) {
    # Compilar sem espaco gera arquivo pela metade sem erro nenhum, e o instalador sai quebrado.
    throw "pouco espaco no C: ($livre GB). Libere espaco antes de gerar o instalador."
}

# --------------------------------------------------------------- ffmpeg
Passo "ffmpeg"
& powershell -ExecutionPolicy Bypass -File (Join-Path $raiz 'preparar-ffmpeg.ps1')
if ($LASTEXITCODE -ne 0) { throw "falhou ao preparar o ffmpeg" }

# --------------------------------------------------------------- icone
Passo "icone"
& node (Join-Path $raiz 'ativos\gerar-icone.mjs')

# --------------------------------------------------------------- app
Passo "compilando o aplicativo"
Get-Process QubitsCast -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Remove-Item $publicado -Recurse -Force -ErrorAction SilentlyContinue
$env:NUGET_PACKAGES = Join-Path $trabalho 'nuget'
$env:DOTNET_NOLOGO = '1'

& $dotnet publish (Join-Path $raiz 'app\QubitsCast.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
    -o $publicado
if ($LASTEXITCODE -ne 0) { throw "a compilacao falhou" }

$exePublicado = Join-Path $publicado 'QubitsCast.exe'
if (-not (Test-Path $exePublicado)) { throw "o executavel nao foi gerado" }

Passo "juntando os arquivos"
Remove-Item $destinoApp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $destinoApp | Out-Null
Copy-Item (Join-Path $publicado '*') $destinoApp -Recurse -Force

$n = (Get-ChildItem $destinoApp -Recurse -File).Count
$mb = [math]::Round(((Get-ChildItem $destinoApp -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "$n arquivos, $mb MB"

# --------------------------------------------------------------- instalador
if (-not (Test-Path $iscc)) {
    Write-Warning "Inno Setup nao encontrado em $iscc - parei antes do instalador."
    Write-Host "O aplicativo compilado esta em $destinoApp"
    exit 0
}

Passo "gerando o instalador"
& $iscc (Join-Path $raiz 'instalador\QubitsCast.iss')
if ($LASTEXITCODE -ne 0) { throw "o Inno Setup falhou" }

$saida = Join-Path $raiz 'instalador\saida\QubitsCast-instalador.exe'
if (-not (Test-Path $saida)) { throw "o instalador nao foi gerado" }

$mbInstalador = [math]::Round((Get-Item $saida).Length / 1MB, 1)
Write-Host "`nPRONTO: $saida ($mbInstalador MB)" -ForegroundColor Green
