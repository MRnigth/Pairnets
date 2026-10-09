// node --test "scripts/deploy-gate/*.test.mjs"
// The gate against a throwaway Pairnets-shaped repository with a local "GitHub" (a bare repository).
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pn-gate-'));
process.env.PAIRNETS_PREDEPLOY_HOME = path.join(root, 'Pairnets-predeploy');
const gate = await import('./deploy-gate.mjs');
const { classify } = await import('./classify.mjs');

const repo = path.join(root, 'repo');
const other = path.join(root, 'other');
const origin = path.join(root, 'origin.git');

function run(cwd, ...args) {
  const r = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true });
  if (r.status !== 0) throw new Error(`git ${args.join(' ')}: ${r.stderr}`);
  return r.stdout.trim();
}
const head = (dir = repo) => run(dir, 'rev-parse', 'HEAD');

function commit(dir, file, text) {
  fs.mkdirSync(path.dirname(path.join(dir, file)), { recursive: true });
  fs.writeFileSync(path.join(dir, file), text);
  run(dir, 'add', '-A');
  run(dir, '-c', 'user.name=t', '-c', 'user.email=t@example.com', 'commit', '-q', '-m', file);
  return head(dir);
}

function stamp(sha) {
  fs.mkdirSync(path.join(process.env.PAIRNETS_PREDEPLOY_HOME, 'stamps'), { recursive: true });
  fs.writeFileSync(gate.stampPath(sha), `schema=1\nsha=${sha}\nfinished=2026-01-01T00:00:00Z\nsteps=all\nreport=-\n`);
}

/** Runs the hook on a Bash tool call; returns its exit code and what it printed. */
async function hook(command, { cwd = repo, tool = 'Bash', dry = false } = {}) {
  const out = [];
  const err = [];
  const [o, e] = [gate.io.out, gate.io.err];
  gate.io.out = (s) => out.push(String(s));
  gate.io.err = (s) => err.push(String(s));
  try {
    const code = await gate.hook({ tool_name: tool, tool_input: tool === 'Bash' || tool === 'PowerShell' ? { command } : { file_path: command }, cwd }, { dry });
    return { code, out: out.join(''), err: err.join('') };
  } finally {
    gate.io.out = o;
    gate.io.err = e;
  }
}

before(() => {
  run(root, 'init', '-q', '--bare', '-b', 'main', origin);
  fs.mkdirSync(repo);
  run(repo, 'init', '-q', '-b', 'main');
  commit(repo, 'Pairnets.sln', 'x');
  commit(repo, 'src/Pairnets.Server/x.txt', 'x');
  run(repo, 'remote', 'add', 'origin', origin);
  run(repo, 'push', '-q', '-u', 'origin', 'main');
  run(repo, 'checkout', '-q', '-b', 'feature');
  commit(repo, 'a.txt', 'a');
  fs.mkdirSync(other);
  run(other, 'init', '-q', '-b', 'main');
  commit(other, 'readme.txt', 'not pairnets');
  run(other, 'remote', 'add', 'origin', origin);
});

after(() => fs.rmSync(root, { recursive: true, force: true }));

const targets = (command, dir = repo) => gate.pushTargets(classify(command).intents[0], dir).map(t => t.kind);

test('what a push updates', () => {
  run(repo, 'checkout', '-q', 'feature');
  assert.deepEqual(targets('git push origin feature'), []);
  assert.deepEqual(targets('git push -u origin feature'), []);
  assert.deepEqual(targets('git push origin main'), ['main']);
  assert.deepEqual(targets('git push origin HEAD:main'), ['main']);
  assert.deepEqual(targets('git push origin feature:refs/heads/main'), ['main']);
  assert.deepEqual(targets('git push origin +HEAD:main'), ['main']);
  assert.deepEqual(targets('git push origin :main'), ['delete-main']);
  assert.deepEqual(targets('git push --delete origin main'), ['delete-main']);
  assert.deepEqual(targets('git push --all origin'), ['main']);
  assert.deepEqual(targets('git push origin "refs/heads/*:refs/heads/*"'), ['main']);
  assert.deepEqual(targets('git push'), []); // feature has no upstream
  run(repo, 'checkout', '-q', 'main');
  assert.deepEqual(targets('git push'), ['main']);
  assert.deepEqual(targets('git push origin'), ['main']);
  assert.deepEqual(targets('git push origin HEAD'), ['main']);
  run(repo, 'checkout', '-q', 'feature');
  run(repo, 'branch', '-q', '--set-upstream-to=origin/main');
  run(repo, 'config', 'push.default', 'upstream');
  assert.deepEqual(targets('git push'), ['main']);
  run(repo, 'config', '--unset', 'push.default');
  run(repo, 'branch', '-q', '--unset-upstream');
});

