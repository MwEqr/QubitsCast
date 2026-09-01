#!/usr/bin/env bash
# Reproduz a situacao de quem esta assistindo numa maquina que nao da conta:
# sobe VARIOS espectadores ao mesmo tempo, disputando a mesma CPU.
#
# O que este teste procura: antes da correcao, um espectador atrasado prendia a
# thread de rede, o servidor empilhava e a taxa de TODOS desabava. Agora quem esta
# atrasado deve perder quadro solto e continuar recebendo perto do que e enviado.
#
#   bash testar-carga.sh [servidor] [quantos]

set -u
SERVIDOR="${1:-https://cast.qubitslab.com.br}"
QUANTOS="${2:-3}"
EXE="${QCAST_EXE:-/d/qcast-build/bin/Release/net8.0-windows/win-x64/QubitsCast.exe}"
SAIDA="/d/qcast-build/carga"

PIDS=()
limpar() {
  for p in "${PIDS[@]:-}"; do [ -n "$p" ] && kill "$p" 2>/dev/null; done
  powershell -NoProfile -Command \
    "Get-Process QubitsCast -ErrorAction SilentlyContinue | Stop-Process -Force" 2>/dev/null
}
trap limpar EXIT INT TERM

[ -f "$EXE" ] || { echo "nao achei o executavel em $EXE"; exit 1; }
rm -rf "$SAIDA"; mkdir -p "$SAIDA"
limpar; sleep 1

echo "== anfitriao: 720p a 60 fps, 2 Mb/s"
"$EXE" --autoteste anfitriao "$SERVIDOR" "$SAIDA/anfitriao.txt" 1280 60 2 100 &
PIDS+=($!)

CODIGO=""
for i in $(seq 1 60); do
  CODIGO=$(grep -m1 '^codigo=' "$SAIDA/anfitriao.txt" 2>/dev/null | cut -d= -f2 | tr -d '\r')
  [ -n "$CODIGO" ] && break
  sleep 0.5
done
[ -n "$CODIGO" ] || { echo "FALHOU: sem sala"; exit 1; }
echo "== sala $CODIGO, subindo $QUANTOS espectadores juntos"
sleep 4

for n in $(seq 1 "$QUANTOS"); do
  "$EXE" --autoteste espectador "$SERVIDOR" "$CODIGO" "$SAIDA/espectador-$n.txt" &
  PIDS+=($!)
  sleep 1
done

wait "${PIDS[@]}" 2>/dev/null
PIDS=()

echo
echo "===================== RESULTADO ====================="
ENVIADO=$(grep -m1 '^media-mbps=' "$SAIDA/anfitriao.txt" | cut -d= -f2 | tr -d '\r')
FPSENV=$(grep -m1 '^media-fps=' "$SAIDA/anfitriao.txt" | cut -d= -f2 | tr -d '\r')
echo "anfitriao enviou:  $FPSENV fps, $ENVIADO Mb/s"
echo
FALHAS=0
for n in $(seq 1 "$QUANTOS"); do
  ARQ="$SAIDA/espectador-$n.txt"
  [ -f "$ARQ" ] || { echo "espectador $n: sem relatorio"; FALHAS=$((FALHAS+1)); continue; }
  F=$(grep -m1 '^media-fps=' "$ARQ" | cut -d= -f2 | tr -d '\r')
  M=$(grep -m1 '^media-mbps=' "$ARQ" | cut -d= -f2 | tr -d '\r')
  P=$(grep -m1 '^total-perdidos=' "$ARQ" | cut -d= -f2 | tr -d '\r')
  R=$(grep -m1 '^RESULTADO=' "$ARQ" | cut -d= -f2 | tr -d '\r')
  echo "espectador $n:  $F fps, $M Mb/s, $P perdidos  ($R)"
  [ "$R" = "ok" ] || FALHAS=$((FALHAS+1))
done
echo "====================================================="

if [ "$FALHAS" -eq 0 ]; then echo "TESTE PASSOU: nenhum espectador ficou sem imagem"; exit 0; fi
echo "TESTE FALHOU: $FALHAS espectador(es) com problema"
exit 1
