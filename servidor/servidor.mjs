/**
 * QubitsCast — servidor de salas e retransmissão.
 *
 * Faz duas coisas e mais nada:
 *   1. Guarda as salas (quem está em qual, quem transmite) e entrega o link de convite.
 *   2. Retransmite os pacotes de vídeo/áudio do transmissor para quem está assistindo.
 *
 * Sem dependência nenhuma: o WebSocket é implementado aqui mesmo (RFC 6455).
 * Sobe com:  node servidor.mjs
 */

import http from 'node:http';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const AQUI = path.dirname(fileURLToPath(import.meta.url));

const PORTA = Number(process.env.PORTA || 8790);
const ENDERECO = process.env.ENDERECO || '0.0.0.0';
// Endereço público usado para montar o link de convite.
const SITE = (process.env.SITE || `http://localhost:${PORTA}`).replace(/\/+$/, '');
// Endereço de WebSocket que o app usa. Derivado do SITE quando não informado.
const WS_PUBLICO = process.env.WS_PUBLICO || SITE.replace(/^http/, 'ws') + '/ws';
const LINK_DOWNLOAD = process.env.LINK_DOWNLOAD || `${SITE}/baixar`;

const MAX_SALAS = Number(process.env.MAX_SALAS || 50);
const MAX_POR_SALA = Number(process.env.MAX_POR_SALA || 10);
const MAX_PACOTE = Number(process.env.MAX_PACOTE || 8 * 1024 * 1024); // 8 MB por quadro
const SALA_OCIOSA_MS = Number(process.env.SALA_OCIOSA_MS || 60_000);

// Quanto pode ficar esperando no soquete de cada espectador antes de começar a pular.
// A 2 Mb/s, 768 KB são uns 3 segundos de vídeo — passou disso, o atraso já incomoda mais
// do que a falha de um quadro. O valor antigo (24 MB) equivalia a mais de um minuto e meio
// empilhado: quando chegava lá, a imagem já estava parada havia muito tempo.
const FILA_SOQUETE_ALTA = Number(process.env.FILA_SOQUETE_ALTA || 768 * 1024);
const FILA_SOQUETE_CHEIA = Number(process.env.FILA_SOQUETE_CHEIA || 6 * 1024 * 1024);

// Tipos de pacote binário. Byte 0 = tipo, byte 1 = id de quem enviou (preenchido aqui).
const PKT_VIDEO_PARAM = 1; // SPS/PPS — guardado e reenviado a quem chega depois
const PKT_VIDEO_CHAVE = 2; // quadro-chave (IDR)
const PKT_VIDEO_INTER = 3; // quadro normal
const PKT_AUDIO_TELA = 10; // som do que está sendo transmitido
const PKT_AUDIO_VOZ = 11; // microfone de um participante

const log = (...a) => console.log(new Date().toISOString(), ...a);

// ---------------------------------------------------------------- salas

/** @type {Map<string, Sala>} */
const salas = new Map();

const ALFABETO = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'; // sem I, O, 0, 1

function novoCodigo() {
  for (let tentativa = 0; tentativa < 200; tentativa++) {
    const bytes = crypto.randomBytes(6);
    let c = '';
    for (const b of bytes) c += ALFABETO[b % ALFABETO.length];
    if (!salas.has(c)) return c;
  }
  throw new Error('sem código livre');
}

class Sala {
  constructor(codigo, nome) {
    this.codigo = codigo;
    this.nome = nome || 'Sala';
    /** @type {Membro[]} */
    this.membros = [];
    this.proximoId = 1;
    this.transmissorId = 0;
    this.formato = null; // { largura, altura, fps, codec }
    this.videoParam = null; // último SPS/PPS visto
    this.criadaEm = Date.now();
    this.vaziaDesde = Date.now();
  }

  membro(id) {
    return this.membros.find((m) => m.id === id);
  }

  entra(membro) {
    membro.id = this.proximoId++;
    if (this.proximoId > 250) this.proximoId = 1;
    this.membros.push(membro);
    this.vaziaDesde = 0;
  }

