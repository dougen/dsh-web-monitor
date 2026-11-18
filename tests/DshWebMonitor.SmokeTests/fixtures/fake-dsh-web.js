const http = require('http');

const portIndex = process.argv.indexOf('--port');
const port = portIndex >= 0 ? Number(process.argv[portIndex + 1]) : 31801;

const server = http.createServer((request, response) => {
  response.writeHead(200, { 'Content-Type': 'text/plain' });
  response.end('mock dsh web');
});

server.listen(port, '127.0.0.1', () => {
  console.log('mock dsh web listening on 127.0.0.1:' + port);
});
