#!/usr/bin/env node
// The Pairnets deploy gate. Nothing that deploys Pairnets goes through until the exact commit being deployed
// has passed the pre-deploy check (scripts/pre-deploy.ps1: every API request, every button, the real Linux
// install). See docs/PREDEPLOY.md.
//
//   (no arguments)        Claude Code PreToolUse hook: reads the tool call as JSON on stdin. Exit 0 lets it run,
//                         exit 2 blocks it (stderr says why). Runs the check itself when a deploy needs one.
//   --pre-push <remote>   git pre-push hook: refuses to push main or a v* tag without a pass record. Never runs
//                         the check (that takes 25 minutes); it says how to.
//   --dry-classify        like the hook, but only says what it would do, and blocks anything that deploys.
//   --status [<commit>]   shows the pass records.
//
// Pass records ("stamps") live in %LOCALAPPDATA%\Pairnets-predeploy\stamps\<commit>.pass and are written only
// by the check itself, after every step passed.

import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { classify, looksLikeDeploy, pushIntent } from './classify.mjs';

export const STATE = process.env.PAIRNETS_PREDEPLOY_HOME
  || path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), '.local', 'share'), 'Pairnets-predeploy');
const STAMP_SCHEMA = '1';
const REPO = 'MRnigth/Pairnets';
// The hook's own timeout is 7200 s: give up a little earlier, so a hung check blocks instead of timing out.
const WATCHDOG_MS = Number(process.env.PAIRNETS_GATE_WATCHDOG_MS || 6900_000);
const ZERO = /^0+$/;

const short = sha => (sha || '').slice(0, 7);

// ------------------------------------------------------------------ small helpers

function git(top, args, timeout = 30_000) {
  const r = spawnSync('git', top ? ['-C', top, ...args] : args, { encoding: 'utf8', windowsHide: true, timeout });
  return { ok: r.status === 0, out: (r.stdout || '').trim(), err: (r.stderr || '').trim() };
}

function gh(top, args, timeout = 60_000) {
  const r = spawnSync('gh', args, { cwd: top || undefined, encoding: 'utf8', windowsHide: true, timeout });
  return { ok: r.status === 0, out: (r.stdout || '').trim(), err: (r.stderr || r.error?.message || '').trim() };
}

function revParse(top, ref) {
  const r = git(top, ['rev-parse', '--verify', '--quiet', ref]);
  return r.ok && /^[0-9a-f]{40}$/.test(r.out) ? r.out : null;
}

/** "/c/Users/x" (Git Bash) -> "C:/Users/x" on Windows; "~" -> home. */
export function nativePath(p) {
  if (!p) return p;
  if (p === '~' || p.startsWith('~/') || p.startsWith('~\\')) p = path.join(os.homedir(), p.slice(1));
  if (process.platform === 'win32') {
    const m = /^\/([a-zA-Z])(\/.*)?$/.exec(p);
    if (m) p = `${m[1].toUpperCase()}:${m[2] || '/'}`;
  }
  return p;
}

/** The checkout a folder belongs to (the folder holding .git), or null. */
export function findRepo(dir) {
  let d = path.resolve(dir);
  for (;;) {
    if (fs.existsSync(path.join(d, '.git'))) return d;
    const parent = path.dirname(d);
    if (parent === d) return null;
    d = parent;
  }
}

/** Pairnets is recognised by what is in the checkout, not by its remote, so a test clone is guarded too. */
export function isPairnets(top) {
  return !!top && fs.existsSync(path.join(top, 'Pairnets.sln')) && fs.existsSync(path.join(top, 'src', 'Pairnets.Server'));
}

// ------------------------------------------------------------------ pass records

export function stampPath(sha) {
  return path.join(STATE, 'stamps', `${sha}.pass`);
}

export function readStamp(sha) {
  try {
    const fields = Object.fromEntries(fs.readFileSync(stampPath(sha), 'utf8').split(/\r?\n/).filter(l => l.includes('='))
      .map(l => [l.slice(0, l.indexOf('=')).trim(), l.slice(l.indexOf('=') + 1).trim()]));
    return fields.schema === STAMP_SCHEMA && fields.sha === sha ? fields : null;
  } catch {
    return null;
  }
}

// ------------------------------------------------------------------ what a git push updates

const isMainRef = r => r === 'main' || r === 'refs/heads/main' || r === 'heads/main';