  sai(membro) {
    const i = this.membros.indexOf(membro);
    if (i >= 0) this.membros.splice(i, 1);
    if (this.transmissorId === membro.id) this.pararTransmissao();
    if (this.membros.length === 0) this.vaziaDesde = Date.now();
  }

  pararTransmissao() {
    const tinha = this.transmissorId !== 0;
    this.transmissorId = 0;
    this.formato = null;
    this.videoParam = null;
    if (tinha) this.avisarTransmissao();
  }

  resumo() {
    return {
      codigo: this.codigo,
      nome: this.nome,
      link: `${SITE}/s/${this.codigo}`,
      participantes: this.membros.map((m) => ({
        id: m.id,
        apelido: m.apelido,
        transmitindo: m.id === this.transmissorId,
        microfone: m.microfone,
      })),
      transmissao: this.transmissorId
        ? { id: this.transmissorId, ...(this.formato || {}) }
        : null,
    };
  }

  enviarTodos(obj, exceto = null) {
    const texto = JSON.stringify(obj);
    for (const m of this.membros) if (m !== exceto) m.texto(texto);
  }

  avisarParticipantes() {
    this.enviarTodos({ t: 'sala', ...this.resumo() });
  }

  avisarTransmissao() {
    this.enviarTodos({ t: 'sala', ...this.resumo() });
  }
}

setInterval(() => {
  const agora = Date.now();
  for (const [codigo, sala] of salas) {
    if (sala.vaziaDesde && agora - sala.vaziaDesde > SALA_OCIOSA_MS) {
      salas.delete(codigo);
      log('sala expirada', codigo);
    }
  }
}, 15_000).unref();

// ---------------------------------------------------------------- membro

class Membro {
  /** @param {import('node:net').Socket} soquete */
  constructor(soquete) {
    this.soquete = soquete;
    this.id = 0;
    this.apelido = 'convidado';
    this.microfone = false;
    /** @type {Sala|null} */
    this.sala = null;
    this.vivo = true;
    this.ultimoPing = Date.now();
    this.pulados = 0;
    this.puladosRelatados = 0;
  }

  texto(s) {
    enviarQuadro(this.soquete, 1, Buffer.from(s, 'utf8'));
  }

  json(obj) {
    this.texto(JSON.stringify(obj));
  }

  /**
   * Manda mídia para este participante, pulando o que ele não consegue acompanhar.
   *
   * O que se pula importa tanto quanto quando: quadro comum some sem quebrar nada, porque
   * o próximo quadro-chave reconstrói a imagem inteira. Pular quadro-chave junto deixa a
   * pessoa sem nada para reconstruir, e a imagem trava até o keyframe seguinte.
   */
  binario(buf) {
    const acumulado = this.soquete.writableLength;
    const tipo = buf[0];

    if (acumulado > FILA_SOQUETE_ALTA) {
      // Atraso alto: só passa o que reconstrói imagem.
      if (tipo === PKT_VIDEO_INTER) { this.pulados++; return; }
    }
    if (acumulado > FILA_SOQUETE_CHEIA) {
      // Nem quadro-chave cabe: a conexão dessa pessoa não está dando conta agora.
      this.pulados++;
      return;
    }

    enviarQuadro(this.soquete, 2, buf);
  }

  erro(msg) {
    this.json({ t: 'erro', msg });
  }

  fechar() {
    this.vivo = false;
    try {
      this.soquete.end();
    } catch {}
  }
}

// ---------------------------------------------------------------- WebSocket (RFC 6455)

const GUID_WS = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

function enviarQuadro(soquete, opcode, dados) {
  if (soquete.destroyed) return;
  const n = dados.length;
  let cabecalho;
  if (n < 126) {
    cabecalho = Buffer.allocUnsafe(2);
    cabecalho[1] = n;
  } else if (n < 65536) {
    cabecalho = Buffer.allocUnsafe(4);
    cabecalho[1] = 126;
    cabecalho.writeUInt16BE(n, 2);
  } else {
    cabecalho = Buffer.allocUnsafe(10);
    cabecalho[1] = 127;
    cabecalho.writeUInt32BE(0, 2);
    cabecalho.writeUInt32BE(n, 6);
  }
  cabecalho[0] = 0x80 | opcode; // FIN + opcode
  soquete.write(cabecalho);
  soquete.write(dados);
}

