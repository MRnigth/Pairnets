// node --test "scripts/deploy-gate/*.test.mjs"
// scripts/check-secrets.sh and the git pre-push hook against a throwaway repository with a local "GitHub" (a bare
// repository). The fake keys are put together at run time, so this file itself has nothing for the scanner to find.
import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const scanner = path.join(here, '..', 'check-secrets.sh');

// Git's own bash: on Windows a plain "bash" can be WSL's, which can't run this.
function findBash() {
  if (process.platform !== 'win32') return 'bash';
  const exec = spawnSync('git', ['--exec-path'], { encoding: 'utf8', windowsHide: true }).stdout.trim();
  const gitRoot = path.resolve(exec, '..', '..', '..');
  return [path.join(gitRoot, 'bin', 'bash.exe'), path.join(gitRoot, 'usr', 'bin', 'bash.exe')].find(f => fs.existsSync(f));
}
const bash = findBash();
const skip = !bash && 'no Git Bash here';

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pn-secrets-'));
const repo = path.join(root, 'repo');
const origin = path.join(root, 'origin.git');
const home = path.join(root, 'home');

function git(cwd, args, env = {}) {
  return spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true, env: { ...process.env, ...env } });
}
function run(cwd, ...args) {
  const r = git(cwd, args);
  if (r.status !== 0) throw new Error(`git ${args.join(' ')}: ${r.stderr}`);
  return r.stdout.trim();
}
function write(file, text) {
  fs.mkdirSync(path.dirname(path.join(repo, file)), { recursive: true });
  fs.writeFileSync(path.join(repo, file), text);
}
function commit(file, text, message = file) {
  write(file, text);
  run(repo, 'add', '-A');
  run(repo, 'commit', '-q', '-m', message);
}
const scan = (...args) => spawnSync(bash, [scanner, ...args], { cwd: repo, encoding: 'utf8', windowsHide: true });

// A made-up string of exactly n letters and digits.
const chars = n => 'Ab3Xk9Qz'.repeat(Math.ceil(n / 8)).slice(0, n);

// One fake of every kind of secret the scanner knows.
const fakes = {
  'GitHub token': 'gh' + 'p_' + chars(36),
  'GitHub fine-grained token': 'github' + '_pat_' + chars(30),
  'Google API key': 'AI' + 'za' + chars(35),
  'Google OAuth client secret': 'GOC' + 'SPX-' + chars(28),
  'AWS key': 'AK' + 'IA' + 'ABCDEFGH23456789',
  'Stripe live key': 'sk' + '_live_' + chars(24),
  'Slack token': 'xo' + 'xb-' + '1234-5678-abcdef',
  'SendGrid key': 'SG' + '.' + chars(22) + '.' + chars(43),
  'npm token': 'np' + 'm_' + chars(36),
  'Tailscale key': 'tsk' + 'ey-auth-' + chars(20),
  'Resend key': 're' + '_' + chars(8) + '_' + chars(20),
  'Turnstile secret': '0x4' + 'AAAAAAA' + chars(20),
  'Cloudflare tunnel token': 'eyJh' + 'Ijoi' + chars(40),
  'signed token': 'ey' + 'J' + chars(20) + '.ey' + 'J' + chars(20) + '.' + chars(20),
  'Pairnets computer key': 'pn' + '_' + chars(43),
  'password in a URL': 'https://' + 'admin:' + 'hunter22' + '@sync.pairnets.dev/',
  'private key': '-----BEGIN ' + 'PRIVATE KEY-----',
  'JSON Web Key private part': '"d"' + ': "' + chars(43) + '"',
  '64-hex token': 'a1b2c3d4'.repeat(8),
  'VPN address': '100.' + '101.2.3',
  'Windows profile path': 'C:' + '\\Users\\' + 'someone\\Documents',
};

before(() => {
  if (skip) return;
  run(root, 'init', '-q', '--bare', '-b', 'main', origin);
  fs.mkdirSync(repo);
  run(repo, 'init', '-q', '-b', 'main');
  run(repo, 'config', 'user.name', 't');
  run(repo, 'config', 'user.email', 't@example.com');
  run(repo, 'config', 'core.autocrlf', 'false');
  run(repo, 'remote', 'add', 'origin', origin);
  commit('README.md', 'hello\n');
  run(repo, 'push', '-q', 'origin', 'main');
});

test('every kind of secret is caught', { skip }, () => {
  write('leak.txt', Object.values(fakes).map(v => `value = ${v}`).join('\n') + '\n');
  run(repo, 'add', 'leak.txt');
  const r = scan('--diff');
  run(repo, 'reset', '-q');
  fs.rmSync(path.join(repo, 'leak.txt'));
  assert.equal(r.status, 1, r.stdout);
  for (const [kind, value] of Object.entries(fakes)) assert.ok(r.stderr.includes(value), `${kind} was not caught`);
});

