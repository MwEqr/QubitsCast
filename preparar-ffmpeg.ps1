# Baixa o ffmpeg que o QubitsCast usa e deixa em instalador\ffmpeg\.
# Nao e versionado por causa do tamanho (mais de 100 MB).
#
#   powershell -ExecutionPolicy Bypass -File preparar-ffmpeg.ps1

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $MyInvocation.MyCommand.Path
$destino = Join-Path $raiz 'instalador\ffmpeg'
$exe = Join-Path $destino 'ffmpeg.exe'

# Versao 8.1 de proposito, nao a mais nova: a 9.x exige driver NVIDIA 610 ou mais novo
# e recusa o NVENC em placas com driver anterior, caindo para o processador sem avisar.
$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-gpl-8.1.zip'

if (Test-Path $exe) {
    $tamanho = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "ffmpeg ja esta em $exe ($tamanho MB)"
    exit 0
}

New-Item -ItemType Directory -Force $destino | Out-Null
$temporario = Join-Path $env:TEMP "qcast-ffmpeg-$PID"
New-Item -ItemType Directory -Force $temporario | Out-Null
$zip = Join-Path $temporario 'ffmpeg.zip'

try {
    Write-Host "Baixando o ffmpeg (cerca de 160 MB)..."
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -UseBasicParsing $url -OutFile $zip

    Write-Host "Extraindo..."
    Expand-Archive -Path $zip -DestinationPath $temporario -Force

    $achado = Get-ChildItem -Path $temporario -Filter 'ffmpeg.exe' -Recurse | Select-Object -First 1
    if (-not $achado) { throw "nao achei ffmpeg.exe dentro do pacote" }

    Copy-Item $achado.FullName $exe -Force
    $tamanho = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "pronto: $exe ($tamanho MB)"
}
finally {
    Remove-Item $temporario -Recurse -Force -ErrorAction SilentlyContinue
}
