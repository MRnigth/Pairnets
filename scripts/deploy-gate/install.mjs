#!/usr/bin/env node
// Installs the Pairnets deploy gate on this computer (see docs/PREDEPLOY.md):
//   1. copies deploy-gate.mjs and classify.mjs to ~/.claude/hooks/pairnets-deploy-gate/
//   2. adds its PreToolUse hook to ~/.claude/settings.json (the old file is kept as settings.json.bak-<time>),
//      so every Claude Code session on this computer, in every worktree, goes through it
//   3. puts the git pre-push hook into this repository's hooks folder, which all its worktrees share
//
//   node scripts/deploy-gate/install.mjs              install or update
//   node scripts/deploy-gate/install.mjs --check      say whether everything is in place
//   node scripts/deploy-gate/install.mjs --uninstall  remove all three
import { spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const claudeDir = process.env.CLAUDE_CONFIG_DIR || path.join(os.homedir(), '.claude');
const hookDir = path.join(claudeDir, 'hooks', 'pairnets-deploy-gate');
const settingsFile = path.join(claudeDir, 'settings.json');
const FILES = ['deploy-gate.mjs', 'classify.mjs'];
const MARK = 'pairnets-deploy-gate';
// Claude Code versions from this one on can be told to block (not allow) a tool call when the hook itself fails.
const ON_FAILURE_SINCE = [2, 1, 295];

const fwd = p => p.replace(/\\/g, '/');
const hash = f => crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');

function git(args, cwd = process.cwd()) {
  const r = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true });
  return r.status === 0 ? r.stdout.trim() : null;
}

function claudeVersion() {
  const r = spawnSync('claude', ['--version'], { encoding: 'utf8', windowsHide: true, shell: process.platform === 'win32' });
  const m = /(\d+)\.(\d+)\.(\d+)/.exec(r.stdout || '');
  return m ? m.slice(1).map(Number) : null;
}

const atLeast = (v, min) => !!v && (v[0] - min[0] || v[1] - min[1] || v[2] - min[2]) >= 0;

function readSettings() {
  if (!fs.existsSync(settingsFile)) return {};
  return JSON.parse(fs.readFileSync(settingsFile, 'utf8').replace(/^﻿/, ''));
}

function writeSettings(settings) {
  if (fs.existsSync(settingsFile)) {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    fs.copyFileSync(settingsFile, `${settingsFile}.bak-${stamp}`);
  }
  fs.mkdirSync(claudeDir, { recursive: true });
  fs.writeFileSync(`${settingsFile}.tmp`, JSON.stringify(settings, null, 2) + '\n');
  fs.renameSync(`${settingsFile}.tmp`, settingsFile);
}

const ours = group => (group.hooks || []).some(h => String(h.command || '').includes(MARK) || (h.args || []).some(a => String(a).includes(MARK)));

function withoutOurHook(settings) {
  const pre = settings.hooks?.PreToolUse;
  if (!Array.isArray(pre)) return settings;
  settings.hooks.PreToolUse = pre.filter(g => !ours(g));
  if (!settings.hooks.PreToolUse.length) delete settings.hooks.PreToolUse;
  if (settings.hooks && !Object.keys(settings.hooks).length) delete settings.hooks;
  return settings;
}

function hookEntry() {
  const handler = {
    type: 'command',
    command: `node "${fwd(path.join(hookDir, 'deploy-gate.mjs'))}"`,
    timeout: 7200,
    statusMessage: 'Pairnets deploy gate (a deploy is tested first: about 25 minutes)',
  };
  if (atLeast(claudeVersion(), ON_FAILURE_SINCE)) handler.onFailure = 'block';
  return { matcher: 'Bash|PowerShell|Write|Edit|MultiEdit|NotebookEdit', hooks: [handler] };
}

function prePushTarget() {
  const top = git(['rev-parse', '--show-toplevel']);
  if (!top) return null;
  const hooksPath = git(['config', '--get', 'core.hooksPath'], top);
  const common = git(['rev-parse', '--git-common-dir'], top);
  const dir = hooksPath ? path.resolve(top, hooksPath) : path.join(path.resolve(top, common), 'hooks');
  return path.join(dir, 'pre-push');
}

function install() {
  fs.mkdirSync(hookDir, { recursive: true });
  for (const f of FILES) fs.copyFileSync(path.join(here, f), path.join(hookDir, f));
  console.log(`Copied the gate to ${hookDir}`);

  const settings = withoutOurHook(readSettings());
  settings.hooks ??= {};
  settings.hooks.PreToolUse ??= [];
  const entry = hookEntry();
  settings.hooks.PreToolUse.push(entry);
  writeSettings(settings);
  console.log(`Added the hook to ${settingsFile}${entry.hooks[0].onFailure ? '' : ' (update Claude Code to 2.1.295 or later, then run this again, so a hook that fails to start blocks too)'}`);

  const target = prePushTarget();
  if (!target) {
    console.log('Not inside a git checkout: the git pre-push hook was not installed.');
    return 1;
  }
  if (fs.existsSync(target) && !fs.readFileSync(target, 'utf8').includes(MARK)) {
    console.log(`${target} already exists and is not ours; it was left alone. Add this line to it: node "${fwd(path.join(hookDir, 'deploy-gate.mjs'))}" --pre-push "$@" || exit 1`);
    return 1;
  }
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.writeFileSync(target, fs.readFileSync(path.join(here, 'pre-push'), 'utf8').replace(/\r\n/g, '\n'));
  fs.chmodSync(target, 0o755);
  console.log(`Installed the git pre-push hook: ${target}`);
  return check();
}

function check() {
  let ok = true;
  const say = (good, text) => {
    console.log(`${good ? 'ok  ' : 'MISSING'}  ${text}`);
    ok &&= good;
  };
  for (const f of FILES) {
    const installed = path.join(hookDir, f);
    say(fs.existsSync(installed) && hash(installed) === hash(path.join(here, f)), `${installed} is this checkout's version`);
  }
  const entry = (readSettings().hooks?.PreToolUse || []).find(ours);
  say(!!entry, `the PreToolUse hook in ${settingsFile}`);
  if (entry) say(entry.hooks[0].onFailure === 'block' || !atLeast(claudeVersion(), ON_FAILURE_SINCE), 'blocks when the hook fails to start (onFailure)');
  const target = prePushTarget();
  say(!!target && fs.existsSync(target) && fs.readFileSync(target, 'utf8').includes(MARK), `the git pre-push hook${target ? ` (${target})` : ''}`);
  const node = spawnSync('node', ['--version'], { encoding: 'utf8', windowsHide: true });
  say(node.status === 0, `node on PATH (${(node.stdout || '').trim() || 'not found'})`);
  return ok ? 0 : 1;
}

function uninstall() {
  writeSettings(withoutOurHook(readSettings()));
  fs.rmSync(hookDir, { recursive: true, force: true });
  const target = prePushTarget();
  if (target && fs.existsSync(target) && fs.readFileSync(target, 'utf8').includes(MARK)) fs.rmSync(target);
  console.log('The deploy gate is removed (the pass records in Pairnets-predeploy are kept).');
  return 0;
}

const arg = process.argv[2];
process.exit(arg === '--check' ? check() : arg === '--uninstall' ? uninstall() : install());