/**
 * Lê o fluxo TCP e devolve mensagens completas (juntando fragmentos).
 * @param {(opcode:number, dados:Buffer)=>void} aoReceber
 */
function criarLeitor(aoReceber, aoFechar) {
  let buffer = Buffer.alloc(0);
  let fragOpcode = 0;
  /** @type {Buffer[]} */
  let fragPartes = [];
  let fragTamanho = 0;

  return (pedaco) => {
    buffer = buffer.length ? Buffer.concat([buffer, pedaco]) : pedaco;

    for (;;) {
      if (buffer.length < 2) return;
      const b0 = buffer[0];
      const b1 = buffer[1];
      const fin = (b0 & 0x80) !== 0;
      const opcode = b0 & 0x0f;
      const mascarado = (b1 & 0x80) !== 0;
      let tamanho = b1 & 0x7f;
      let deslocamento = 2;

      if (tamanho === 126) {
        if (buffer.length < 4) return;
        tamanho = buffer.readUInt16BE(2);
        deslocamento = 4;
      } else if (tamanho === 127) {
        if (buffer.length < 10) return;
        const alto = buffer.readUInt32BE(2);
        const baixo = buffer.readUInt32BE(6);
        if (alto !== 0) return aoFechar('quadro grande demais');
        tamanho = baixo;
        deslocamento = 10;
      }

      if (tamanho > MAX_PACOTE) return aoFechar('quadro acima do limite');
      if (!mascarado) return aoFechar('cliente sem máscara'); // exigido pela RFC

      const totalNecessario = deslocamento + 4 + tamanho;
      if (buffer.length < totalNecessario) return;

      const mascara = buffer.subarray(deslocamento, deslocamento + 4);
      const carga = Buffer.allocUnsafe(tamanho);
      const inicio = deslocamento + 4;
      for (let i = 0; i < tamanho; i++) carga[i] = buffer[inicio + i] ^ mascara[i & 3];
      buffer = buffer.subarray(totalNecessario);

      if (opcode === 8) return aoFechar(null); // close
      if (opcode === 9) {
        // ping → pong
        aoReceber(9, carga);
        continue;
      }
      if (opcode === 10) {
        aoReceber(10, carga);
        continue;
      }

      if (opcode === 0) {
        // continuação
        fragPartes.push(carga);
        fragTamanho += carga.length;
        if (fragTamanho > MAX_PACOTE) return aoFechar('mensagem fragmentada grande demais');
        if (fin) {
          aoReceber(fragOpcode, Buffer.concat(fragPartes, fragTamanho));
          fragPartes = [];
          fragTamanho = 0;
          fragOpcode = 0;
        }
        continue;
      }

      if (!fin) {
        fragOpcode = opcode;
        fragPartes = [carga];
        fragTamanho = carga.length;
        continue;
      }

      aoReceber(opcode, carga);
    }
  };
}

// ---------------------------------------------------------------- comandos da sala

