// Generates byte-exact reference vectors from the packet/payload logic of the WORKING reference web configurator
// (kabelklaus/ajazz-ak820-pro, MIT, app.js — see THIRD_PARTY_LICENSE_kabelklaus.txt).
// The functions below are transcribed 1:1 from app.js (cmdPacket, modeDataPacket, imageCfgPacket, encodeDelay, the
// header/payload assembly of #imgUpload, toRgb565's pixel packing and the sequences of #rgbApply / #imgUpload / finalize).
// Only pure byte math runs here: no network, no HID, no DOM.
//   node make-golden-vectors.mjs <app.js sha256> <output.json>
import { createHash } from "node:crypto";
import { writeFileSync } from "node:fs";

const [appSha, out] = process.argv.slice(2);
const FRAME_PX = 128 * 128 * 2, CHUNK = 4096;

// ---- verbatim from app.js ----
function cmdPacket(cmd, sub, enable) { const p = new Uint8Array(64); p[0] = 0x04; p[1] = cmd; p[2] = sub & 0xff; p[8] = enable & 0xff; return p; }
function modeDataPacket(mode, r, g, b, rainbow, brightness, speed, dir) {
  const p = new Uint8Array(64);
  p[0] = mode; p[1] = r; p[2] = g; p[3] = b; p[8] = rainbow ? 1 : 0;
  p[9] = Math.min(brightness, 5); p[10] = Math.min(speed, 5); p[11] = dir; p[14] = 0x55; p[15] = 0xAA;
  return p;
}
function imageCfgPacket(totalChunks, sub) { const p = new Uint8Array(64); p[0] = 0x04; p[1] = 0x72; p[2] = sub; p[8] = totalChunks & 0xff; p[9] = (totalChunks >> 8) & 0xff; return p; }
const encodeDelay = (ms) => Math.min(255, Math.max(1, Math.round(ms / 2)));
const hex = (u8) => Buffer.from(u8).toString("hex");

// sequence of the "variant A" framing: reportId 0x00, 64 data bytes, for every feature report
const step = (label, kind, pkt, sleepBefore) => ({ label, kind, hex: hex(pkt), sleepBeforeHandshakeMs: sleepBefore });

function lighting({ mode, r, g, b, rainbow, bright, speed, dir, saveByte8, finish }) {
  const seq = [];
  seq.push({ ...step("START", "feature", cmdPacket(0x18, 0x00, 0x01), 40), handshake: true, settleMs: 0 });
  seq.push({ ...step("MODE_CFG", "feature", cmdPacket(0x13, 0x00, 0x01), 40), handshake: true, settleMs: 0 });
  seq.push({ ...step("MODE_DATA", "feature", modeDataPacket(mode, r, g, b, rainbow, bright, speed, dir), 0), handshake: false, settleMs: 40 });
  seq.push({ ...step("SAVE", "feature", cmdPacket(0x02, 0x00, saveByte8), 100), handshake: true, settleMs: 0 });   // finalize(): SAVE, sleep(100), handshake
  if (finish) seq.push({ ...step("FINISH", "feature", cmdPacket(0xf0, 0x00, 0x01), 100), handshake: true, settleMs: 0 });
  return seq;
}

// verbatim from app.js: timeDataPacket(d) and sleepDataPacket(level)
function timeDataPacket(d) {
  const p = new Uint8Array(64);
  p[0] = 0x00; p[1] = 0x01; p[2] = 0x5A;
  p[3] = d.getFullYear() - 2000; p[4] = d.getMonth() + 1; p[5] = d.getDate(); p[6] = d.getHours(); p[7] = d.getMinutes(); p[8] = d.getSeconds();
  p[9] = 0x00; p[10] = 0x04; p[62] = 0xAA; p[63] = 0x55;
  return p;
}
function sleepDataPacket(level) { const p = new Uint8Array(64); p[8] = level; p[62] = 0xAA; p[63] = 0x55; return p; }

// "#timesync": START(04 18, en 1) -40ms-> hs, TIME_CFG(04 28, en 1) -40ms-> hs, TIME_DATA -40ms-, finalize(SAVE b8, 100ms hs [, FINISH 100ms hs])
function timeSync(d, saveByte8, finish) {
  const seq = [];
  seq.push({ ...step("START", "feature", cmdPacket(0x18, 0x00, 0x01), 40), handshake: true, settleMs: 0 });
  seq.push({ ...step("TIME_CFG", "feature", cmdPacket(0x28, 0x00, 0x01), 40), handshake: true, settleMs: 0 });
  seq.push({ ...step("TIME_DATA", "feature", timeDataPacket(d), 0), handshake: false, settleMs: 40 });
  seq.push({ ...step("SAVE", "feature", cmdPacket(0x02, 0x00, saveByte8), 100), handshake: true, settleMs: 0 });
  if (finish) seq.push({ ...step("FINISH", "feature", cmdPacket(0xf0, 0x00, 0x01), 100), handshake: true, settleMs: 0 });
  return seq;
}
// "#sleepApply": START, SLEEP_CFG(04 17 01, en 1), SLEEP_DATA, finalize
function standby(level, saveByte8, finish) {
  const seq = [];
  seq.push({ ...step("START", "feature", cmdPacket(0x18, 0x00, 0x01), 40), handshake: true, settleMs: 0 });
  seq.push({ ...step("SLEEP_CFG", "feature", cmdPacket(0x17, 0x01, 0x01), 40), handshake: true, settleMs: 0 });
  seq.push({ ...step("SLEEP_DATA", "feature", sleepDataPacket(level), 0), handshake: false, settleMs: 40 });
  seq.push({ ...step("SAVE", "feature", cmdPacket(0x02, 0x00, saveByte8), 100), handshake: true, settleMs: 0 });
  if (finish) seq.push({ ...step("FINISH", "feature", cmdPacket(0xf0, 0x00, 0x01), 100), handshake: true, settleMs: 0 });
  return seq;
}

