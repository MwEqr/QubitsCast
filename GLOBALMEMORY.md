# QubitsCast — mapa do projeto

Transmissão de tela para Windows, nativa (WPF/.NET 8), com sala por link.
Criado em 30/08/2026.

---

## 1. Onde fica o quê

| Caminho | O que é |
|---|---|
| `app/` | aplicativo WPF (.NET 8, `net8.0-windows`, self-contained x64) |
| `app/Core/Base.cs` | registro em arquivo, ajustes, lista de monitores |
| `app/Core/Ffmpeg.cs` | sondagem de placa/captura, linhas de comando do ffmpeg |
| `app/Core/Sinal.cs` | cliente WebSocket (sala + mídia) |
| `app/Core/Transmissor.cs` | captura + compressão + recorte do fluxo H.264 |
| `app/Core/Receptor.cs` | descompressão + entrega do último quadro |
| `app/Core/Cores.cs` | conversão NV12 → BGRA |
| `app/Core/Audio.cs` | captura (loopback e microfone) e reprodução, Opus |
| `app/Core/Fontes.cs` | lista o que dá para transmitir: monitores e janelas abertas |
| `app/Core/AudioPorApp.cs` | captura do som de um programa só (exige Windows 11) |
| `app/Core/Atualizacao.cs` | procura, baixa, confere e instala versão nova |
| `app/Core/Medidor.cs` | mede a subida da internet para escolher a qualidade |
| `app/Core/Autoteste.cs` | modo sem interface que prova o caminho inteiro |
| `publicar.ps1` | publica uma versão nova inteira, com um comando |
| `servidor/servidor.mjs` | servidor de salas e retransmissão, Node puro, sem dependências |
| `instalador/QubitsCast.iss` | Inno Setup, instala sem administrador |
| `build.ps1` | compila e gera o instalador |
| `testar.sh` | teste de ponta a ponta com processos reais |

