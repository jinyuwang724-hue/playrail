import fs from 'node:fs';
import path from 'node:path';
import { once } from 'node:events';
const targets = await (await fetch('http://127.0.0.1:8080/json/list')).json();
const target = targets.find(target => target.title.startsWith('QuickAccess_'));
if (!target) throw Error('Open Steam Quick Access first');
const socket = new WebSocket(target.webSocketDebuggerUrl);
await once(socket, 'open');
let serial = 0;
async function rpc(method, params = {}) {
  const id = ++serial;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(Error('Panel inspection timed out')), 8000);
    const listener = event => {
      const reply = JSON.parse(event.data);
      if (reply.id !== id) return;
      clearTimeout(timer); socket.removeEventListener('message', listener);
      if (reply.error) reject(Error(reply.error.message)); else resolve(reply.result);
    };
    socket.addEventListener('message', listener); socket.send(JSON.stringify({ id, method, params }));
  });
}
try {
  const directory = path.resolve(import.meta.dirname, '../artifacts/scaling-validation');
  const result = await rpc('Runtime.evaluate', { expression: `({text:document.body.innerText,
    expanded:document.getElementById("claw-module-scaling")?.getAttribute("aria-expanded"),
    sliderCount:document.querySelectorAll("#claw-module-scaling-content [role=slider]").length})`, returnByValue: true });
  fs.writeFileSync(path.join(directory, 'panel-state.json'), JSON.stringify(result.result.value, null, 2));
  // Steam's separate QAM surface does not answer CDP screenshot requests on this build.
  // Keep the actual DOM/control report; never substitute a rendered mockup for a screenshot.
  console.log(JSON.stringify(result.result.value));
} finally { socket.close(); }
