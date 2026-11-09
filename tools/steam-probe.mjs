import fs from 'node:fs';
const targets = await (await fetch('http://127.0.0.1:8080/json/list')).json();
const title = process.argv[2] === '--target-title' ? process.argv[3] : null;
const target = title ? targets.find(t => t.title === title) : targets.find(t => t.title === 'SharedJSContext') ?? targets.find(t => t.title.includes('大屏幕'));
if (!target) throw new Error('Steam shared context not available');
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { ws.addEventListener('open',resolve,{once:true});ws.addEventListener('error',reject,{once:true}); });
// Query through the existing router. Calling connect() again replaces live plugin listeners.
const expression = title ? (process.argv[4] === '--file' ? fs.readFileSync(process.argv[5], 'utf8') : process.argv[4]) : process.argv[2] === '--plugin-state'
  ? 'window.DeckyBackend.call("loader/call_plugin_method", window.DeckyPluginLoader.plugins.find(p => p.name === "Claw 掌机工具箱" || p.name === "Claw 按键切换").name, "get_state")'
  : process.argv[2] === '--file' ? fs.readFileSync(process.argv[3], 'utf8') : process.argv[2];
const result = await new Promise((resolve,reject) => {
  const timeout=setTimeout(() => reject(new Error('Steam inspection timed out')),8000);
  ws.addEventListener('message', event => { const message=JSON.parse(event.data);if(message.id===1){clearTimeout(timeout);resolve(message);} });
  ws.send(JSON.stringify({id:1,method:'Runtime.evaluate',params:{expression,awaitPromise:true,returnByValue:true}}));
});
ws.close();
if (result.error) throw new Error(result.error.message ?? 'Steam inspection failed');
if (result.result?.exceptionDetails) throw new Error(result.result.exceptionDetails.exception?.description ?? result.result.exceptionDetails.text);
console.log(JSON.stringify(result.result ?? result));
