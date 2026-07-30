// JavaScript source code
const fs = require('fs');
const path = require('path');
const http = require('http');

const SERVER_IP = '192.168.0.101';
const SERVER_PORT = 11434;
const MODEL = 'hf.co/nerkyor/Qwen3.5-122B-A10B-44GB-GPT5.6Sol-SFT-LynnStyle-GGUF';

function getAllFiles(dirPath, arrayOfFiles = []) {
    const files = fs.readdirSync(dirPath);
    files.forEach(file => {
        const fullPath = path.join(dirPath, file);
        if (fs.statSync(fullPath).isDirectory()) {
            if (file !== 'bin' && file !== 'obj' && file !== '.git') {
                getAllFiles(fullPath, arrayOfFiles);
            }
        } else if (['.cs', '.md'].includes(path.extname(file)) && file !== 'qwen.md') {
            arrayOfFiles.push(fullPath);
        }
    });
    return arrayOfFiles;
}

let instructions = 'Provide a general code optimization review.';
if (fs.existsSync('./qwen.md')) {
    instructions = fs.readFileSync('./qwen.md', 'utf8');
}

console.log('[PROCESSING] Ingesting source files and documentation...');
const targetFiles = getAllFiles('.');
let codebaseText = '';
targetFiles.forEach(file => {
    codebaseText += `\n--- File: ${path.basename(file)} ---\n` + fs.readFileSync(file, 'utf8');
});

const payload = JSON.stringify({
    model: MODEL,
    prompt: instructions + '\n\n' + codebaseText,
    stream: true,
    options: {
        think: false
    }
});


console.log('[SENDING] Uploading code array to 110Gi server pool...');
const req = http.request({
    hostname: SERVER_IP,
    port: SERVER_PORT,
    path: '/api/generate',
    method: 'POST',
    headers: {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(payload)
    }
}, (res) => {
    res.setEncoding('utf8');
    res.on('data', (chunk) => {
        const lines = chunk.split('\n');
        lines.forEach(line => {
            if (line.trim()) {
                try {
                    const json = JSON.parse(line);
                    if (json.response) process.stdout.write(json.response);
                } catch (e) { }
            }
        });
    });
});

req.on('error', (e) => console.error(`\n[NETWORK ERROR] ${e.message}`));
req.write(payload);
req.end();
