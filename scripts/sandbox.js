const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const { buildWeb } = require('../apps/web/scripts/build-web');

async function createSandbox() {
  const { site } = await buildWeb();
  const fixture = JSON.parse(await fs.readFile(path.join(site, 'data/live-demo.json'), 'utf8'));
  const apiMatches = fixture.matches.map((match, index) => ({
    id: match.id, title: match.title, category: match.sport, date: Date.now() - 60000,
    popular: index < 3, sources: [{ source: 'demo', id: match.id }]
  }));
  const mime = { '.html': 'text/html', '.css': 'text/css', '.js': 'text/javascript', '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png' };
  return http.createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://sandbox.invalid');
      const json = data => { res.writeHead(200, { 'Content-Type': 'application/json', 'Access-Control-Allow-Origin': '*' }); res.end(JSON.stringify(data)); };
      if (url.pathname === '/health') return json({ status: 'ok', mode: 'sandbox', authentication: 'disabled-fixtures-only' });
      if (url.pathname === '/api/sports') return json(fixture.sports);
      if (url.pathname.startsWith('/api/matches/')) {
        const sport = url.pathname.split('/')[3];
        return json(fixture.sports.some(item => item.id === sport) ? apiMatches.filter(match => match.category === sport) : apiMatches);
      }
      if (url.pathname.startsWith('/api/stream/demo/')) {
        const match = fixture.matches.find(item => item.id === decodeURIComponent(url.pathname.slice('/api/stream/demo/'.length)));
        return json((match?.streams || []).map((stream, index) => ({ id: `${match.id}-${index}`, streamNo: index + 1, language: 'English', hd: false, source: 'demo', embedUrl: stream.url })));
      }
      if (url.pathname === '/v1/config') return json({ streamedBaseUrl: '/', useBackendStreamProxy: false, sandbox: true });
      const file = path.resolve(site, decodeURIComponent(url.pathname).replace(/^\/+/, '') || 'index.html');
      if (!file.startsWith(site + path.sep)) { res.writeHead(403); res.end(); return; }
      const data = await fs.readFile(file);
      res.writeHead(200, { 'Content-Type': mime[path.extname(file)] || 'application/octet-stream' }); res.end(data);
    } catch { res.writeHead(404); res.end('Not found'); }
  });
}

if (require.main === module) {
  createSandbox().then(server => server.listen(Number(process.env.SANDBOX_PORT || 8080), process.env.SANDBOX_HOST || '127.0.0.1', () => {
    console.log('Fixture sandbox running; web UI at /, catalog API at /api/, health at /health.');
  })).catch(error => { console.error(error.message); process.exitCode = 1; });
}
module.exports = { createSandbox };
