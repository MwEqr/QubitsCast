/**
 * Gera o ícone do aplicativo (.ico com PNGs de 16 a 256) sem depender de
 * nenhuma biblioteca de imagem: o PNG é montado byte a byte.
 *
 *   node ativos/gerar-icone.mjs
 */
import zlib from 'node:zlib';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const AQUI = path.dirname(fileURLToPath(import.meta.url));
const DESTINO = path.join(AQUI, '..', 'app', 'Recursos', 'qubitscast.ico');

const FUNDO = [0x17, 0x1a, 0x21];
const ACENTO = [0x5b, 0x8c, 0xff];
const ACENTO_CLARO = [0x7a, 0xa2, 0xff];

/** Desenha o ícone num buffer RGBA de lado×lado, com 4x de supersampling. */
function desenhar(lado) {
  const S = 4;                       // amostras por eixo
  const n = lado * S;
  const px = Buffer.alloc(lado * lado * 4);

  const raio = n * 0.22;             // canto arredondado da base
  // Moldura do monitor
  const mx0 = n * 0.14, mx1 = n * 0.86;
  const my0 = n * 0.24, my1 = n * 0.68;
  const espessura = n * 0.062;
  // Pé
  const px0 = n * 0.36, px1 = n * 0.64;
  const py0 = n * 0.76, py1 = n * 0.83;
  // Ponto central
  const cx = n / 2, cy = (my0 + my1) / 2, rp = n * 0.085;

  const dentroBaseArredondada = (x, y) => {
    const dx = Math.max(raio - x, 0, x - (n - raio));
    const dy = Math.max(raio - y, 0, y - (n - raio));
    return dx * dx + dy * dy <= raio * raio;
  };

  for (let y = 0; y < lado; y++) {
    for (let x = 0; x < lado; x++) {
      let somaR = 0, somaG = 0, somaB = 0, somaA = 0;

      for (let sy = 0; sy < S; sy++) {
        for (let sx = 0; sx < S; sx++) {
          const fx = x * S + sx + 0.5;
          const fy = y * S + sy + 0.5;

          if (!dentroBaseArredondada(fx, fy)) continue;

          let cor = FUNDO;
          const naMoldura =
            fx >= mx0 && fx <= mx1 && fy >= my0 && fy <= my1 &&
            (fx <= mx0 + espessura || fx >= mx1 - espessura ||
             fy <= my0 + espessura || fy >= my1 - espessura);
          const noPe = fx >= px0 && fx <= px1 && fy >= py0 && fy <= py1;
          const noPonto = (fx - cx) ** 2 + (fy - cy) ** 2 <= rp * rp;

          if (noPonto) cor = ACENTO_CLARO;
          else if (naMoldura || noPe) cor = ACENTO;

          somaR += cor[0]; somaG += cor[1]; somaB += cor[2]; somaA += 255;
        }
      }

      const total = S * S;
      const i = (y * lado + x) * 4;
      const a = somaA / total;
      // Compõe sobre transparente: a média já traz a cobertura da borda.
      px[i] = a > 0 ? Math.round(somaR / (somaA / 255) ) : 0;
      px[i + 1] = a > 0 ? Math.round(somaG / (somaA / 255)) : 0;
      px[i + 2] = a > 0 ? Math.round(somaB / (somaA / 255)) : 0;
      px[i + 3] = Math.round(a);
    }
  }
  return px;
}

function crc32(buf) {
  let c, tabela = crc32.tabela;
  if (!tabela) {
    tabela = crc32.tabela = new Int32Array(256);
    for (let n = 0; n < 256; n++) {
      c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      tabela[n] = c;
    }
  }
  let crc = -1;
  for (const b of buf) crc = (crc >>> 8) ^ tabela[(crc ^ b) & 0xff];
  return (crc ^ -1) >>> 0;
}

function pedaco(tipo, dados) {
  const comp = Buffer.concat([Buffer.from(tipo, 'ascii'), dados]);
  const tamanho = Buffer.alloc(4);
  tamanho.writeUInt32BE(dados.length);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(comp));
  return Buffer.concat([tamanho, comp, crc]);
}

function montarPng(lado, rgba) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(lado, 0);
  ihdr.writeUInt32BE(lado, 4);
  ihdr[8] = 8;    // bits por canal
  ihdr[9] = 6;    // RGBA
  ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;

  // Cada linha vai precedida do byte de filtro (0 = nenhum).
  const cru = Buffer.alloc(lado * (lado * 4 + 1));
  for (let y = 0; y < lado; y++) {
    cru[y * (lado * 4 + 1)] = 0;
    rgba.copy(cru, y * (lado * 4 + 1) + 1, y * lado * 4, (y + 1) * lado * 4);
  }

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    pedaco('IHDR', ihdr),
    pedaco('IDAT', zlib.deflateSync(cru, { level: 9 })),
    pedaco('IEND', Buffer.alloc(0)),
  ]);
}

const TAMANHOS = [16, 24, 32, 48, 64, 128, 256];
const pngs = TAMANHOS.map((t) => ({ lado: t, dados: montarPng(t, desenhar(t)) }));

// Cabeçalho ICO: 6 bytes + 16 por imagem.
const cabecalho = Buffer.alloc(6);
cabecalho.writeUInt16LE(0, 0);
cabecalho.writeUInt16LE(1, 2);   // 1 = ícone
cabecalho.writeUInt16LE(pngs.length, 4);

let deslocamento = 6 + pngs.length * 16;
const entradas = [];
for (const p of pngs) {
  const e = Buffer.alloc(16);
  e[0] = p.lado >= 256 ? 0 : p.lado;   // 0 significa 256
  e[1] = p.lado >= 256 ? 0 : p.lado;
  e[2] = 0; e[3] = 0;
  e.writeUInt16LE(1, 4);               // planos
  e.writeUInt16LE(32, 6);              // bits por pixel
  e.writeUInt32LE(p.dados.length, 8);
  e.writeUInt32LE(deslocamento, 12);
  deslocamento += p.dados.length;
  entradas.push(e);
}

fs.mkdirSync(path.dirname(DESTINO), { recursive: true });
fs.writeFileSync(DESTINO, Buffer.concat([cabecalho, ...entradas, ...pngs.map((p) => p.dados)]));
console.log('ícone gravado:', DESTINO, fs.statSync(DESTINO).size, 'bytes,', pngs.length, 'tamanhos');
