// 裸像素扫描器原型：验证规则能否抓住真实缺陷向量（tianshu 在 council 指出的 const/-44/Pen/Font/Point 等）
// 规则：剥离注释与字符串后，任何非豁免数字字面量都算违规。
// 豁免：0 / 1 / 0.0 / 1.0（以及 0f/1f）；0x 十六进制；行尾 `// metrics-ok: <理由>`
import { readFileSync } from 'node:fs';

const EXEMPT = new Set(['0', '1', '0.0', '1.0', '0f', '1f', '0.0f', '1.0f']);
const ALLOW_MARK = /\/\/\s*metrics-ok\b/;

function stripNoise(src) {
  // 去块注释
  let s = src.replace(/\/\*[\s\S]*?\*\//g, m => m.replace(/[^\n]/g, ' '));
  // 去行注释（保留行尾标记检测所需信息由原行另行处理）
  s = s.replace(/\/\/[^\n]*/g, m => ' '.repeat(m.length));
  // 去字符串（逐行处理避免跨行误伤，含 $"" @"")
  s = s.split('\n').map(line => line.replace(/\$?@?"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*'/g, '""')).join('\n');
  return s;
}

function scan(file) {
  const raw = readFileSync(file, 'utf8');
  const rawLines = raw.split('\n');
  const clean = stripNoise(raw);
  const lines = clean.split('\n');
  const violations = [];
  lines.forEach((line, i) => {
    if (ALLOW_MARK.test(rawLines[i] ?? '')) return;
    const re = /(?<![\w.$])(\d+(?:\.\d+)?)([fFdDmM])?(?![\w.])/g;
    let m;
    while ((m = re.exec(line)) !== null) {
      const lit = m[1] + (m[2] ?? '');
      if (EXEMPT.has(lit)) continue;
      // 排除 0x 十六进制（正则已剥离 x，这里看前一个字符）
      const before = line.slice(Math.max(0, m.index - 2), m.index);
      if (/0x$/i.test(before)) continue;
      violations.push({ line: i + 1, lit, text: (rawLines[i] ?? '').trim().slice(0, 120) });
    }
  });
  return violations;
}

const file = process.argv[2];
if (!file) { console.error('usage: node rawpixel-scan.mjs <file.cs>'); process.exit(2); }
const v = scan(file);
console.log(`file=${file}`);
console.log(`violations=${v.length}`);
for (const x of v) console.log(`  L${x.line}  literal=${x.lit}  | ${x.text}`);
process.exit(v.length > 0 ? 1 : 0);