/**
 * The deploys a push makes: main updated (with the commit it gets), main deleted, or v* tags pushed.
 * @returns {{kind: 'main'|'tag'|'delete-main'|'unknown', sha?: string, label: string}[]}
 */
export function pushTargets(intent, top) {
  const targets = [];
  const remote = intent.remote || defaultRemote(top);
  const localMain = revParse(top, 'refs/heads/main');
  const pushDefault = git(top, ['config', '--get', 'push.default']).out || 'simple';
  let specs = intent.refspecs.slice();

  if (intent.subtree) {
    targets.push({ kind: 'unknown', label: 'git subtree push (cannot tell what it pushes)' });
    return targets;
  }
  if (intent.all || intent.mirror || (specs.length === 0 && pushDefault === 'matching')) {
    if (localMain) targets.push({ kind: 'main', sha: localMain, label: 'main (all branches are pushed)' });
    else if (intent.mirror || intent.prune) targets.push({ kind: 'delete-main', label: 'main would be deleted (no local main)' });
  }
  if (intent.tags || intent.mirror) targets.push(...unpushedReleaseTags(top, remote));
  if (specs.length === 0 && !intent.all && !intent.mirror) {
    const configured = remote ? git(top, ['config', '--get-all', `remote.${remote}.push`]).out.split(/\r?\n/).filter(Boolean) : [];
    if (configured.length) {
      specs = configured;
    } else {
      const branch = git(top, ['symbolic-ref', '--quiet', '--short', 'HEAD']);
      if (branch.ok && branch.out) {
        const dests = new Set([branch.out]);
        for (const ref of ['@{push}', '@{upstream}']) {
          const r = git(top, ['rev-parse', '--abbrev-ref', '--symbolic-full-name', ref]);
          if (r.ok && r.out.includes('/')) dests.add(r.out.slice(r.out.indexOf('/') + 1));
        }
        if ([...dests].some(isMainRef)) targets.push({ kind: 'main', sha: revParse(top, 'HEAD'), label: `main (your branch ${branch.out} pushes there)` });
      }
    }
  }
  for (const raw of specs) {
    const spec = raw.replace(/^\+/, '');
    const colon = spec.indexOf(':');
    let src = colon >= 0 ? spec.slice(0, colon) : spec;
    let dst = colon >= 0 ? spec.slice(colon + 1) : null;
    if (intent.deleting) {
      if (isMainRef(src)) targets.push({ kind: 'delete-main', label: 'main would be deleted' });
      continue;
    }
    if (src === '' && dst !== null) {
      if (isMainRef(dst)) targets.push({ kind: 'delete-main', label: 'main would be deleted' });
      continue;
    }
    if (src.includes('*')) {
      const pattern = dst ?? src;
      if (localMain && globMatches(pattern, ['refs/heads/main', 'main'])) targets.push({ kind: 'main', sha: localMain, label: `main (pattern ${raw})` });
      if (/tags/.test(pattern)) targets.push(...unpushedReleaseTags(top, remote));
      continue;
    }
    if (dst === null) {
      if (src === 'HEAD' || src === '@') {
        const branch = git(top, ['symbolic-ref', '--quiet', '--short', 'HEAD']);
        dst = branch.ok ? branch.out : null;
      } else if (src.startsWith('refs/')) {
        dst = src;
      } else if (revParse(top, `refs/tags/${src}`) && !revParse(top, `refs/heads/${src}`)) {
        dst = `refs/tags/${src}`;
      } else {
        dst = src;
      }
    }
    if (dst === null) continue; // git refuses this push itself
    const releaseTag = /^(refs\/)?tags\/v[^/]*$/.test(dst) || (/^v[^/]*$/.test(dst) && !!revParse(top, `refs/tags/${dst}`) && !revParse(top, `refs/heads/${dst}`));
    if (isMainRef(dst)) {
      const sha = revParse(top, `${src}^{commit}`);
      targets.push(sha ? { kind: 'main', sha, label: `main (from ${src})` } : { kind: 'unknown', label: `main (cannot find commit ${src})` });
    } else if (releaseTag) {
      const sha = revParse(top, `${src}^{commit}`);
      targets.push(sha ? { kind: 'tag', sha, label: `release tag ${dst.replace(/^(refs\/)?tags\//, '')}` } : { kind: 'unknown', label: `tag ${dst} (cannot find its commit)` });
    }
  }
  return targets;
}

