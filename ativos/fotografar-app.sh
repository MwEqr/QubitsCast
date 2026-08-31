#!/usr/bin/env bash
# Sobe uma transmissão de verdade, abre o aplicativo instalado nela pelo link de convite
# e fotografa a tela. Serve para olhar a interface funcionando, não uma maquete.
#
#   bash ativos/fotografar-app.sh [servidor]

set -u
SERVIDOR="${1:-https://cast.qubitslab.com.br}"
APP="$LOCALAPPDATA/Programs/QubitsCast/QubitsCast.exe"
FONTE="${QCAST_EXE:-/d/qcast-build/bin/Release/net8.0-windows/win-x64/QubitsCast.exe}"
FF="/d/qcast-build/ff/x81/ffmpeg-n8.1-latest-win64-gpl-8.1/bin/ffmpeg.exe"
SAIDA="/d/qcast-build/fotos"

PID_ANFITRIAO=""
limpar() {
  [ -n "$PID_ANFITRIAO" ] && kill "$PID_ANFITRIAO" 2>/dev/null
  powershell -NoProfile -Command \
    "Get-Process QubitsCast -ErrorAction SilentlyContinue | Stop-Process -Force" 2>/dev/null
}
trap limpar EXIT INT TERM

[ -f "$APP" ] || { echo "o aplicativo nao esta instalado em $APP"; exit 1; }
rm -rf "$SAIDA"; mkdir -p "$SAIDA"
limpar; sleep 1

echo "== foto 1: tela de entrada"
"$APP" &
sleep 12
"$FF" -hide_banner -loglevel error -f gdigrab -framerate 1 -offset_x 0 -offset_y 0 \
      -video_size 1920x1080 -i desktop -frames:v 1 -y "$SAIDA/1-entrada.png" 2>/dev/null
powershell -NoProfile -Command "Get-Process QubitsCast -ErrorAction SilentlyContinue | Stop-Process -Force"
sleep 2

echo "== subindo uma transmissao para entrar nela"
"$FONTE" --autoteste anfitriao "$SERVIDOR" "$SAIDA/anfitriao.txt" 1280 30 2 120 &
PID_ANFITRIAO=$!

CODIGO=""
for i in $(seq 1 60); do
  CODIGO=$(grep -m1 '^codigo=' "$SAIDA/anfitriao.txt" 2>/dev/null | cut -d= -f2 | tr -d '\r')
  [ -n "$CODIGO" ] && break
  sleep 0.5
done
[ -n "$CODIGO" ] || { echo "o anfitriao nao criou sala"; exit 1; }
echo "   sala: $CODIGO"
sleep 5

echo "== foto 2: sala com a tela chegando"
"$APP" "qubitscast://entrar/$CODIGO" &
sleep 30
"$FF" -hide_banner -loglevel error -f gdigrab -framerate 1 -offset_x 0 -offset_y 0 \
      -video_size 1920x1080 -i desktop -frames:v 1 -y "$SAIDA/2-sala.png" 2>/dev/null

echo
ls -la "$SAIDA"/*.png 2>/dev/null