function tratarTexto(membro, texto) {
  let m;
  try {
    m = JSON.parse(texto);
  } catch {
    return membro.erro('mensagem inválida');
  }
  if (!m || typeof m.t !== 'string') return;

  switch (m.t) {
    case 'criar': {
      if (membro.sala) sairDaSala(membro);
      if (salas.size >= MAX_SALAS) return membro.erro('O servidor está cheio. Tente daqui a pouco.');
      const codigo = novoCodigo();
      const sala = new Sala(codigo, limpar(m.nome, 40) || 'Sala de ' + limpar(m.apelido, 20));
      salas.set(codigo, sala);
      membro.apelido = limpar(m.apelido, 20) || 'anfitrião';
      sala.entra(membro);
      membro.sala = sala;
      log('sala criada', codigo, 'por', membro.apelido);
      membro.json({ t: 'entrou', voce: membro.id, ...sala.resumo() });
      return;
    }

    case 'entrar': {
      if (membro.sala) sairDaSala(membro);
      const codigo = String(m.codigo || '').toUpperCase().replace(/[^A-Z0-9]/g, '');
      const sala = salas.get(codigo);
      if (!sala) return membro.erro('Sala não encontrada. Confira o código.');
      if (sala.membros.length >= MAX_POR_SALA) return membro.erro('Essa sala já está cheia.');
      membro.apelido = limpar(m.apelido, 20) || 'convidado';
      sala.entra(membro);
      membro.sala = sala;
      log('entrou', codigo, membro.apelido, `(${sala.membros.length})`);
      membro.json({ t: 'entrou', voce: membro.id, ...sala.resumo() });
      sala.avisarParticipantes();
      // Quem chega no meio de uma transmissão precisa do SPS/PPS antes do próximo quadro-chave.
      if (sala.transmissorId && sala.videoParam) membro.binario(sala.videoParam);
      return;
    }

    case 'sair':
      sairDaSala(membro);
      return;

    case 'transmitir': {
      const sala = membro.sala;
      if (!sala) return membro.erro('Você não está em uma sala.');
      if (m.ativa) {
        if (sala.transmissorId && sala.transmissorId !== membro.id)
          return membro.erro('Alguém já está transmitindo nesta sala.');
        sala.transmissorId = membro.id;
        sala.formato = {
          largura: Number(m.largura) || 0,
          altura: Number(m.altura) || 0,
          fps: Number(m.fps) || 0,
          codec: limpar(m.codec, 12) || 'h264',
        };
        sala.videoParam = null;
        log('transmissão ligada', sala.codigo, sala.formato);
      } else if (sala.transmissorId === membro.id) {
        sala.pararTransmissao();
        log('transmissão desligada', sala.codigo);
      }
      sala.avisarTransmissao();
      return;
    }

    case 'microfone': {
      const sala = membro.sala;
      if (!sala) return;
      membro.microfone = !!m.ativo;
      sala.avisarParticipantes();
      return;
    }

    case 'recado': {
      const sala = membro.sala;
      if (!sala) return;
      const texto = limpar(m.texto, 500);
      if (!texto) return;
      sala.enviarTodos({ t: 'recado', de: membro.id, apelido: membro.apelido, texto });
      return;
    }

    case 'ping':
      membro.json({ t: 'pong', em: Date.now() });
      return;
  }
}

function tratarBinario(membro, dados) {
  const sala = membro.sala;
  if (!sala || dados.length < 2) return;
  const tipo = dados[0];

  if (tipo === PKT_VIDEO_PARAM || tipo === PKT_VIDEO_CHAVE || tipo === PKT_VIDEO_INTER) {
    if (sala.transmissorId !== membro.id) return; // só o transmissor manda vídeo
    dados[1] = membro.id;
    if (tipo === PKT_VIDEO_PARAM) sala.videoParam = Buffer.from(dados);
    for (const outro of sala.membros) if (outro !== membro) outro.binario(dados);
    return;
  }

  if (tipo === PKT_AUDIO_TELA) {
    if (sala.transmissorId !== membro.id) return;
    dados[1] = membro.id;
    for (const outro of sala.membros) if (outro !== membro) outro.binario(dados);
    return;
  }

  if (tipo === PKT_AUDIO_VOZ) {
    dados[1] = membro.id;
    for (const outro of sala.membros) if (outro !== membro) outro.binario(dados);
    return;
  }
}

function sairDaSala(membro) {
  const sala = membro.sala;
  if (!sala) return;
  membro.sala = null;
  sala.sai(membro);
  log('saiu', sala.codigo, membro.apelido, `(${sala.membros.length})`);
  if (sala.membros.length) sala.avisarParticipantes();
}

function limpar(v, max) {
  if (typeof v !== 'string') return '';
  return v.replace(/[\u0000-\u001f\u007f]/g, '').trim().slice(0, max);
}

// ---------------------------------------------------------------- HTTP

const PAGINA_ENTRAR = fs.readFileSync(path.join(AQUI, 'publico', 'entrar.html'), 'utf8');
const PAGINA_INICIO = fs.readFileSync(path.join(AQUI, 'publico', 'inicio.html'), 'utf8');