test('release tags', () => {
  run(repo, 'checkout', '-q', 'feature');
  run(repo, 'tag', 'v9.9.9');
  run(repo, 'tag', 'not-a-release');
  assert.deepEqual(targets('git push origin v9.9.9'), ['tag']);
  assert.deepEqual(targets('git push origin tag v9.9.9'), ['tag']);
  assert.deepEqual(targets('git push origin refs/tags/v9.9.9'), ['tag']);
  assert.deepEqual(targets('git push origin not-a-release'), []);
  assert.deepEqual(targets('git push --tags origin'), ['tag']);
  run(repo, 'push', '-q', 'origin', 'v9.9.9');
  assert.deepEqual(targets('git push --tags origin'), []); // already on the remote
  run(repo, 'tag', '-d', 'v9.9.9', 'not-a-release');
});

test('a push to main needs a pass record for exactly that commit', async () => {
  run(repo, 'checkout', '-q', 'feature');
  const sha = head();
  const blocked = await hook('git push origin HEAD:main');
  assert.equal(blocked.code, 2);
  assert.match(blocked.err, /does not have the pre-deploy check yet/);
  stamp(sha);
  assert.equal((await hook('git push origin HEAD:main')).code, 0);
  assert.equal((await hook('git push origin feature:main 2>&1 | tail -3')).code, 0);
  const newer = commit(repo, 'b.txt', 'b');
  assert.notEqual(newer, sha);
  assert.equal((await hook('git push origin HEAD:main')).code, 2, 'a new commit needs its own pass');
  stamp(newer);
  assert.equal((await hook('git push origin HEAD:main')).code, 0);
});

test('ordinary commands and other repositories are left alone', async () => {
  assert.equal((await hook('git push origin feature')).code, 0);
  assert.equal((await hook('git status')).code, 0);
  assert.equal((await hook('dotnet test')).code, 0);
  assert.equal((await hook('git push origin main', { cwd: other })).code, 0);
  assert.equal((await hook('git push origin main', { tool: 'PowerShell', cwd: other })).code, 0);
  assert.equal((await hook(path.join(repo, 'src', 'x.cs'), { tool: 'Edit' })).code, 0);
});

test('commands that point into the repository from elsewhere are still guarded', async () => {
  const sha = commit(repo, 'c.txt', 'c');
  const fwd = repo.replace(/\\/g, '/');
  assert.equal((await hook(`git -C "${fwd}" push origin HEAD:main`, { cwd: other })).code, 2);
  assert.equal((await hook(`cd "${fwd}" && git push origin HEAD:main`, { cwd: other })).code, 2);
  assert.equal((await hook(`Set-Location '${repo}'; git push origin HEAD:main`, { cwd: other, tool: 'PowerShell' })).code, 2);
  stamp(sha);
  assert.equal((await hook(`git -C "${fwd}" push origin HEAD:main`, { cwd: other })).code, 0);
});

test('bypasses are refused', async () => {
  stamp(head());
  assert.equal((await hook('git push --no-verify origin HEAD:main')).code, 2);
  assert.equal((await hook('git -c core.hooksPath=/dev/null push origin HEAD:main')).code, 2);
  assert.equal((await hook('git push origin :main')).code, 2);
  assert.equal((await hook(`echo x > "${process.env.PAIRNETS_PREDEPLOY_HOME}/stamps/abc.pass"`)).code, 2);
  assert.equal((await hook(path.join(process.env.PAIRNETS_PREDEPLOY_HOME, 'stamps', 'abc.pass'), { tool: 'Write' })).code, 2);
  assert.equal((await hook('git push --no-verify origin feature')).code, 0, 'skipping hooks is fine where nothing deploys');
});

test('aliases are followed', async () => {
  run(repo, 'config', 'alias.p', 'push');
  run(repo, 'config', 'alias.ship', '!git push origin HEAD:main');
  commit(repo, 'd.txt', 'd');
  assert.equal((await hook('git p origin HEAD:main')).code, 2);
  assert.equal((await hook('git ship')).code, 2);
  assert.equal((await hook('git p origin feature')).code, 0);
});