function rgb565(r, g, b) { const v = ((r & 0xf8) << 8) | ((g & 0xfc) << 3) | (b >> 3); return [v & 0xff, (v >> 8) & 0xff]; }

// deterministic synthetic frames: pixel (x,y) of frame i
function px(i, x, y) { return [(x * 2 + i * 10) & 255, (y * 2 + i * 3) & 255, ((x + y) * 3 + i * 7) & 255]; }

function image({ delaysMs, variant }) {
  const frames = delaysMs.map((d, i) => {
    const data = new Uint8Array(FRAME_PX);
    for (let y = 0, j = 0; y < 128; y++) for (let x = 0; x < 128; x++, j += 2) { const [r, g, b] = px(i, x, y); const [lo, hi] = rgb565(r, g, b); data[j] = lo; data[j + 1] = hi; }
    return { data, delayMs: d };
  });
  const header = new Uint8Array(256).fill(0xff);
  header[0] = frames.length & 0xff;
  if (frames.length === 1) header[1] = 0x00; else frames.forEach((f, i) => { if (1 + i < 256) header[1 + i] = encodeDelay(f.delayMs); });
  const dataLen = header.length + frames.length * FRAME_PX;
  const totalChunks = Math.ceil(dataLen / CHUNK);
  const payload = new Uint8Array(totalChunks * CHUNK).fill(0xff);
  payload.set(header, 0);
  frames.forEach((f, i) => payload.set(f.data, header.length + i * FRAME_PX));
  const gohv = variant === "gohv";
  const startEnable = gohv ? 0x01 : 0x00, cfgSub = gohv ? 0x02 : 0x03;
  const before = [
    { ...step("START", "feature", cmdPacket(0x18, 0x00, startEnable), 0), handshake: true },        // handshake without sleep
    { ...step("IMAGE_CFG", "feature", imageCfgPacket(totalChunks, cfgSub), 0), handshake: true },
  ];
  const after = gohv ? [{ ...step("FINISH", "feature", cmdPacket(0xf0, 0x00, 0x01), 0), handshake: true }]
                     : [{ ...step("SAVE", "feature", cmdPacket(0x02, 0x00, 0x00), 0), handshake: true }];
  return {
    variant, delaysMs, frames: frames.length, totalChunks, payloadLength: payload.length,
    headerHex: hex(header), payloadSha256: createHash("sha256").update(payload).digest("hex"),
    firstChunkHex64: hex(payload.subarray(0, 64)), lastChunkSha256: createHash("sha256").update(payload.subarray((totalChunks - 1) * CHUNK)).digest("hex"),
    before, after, chunkAckWaitMs: 300, chunkBytes: CHUNK,
  };
}

const result = {
  source: "kabelklaus/ajazz-ak820-pro app.js (MIT)", appJsSha256: appSha,
  framing: { reportId: 0, featureDataBytes: 64, outputDataBytes: 4096, defaults: { saveByte8: 0, saveFinish: false, imgVariant: "aks075", brightness: 5, speed: 3, maxFrames: 120 } },
  pixelPattern: "r=(2x+10i)&255 g=(2y+3i)&255 b=(3(x+y)+7i)&255",
  lighting: [
    { params: { mode: 1, r: 255, g: 69, b: 0, rainbow: false, bright: 5, speed: 3, dir: 0, saveByte8: 0, finish: false }, seq: lighting({ mode: 1, r: 255, g: 69, b: 0, rainbow: false, bright: 5, speed: 3, dir: 0, saveByte8: 0, finish: false }) },
    { params: { mode: 0x13, r: 1, g: 2, b: 3, rainbow: true, bright: 2, speed: 5, dir: 3, saveByte8: 1, finish: true }, seq: lighting({ mode: 0x13, r: 1, g: 2, b: 3, rainbow: true, bright: 2, speed: 5, dir: 3, saveByte8: 1, finish: true }) },
    { params: { mode: 0, r: 0, g: 0, b: 0, rainbow: false, bright: 0, speed: 0, dir: 1, saveByte8: 0, finish: false }, seq: lighting({ mode: 0, r: 0, g: 0, b: 0, rainbow: false, bright: 0, speed: 0, dir: 1, saveByte8: 0, finish: false }) },
  ],
  clock: [
    { date: [2026, 10, 9, 13, 5, 7], saveByte8: 0, finish: false, seq: timeSync(new Date(2026, 9, 9, 13, 5, 7), 0, false) },
    { date: [2031, 12, 31, 23, 59, 58], saveByte8: 1, finish: true, seq: timeSync(new Date(2031, 11, 31, 23, 59, 58), 1, true) },
  ],
  standby: [0, 1, 2, 3].map((l) => ({ level: l, saveByte8: 0, finish: false, seq: standby(l, 0, false) })),
  images: [
    image({ delaysMs: [0], variant: "aks075" }),
    image({ delaysMs: [100, 250, 7, 1000], variant: "aks075" }),
    image({ delaysMs: [100, 250, 7, 1000], variant: "gohv" }),
    image({ delaysMs: Array.from({ length: 30 }, (_, i) => 40 + i * 13), variant: "aks075" }),
  ],
};
writeFileSync(out, JSON.stringify(result, null, 1));
console.log("golden vectors written:", out, "lighting:", result.lighting.length, "images:", result.images.length);