function responder(res, status, tipo, corpo) {
  res.writeHead(status, {
    'content-type': tipo,
    'cache-control': 'no-store',
    'x-content-type-options': 'nosniff',
  });
  res.end(corpo);
}

function escapar(s) {
  return String(s).replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])
  );
}

const servidor = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  const caminho = url.pathname.replace(/\/+$/, '') || '/';

  if (caminho === '/saude') {
    return responder(res, 200, 'application/json', JSON.stringify({
      ok: true,
      salas: salas.size,
      pessoas: [...salas.values()].reduce((n, s) => n + s.membros.length, 0),
      desde: inicio,
    }));
  }

  if (caminho === '/config') {
    return responder(res, 200, 'application/json', JSON.stringify({
      ws: WS_PUBLICO,
      site: SITE,
      maxPorSala: MAX_POR_SALA,
    }));
  }

  // Versão publicada. O app consulta isto ao abrir para se atualizar sozinho.
  // O arquivo é escrito pelo publicar.ps1 e não é versionado — cada publicação o reescreve.
  if (caminho === '/versao') {
    try {
      // O replace tira a marca de ordem de bytes: editor e script do Windows gravam
      // esse caractere invisível no começo do arquivo, e o JSON.parse recusa por causa dele.
      const bruto = fs.readFileSync(path.join(AQUI, 'publico', 'versao.json'), 'utf8');
      const dados = JSON.parse(bruto.replace(/^﻿/, ''));
      // O endereço do instalador é montado aqui, e não gravado no arquivo: assim trocar o
      // domínio do servidor não deixa o app baixando de um lugar que não existe mais.
      dados.url = `${SITE}/baixar`;
      return responder(res, 200, 'application/json', JSON.stringify(dados));
    } catch {
      return responder(res, 200, 'application/json', JSON.stringify({ versao: '' }));
    }
  }

  // Medidor de velocidade. O app usa para saber que qualidade a internet de quem
  // transmite aguenta, em vez de deixar a pessoa escolher 4K num link que não sobe 2 Mb/s.
  if (caminho === '/medir') {
    if (req.method === 'POST') {
      let recebido = 0;
      const comeco = process.hrtime.bigint();
      req.on('data', (p) => {
        recebido += p.length;
        if (recebido > 64 * 1024 * 1024) req.destroy();  // teto de sanidade
      });
      req.on('end', () => {
        const ms = Number(process.hrtime.bigint() - comeco) / 1e6;
        responder(res, 200, 'application/json', JSON.stringify({ bytes: recebido, ms }));
      });
      req.on('error', () => {});
      return;
    }
    // GET devolve bytes para medir a descida.
    const quanto = Math.min(Number(url.searchParams.get('bytes')) || 2_000_000, 32 * 1024 * 1024);
    res.writeHead(200, {
      'content-type': 'application/octet-stream',
      'content-length': quanto,
      'cache-control': 'no-store',
    });
    const bloco = Buffer.alloc(64 * 1024);
    let restante = quanto;
    const escrever = () => {
      while (restante > 0) {
        const pedaco = restante >= bloco.length ? bloco : bloco.subarray(0, restante);
        restante -= pedaco.length;
        if (!res.write(pedaco)) return res.once('drain', escrever);
      }
      res.end();
    };
    escrever();
    return;
  }

  const mSala = caminho.match(/^\/s\/([A-Za-z0-9]{1,12})$/);
  if (mSala) {
    const codigo = mSala[1].toUpperCase();
    const sala = salas.get(codigo);
    const html = PAGINA_ENTRAR
      .replaceAll('{{CODIGO}}', escapar(codigo))
      .replaceAll('{{NOME}}', escapar(sala ? sala.nome : 'Sala não encontrada'))
      .replaceAll('{{EXISTE}}', sala ? '1' : '0')
      .replaceAll('{{PESSOAS}}', sala ? String(sala.membros.length) : '0')
      .replaceAll('{{DOWNLOAD}}', escapar(LINK_DOWNLOAD));
    return responder(res, 200, 'text/html; charset=utf-8', html);
  }

  if (caminho === '/api/sala' && url.searchParams.get('codigo')) {
    const sala = salas.get(url.searchParams.get('codigo').toUpperCase());
    return responder(res, 200, 'application/json', JSON.stringify(
      sala ? { existe: true, nome: sala.nome, pessoas: sala.membros.length } : { existe: false }
    ));
  }

  if (caminho === '/baixar') {
    const arquivo = path.join(AQUI, 'publico', 'QubitsCast-instalador.exe');
    if (fs.existsSync(arquivo)) {
      const tamanho = fs.statSync(arquivo).size;
      res.writeHead(200, {
        'content-type': 'application/octet-stream',
        'content-length': tamanho,
        'content-disposition': 'attachment; filename="QubitsCast-instalador.exe"',
      });
      return fs.createReadStream(arquivo).pipe(res);
    }
    return responder(res, 404, 'text/html; charset=utf-8',
      '<meta charset="utf-8"><p style="font:16px system-ui">O instalador ainda não foi publicado.</p>');
  }

  if (caminho === '/') {
    return responder(res, 200, 'text/html; charset=utf-8',
      PAGINA_INICIO.replaceAll('{{DOWNLOAD}}', escapar(LINK_DOWNLOAD)));
  }

  responder(res, 404, 'text/plain; charset=utf-8', 'não encontrado');
});

