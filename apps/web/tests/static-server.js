// Test fixture for the exact packaged site under a GitHub Pages-style subpath.
const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const root = path.resolve(__dirname, '../dist/site');
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png' };
http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1:3102');
  if (!url.pathname.startsWith('/Streamed/')) { res.writeHead(404); res.end(); return; }
  const file = path.resolve(root, decodeURIComponent(url.pathname.slice('/Streamed/'.length)) || 'index.html');
  if (!file.startsWith(root + path.sep)) { res.writeHead(403); res.end(); return; }
  try {
    const data = await fs.readFile(file);
    res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream' });
    res.end(data);
  } catch { res.writeHead(404); res.end(); }
}).listen(3102, '127.0.0.1');
