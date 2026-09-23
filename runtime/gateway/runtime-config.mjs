import path from 'node:path';
import {fileURLToPath} from 'node:url';
import fs from 'node:fs/promises';

export function loopbackUrl(value, name) {
  const url = new URL(value);
  if (url.protocol !== 'http:' || !['localhost', '127.0.0.1', '[::1]'].includes(url.hostname) || url.username || url.password || url.search || url.hash || !['', '/'].includes(url.pathname)) {
    throw Error(`${name} must be an HTTP loopback origin without credentials or a path.`);
  }
  return url.origin;
}

export function readConfig(env = process.env) {
  if (Number(process.versions.node.split('.')[0]) < 22) throw Error('Node.js 22 or newer is required.');
  const defaultRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
  const appRoot = env.QWEN_APP_ROOT || defaultRoot;
  if (!path.isAbsolute(appRoot)) throw Error('QWEN_APP_ROOT must be an absolute directory.');
  const port = Number(env.QWEN_QUEUE_PORT || 18022);
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw Error('QWEN_QUEUE_PORT must be 1–65535.');
  const backend = (env.QWEN_BACKEND || 'vllm').toLowerCase();
  if (!['vllm', 'ninfer'].includes(backend)) throw Error('QWEN_BACKEND must be vllm or ninfer.');
  const model = env.QWEN_MODEL_ID || 'qwen3.8-27b';
  if (!model.trim() || /[\r\n]/.test(model)) throw Error('Invalid QWEN_MODEL_ID.');
  const absolute = (key, fallback) => {
    const value = env[key] || fallback;
    if (value && !path.isAbsolute(value)) throw Error(`${key} must be an absolute path.`);
    return value;
  };
  return {
    appRoot: path.resolve(appRoot), port, model, backend,
    backendFile: absolute('QWEN_BACKEND_FILE'),
    upstream: loopbackUrl(env.QWEN_UPSTREAM_URL || 'http://127.0.0.1:18021', 'QWEN_UPSTREAM_URL'),
    queueUrl: loopbackUrl(env.QWEN_QUEUE_URL || `http://127.0.0.1:${port}`, 'QWEN_QUEUE_URL'),
    gatewayRoot: absolute('QWEN_GATEWAY_DATA_DIR', path.join(appRoot, 'runtime', 'gateway')),
    workerRoot: absolute('QWEN_WORKER_DATA_DIR', path.join(appRoot, 'runtime', 'worker')),
    workDir: absolute('QWEN_WORK_DIR', path.join(appRoot, 'work')),
    hermes: {
      python: absolute('QWEN_HERMES_PYTHON'), root: absolute('QWEN_HERMES_ROOT'),
      home: absolute('QWEN_HERMES_HOME'), bash: absolute('QWEN_HERMES_GIT_BASH'),
    },
  };
}

export async function readBackend(config) {
  if(!config.backendFile)return config.backend;
  let file;
  try{file=await fs.open(config.backendFile,'r');}
  catch(error){if(error.code==='ENOENT')return config.backend;throw Error('Cannot read QWEN_BACKEND_FILE: '+error.code);}
  try{
    const buffer=Buffer.alloc(65),{bytesRead}=await file.read(buffer,0,65,0);
    const backend=buffer.subarray(0,bytesRead).toString('utf8').trim().toLowerCase();
    if(bytesRead>64||!['vllm','ninfer'].includes(backend))throw Error('QWEN_BACKEND_FILE must contain only vllm or ninfer (up to 64 bytes).');
    return backend;
  }finally{await file.close();}
}
