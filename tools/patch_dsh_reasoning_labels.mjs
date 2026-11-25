// Local compatibility patch for DSH 0.2.0-rc.2. Prepare a separate ASAR;
// keep the original archive as a backup before deploying the result.
import fs from 'node:fs';
import crypto from 'node:crypto';
const [source, output] = process.argv.slice(2);
if (!source || !output || source === output) throw new Error('Usage: node patch_dsh_reasoning_labels.mjs source.asar output.asar');
const archive = fs.readFileSync(source);
const header = JSON.parse(archive.subarray(16, 16 + archive.readUInt32LE(12)).toString());
const base = 8 + archive.readUInt32LE(4);
const path = 'dsh/node_modules/@deepseek-ai/dsh-client-ui-model-selection/lib/client.js';
let entry = header;
for (const part of path.split('/')) entry = entry.files[part];
let client = archive.subarray(base + Number(entry.offset), base + Number(entry.offset) + entry.size).toString();
function replaceOnce(from, to) {
  if (client.split(from).length !== 2) throw new Error('DSH source changed; refusing patch: ' + from);
  client = client.replace(from, to);
}
replaceOnce('const reasoning = currentChoice?.model.reasoning;', `const reasoning = currentChoice?.model.reasoning;
            const petThinkingSwitch = currentChoice?.selection.provider === "pet-local"
                && /^qwen3\\.5:/.test(currentChoice?.model.id ?? "")
                && reasoning?.efforts.length === 2
                && reasoning.efforts.every((effort) => effort.id === "off" || effort.id === "high");
            const petEffortName = (id, name) => petThinkingSwitch
                ? (id === void 0 ? "默认" : id === "off" ? "关闭" : id === "high" ? "开启" : name)
                : name;`);
replaceOnce('effectiveEffort === void 0 ? t("effort.providerDefault") : reasoning.efforts.find((level) => level.id === effectiveEffort)?.name ?? effectiveEffort;', 'petEffortName(effectiveEffort, effectiveEffort === void 0 ? t("effort.providerDefault") : reasoning.efforts.find((level) => level.id === effectiveEffort)?.name ?? effectiveEffort);');
replaceOnce('label: t("effort.providerDefault")', 'label: petEffortName(void 0, t("effort.providerDefault"))');
replaceOnce('label: effort.name', 'label: petEffortName(effort.id, effort.name)');
replaceOnce('}))], [reasoning, t]);', '}))], [reasoning, t, petThinkingSwitch]);');
const replacement = Buffer.from(client);
const chunks = [];
let offset = 0;
function walk(dir) {
  for (const item of Object.values(dir.files ?? {})) {
    if (item.files) { walk(item); continue; }
    if (item.offset === undefined || item.unpacked) continue;
    const data = item === entry ? replacement : archive.subarray(base + Number(item.offset), base + Number(item.offset) + item.size);
    item.offset = String(offset);
    item.size = data.length;
    if (item === entry && item.integrity) {
      const hash = (b) => crypto.createHash('sha256').update(b).digest('hex');
      const blockSize = item.integrity.blockSize;
      const blocks = [];
      for (let p = 0; p < data.length; p += blockSize) blocks.push(hash(data.subarray(p, p + blockSize)));
      item.integrity = { algorithm: 'SHA256', hash: hash(data), blockSize, blocks };
    }
    chunks.push(data);
    offset += data.length;
  }
}
walk(header);
const json = Buffer.from(JSON.stringify(header));
const padded = Math.ceil((4 + json.length) / 4) * 4;
const prefix = Buffer.alloc(12 + padded);
prefix.writeUInt32LE(4, 0);
prefix.writeUInt32LE(4 + padded, 4);
prefix.writeUInt32LE(padded, 8);
prefix.writeUInt32LE(json.length, 12);
json.copy(prefix, 16);
fs.writeFileSync(output, Buffer.concat([prefix, ...chunks]));
console.log('Prepared Qwen thinking labels: 默认 / 关闭 / 开启');