function globMatches(pattern, names) {
  const re = new RegExp('^' + pattern.split('*').map(s => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('.*') + '$');
  return names.some(n => re.test(n));
}

function defaultRemote(top) {
  const branch = git(top, ['symbolic-ref', '--quiet', '--short', 'HEAD']).out;
  for (const key of branch ? [`branch.${branch}.pushRemote`, 'remote.pushDefault', `branch.${branch}.remote`] : ['remote.pushDefault']) {
    const v = git(top, ['config', '--get', key]).out;
    if (v) return v;
  }
  return 'origin';
}

/** v* tags here that the remote does not have yet (or has at another commit): pushing them makes a release. */
function unpushedReleaseTags(top, remote) {
  const local = git(top, ['for-each-ref', '--format=%(refname:short) %(objectname)', 'refs/tags/v*']).out.split(/\r?\n/).filter(Boolean)
    .map(l => l.split(' '));
  if (!local.length) return [];
  const remoteTags = new Map();
  const ls = git(top, ['ls-remote', '--tags', remote], 30_000);
  if (ls.ok) {
    for (const line of ls.out.split(/\r?\n/)) {
      const [sha, ref] = line.split(/\s+/);
      if (ref && !ref.endsWith('^{}')) remoteTags.set(ref.replace('refs/tags/', ''), sha);
    }
  }
  return local.filter(([name, obj]) => !ls.ok || remoteTags.get(name) !== obj).map(([name]) => {
    const sha = revParse(top, `refs/tags/${name}^{commit}`);
    return sha ? { kind: 'tag', sha, label: `release tag ${name}` } : { kind: 'unknown', label: `tag ${name}` };
  });
}

// ------------------------------------------------------------------ deciding

/**
 * Turns what the command does into commits that need a pass record, or reasons to block outright.
 * @returns {Promise<{needs: {sha: string, top: string, why: string}[], blocks: string[], notes: string[]}>}
 */
export async function decide(intents, cwd, command) {
  const needs = [];
  const blocks = [];
  const notes = [];
  const cwdTop = findRepo(cwd);
  const cwdIsPairnets = isPairnets(cwdTop);
  const mentionsPairnets = /pairnets/i.test(command);

  const where = (dir) => {
    if (!dir) return { top: cwdTop, uncertain: false };
    if (/[$%]/.test(dir)) return { top: cwdTop, uncertain: true };
    const native = nativePath(dir);
    const abs = path.isAbsolute(native) ? native : path.resolve(cwd, native);
    return { top: fs.existsSync(abs) ? findRepo(abs) : cwdTop, uncertain: !fs.existsSync(abs) };
  };
  const relevant = (top, uncertain, repoFlag) => {
    if (repoFlag) return repoFlag.toLowerCase() === REPO.toLowerCase() || /(^|\/)pairnets$/i.test(repoFlag);
    if (isPairnets(top)) return true;
    if (uncertain && (cwdIsPairnets || mentionsPairnets)) {
      blocks.push('Could not tell which folder this command runs in, so it was stopped. Run it from the Pairnets folder without variables in the path.');
    }
    return false;
  };
  const fetchMain = (top) => {
    const f = git(top, ['fetch', '--quiet', 'origin', 'main'], 120_000);
    const sha = revParse(top, 'refs/remotes/origin/main');
    if (!f.ok || !sha) {
      blocks.push('Could not reach GitHub to see what main is now, so this was stopped. Try again when online.');
      return null;
    }
    return sha;
  };

  const queue = intents.slice();
  for (let guard = 0; queue.length && guard < 50; guard++) {
    const it = queue.shift();
    const { top, uncertain } = where(it.dir);
    switch (it.type) {
      case 'tamper':
        blocks.push('This command writes to the pre-deploy check\'s own files (its pass records or its hook). Only the check itself writes them.');
        break;
      case 'unparsed':
        if (cwdIsPairnets || mentionsPairnets) blocks.push(`This command could not be read safely (${it.detail}), and it looks like it may deploy. Split it into simpler commands.`);
        break;
      case 'git-alias': {
        if (!isPairnets(top)) break;
        const alias = git(top, ['config', '--get', `alias.${it.alias}`]);
        if (!alias.ok || !alias.out) break;
        if (alias.out.startsWith('!')) {
          queue.push(...classify(`${alias.out.slice(1)} ${it.args.join(' ')}`, 'bash').intents.map(x => ({ ...x, dir: x.dir ?? it.dir })));
        } else {
          const words = alias.out.split(/\s+/);
          if (words[0] === 'push') queue.push(pushIntent([...words.slice(1), ...it.args], it.dir, it.gitDir, it.hooksOverride));
        }
        break;
      }
      case 'git-push': {
        if (!relevant(top, uncertain)) break;
        const targets = pushTargets(it, top);
        const deploys = targets.filter(t => t.kind !== 'delete-main');
        for (const t of targets.filter(t => t.kind === 'delete-main')) blocks.push(`Blocked: ${t.label}. Deleting main would break every installed app's updates.`);
        for (const t of deploys.filter(t => t.kind === 'unknown')) blocks.push(`Blocked: ${t.label}.`);
        if (deploys.length && (it.noVerify || it.hooksOverride)) {
          blocks.push('Blocked: this push to main or of a release tag skips git\'s hooks (--no-verify or core.hooksPath). Push without that.');
        }
        for (const t of deploys.filter(t => t.sha)) needs.push({ sha: t.sha, top, why: t.label });
        break;
      }
      case 'gh-pr-merge': {
        if (!relevant(top, uncertain, it.repo)) break;
        const repoArgs = it.repo ? ['-R', it.repo] : [];
        const view = gh(top, ['pr', 'view', ...(it.selector ? [it.selector] : []), ...repoArgs, '--json', 'baseRefName,headRefOid,number']);
        if (!view.ok) {
          blocks.push(`Could not look up the pull request (${view.err.split('\n')[0] || 'gh failed'}), so the merge was stopped.`);
          break;
        }
        const pr = JSON.parse(view.out);
        if (pr.baseRefName !== 'main') break;
        const gitTop = isPairnets(top) ? top : cwdTop;
        if (!isPairnets(gitTop)) {
          blocks.push('Merging into main is a release; run this from the Pairnets folder so the check can test the pull request.');
          break;
        }
        git(gitTop, ['fetch', '--quiet', 'origin', 'main', `refs/pull/${pr.number}/head`], 120_000);
        const mainSha = revParse(gitTop, 'refs/remotes/origin/main');
        if (!revParse(gitTop, `${pr.headRefOid}^{commit}`) || !mainSha) {
          blocks.push(`Could not fetch pull request #${pr.number} or main from GitHub, so the merge was stopped.`);
          break;
        }
        if (!git(gitTop, ['merge-base', '--is-ancestor', mainSha, pr.headRefOid]).ok) {
          blocks.push(`Pull request #${pr.number} is behind main. Bring main into it first, so the commit that is tested is exactly what lands.`);
          break;
        }
        needs.push({ sha: pr.headRefOid, top: gitTop, why: `pull request #${pr.number} merged into main` });
        break;
      }
      case 'gh-release': {
        if (!relevant(top, uncertain, it.repo)) break;
        if (!it.tag) {
          blocks.push('gh release without a tag name: say which release, so the check can test that commit.');
          break;
        }
        let sha = revParse(top, `refs/tags/${it.tag}^{commit}`);
        if (!sha && it.target) sha = revParse(top, `refs/remotes/origin/${it.target}^{commit}`) || revParse(top, `${it.target}^{commit}`);
        if (!sha) sha = fetchMain(top);
        if (sha) needs.push({ sha, top, why: `gh release ${it.action} ${it.tag}` });
        break;
      }
      case 'gh-workflow-run': {
        if (!relevant(top, uncertain, it.repo)) break;
        if (it.workflow && !/^(release(\.ya?ml)?|\d+)$/i.test(it.workflow)) break;
        if (!it.workflow) {
          blocks.push('gh workflow run without a workflow name: say which workflow.');
          break;
        }
        let sha = null;
        if (it.ref) {
          git(top, ['fetch', '--quiet', 'origin', it.ref], 120_000);
          sha = revParse(top, `refs/remotes/origin/${it.ref}^{commit}`) || revParse(top, `${it.ref}^{commit}`);
        } else {
          sha = fetchMain(top);
        }
        if (sha) needs.push({ sha, top, why: `the release workflow on ${it.ref || 'main'}` });
        else if (it.ref) blocks.push(`Could not find ${it.ref} to test it before running the release workflow.`);
        break;
      }
      case 'gh-run-rerun': {
        if (!relevant(top, uncertain, it.repo)) break;
        if (!it.runId) {
          blocks.push('gh run rerun without a run id: say which run.');
          break;
        }
        const view = gh(top, ['run', 'view', it.runId, ...(it.repo ? ['-R', it.repo] : []), '--json', 'workflowName,headSha']);
        if (!view.ok) {
          blocks.push(`Could not look up run ${it.runId}, so it was not rerun.`);
          break;
        }
        const run = JSON.parse(view.out);
        if (!/release/i.test(run.workflowName)) break;
        if (!revParse(top, `${run.headSha}^{commit}`)) git(top, ['fetch', '--quiet', 'origin', run.headSha], 120_000);
        needs.push({ sha: run.headSha, top, why: `rerunning the release workflow (${run.workflowName})` });
        break;
      }
      case 'gh-api':
        if (relevant(top, uncertain, /pairnets/i.test(it.endpoint) ? REPO : null) || /pairnets/i.test(it.endpoint)) {
          blocks.push(`Blocked: "gh api ${it.method} ${it.endpoint}" can merge, move main or release without the check seeing what lands. Use git push, gh pr merge or gh release instead.`);
        }
        break;
      case 'wrangler': {
        if (!relevant(top, uncertain)) break;
        const status = git(top, ['status', '--porcelain']);
        if (status.out) {
          blocks.push(`wrangler ${it.what} uploads the files on disk, and this checkout has uncommitted changes. Commit them first, so the tested commit is what goes live.`);
          break;
        }
        const sha = revParse(top, 'HEAD');
        if (sha) needs.push({ sha, top, why: `wrangler ${it.what}` });
        break;
      }
      case 'remote': {
        if (!it.pairnets && !cwdIsPairnets) break;
        if (it.kind === 'release') {
          const release = await latestReleaseCommit();
          if (!release) {
            blocks.push(`Could not read which commit the newest release is (version.json), so installing it on ${it.host} was stopped.`);
            break;
          }
          const gitTop = cwdIsPairnets ? cwdTop : null;
          if (!gitTop) {
            blocks.push(`Installing the newest release on ${it.host}: run this from the Pairnets folder so the check can test release ${release.version}.`);
            break;
          }
          if (!revParse(gitTop, `${release.commit}^{commit}`)) git(gitTop, ['fetch', '--quiet', 'origin', release.commit], 120_000);
          needs.push({ sha: release.commit, top: gitTop, why: `installing release ${release.version} on ${it.host}` });
        } else {
          if (!cwdIsPairnets) {
            blocks.push(`Copying or installing Pairnets on ${it.host}: run this from the Pairnets folder, so the check knows which code it is.`);
            break;
          }
          if (git(cwdTop, ['status', '--porcelain']).out) {
            blocks.push(`Installing on ${it.host} copies files from this folder, and it has uncommitted changes. Commit them first.`);
            break;
          }
          const sha = revParse(cwdTop, 'HEAD');
          if (sha) needs.push({ sha, top: cwdTop, why: `installing this folder's code on ${it.host}` });
        }
        break;
      }
      default:
        break;
    }
  }
  const seen = new Set();
  return { needs: needs.filter(n => !seen.has(n.sha) && seen.add(n.sha)), blocks: [...new Set(blocks)], notes };
}

async function latestReleaseCommit() {
  try {
    const res = await fetch(`https://github.com/${REPO}/releases/latest/download/version.json`, { signal: AbortSignal.timeout(20_000) });
    if (!res.ok) return null;
    const v = await res.json();
    return /^[0-9a-f]{40}$/.test(v.commit) ? v : null;
  } catch {
    return null;
  }
}

// ------------------------------------------------------------------ running the check

/** Runs the commit's own scripts/pre-deploy.ps1. @returns {Promise<{ok: boolean, lines: string[], report?: string}>} */
export async function runCheck(top, sha) {
  if (!git(top, ['cat-file', '-e', `${sha}^{commit}`]).ok) {
    return { ok: false, lines: [`Commit ${short(sha)} is not in ${top}; fetch it first.`] };
  }
  const script = git(top, ['show', `${sha}:scripts/pre-deploy.ps1`], 30_000);
  if (!script.ok) {
    return { ok: false, lines: [`Commit ${short(sha)} does not have the pre-deploy check yet (scripts/pre-deploy.ps1). Bring main into this branch first, then deploy.`] };
  }
  const work = path.join(STATE, 'gate');
  const scripts = path.join(work, `${short(sha)}-${process.pid}`);
  fs.mkdirSync(scripts, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const runner = path.join(scripts, 'pre-deploy.ps1');
  const summaryFile = path.join(work, `summary-${short(sha)}-${process.pid}.json`);
  const log = path.join(work, `run-${short(sha)}-${stamp}.log`);
  // The runner and the helpers it loads from its own folder, as they are in that commit. Windows PowerShell 5.1
  // reads a file without a byte-order mark as ANSI.
  fs.writeFileSync(runner, '\uFEFF' + script.out + '\n', 'utf8');
  for (const name of git(top, ['ls-tree', '--name-only', `${sha}:scripts`]).out.split(/\r?\n/)) {
    if (!/\.ps1$/i.test(name) || name === 'pre-deploy.ps1') continue;
    const helper = git(top, ['show', `${sha}:scripts/${name}`], 30_000);
    if (helper.ok) fs.writeFileSync(path.join(scripts, name), '\uFEFF' + helper.out + '\n', 'utf8');
  }
  fs.rmSync(summaryFile, { force: true });
  const fd = fs.openSync(log, 'a');
  const exe = process.platform === 'win32' ? 'powershell.exe' : 'pwsh';
  const started = Date.now();
  const code = await new Promise((resolve) => {
    const child = spawn(exe, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', runner,
      '-Repo', top, '-Commit', sha, '-SummaryFile', summaryFile], { stdio: ['ignore', fd, fd], windowsHide: true });
    const timer = setTimeout(() => {
      if (process.platform === 'win32') spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true });
      else child.kill('SIGKILL');
      resolve('timeout');
    }, WATCHDOG_MS);
    child.on('error', () => { clearTimeout(timer); resolve('spawn-failed'); });
    child.on('exit', (c) => { clearTimeout(timer); resolve(c); });
  });
  fs.closeSync(fd);
  fs.rmSync(scripts, { recursive: true, force: true });
  let summary = null;
  try {
    summary = JSON.parse(fs.readFileSync(summaryFile, 'utf8').replace(/^\uFEFF/, ''));
  } catch {
    // reported below
  }
  fs.rmSync(summaryFile, { force: true });
  const minutes = Math.round((Date.now() - started) / 60000);
  if (code === 0 && summary?.passed && readStamp(sha)) return { ok: true, lines: [], report: summary.report, minutes };
  const lines = [];
  if (code === 'timeout') lines.push(`The check took longer than ${Math.round(WATCHDOG_MS / 60000)} minutes and was stopped.`);
  if (code === 'spawn-failed') lines.push(`Could not start ${exe} to run the check.`);
  for (const step of summary?.steps ?? []) {
    if (step.passed) continue;
    const problems = (step.problems ?? []).slice(0, 4);
    lines.push(`- ${step.name}${problems.length ? ': ' + problems[0] : ''}`);
    for (const p of problems.slice(1)) lines.push(`    ${p}`);
    if ((step.problems ?? []).length > 4) lines.push(`    ... and ${step.problems.length - 4} more (see the report)`);
  }
  if (!summary) lines.push(`The check ended without a summary (exit ${code}). Its log: ${log}`);
  else if (code === 0 && summary.passed) lines.push('The check said it passed, but wrote no pass record.');
  return { ok: false, lines: lines.slice(0, 15), report: summary?.report ?? log, minutes };
}

