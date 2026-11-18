// 模拟“进程存在但一直不监听端口”的 dsh web，用于验证 60 秒启动超时路径。
console.log('mock dsh bootstrap: resolving packages');
console.error('mock dsh stderr: simulated startup error line 1');
console.error('mock dsh stderr: simulated startup error line 2');
setTimeout(() => process.exit(0), 60000);
