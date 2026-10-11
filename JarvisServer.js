const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');

const host = '127.0.0.1';
const port = 48371;
const page = path.join(__dirname, 'Jarvis.html');

function openProgram(program, args = []) {
  const child = spawn(program, args, { detached: true, stdio: 'ignore' });
  child.unref();
}

function send(response, status, body, contentType = 'text/plain; charset=utf-8') {
  response.writeHead(status, {
    'Content-Type': contentType,
    'X-Content-Type-Options': 'nosniff',
    'Cache-Control': 'no-store'
  });
  response.end(body);
}

const server = http.createServer((request, response) => {
  if (request.method === 'GET' && request.url === '/') {
    fs.readFile(page, (error, content) => {
      if (error) return send(response, 500, 'Could not load the assistant page');
      send(response, 200, content, 'text/html; charset=utf-8');
    });
    return;
  }

  if (request.method !== 'POST' || request.url !== '/api/action') {
    return send(response, 404, 'Not found');
  }

  let body = '';
  request.on('data', chunk => {
    body += chunk;
    if (body.length > 4096) request.destroy();
  });
  request.on('end', () => {
    let data;
    try { data = JSON.parse(body); }
    catch { return send(response, 400, 'Invalid request'); }

    switch (data.action) {
      case 'notepad': openProgram('notepad.exe'); break;
      case 'calculator': openProgram('explorer.exe', ['shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App']); break;
      case 'files': openProgram('explorer.exe'); break;
      case 'browser': openProgram('rundll32.exe', ['url.dll,FileProtocolHandler', 'https://www.google.com']); break;
      case 'search': {
        const query = String(data.query || '').trim().slice(0, 300);
        if (!query) return send(response, 400, 'Search query required');
        const url = `https://www.google.com/search?q=${encodeURIComponent(query)}`;
        openProgram('rundll32.exe', ['url.dll,FileProtocolHandler', url]);
        break;
      }
      default: return send(response, 400, 'Unknown action');
    }
    send(response, 200, 'OK');
  });
});

server.on('error', error => {
  console.error(`Ashley could not start: ${error.message}`);
  process.exitCode = 1;
});

server.listen(port, host, () => {
  const address = `http://${host}:${port}/`;
  console.log(`Ashley is ready at ${address}`);
  openProgram('rundll32.exe', ['url.dll,FileProtocolHandler', address]);
  console.log('Keep this window open while you use Ashley. Press Ctrl+C to stop it.');
});