test('made-up examples, marked lines and test vectors pass', { skip }, () => {
  commit('allowed.txt', [
    'const url = "https://user:pass@nest.example.com/";',
    'Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Hash.Of([]));',
    `const key = "${fakes['Pairnets computer key']}"; // check-secrets: allow`,
    'public const string KeyPrefix = "pn_";',
    'expect(key).toMatch(/^pn_/);',
    'The range 100.64.0.0/10 is for VPNs.',
  ].join('\n') + '\n');
  commit('cloud/test/vectors/golden.json', `{ "jws": "${fakes['signed token']}" }\n`);
  for (const mode of ['--tree', '--history']) {
    const r = scan(mode);
    assert.equal(r.status, 0, `${mode}: ${r.stderr}`);
  }
});

test('--range scans exactly the commits a push would send, messages too', { skip }, () => {
  run(repo, 'push', '-q', 'origin', 'main');
  assert.equal(scan('--range', 'HEAD', '--not', '--remotes').status, 0, 'nothing new');
  commit('clean.txt', 'nothing to see\n');
  assert.equal(scan('--range', 'HEAD', '--not', '--remotes').status, 0, 'a clean commit');
  commit('notes.txt', 'still nothing\n', `notes, with ${fakes['GitHub token']} in the message`);
  const r = scan('--range', 'HEAD', '--not', '--remotes');
  assert.equal(r.status, 1, 'a key in a commit message');
  assert.ok(r.stderr.includes(fakes['GitHub token']));
  run(repo, 'reset', '-q', '--hard', 'origin/main');
});

test('personal details from the local deny-list are refused in new work only', { skip }, () => {
  const denylist = path.join(repo, '.git', 'info', 'secret-denylist');
  fs.mkdirSync(path.dirname(denylist), { recursive: true });
  fs.writeFileSync(denylist, '# real name\r\nZebedee Quux\r\n\r\n');
  try {
    write('about.txt', 'Made by zebedee quux.\n');
    run(repo, 'add', 'about.txt');
    const staged = scan('--diff');
    assert.equal(staged.status, 1);
    assert.match(staged.stderr, /Personal data/);
    run(repo, 'commit', '-q', '-m', 'about');
    assert.equal(scan('--range', 'HEAD', '--not', '--remotes').status, 1, '--range');
    assert.equal(scan('--tree').status, 0, '--tree leaves old work alone');
    assert.equal(scan('--history').status, 0, '--history leaves old work alone');
  } finally {
    fs.rmSync(denylist);
    run(repo, 'reset', '-q', '--hard', 'origin/main');
  }
});

test('the pre-push hook stops a secret before it reaches the remote', { skip }, () => {
  const hookDir = path.join(home, '.claude', 'hooks', 'pairnets-deploy-gate');
  fs.mkdirSync(hookDir, { recursive: true });
  fs.copyFileSync(scanner, path.join(hookDir, 'check-secrets.sh'));
  // A stand-in for the gate: it keeps what git told it and lets the push through.
  const told = path.join(root, 'gate-input.txt');
  fs.writeFileSync(path.join(hookDir, 'deploy-gate.mjs'),
    `import fs from 'node:fs';\nlet s = '';\nfor await (const c of process.stdin) s += c;\nfs.appendFileSync(${JSON.stringify(told)}, s);\n`);
  const hook = path.join(repo, '.git', 'hooks', 'pre-push');
  fs.mkdirSync(path.dirname(hook), { recursive: true });
  fs.writeFileSync(hook, fs.readFileSync(path.join(here, 'pre-push'), 'utf8').replace(/\r\n/g, '\n'));
  fs.chmodSync(hook, 0o755);
  const push = (...args) => git(repo, ['push', 'origin', ...args], { HOME: home, USERPROFILE: home });
  const onRemote = branch => run(origin, 'branch', '--list', branch) !== '';

  run(repo, 'checkout', '-q', '-b', 'leaky');
  commit('config.txt', `token = ${fakes['Resend key']}\n`);
  const refused = push('leaky');
  assert.notEqual(refused.status, 0, 'the push went through');
  assert.match(refused.stderr, /nothing was pushed/);
  assert.ok(!onRemote('leaky'), 'the key reached the remote');

  run(repo, 'checkout', '-q', 'main');
  run(repo, 'checkout', '-q', '-b', 'tidy');
  commit('tidy.txt', 'fine\n');
  const pushed = push('tidy');
  assert.equal(pushed.status, 0, pushed.stderr);
  assert.ok(onRemote('tidy'));
  assert.match(fs.readFileSync(told, 'utf8'), /refs\/heads\/tidy/, 'the gate still gets every pushed ref');

  assert.equal(push('--delete', 'tidy').status, 0, 'deleting a branch sends nothing to scan');

  fs.rmSync(path.join(hookDir, 'check-secrets.sh'));
  const broken = push('tidy');
  assert.notEqual(broken.status, 0, 'a push without the scanner installed went through');
  assert.match(broken.stderr, /not installed properly/);
});
