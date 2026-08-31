# Publica uma versão nova do QubitsCast.
#
# Faz tudo de uma vez: sobe o número da versão, compila, gera o instalador, calcula o
# resumo, manda para o servidor e avisa os aplicativos instalados de que saiu versão nova.
# Quem estiver com o app aberto vê o botão "Atualizar" aparecer sozinho.
#
#   powershell -ExecutionPolicy Bypass -File publicar.ps1 -Notas "o que mudou"
#   powershell -ExecutionPolicy Bypass -File publicar.ps1 -Versao 1.2.0 -Notas "..."
#
# Sem -Versao, o último número sobe em um (1.0.3 vira 1.0.4).

param(
    [string]$Versao = '',
    [string]$Notas = '',
    [string]$Servidor = 'vps-new',
    [string]$PastaRemota = '/www/wwwroot/cast.qubitslab.com.br/servidor/publico',
    [switch]$SemCommit
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path $raiz 'app\QubitsCast.csproj'
$instalador = Join-Path $raiz 'instalador\saida\QubitsCast-instalador.exe'

function Passo($t) { Write-Host "`n=== $t" -ForegroundColor Cyan }

# --------------------------------------------------------------- versão
Passo 'versao'
[xml]$xml = Get-Content $csproj
$no = $xml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
$atual = [string]$no.Version
Write-Host "versao atual: $atual"

if (-not $Versao) {
    $p = $atual.Split('.')
    if ($p.Count -lt 3) { throw "versao '$atual' fora do formato x.y.z" }
    $Versao = "$($p[0]).$($p[1]).$([int]$p[2] + 1)"
}
if ($Versao -notmatch '^\d+\.\d+\.\d+$') { throw "versao '$Versao' fora do formato x.y.z" }
if ([version]$Versao -le [version]$atual) {
    throw "a versao nova ($Versao) precisa ser maior que a atual ($atual)"
}

Write-Host "versao nova:  $Versao"

$no.Version = $Versao
($xml.Project.PropertyGroup | Where-Object { $_.FileVersion } | Select-Object -First 1).FileVersion = "$Versao.0"
($xml.Project.PropertyGroup | Where-Object { $_.AssemblyVersion } | Select-Object -First 1).AssemblyVersion = "$Versao.0"
$xml.Save($csproj)

# O instalador carrega o mesmo numero, senao o Windows mostra versao velha em
# "Aplicativos instalados" mesmo depois de atualizar.
$iss = Join-Path $raiz 'instalador\QubitsCast.iss'
$texto = Get-Content $iss -Raw
$texto = [regex]::Replace($texto, '(#define MeuVersao ")[^"]+(")', "`${1}$Versao`${2}")
Set-Content $iss $texto -Encoding UTF8 -NoNewline

# --------------------------------------------------------------- compilar
Passo 'compilando'
& powershell -ExecutionPolicy Bypass -File (Join-Path $raiz 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'a compilacao falhou' }
if (-not (Test-Path $instalador)) { throw 'o instalador nao foi gerado' }

$tamanho = (Get-Item $instalador).Length
$resumo = (Get-FileHash $instalador -Algorithm SHA256).Hash
Write-Host "instalador: $([math]::Round($tamanho/1MB,1)) MB"
Write-Host "resumo:     $resumo"

# --------------------------------------------------------------- enviar
Passo 'enviando para o servidor'

# Sobe com nome temporario e so depois troca: assim ninguem baixa um arquivo pela metade.
& scp -q $instalador "${Servidor}:/tmp/qubitscast-novo.exe"
if ($LASTEXITCODE -ne 0) { throw 'falhou ao enviar o instalador' }

$dados = [ordered]@{
    versao   = $Versao
    sha256   = $resumo
    tamanho  = $tamanho
    notas    = $Notas
    data     = (Get-Date -Format 'yyyy-MM-dd HH:mm')
}
$json = ($dados | ConvertTo-Json -Compress)
$jsonLocal = Join-Path $env:TEMP 'qubitscast-versao.json'
Set-Content $jsonLocal $json -Encoding UTF8 -NoNewline
& scp -q $jsonLocal "${Servidor}:/tmp/qubitscast-versao.json"
if ($LASTEXITCODE -ne 0) { throw 'falhou ao enviar o arquivo de versao' }
Remove-Item $jsonLocal -ErrorAction SilentlyContinue

$comandos = @(
    "sudo mv /tmp/qubitscast-novo.exe '$PastaRemota/QubitsCast-instalador.exe'",
    "sudo mv /tmp/qubitscast-versao.json '$PastaRemota/versao.json'",
    "sudo chmod 644 '$PastaRemota/QubitsCast-instalador.exe' '$PastaRemota/versao.json'",
    "echo publicado"
) -join ' && '

& ssh $Servidor $comandos
if ($LASTEXITCODE -ne 0) { throw 'falhou ao trocar os arquivos no servidor' }

# --------------------------------------------------------------- conferir
Passo 'conferindo pelo endereco publico'
$anunciado = Invoke-RestMethod -Uri 'https://cast.qubitslab.com.br/versao' -TimeoutSec 20
Write-Host "servidor anuncia: $($anunciado.versao)"
if ($anunciado.versao -ne $Versao) { throw "o servidor ainda anuncia $($anunciado.versao)" }
if ($anunciado.sha256 -ne $resumo) { throw 'o resumo publicado nao bate com o do arquivo' }

# Confere que o arquivo que o link entrega e mesmo este, e nao um pedaco antigo em cache.
$cabecalho = Invoke-WebRequest -Uri 'https://cast.qubitslab.com.br/baixar' -Method Head -TimeoutSec 30
$tamanhoServido = [int64]$cabecalho.Headers['Content-Length'][0]
if ($tamanhoServido -ne $tamanho) {
    throw "o link entrega $tamanhoServido bytes, mas o instalador tem $tamanho"
}
Write-Host "link de download: $([math]::Round($tamanhoServido/1MB,1)) MB, confere"

# --------------------------------------------------------------- registrar
if (-not $SemCommit) {
    Passo 'commit'
    & git -C $raiz add app/QubitsCast.csproj instalador/QubitsCast.iss
    $mensagem = if ($Notas) { "Versao $Versao`n`n$Notas" } else { "Versao $Versao" }
    & git -C $raiz commit -q -m $mensagem
    & git -C $raiz push -q origin main
    Write-Host 'commit e push feitos'
}

Write-Host "`nPUBLICADO: versao $Versao no ar" -ForegroundColor Green
Write-Host "Quem estiver com o app aberto vai ver o botao Atualizar em ate 6 horas;"
Write-Host "quem abrir o app agora ve na hora."
