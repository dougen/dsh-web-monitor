// 模拟启动即失败的 dsh web，用于验证进程提前退出路径。
console.error('mock dsh fatal: cannot bind 127.0.0.1:0 (EADDRINUSE)');
process.exit(3);