const inicio = new Date().toISOString();

// ---------------------------------------------------------------- upgrade para WebSocket

servidor.on('upgrade', (req, soquete) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname !== '/ws') {
    soquete.end('HTTP/1.1 404 Not Found\r\n\r\n');
    return;
  }
  const chave = req.headers['sec-websocket-key'];
  if (!chave || (req.headers.upgrade || '').toLowerCase() !== 'websocket') {
    soquete.end('HTTP/1.1 400 Bad Request\r\n\r\n');
    return;
  }

  const aceite = crypto.createHash('sha1').update(chave + GUID_WS).digest('base64');
  soquete.write(
    'HTTP/1.1 101 Switching Protocols\r\n' +
      'Upgrade: websocket\r\n' +
      'Connection: Upgrade\r\n' +
      `Sec-WebSocket-Accept: ${aceite}\r\n\r\n`
  );

  soquete.setNoDelay(true);
  soquete.setTimeout(0);

  const membro = new Membro(soquete);

  const encerrar = (motivo) => {
    if (!membro.vivo) return;
    if (motivo) log('desconectado:', motivo);
    sairDaSala(membro);
    membro.fechar();
  };

  const ler = criarLeitor((opcode, dados) => {
    membro.ultimoPing = Date.now();
    if (opcode === 1) tratarTexto(membro, dados.toString('utf8'));
    else if (opcode === 2) tratarBinario(membro, dados);
    else if (opcode === 9) enviarQuadro(soquete, 10, dados); // pong
  }, encerrar);

  soquete.on('data', (p) => {
    try {
      ler(p);
    } catch (e) {
      encerrar('erro de leitura: ' + e.message);
    }
  });
  soquete.on('error', () => encerrar(null));
  soquete.on('close', () => encerrar(null));
});

// Ping periódico: derruba quem sumiu sem fechar (queda de rede, notebook fechado).
setInterval(() => {
  const limite = Date.now() - 90_000;
  for (const sala of salas.values())
    for (const m of [...sala.membros]) {
      if (m.ultimoPing < limite) {
        sairDaSala(m);
        m.fechar();
      } else {
        // Quem está perdendo quadro aparece no log: sem isso, "travou" chega como
        // reclamação sem número nenhum para investigar.
        if (m.pulados > m.puladosRelatados) {
          log(`${sala.codigo}: ${m.apelido} nao acompanha ` +
              `(+${m.pulados - m.puladosRelatados} pacotes pulados, ` +
              `${Math.round(m.soquete.writableLength / 1024)} KB na fila)`);
          m.puladosRelatados = m.pulados;
        }
        enviarQuadro(m.soquete, 9, Buffer.alloc(0));
      }
    }
}, 25_000).unref();

servidor.listen(PORTA, ENDERECO, () => {
  log(`QubitsCast no ar em ${ENDERECO}:${PORTA}`);
  log(`site: ${SITE}   websocket: ${WS_PUBLICO}`);
});
