// Run with the installed OpenCode Electron executable and ELECTRON_RUN_AS_NODE=1.
// This starts a finite client acceptance instance; it never edits the user's OpenCode config.
import { EventEmitter } from 'node:events';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
const [sidecar, root] = process.argv.slice(2);
if (!sidecar || !root) throw new Error('Expected sidecar.js and isolated acceptance directory');
process.env.OPENCODE_CONFIG = path.join(root, 'opencode.json');
process.env.OPENCODE_DISABLE_TELEMETRY = 'true';
const port = new EventEmitter(); process.parentPort = port;
const api = 'http://127.0.0.1:11438';
let finish;
const complete = new Promise(resolve => { finish = resolve; });
port.postMessage = async message => {
  if (message.type === 'error') { console.error(message.message ?? 'sidecar error'); finish(1); }
  if (message.type !== 'ready') return;
  try {
    const health = await fetch(api + '/global/health'); if (!health.ok) throw new Error('health failed');
    const config = await (await fetch(api + '/config')).json();
    if (!config.provider?.['pet-local']?.models?.['qwen3.5:9b']) throw new Error('managed provider missing');
    if (config.provider['pet-local'].models['qwen3.5:9b'].limit.context !== 65536) throw new Error('context budget mismatch');
    console.log('PASS OpenCode accepts generated provider and context budget');
    const session = await (await fetch(api + '/session', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({title:'Pet local plugin acceptance'})})).json();
    const response = await fetch(`${api}/session/${session.id}/message`, {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({model:{providerID:'pet-local',modelID:'qwen3.5:9b'}, parts:[{type:'text',text:`Use the read tool to read ${path.join(root, 'acceptance.txt').replaceAll('\\','/')}, then reply with the exact text from that file. Do not modify files or call other tools.`}]})});
    if (!response.ok) throw new Error('message failed ' + response.status);
    const result = await response.json();
    const messages = await (await fetch(`${api}/session/${session.id}/message`)).json();
    fs.writeFileSync(path.join(root,'opencode-events.json'), JSON.stringify(messages,null,2));
    const parts = messages.flatMap(m => m.parts ?? []);
    if (!parts.some(p => p.type === 'tool' && p.tool === 'read' && p.state?.status === 'completed')) throw new Error('read tool did not complete');
    if (!parts.some(p => p.type === 'text' && p.text.includes('PET_LOCAL_PLUGIN_TEST_OK'))) throw new Error('final text missing');
    console.log('PASS installed OpenCode streaming tool round trip and final text'); finish(0);
  } catch (error) { console.error(error.message); finish(1); }
};
await import(pathToFileURL(sidecar).href);
port.emit('message',{data:{type:'start',hostname:'127.0.0.1',port:11438,password:'',userDataPath:path.join(root,'opencode-data')}});
const timeout = setTimeout(() => { console.error('client acceptance timed out'); finish(1); },150000);
const result = await complete; clearTimeout(timeout);
port.emit('message',{data:{type:'stop'}});
await new Promise(resolve => setTimeout(resolve,1000));
process.exit(result);
