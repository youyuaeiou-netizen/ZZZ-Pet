// Deterministic browser-client behavior with a tiny DOM fixture. No network or native system actions.
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
class Element {
  constructor(tag) { this.tag = tag; this.children = []; this.style = {}; this.textContent = ''; this.className = ''; this.parent = null; }
  append(...children) { for (const child of children) { child.parent = this; this.children.push(child); } }
  replaceChildren(...children) { for (const child of this.children) child.parent = null; this.children = []; this.append(...children); }
  get isConnected() { return this === root || Boolean(this.parent?.isConnected); }
}
const root = new Element('body'), main = new Element('main'), heading = new Element('h1'), rows = new Element('div'), status = new Element('small');
root.append(main); main.append(heading, rows, status);
function find(node, cls) { return [...(node.className === cls ? [node] : []), ...node.children.flatMap(child => find(child, cls))]; }
let time = 0, callbacks = [], online = true, value = 25;
const data = () => ({ Language: 'en', Title: 'Pet · fixture', Disconnected: 'Disconnected', Timestamp: new Date().toISOString(),
  Background: '#FFFBD8', Foreground: '#55416B', FontFamily: 'sans-serif', FontSize: 13, PanelWidth: 350, RowSpacing: 4,
  Visual: { Padding: 22, CornerRadius: 12, TitleSize: 16, TitleColor: '#55416B', ShowGroups: true, GroupPadding: 8, GroupRadius: 8,
    GroupSpacing: 8, GroupColor: '#55416B', GroupSize: 13, Bold: true, ValueFamily: 'monospace', ValueSize: 16,
    ShowBars: true, BarBackground: '#DDDDEE', SmoothValues: true, SmoothMs: 2000 },
  Items: [{ Id: 'CPU.Load', DeviceId: 'CPU', Name: '<script>untrusted label</script>', Value: value, Unit: '%', Display: value + ' %',
    Color: '#26735A', Bar: value, BarColor: '#26735A' }] });
const context = vm.createContext({ URLSearchParams, location: { search: '?session=fixture-only' }, performance: { now: () => time },
  document: { documentElement: {}, body: root, createElement: tag => new Element(tag), querySelector: selector => selector === 'main' ? main : heading,
    getElementById: id => id === 'rows' ? rows : status, querySelectorAll: selector => find(root, selector.slice(1)) },
  fetch: async () => { if (!online) throw Error('fixture disconnected'); return { ok: true, json: async () => data() }; },
  requestAnimationFrame: fn => callbacks.push(fn), setInterval: () => {} });
async function frame(at) { time = at; const pending = callbacks; callbacks = []; for (const fn of pending) fn(at); await Promise.resolve(); }
(async () => {
  vm.runInContext(fs.readFileSync(process.argv[2], 'utf8'), context);
  await context.update();
  assert.equal(find(root, 'value')[0].textContent, '25 %');
  assert.equal(find(root, 'label')[0].textContent, '<script>untrusted label</script>');
  assert.equal(find(root, 'group').length, 1);
  assert.equal(find(root, 'fill')[0].style.width, '25%');
  value = 75; await context.update(); await frame(100);
  assert.notEqual(find(root, 'value')[0].textContent, '75 %');
  online = false; await context.update(); await frame(2000);
  assert.equal(find(root, 'value')[0].textContent, 'Disconnected', 'in-flight animation must not restore stale data after disconnect');
  assert.equal(find(root, 'bar')[0].hidden, true);
  online = true; value = 42; await context.update(); await frame(2200);
  assert.equal(find(root, 'value')[0].textContent, '42 %', 'reconnect must start with raw current value rather than old animation state');
  console.log('PASS 8 web-client behavior checks (DOM fixture; no browser pixel acceptance)');
})().catch(error => { console.error(error); process.exitCode = 1; });
