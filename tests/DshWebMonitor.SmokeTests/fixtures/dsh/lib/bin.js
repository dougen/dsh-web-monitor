// 模拟 dsh web 的监听进程 fixture。
// 路径刻意保持为 `.../dsh/lib/bin.js` 且带 `web` 子命令，
// 以便 PortProbe.IsDshWebNode 的真实身份规则能识别它（R-02 回归路径），
// 同时也覆盖「监控器自己启动的进程后代」分支。
const http = require('http');

function readPort(defaultPort) {
  const portIndex = process.argv.indexOf('--port');
  if (portIndex >= 0) {
    const parsed = Number(process.argv[portIndex + 1]);
    if (Number.isInteger(parsed) && parsed > 0) {
      return parsed;
    }
  }

  return defaultPort;
}

const port = readPort(31810);

const server = http.createServer((request, response) => {
  response.writeHead(200, { 'Content-Type': 'text/plain' });
  response.end('mock dsh web');
});

server.on('error', (error) => {
  console.error('mock dsh web failed: ' + error.message);
  process.exit(1);
});

server.listen(port, '127.0.0.1', () => {
  console.log(`mock dsh web listening on 127.0.0.1:${port}`);
});

process.on('SIGTERM', () => {
  server.close(() => process.exit(0));
});