// ------------------------------------------------------------------ the hook

async function readStdin() {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString('utf8');
}

/** Where the gate writes (tests swap these: node --test itself talks over stdout). */
export const io = { out: s => process.stdout.write(s), err: s => process.stderr.write(s) };

function block(lines) {
  io.err(['Pairnets pre-deploy check: this was not run.', ...lines].join('\n') + '\n');
  return 2;
}

const PROTECTED_FILE = /pairnets-predeploy[\\/]+stamps|[\\/]pairnets-deploy-gate[\\/]/i;

export async function hook(input, { dry = false } = {}) {
  const tool = input.tool_name;
  const ti = input.tool_input || {};
  const cwd = input.cwd || process.cwd();
  if (['Write', 'Edit', 'MultiEdit', 'NotebookEdit'].includes(tool)) {
    const file = ti.file_path || ti.notebook_path || '';
    return PROTECTED_FILE.test(file) ? block(['Only the pre-deploy check itself writes its pass records and its hook.']) : 0;
  }
  if (tool !== 'Bash' && tool !== 'PowerShell') return 0;
  const command = String(ti.command || '');
  const { intents } = classify(command, tool === 'PowerShell' ? 'powershell' : 'bash');
  if (!intents.length) return 0;
  const { needs, blocks } = await decide(intents, cwd, command);
  if (dry) {
    io.out(JSON.stringify({ intents, needs: needs.map(n => ({ ...n, stamped: !!readStamp(n.sha) })), blocks }, null, 2) + '\n');
    return needs.length || blocks.length ? block(['(dry run: nothing was checked or run)', ...blocks]) : 0;
  }
  if (blocks.length) return block(blocks);
  if (!needs.length) return 0;

  const passed = [];
  for (const need of needs) {
    if (readStamp(need.sha)) {
      passed.push(`${short(need.sha)} (already passed)`);
      continue;
    }
    const result = await runCheck(need.top, need.sha);
    if (!result.ok) {
      return block([
        `Commit ${short(need.sha)} (${need.why}) did not pass the pre-deploy check${result.minutes ? ` (${result.minutes} min)` : ''}:`,
        ...result.lines,
        ...(result.report ? [`Full report: ${result.report}`] : []),
        'Fix the problems, commit, and try again.',
      ]);
    }
    passed.push(`${short(need.sha)} (${result.minutes} min, report: ${result.report})`);
  }
  io.out(JSON.stringify({ systemMessage: `Pairnets pre-deploy check passed: ${passed.join('; ')}` }) + '\n');
  return 0;
}