Compilação sai em `D:\qcast-build\` (o C: dele vive cheio), via `app/Directory.Build.props`,
que é gitignorado.

---

## 2. Infra

- **Domínio:** `cast.qubitslab.com.br` → `23.81.118.248` (vps-new), **DNS-only (cinza)**.
  Cinza de propósito: vídeo ao vivo passando pelo proxy do Cloudflare é uso desproporcional
  no plano grátis. Precisou entrar em `/etc/blindagem/fora-da-trava.txt` da vps-new, senão a
  trava "só Cloudflare" devolve 444.
- **A zona `encrypthost.com.br` está no teto de 200 registros DNS** (erro 81045 ao criar).
  Subdomínio novo para projeto dele vai em `qubitslab.com.br`, que tinha 10.
- Servidor roda no PM2 do root como `qubitscast`, porta 8790, atrás do nginx do aaPanel.

---

## 3. Decisões que custaram medição — não desfazer sem medir de novo

- **O build do ffmpeg é o 8.1, e isso é de propósito.** O 9.0.1 exige driver NVIDIA 610+ e
  recusa o NVENC no driver 582.66 da GTX 1050 Ti (`Driver does not support the required
  nvenc API version. Required: 13.1 Found: 13.0`), caindo para o processador em silêncio.
  Com o 8.1, NVENC abre normalmente. Medido em 30/08/2026.
- **O pipe de quadros crus é NV12, não BGRA.** BGRA a 1080p60 são ~500 MB/s entre os dois
  processos e **não passa**: medido 16 quadros por segundo no espectador contra 59 no
  anfitrião. Em NV12 (1,5 byte por ponto, 186 MB/s) sobe para 53. A conversão para BGRA é
  feita em `Cores.cs`, com tabelas e `Parallel.For`.
- **Um quadro H.264 vem partido em vários slices** — o NVENC usou 8 aqui. Tratar cada slice
  como quadro inflava a contagem para ~480 fps e entupia a fila de envio. O corte certo é
  pelo campo `first_mb_in_slice`: bit mais alto ligado no primeiro byte depois do cabeçalho
  NAL significa começo de quadro (`Transmissor.ComecaQuadro`).
- **A ordem dos monitores no Windows não é a ordem das saídas da placa.** Com dois monitores
  de tamanhos diferentes (1600x900 e 1920x1080 aqui), mandar `ddagrab output_idx=0` e
  redimensionar com a medida do "monitor 0" do Windows captura uma tela com a medida da
  outra. `Telas.CasarComSaidasDaPlaca()` descobre o mapeamento capturando um quadro de cada
  saída e lendo o tamanho no cabeçalho do PNG.
- **Resolução de monitor vem de `EnumDisplaySettings`, não do retângulo do monitor.** Sem
  DPI-aware o Windows devolve a resolução já dividida pela escala (1600x900 numa tela
  1920x1080 a 120%). O modo de vídeo é imune. O `app.manifest` com PerMonitorV2 existe para
  a interface não ficar borrada, mas a resolução não depende dele.
- **O GUID do handshake WebSocket é `258EAFA5-E914-47DA-95CA-C5AB0DC85B11`.** Escrever de
  memória saiu errado (o `C` do último grupo trocado de lugar) e o .NET recusou com
  `Sec-WebSocket-Accept header value ... is invalid`.
- **Cair no processador não é guardado no cache de capacidades.** A placa aceita um número
  pequeno de codificações simultâneas; um teste feito enquanto outra transmissão roda falha
  sem a máquina ser incapaz, e guardar isso deixaria o app lento para sempre.
- **`hwmap=derive_device=cuda` não funciona neste build** (erro -40): o caminho é
  `ddagrab → hwdownload → scale → nv12 → nvenc`. `scale_d3d11` também falhou.
- **Arquivo de teste leva nome único (GUID).** Com nome fixo, sobra ainda presa pelo ffmpeg
  fazia o `File.Delete` lançar `IOException`, e a exceção era lida como "esta máquina não
  consegue" — foi assim que a captura pela placa apareceu como indisponível sem motivo, e o
  app rodou em modo lento mostrando "captura simples".
- **`StartupUri` é processado pelo `Run()`, depois do `OnStartup`.** Sair mais cedo do
  `OnStartup` não impede a janela de abrir; o modo `--autoteste` abria janela mesmo assim, e
  ela era fotografada no lugar da janela de verdade. A janela agora é criada no código.
- **`Application.StartupUri = null` lança `ArgumentNullException`** — não serve para
  cancelar a abertura da janela.
- **O nginx do painel é anterior à diretiva `http2 on;`** — usar `listen 443 ssl http2;`.
  E `nginx -t` dentro de pipe esconde o código de saída: o `kill -HUP` roda mesmo com a
  config quebrada, o nginx mantém a antiga e tudo parece ter dado certo.
- **`gdigrab` aceita `-i "hwnd=0x..."`**, e é assim que a captura de uma janela é feita.
  Por título (`title=`) o alvo se perde sozinho: título de navegador e de editor muda a
  cada aba e a cada arquivo aberto. As opções do demuxer não listam `hwnd`, mas funciona
  (testado: capturou a janela certa em 1920x1040).
- **Captura do som de UM programa exige Windows 10 compilação 20348** (Windows 11 /
  Server 2022) — a página `AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS` diz isso em
  "Minimum supported client". A máquina do Yuri é 19045, então ali a opção não aparece.
  O código está pronto (`AudioPorApp.cs`) e cai para o som do computador se a abertura
  falhar, mas **o caminho feliz nunca foi executado aqui** — quando alguém com Windows 11
  usar, é a primeira vez que roda de verdade.

---

## 3.1 A internet do Yuri (medida em 30/08/2026)

| | |
|---|---|
| Subida (upload) até a vps-new | **3,1 Mb/s** |
| Descida (download) | 19 Mb/s |
| Latência até a VPS | 188 ms |

Isso limita o que ele consegue **transmitir** a 720p30 (2 Mb/s), não o que o app aguenta: em
rede local o app entrega 1080p60 com 59 quadros por segundo enviados e 53 recebidos. Quem
assiste não tem esse problema, porque a descida é 6 vezes maior.

**`scp` não serve para medir banda** — o SSH tem janela de fluxo própria e limita sozinho
(deu 2,3 Mb/s onde o HTTP deu 3,1). Medir pelo endpoint `/medir` do próprio servidor.

---

## 4. Protocolo

WebSocket em `/ws`. Texto é JSON de controle (`criar`, `entrar`, `transmitir`, `microfone`,
`recado`); binário é mídia, com `[tipo:1][origem:1][conteúdo]`. O servidor reescreve o byte
de origem — o cliente não consegue se passar por outro.

| tipo | conteúdo |
|---|---|
| 1 | SPS/PPS (o servidor guarda e reenvia a quem entra depois) |
| 2 | quadro-chave |
| 3 | quadro normal |
| 10 | som do sistema (Opus estéreo, 96 kb/s) |
| 11 | microfone (Opus mono, 28 kb/s) |

Todo quadro-chave sai com SPS/PPS na frente, para quem chega no meio conseguir decodificar
sem ter visto o começo.

---

## 5. Testar

`testar.sh` sobe um anfitrião e um espectador de verdade (`--autoteste`), mede taxa dos dois
lados e grava um quadro em disco. O relatório conta quantos pontos da imagem têm cor — um
quadro todo preto denunciaria captura vazia passando por sucesso.

Rodar anfitrião e espectador na mesma máquina é o caso pessimista: são dois ffmpeg, o
servidor e dois apps disputando a mesma CPU e a mesma placa.
