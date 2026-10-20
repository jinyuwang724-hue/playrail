// Local maintenance for when Steam's debugging context is unavailable.
// Uses Decky's authenticated backend API; never prints the authentication token.
const [route, argumentsJson = '[]'] = process.argv.slice(2);
const allowed = new Set(['loader/get_plugins', 'loader/call_plugin_method',
  'loader/call_legacy_plugin_method', 'utilities/uninstall_plugin']);
if (!allowed.has(route)) throw new Error('Unsupported maintenance route');
const args = JSON.parse(argumentsJson);
if (!Array.isArray(args)) throw new Error('Arguments must be a JSON array');
const auth = await fetch('http://127.0.0.1:1337/auth/token', {
  signal: AbortSignal.timeout(5000),
}).then(response => {
  if (!response.ok) throw new Error(`Decky authentication HTTP ${response.status}`);
  return response.text();
});
const socket = new WebSocket(`ws://127.0.0.1:1337/ws?auth=${encodeURIComponent(auth)}`);
try {
  const result = await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('Decky maintenance call timed out')), 45000);
    socket.addEventListener('error', () => {
      clearTimeout(timer); reject(new Error('Decky backend connection failed'));
    }, { once: true });
    socket.addEventListener('open', () => {
      socket.send(JSON.stringify({ type: 4, messages: [] }));
      socket.send(JSON.stringify({ type: 0, route, args, id: 1 }));
    }, { once: true });
    socket.addEventListener('message', event => {
      const reply = JSON.parse(event.data);
      if (reply.id !== 1 || ![1, -1, 2].includes(reply.type)) return;
      clearTimeout(timer);
      socket.send(JSON.stringify({ type: 3, id: 1 }));
      if (reply.type === 1) resolve(reply.result);
      else reject(new Error(reply.error?.error ?? 'Decky rejected the maintenance call'));
    });
  });
  console.log(JSON.stringify(result));
} finally {
  socket.close();
}