// ------------------------------------------------------------------ git pre-push

export function prePush(lines, top) {
  if (!isPairnets(top)) return 0;
  const missing = [];
  const refused = [];
  for (const line of lines) {
    const [localRef, localSha, remoteRef] = line.trim().split(/\s+/);
    if (!remoteRef) continue;
    const toMain = remoteRef === 'refs/heads/main';
    const releaseTag = /^refs\/tags\/v[^/]*$/.test(remoteRef);
    if (!toMain && !releaseTag) continue;
    if (ZERO.test(localSha)) {
      if (toMain) refused.push('main would be deleted; that breaks every installed app\'s updates.');
      continue;
    }
    const sha = revParse(top, `${localSha}^{commit}`) || localSha;
    if (!readStamp(sha)) missing.push(`${short(sha)} (${localRef} -> ${remoteRef})`);
  }
  if (!missing.length && !refused.length) return 0;
  const out = ['Not pushed: pushing main or a v* tag releases Pairnets to every installed app.'];
  for (const r of refused) out.push(`- ${r}`);
  if (missing.length) {
    out.push(`These commits have not passed the pre-deploy check: ${missing.join(', ')}.`);
    out.push('Run the check first (about 25 minutes), then push again:');
    for (const m of missing) out.push(`  powershell -ExecutionPolicy Bypass -File scripts/pre-deploy.ps1 -Repo . -Commit ${m.split(' ')[0]}`);
  }
  io.err(out.join('\n') + '\n');
  return 1;
}

