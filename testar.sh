#!/usr/bin/env bash
# Prova o caminho inteiro: anfitrião captura a tela e transmite, espectador recebe e
# grava um quadro. Falha barulhenta se qualquer etapa não acontecer.
#
#   ./testar.sh [servidor]        (padrão: http://localhost:8790)

set -u
SERVIDOR="${1:-http://localhost:8790}"
EXE="${QCAST_EXE:-/d/qcast-build/bin/Release/net8.0-windows/win-x64/QubitsCast.exe}"
SAIDA="/d/qcast-build/teste"
FF="/d/qcast-build/ff/x81/ffmpeg-n8.1-latest-win64-gpl-8.1/bin/ffmpeg.exe"

PID_ANFITRIAO=""
limpar() {
  [ -n "$PID_ANFITRIAO" ] && kill "$PID_ANFITRIAO" 2>/dev/null
  # Não deixa ffmpeg órfão segurando a placa para a próxima execução.
  powershell -NoProfile -Command \
    "Get-Process QubitsCast -ErrorAction SilentlyContinue | Stop-Process -Force" 2>/dev/null
}
trap limpar EXIT INT TERM

[ -f "$EXE" ] || { echo "não achei o executável em $EXE"; exit 1; }
rm -rf "$SAIDA"; mkdir -p "$SAIDA"

# Sobra de execução anterior mede a run passada, não esta: começar sempre do zero.
limpar
sleep 1

echo "== servidor: $SERVIDOR"
curl -sf "$SERVIDOR/saude" >/dev/null || { echo "servidor não responde"; exit 1; }

LARGURA="${QCAST_LARGURA:-1280}"
FPS="${QCAST_FPS:-30}"
MBPS="${QCAST_MBPS:-4}"
SEGUNDOS="${QCAST_SEGUNDOS:-26}"
JANELA="${QCAST_JANELA:-}"      # vazio = tela inteira

echo "== subindo o anfitrião (${LARGURA}px, ${FPS} fps, ${MBPS} Mbps${JANELA:+, janela \"$JANELA\"})"
"$EXE" --autoteste anfitriao "$SERVIDOR" "$SAIDA/anfitriao.txt" \
       "$LARGURA" "$FPS" "$MBPS" "$SEGUNDOS" ${JANELA:+"$JANELA"} &
PID_ANFITRIAO=$!

CODIGO=""
for i in $(seq 1 60); do
  if [ -f "$SAIDA/anfitriao.txt" ]; then
    CODIGO=$(grep -m1 '^codigo=' "$SAIDA/anfitriao.txt" 2>/dev/null | cut -d= -f2 | tr -d '\r')
    [ -n "$CODIGO" ] && break
  fi
  sleep 0.5
done

[ -n "$CODIGO" ] || { echo "FALHOU: o anfitrião não criou sala"; cat "$SAIDA/anfitriao.txt" 2>/dev/null; exit 1; }
echo "== sala criada: $CODIGO"

# Dá tempo de a captura entrar em regime antes do espectador medir.
sleep 4

echo "== subindo o espectador"
"$EXE" --autoteste espectador "$SERVIDOR" "$CODIGO" "$SAIDA/espectador.txt"
SAIDA_ESPECTADOR=$?

wait "$PID_ANFITRIAO" 2>/dev/null
SAIDA_ANFITRIAO=$?
PID_ANFITRIAO=""

echo
echo "===================== ANFITRIÃO ====================="
cat "$SAIDA/anfitriao.txt" 2>/dev/null
echo "===================== ESPECTADOR ===================="
cat "$SAIDA/espectador.txt" 2>/dev/null
echo "====================================================="

# Converte o quadro cru em PNG para dar para olhar.
BRUTO="$SAIDA/espectador.bgra"
if [ -f "$BRUTO" ]; then
  TAM=$(grep -m1 '^quadro-tamanho=' "$SAIDA/espectador.txt" | cut -d= -f2 | tr -d '\r')
  "$FF" -hide_banner -loglevel error -f rawvideo -pix_fmt bgra -s "$TAM" -i "$BRUTO" \
        -frames:v 1 -y "$SAIDA/quadro.png" 2>/dev/null && echo "quadro em: $SAIDA/quadro.png"
fi

echo
echo "saída anfitrião=$SAIDA_ANFITRIAO  espectador=$SAIDA_ESPECTADOR"
if [ "$SAIDA_ANFITRIAO" = "0" ] && [ "$SAIDA_ESPECTADOR" = "0" ]; then
  echo "TESTE PASSOU"
  exit 0
fi
echo "TESTE FALHOU"
exit 1