test('wrangler deploys need a clean, passed checkout', async () => {
  const sha = commit(repo, 'cloud/wrangler.toml', 'name = "x"');
  stamp(sha);
  assert.equal((await hook('npx wrangler deploy', { cwd: path.join(repo, 'cloud') })).code, 0);
  fs.writeFileSync(path.join(repo, 'cloud', 'dirty.txt'), 'x');
  const dirty = await hook('npx wrangler deploy', { cwd: path.join(repo, 'cloud') });
  assert.equal(dirty.code, 2);
  assert.match(dirty.err, /uncommitted changes/);
  fs.rmSync(path.join(repo, 'cloud', 'dirty.txt'));
});

test('the dry run says what it would do without running anything', async () => {
  const sha = commit(repo, 'e.txt', 'e');
  const r = await hook('git push origin HEAD:main', { dry: true });
  assert.equal(r.code, 2);
  const plan = JSON.parse(r.out);
  assert.equal(plan.needs[0].sha, sha);
  assert.equal(plan.needs[0].stamped, false);
});

test('git pre-push', () => {
  const sha = commit(repo, 'f.txt', 'f');
  const zero = '0'.repeat(40);
  const quiet = (fn) => {
    const e = gate.io.err;
    gate.io.err = () => true;
    try { return fn(); } finally { gate.io.err = e; }
  };
  assert.equal(quiet(() => gate.prePush([`refs/heads/feature ${sha} refs/heads/feature ${zero}`], repo)), 0);
  assert.equal(quiet(() => gate.prePush([`refs/heads/feature ${sha} refs/heads/main ${zero}`], repo)), 1);
  assert.equal(quiet(() => gate.prePush([`(delete) ${zero} refs/heads/main ${sha}`], repo)), 1);
  assert.equal(quiet(() => gate.prePush([`refs/heads/feature ${sha} refs/heads/main ${zero}`], other)), 0);
  stamp(sha);
  assert.equal(quiet(() => gate.prePush([`refs/heads/feature ${sha} refs/heads/main ${zero}`], repo)), 0);
  run(repo, '-c', 'user.name=t', '-c', 'user.email=t@example.com', 'tag', '-a', '-m', 'r', 'v1.0.0');
  const tagObject = run(repo, 'rev-parse', 'v1.0.0');
  assert.equal(quiet(() => gate.prePush([`refs/tags/v1.0.0 ${tagObject} refs/tags/v1.0.0 ${zero}`], repo)), 0, 'an annotated tag is peeled to its commit');
  run(repo, 'tag', '-d', 'v1.0.0');
});

const hasPowerShell = spawnSync(process.platform === 'win32' ? 'powershell.exe' : 'pwsh', ['-NoProfile', '-Command', 'exit 0'], { windowsHide: true }).status === 0;

test('the check is the commit\'s own runner, and its result decides', { skip: !hasPowerShell && 'no PowerShell here' }, async () => {
  // A stand-in runner with the real one's contract: -Repo, -Commit, -SummaryFile; a pass record on success.
  const runner = `param([string]$Repo, [string]$Commit, [string]$SummaryFile)
$state = $env:PAIRNETS_PREDEPLOY_HOME
$ok = -not (Test-Path (Join-Path $Repo 'BREAK'))
$steps = @(@{ name = 'Windows app buttons'; passed = $ok; seconds = 1; problems = @("Pressing 'Pause' on the tray panel crashed: boom") })
@{ passed = $ok; sha = $Commit; report = 'R'; steps = $steps } | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $SummaryFile
if ($ok) {
  New-Item -ItemType Directory -Force (Join-Path $state 'stamps') | Out-Null
  "schema=1\`nsha=$Commit\`nfinished=x\`nsteps=all\`nreport=R" | Set-Content -Encoding ASCII (Join-Path $state "stamps\\$Commit.pass")
  exit 0
}
exit 1
`;
  const sha = commit(repo, 'scripts/pre-deploy.ps1', runner);
  fs.writeFileSync(path.join(repo, 'BREAK'), 'x');
  const failed = await hook('git push origin HEAD:main');
  assert.equal(failed.code, 2);
  assert.match(failed.err, /Windows app buttons: Pressing 'Pause' on the tray panel crashed: boom/);
  assert.equal(gate.readStamp(sha), null);
  fs.rmSync(path.join(repo, 'BREAK'));
  const passed = await hook('git push origin HEAD:main');
  assert.equal(passed.code, 0, passed.err);
  assert.match(passed.out, /passed/);
  assert.ok(gate.readStamp(sha));
});