function status(sha) {
  const dir = path.join(STATE, 'stamps');
  const files = sha ? [`${sha}.pass`] : (fs.existsSync(dir) ? fs.readdirSync(dir).filter(f => f.endsWith('.pass')) : []);
  if (!files.length) console.log('No pass records yet.');
  for (const f of files) {
    const s = readStamp(f.replace(/\.pass$/, ''));
    console.log(s ? `${s.sha}  passed ${s.finished}  report: ${s.report || '-'}` : `${f}: not a valid pass record`);
  }
  return 0;
}

// ------------------------------------------------------------------ entry point

async function main(argv) {
  if (argv[0] === '--pre-push') {
    const text = await readStdin();
    return prePush(text.split(/\r?\n/).filter(Boolean), findRepo(process.cwd()));
  }
  if (argv[0] === '--status') return status(argv[1]);
  const raw = await readStdin();
  try {
    return await hook(JSON.parse(raw || '{}'), { dry: argv[0] === '--dry-classify' });
  } catch (err) {
    // A bug in the gate must not let a deploy through, nor stop everything else: block only what may deploy.
    if (!looksLikeDeploy(raw) && !/stamps|deploy-gate/i.test(raw)) return 0;
    return block([`The gate itself failed (${err?.stack || err}). Nothing was run.`]);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2)).then((code) => process.exit(code));
}
