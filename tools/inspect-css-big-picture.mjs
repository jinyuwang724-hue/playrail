import fs from 'node:fs';
const targets = await (await fetch('http://127.0.0.1:8080/json/list')).json();
const target = targets.find(t => /大屏幕|Big Picture/.test(t.title));
if (!target) throw new Error('Steam Big Picture is not open');
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolve, reject) => {ws.addEventListener('open', resolve, {once:true}); ws.addEventListener('error', reject, {once:true});});
const expression = fs.readFileSync(process.argv[2], 'utf8');
const result = await new Promise((resolve, reject) => {
  const timer = setTimeout(() => {ws.close(); reject(new Error('Steam CSS inspection timed out'));}, 10000);
  ws.addEventListener('message', e => {const m=JSON.parse(e.data); if(m.id===1){clearTimeout(timer);resolve(m);}});
  ws.send(JSON.stringify({id:1,method:'Runtime.evaluate',params:{expression,awaitPromise:true,returnByValue:true}}));
});
ws.close();
if(result.result?.exceptionDetails) throw new Error(result.result.exceptionDetails.text);
console.log(JSON.stringify(result.result?.result?.value ?? result));
