# QubitsCast

Transmissão de tela para Windows. Cria uma sala, você manda o link, e quem recebe
entra e vê a sua tela — com o som do que está tocando e voz por microfone.

Aplicativo nativo (WPF), não é navegador embrulhado. Usa a placa de vídeo para capturar
e comprimir, então roda leve mesmo em 1080p a 60 quadros por segundo.

## Como funciona

```
  quem transmite                    servidor                    quem assiste
  ─────────────────                 ─────────                   ────────────
  captura a tela ──► comprime ──►  retransmite  ──► descomprime ──► desenha
  (placa de vídeo)   (H.264)       (não guarda)     (H.264)         na janela
  som do sistema ──► Opus     ──►               ──► Opus       ──► toca
  microfone      ──► Opus     ──►               ──► Opus       ──► mistura
```

O servidor não guarda nada: repassa os pacotes de quem transmite para quem está na sala
e esquece. A única coisa em memória é a lista de salas abertas.

## O que dá para escolher

**O que transmitir** — a tela inteira (qualquer um dos monitores) ou **uma janela só**. Na
janela, o resto da tela não aparece: dá para deixar a conversa aberta ao lado sem que ela vá
junto. Tela inteira passa pela placa de vídeo e aguenta 60 quadros; janela passa por GDI, que
gasta mais processador e não enxerga jogo em tela cheia exclusiva.

**Que som vai junto** — nenhum, o som do computador inteiro, ou **só o som de um programa**.
A última opção depende de uma função que só existe no Windows 11 (compilação 20348 ou mais
nova); em Windows 10 comum ela não aparece na lista, e o motivo fica no texto de ajuda.

## Qualidades

| Opção | Uso de internet |
|---|---|
| 720p · 30 fps | 3 Mb/s |
| 1080p · 30 fps | 6 Mb/s |
| 1080p · 60 fps | 8 Mb/s |
| 1440p · 60 fps | 14 Mb/s |
| 4K · 60 fps | 25 Mb/s |

Quem transmite precisa desse tanto de **envio** (upload). Quem assiste, de download.

## Placas de vídeo

O aplicativo testa a máquina na primeira vez e escolhe sozinho, nesta ordem:

1. NVIDIA (NVENC)
2. Intel (Quick Sync)
3. AMD (AMF)
4. Processador (libx264) — funciona em qualquer máquina, gasta mais CPU

A captura também tem dois caminhos: Desktop Duplication (pela placa) e, se ela não estiver
disponível, GDI. Se a placa recusar no meio de uma transmissão (ela aceita um número
limitado de codificações ao mesmo tempo), o app troca para o processador sozinho e avisa.

## Compilar

Precisa do .NET 8 SDK, Node (para o ícone) e Inno Setup 6 (para o instalador).

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Sai em `instalador\saida\QubitsCast-instalador.exe`. O instalador não pede permissão de
administrador — instala na pasta do usuário.

## Publicar uma versão nova

Um comando faz tudo — sobe o número da versão, compila, gera o instalador, manda para o
servidor, confere pelo endereço público e registra no git:

```powershell
powershell -ExecutionPolicy Bypass -File publicar.ps1 -Notas "o que mudou"
```

Quem abrir o aplicativo depois disso vê o botão **Atualizar** na barra de cima; quem já
estiver com ele aberto vê em até 6 horas. O app baixa, confere o resumo SHA-256 do arquivo e
só então instala — e nunca instala sozinho no meio de uma transmissão.

Para publicar um número específico: `-Versao 1.2.0`. Para não commitar: `-SemCommit`.

> Os scripts `.ps1` deste projeto são ASCII de propósito. O PowerShell 5.1 lê arquivo UTF-8
> sem BOM como ANSI, e um acento no fonte chega embaralhado — inclusive dentro de um regex.

## Servidor

Um arquivo, sem dependência nenhuma (o WebSocket é implementado nele mesmo):

```bash
PORTA=8790 SITE=https://cast.qubitslab.com.br node servidor/servidor.mjs
```

| Variável | Para que serve |
|---|---|
| `PORTA` | porta local (padrão 8790) |
| `SITE` | endereço público, usado para montar o link de convite |
| `WS_PUBLICO` | endereço do WebSocket (padrão: o `SITE` com `ws`/`wss` + `/ws`) |
| `MAX_POR_SALA` | quantas pessoas cabem numa sala (padrão 10) |
| `MAX_SALAS` | quantas salas ao mesmo tempo (padrão 50) |

## Testar

Prova o caminho inteiro — captura, compressão, rede, descompressão — sem ninguém clicar:

```bash
QCAST_LARGURA=1920 QCAST_FPS=60 QCAST_MBPS=8 bash testar.sh http://localhost:8790
```

Sobe um anfitrião que transmite de verdade, um espectador que recebe, e grava um quadro
em disco para conferir que a imagem chegou. Falha com código diferente de zero se
qualquer etapa não acontecer.
