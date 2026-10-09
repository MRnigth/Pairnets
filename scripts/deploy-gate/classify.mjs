// Reads a shell command the way bash or PowerShell would and lists every part of it that could deploy
// Pairnets: pushing main or a v* tag (= a release), merging a pull request into main, making a GitHub release,
// running the release workflow, a Cloudflare deploy, or installing/updating a real server over ssh.
//
// Pure: no files, no git, no network. deploy-gate.mjs turns what this finds into the commits that need a
// passed pre-deploy check. When in doubt it says "deploy": a false alarm costs a minute, a missed release
// ships untested code to every installed app.

/** Words that have to appear somewhere before anything here is worth parsing (keeps every other command fast). */
export const KEYWORDS = /\bgit\s|push|merge|release|workflow|rerun|deploy|publish|wrangler|versions|rollback|d1\b|ssh|scp|rsync|get\.sh|install\.sh|update\.sh|gh\s+api|pairnets-predeploy|pairnets-deploy-gate|iex|invoke-expression|encodedcommand|-enc\b|-e\s|eval|alias/i;

const STAMPS = /pairnets-predeploy[\\/]+stamps|pairnets-deploy-gate/i;
const WRITES = /(>|\btee\b|\bcp\b|\bmv\b|\brm\b|\bdel\b|\berase\b|\btouch\b|\bsed\s+-i|remove-item|\bri\b|set-content|\bsc\b|add-content|\bac\b|out-file|new-item|\bni\b|copy-item|\bcpi\b|move-item|\bmi\b|rename-item|\brni\b|clear-content|writefile|\bln\b|mklink|\bunlink\b|\btruncate\b|\bdd\b|\binstall\b|\bcopy\b|\bmove\b|\bren\b)/i;

/** True when a command writes into the check's pass records or its installed hook. */
export function tampers(text) {
  return STAMPS.test(text) && WRITES.test(text);
}

/**
 * @param {string} command the command line as the tool received it
 * @param {'bash'|'powershell'} shell which shell runs it
 * @returns {{intents: object[], parsed: boolean}}
 */
export function classify(command, shell = 'bash') {
  const intents = [];
  if (!command || !KEYWORDS.test(command)) return { intents, parsed: true };
  if (tampers(command)) intents.push({ type: 'tamper', detail: 'touches the pre-deploy check\'s own files' });
  let parsed = true;
  try {
    walk(command, shell, { cwd: null }, intents, 0);
  } catch (e) {
    parsed = false;
    if (looksLikeDeploy(command)) intents.push({ type: 'unparsed', detail: String(e.message || e) });
  }
  return { intents: dedupe(intents), parsed };
}

/** For text that could not be parsed: does it mention a deploy at all? */
export function looksLikeDeploy(text) {
  return /\bgit\b[^\n]*\bpush\b|\bgh\b[^\n]*\b(pr\s+merge|release|workflow\s+run|run\s+rerun|api)\b|\bwrangler\b|\b(ssh|scp|rsync)\b/i.test(text);
}

function dedupe(list) {
  const seen = new Set();
  return list.filter(i => {
    const key = JSON.stringify(i);
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

// ------------------------------------------------------------------ walking commands

function walk(text, shell, ctx, intents, depth) {
  if (depth > 6) throw new Error('commands nested too deeply');
  const { commands, nested } = shell === 'powershell' ? lexPowerShell(text) : lexBash(text);
  for (const inner of nested) {
    // a heredoc fed to ssh runs on the server
    const kind = inner.onServer ? remoteDeploy(inner.text) : null;
    if (kind) intents.push({ type: 'remote', dir: ctx.cwd, host: '(ssh)', kind, pairnets: /pairnets/i.test(inner.text) });
    walk(inner.text, inner.shell ?? shell, { cwd: ctx.cwd }, intents, depth + 1);
  }
  for (const words of commands) {
    const cmd = unwrap(words);
    if (!cmd || cmd.length === 0) continue;
    simple(cmd, shell, ctx, intents, depth);
  }
}

/** Program name without folder, extension or case: "C:\Git\cmd\git.exe" -> "git". */
export function programName(word) {
  const base = word.replace(/^.*[\\/]/, '').toLowerCase();
  return base.replace(/\.(exe|cmd|bat|com|ps1)$/, '').replace(/(.)@[^@]*$/, '$1'); // npx wrangler@3 -> wrangler
}

const VALUE_OPTIONS = {
  sudo: new Set(['-u', '-g', '-C', '-D', '-h', '-p', '-r', '-t', '-U', '-T', '--user', '--group', '--chdir', '--host', '--prompt']),
  env: new Set(['-u', '--unset', '-C', '--chdir', '-S', '--split-string']),
  nice: new Set(['-n', '--adjustment']),
  timeout: new Set(['-s', '--signal', '-k', '--kill-after']),
  npx: new Set(['-p', '--package', '-c', '--call']),
};

/** Strips what only runs another command (sudo, env, nohup, npx, the PowerShell call operator, VAR=x ...). */
export function unwrap(words) {
  let w = words.slice();
  for (let guard = 0; guard < 20 && w.length > 0; guard++) {
    const first = w[0];
    if (/^[A-Za-z_][A-Za-z0-9_]*=/.test(first) && w.length > 1) { w = w.slice(1); continue; }
    if (first === '&') { w = w.slice(1); continue; }
    const name = programName(first);
    if (['nohup', 'command', 'builtin', 'exec', 'time', 'stdbuf', 'ionice', 'nice', 'sudo', 'doas', 'env', 'timeout', 'winpty'].includes(name)) {
      const values = VALUE_OPTIONS[name] ?? new Set();
      let i = 1;
      while (i < w.length && (w[i].startsWith('-') || (name === 'env' && /^[A-Za-z_][A-Za-z0-9_]*=/.test(w[i])))) {
        if (w[i] === '--') { i++; break; }
        i += values.has(w[i]) ? 2 : 1;
      }
      if (name === 'timeout' && i < w.length && /^\d/.test(w[i])) i++;
      if (name === 'nice' && i < w.length && /^-?\d+$/.test(w[i])) i++;
      w = w.slice(i);
      continue;
    }
    if (name === 'npx' || name === 'bunx') {
      let i = 1;
      while (i < w.length && w[i].startsWith('-')) i += VALUE_OPTIONS.npx.has(w[i]) ? 2 : 1;
      w = w.slice(i);
      continue;
    }
    if ((name === 'pnpm' || name === 'yarn') && ['dlx', 'exec'].includes(w[1])) { w = w.slice(2); continue; }
    if (name === 'npm' && w[1] === 'exec') {
      const dash = w.indexOf('--');
      w = dash > 0 ? w.slice(dash + 1) : w.slice(2);
      continue;
    }
    break;
  }
  return w;
}

function simple(w, shell, ctx, intents, depth) {
  const name = programName(w[0]);
  const args = w.slice(1);
  switch (name) {
    case 'cd': case 'chdir': case 'pushd': case 'set-location': case 'sl': case 'push-location': {
      const target = args.find(a => !a.startsWith('-')) ?? null;
      if (target) ctx.cwd = joinPath(ctx.cwd, target);
      return;
    }
    case 'git': return git(args, ctx, intents, depth, shell);
    case 'gh': return gh(args, ctx, intents);
    case 'wrangler': return wrangler(args, ctx, intents);
    case 'ssh': return ssh(args, ctx, intents, depth);
    case 'scp': case 'rsync': return copyToHost(name, args, ctx, intents);
    case 'bash': case 'sh': case 'zsh': case 'dash': case 'ksh': {
      const script = shellScriptArg(args);
      if (script !== null) walk(script, 'bash', { cwd: ctx.cwd }, intents, depth + 1);
      return;
    }
    case 'powershell': case 'pwsh': {
      const script = powershellScriptArg(args);
      if (script !== null) walk(script, 'powershell', { cwd: ctx.cwd }, intents, depth + 1);
      return;
    }
    case 'cmd': {
      const i = args.findIndex(a => /^\/[ck]$/i.test(a));
      if (i >= 0) walk(args.slice(i + 1).join(' '), 'bash', { cwd: ctx.cwd }, intents, depth + 1);
      return;
    }
    case 'eval': case 'iex': case 'invoke-expression':
      walk(args.filter(a => !/^-command$/i.test(a)).join(' '), name === 'eval' ? 'bash' : 'powershell', { cwd: ctx.cwd }, intents, depth + 1);
      return;
    case 'start-process': case 'saps': case 'start': {
      const { file, list } = startProcessArgs(args);
      if (file) walk([quoteFor(file), ...list].join(' '), shell, { cwd: ctx.cwd }, intents, depth + 1);
      return;
    }
    case 'invoke-command': case 'icm': case 'start-job': case 'foreach-object': case '%':
      return; // their script blocks are walked as plain commands by the lexer
    case 'xargs': {
      const i = args.findIndex(a => !a.startsWith('-'));
      if (i >= 0) simple(args.slice(i), shell, ctx, intents, depth);
      return;
    }
    default:
      return;
  }
}

function quoteFor(word) {
  return /\s/.test(word) ? `'${word.replace(/'/g, "''")}'` : word;
}

function joinPath(base, target) {
  if (/^([A-Za-z]:[\\/]|[\\/]|~)/.test(target) || !base) return target;
  return base.replace(/[\\/]+$/, '') + '/' + target;
}

function shellScriptArg(args) {
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    if (a === '--') return null;
    if (/^-[a-zA-Z]*c[a-zA-Z]*$/.test(a)) return args[i + 1] ?? '';
    if (!a.startsWith('-')) return null; // bash file.sh: a script file, not inspected (the git pre-push hook still guards pushes)
  }
  return null;
}

function powershellScriptArg(args) {
  for (let i = 0; i < args.length; i++) {
    const a = args[i].toLowerCase();
    if (/^[-/](c|command|co|com|comm|comma|comman)$/.test(a)) return args.slice(i + 1).join(' ');
    if (/^[-/](e|ec|en|enc|encodedcommand|encoded|enco|encod|encode)$/.test(a)) {
      try {
        return Buffer.from(args[i + 1] ?? '', 'base64').toString('utf16le');
      } catch {
        throw new Error('unreadable -EncodedCommand');
      }
    }
    if (/^[-/](f|file)$/.test(a)) return null;
    if (/^[-/](noprofile|nop|noninteractive|noni|nologo|executionpolicy|ep|windowstyle|w|inputformat|outputformat|sta|mta|version|v)$/.test(a)) {
      if (/^[-/](executionpolicy|ep|windowstyle|w|inputformat|outputformat|version|v)$/.test(a)) i++;
      continue;
    }
    if (!a.startsWith('-') && !a.startsWith('/')) return args.slice(i).join(' ');
  }
  return null;
}

function startProcessArgs(args) {
  let file = null;
  const list = [];
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    const lower = a.toLowerCase();
    if (lower === '-filepath') { file = args[++i]; continue; }
    if (lower === '-argumentlist' || lower === '-args') {
      const v = args[++i] ?? '';
      list.push(...v.split(',').map(s => s.trim()).filter(Boolean));
      continue;
    }
    if (/^-(wait|nonewwindow|passthru|verb|workingdirectory|windowstyle|redirectstandard\w*|credential|loaduserprofile)$/.test(lower)) {
      if (/^-(verb|workingdirectory|windowstyle|redirectstandard\w*|credential)$/.test(lower)) i++;
      continue;
    }
    if (!file) file = a;
    else list.push(...a.split(',').map(s => s.trim()).filter(Boolean));
  }
  return { file, list };
}

// ------------------------------------------------------------------ git

const GIT_VALUE_GLOBALS = new Set(['-C', '-c', '--git-dir', '--work-tree', '--namespace', '--exec-path', '--config-env', '--super-prefix', '--list-cmds']);
const PUSH_VALUE_OPTIONS = new Set(['-o', '--push-option', '--repo', '--receive-pack', '--exec']);

function git(args, ctx, intents, depth, shell) {
  let dir = ctx.cwd;
  let gitDir = null;
  const configs = [];
  let i = 0;
  for (; i < args.length; i++) {
    const a = args[i];
    if (!a.startsWith('-')) break;
    if (a === '-C') { dir = joinPath(dir, args[++i] ?? ''); continue; }
    if (a === '-c') { configs.push(args[++i] ?? ''); continue; }
    if (a.startsWith('--git-dir=')) { gitDir = a.slice(10); continue; }
    if (a.startsWith('-c') && a.length > 2) { configs.push(a.slice(2)); continue; }
    if (GIT_VALUE_GLOBALS.has(a)) {
      if (a === '--git-dir') gitDir = args[i + 1] ?? '';
      i++;
      continue;
    }
  }
  const sub = args[i];
  if (!sub) return;
  const rest = args.slice(i + 1);
  const hooksOverride = configs.some(c => /^core\.hookspath\s*=/i.test(c));
  if (sub === 'push') {
    intents.push(pushIntent(rest, dir, gitDir, hooksOverride));
    return;
  }
  if (sub === 'subtree' && rest[0] === 'push') {
    intents.push({ type: 'git-push', dir, gitDir, remote: rest.find(a => !a.startsWith('-') && a !== 'push') ?? null, refspecs: [], all: false, mirror: false, tags: false, deleting: false, noVerify: false, hooksOverride, prune: false, subtree: true });
    return;
  }
  if (sub === 'send-pack') {
    intents.push({ type: 'unparsed', detail: 'git send-pack pushes without the usual checks' });
    return;
  }
  if (['commit', 'add', 'status', 'log', 'diff', 'show', 'fetch', 'pull', 'merge', 'rebase', 'checkout', 'switch', 'branch', 'tag',
    'stash', 'reset', 'restore', 'rev-parse', 'remote', 'config', 'worktree', 'cherry-pick', 'revert', 'clone', 'init', 'ls-files',
    'ls-remote', 'grep', 'blame', 'describe', 'for-each-ref', 'show-ref', 'cat-file', 'merge-base', 'notes', 'clean', 'mv', 'rm',
    'am', 'apply', 'bisect', 'gc', 'prune', 'reflog', 'shortlog', 'submodule', 'symbolic-ref', 'update-ref', 'var', 'help', 'version',
    'archive', 'bundle', 'format-patch', 'name-rev', 'range-diff', 'rev-list', 'sparse-checkout', 'maintenance', 'lfs'].includes(sub)) return;
  // Possibly an alias ("git p" for push): deploy-gate.mjs looks it up in the repository's config.
  intents.push({ type: 'git-alias', dir, gitDir, alias: sub, args: rest, hooksOverride, shell });
}

/** What a "git push" would update. */
export function pushIntent(rest, dir = null, gitDir = null, hooksOverride = false) {
  const intent = { type: 'git-push', dir, gitDir, remote: null, refspecs: [], all: false, mirror: false, tags: false, deleting: false, noVerify: false, hooksOverride, prune: false };
  const positional = [];
  for (let i = 0; i < rest.length; i++) {
    const a = rest[i];
    if (a === '--') { positional.push(...rest.slice(i + 1)); break; }
    if (!a.startsWith('-') || a === '-') { positional.push(a); continue; }
    if (a === '--all' || a === '--branches') intent.all = true;
    else if (a === '--mirror') intent.mirror = true;
    else if (a === '--tags' || a === '--follow-tags') intent.tags = true;
    else if (a === '--delete' || a === '-d') intent.deleting = true;
    else if (a === '--no-verify') intent.noVerify = true;
    else if (a === '--prune') intent.prune = true;
    else if (a.startsWith('--repo=')) positional.unshift(a.slice(7));
    else if (PUSH_VALUE_OPTIONS.has(a)) { if (a === '--repo') positional.unshift(rest[i + 1] ?? ''); i++; }
    else if (/^-[a-zA-Z]+$/.test(a) && !a.startsWith('--')) {
      // bundled short options, e.g. -uf; "o" takes a value
      if (a.includes('d')) intent.deleting = true;
      if (a.endsWith('o')) i++;
    }
  }
  intent.remote = positional[0] ?? null;
  const specs = positional.slice(1);
  for (let i = 0; i < specs.length; i++) {
    if (specs[i] === 'tag' && i + 1 < specs.length) {
      intent.refspecs.push(`refs/tags/${specs[i + 1]}`);
      i++;
    } else {
      intent.refspecs.push(specs[i]);
    }
  }
  return intent;
}

// ------------------------------------------------------------------ gh

const GH_GLOBAL_VALUES = new Set(['-R', '--repo', '--hostname']);
const GH_MERGE_VALUES = new Set(['-b', '--body', '-F', '--body-file', '-t', '--subject', '--match-head-commit', '-A', '--author-email', '-R', '--repo']);
const GH_RELEASE_VALUES = new Set(['-t', '--title', '-n', '--notes', '-F', '--notes-file', '--target', '--discussion-category', '-R', '--repo',
  '--notes-start-tag', '--tag', '--fail-on-no-commits']);

function ghRepo(args) {
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '-R' || args[i] === '--repo') return args[i + 1] ?? null;
    if (args[i].startsWith('--repo=')) return args[i].slice(7);
  }
  return null;
}

function positionals(args, valueOptions) {
  const out = [];
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    if (a === '--') { out.push(...args.slice(i + 1)); break; }
    if (a.startsWith('-')) {
      if (!a.includes('=') && valueOptions.has(a)) i++;
      continue;
    }
    out.push(a);
  }
  return out;
}

function optionValue(args, names) {
  for (let i = 0; i < args.length; i++) {
    for (const n of names) {
      if (args[i] === n) return args[i + 1] ?? '';
      if (args[i].startsWith(n + '=')) return args[i].slice(n.length + 1);
    }
  }
  return null;
}

function gh(args, ctx, intents) {
  const repo = ghRepo(args);
  const words = positionals(args, GH_GLOBAL_VALUES);
  const [group, action] = words;
  const dir = ctx.cwd;
  if (group === 'pr' && action === 'merge') {
    const pos = positionals(args, GH_MERGE_VALUES);
    intents.push({ type: 'gh-pr-merge', dir, repo, selector: pos[2] ?? null, auto: args.includes('--auto') });
    return;
  }
  if (group === 'release' && ['create', 'upload', 'edit', 'delete', 'delete-asset'].includes(action)) {
    const pos = positionals(args, GH_RELEASE_VALUES);
    intents.push({ type: 'gh-release', dir, repo, action, tag: pos[2] ?? null, target: optionValue(args, ['--target']) });
    return;
  }
  if (group === 'workflow' && action === 'run') {
    const pos = positionals(args, new Set([...GH_GLOBAL_VALUES, '-r', '--ref', '-f', '--raw-field', '-F', '--field', '--json']));
    intents.push({ type: 'gh-workflow-run', dir, repo, workflow: pos[2] ?? null, ref: optionValue(args, ['--ref', '-r']) });
    return;
  }
  if (group === 'run' && action === 'rerun') {
    const pos = positionals(args, new Set([...GH_GLOBAL_VALUES, '-j', '--job']));
    intents.push({ type: 'gh-run-rerun', dir, repo, runId: pos[2] ?? null });
    return;
  }
  if (group === 'api') {
    const method = (optionValue(args, ['-X', '--method']) ?? '').toUpperCase();
    const hasFields = args.some(a => /^(-f|-F|--field|--raw-field|--input)(=|$)/.test(a));
    const endpoint = positionals(args, new Set([...GH_GLOBAL_VALUES, '-X', '--method', '-f', '-F', '--field', '--raw-field', '-H', '--header',
      '--input', '-q', '--jq', '-t', '--template', '--cache', '-p', '--preview']))[1] ?? '';
    const mutating = (method && method !== 'GET' && method !== 'HEAD') || (!method && hasFields);
    if (mutating && /(merges|pulls\/\d+\/merge|git\/refs|releases|dispatches|actions\/runs\/\d+\/rerun)/i.test(endpoint))
      intents.push({ type: 'gh-api', dir, repo, endpoint, method: method || 'POST' });
  }
}

// ------------------------------------------------------------------ wrangler

function wrangler(args, ctx, intents) {
  const words = positionals(args, new Set(['-c', '--config', '-e', '--env', '--cwd', '--name', '--project-name', '--branch', '--commit-hash',
    '--commit-message', '--outdir', '--compatibility-date', '--message', '--tag', '--version-id', '--database', '--file', '--command']));
  const [a, b, c] = words;
  const dry = args.includes('--dry-run');
  const local = args.includes('--local');
  const cwdOpt = optionValue(args, ['--cwd']);
  const dir = cwdOpt ? joinPath(ctx.cwd, cwdOpt) : ctx.cwd;
  let what = null;
  if ((a === 'deploy' || a === 'publish') && !dry) what = a;
  else if (a === 'pages' && (b === 'deploy' || b === 'publish')) what = 'pages ' + b;
  else if (a === 'versions' && (b === 'upload' || b === 'deploy') && !dry) what = 'versions ' + b;
  else if (a === 'rollback') what = 'rollback';
  else if (a === 'd1' && b === 'migrations' && c === 'apply' && !local) what = 'd1 migrations apply';
  else if (a === 'd1' && b === 'execute' && args.includes('--remote')) what = 'd1 execute --remote';
  if (what) intents.push({ type: 'wrangler', dir, what });
}

// ------------------------------------------------------------------ ssh, scp, rsync

const SSH_VALUES = new Set(['-b', '-c', '-D', '-E', '-e', '-F', '-I', '-i', '-J', '-L', '-l', '-m', '-O', '-o', '-p', '-Q', '-R', '-S', '-W', '-w', '-B', '-P']);

/** A command on a server that installs or updates Pairnets. */
export function remoteDeploy(text) {
  if (/\bget\.sh\b/.test(text)) return 'release';
  if (/\bupdate\.sh\b/.test(text) || /\/var\/lib\/pairnets\/update\/request/.test(text) || /systemctl\s+(\S+\s+)*start\s+(\S+\s+)*pairnets-update/.test(text)) return 'release';
  if (/\binstall\.sh\b/.test(text)) return 'local';
  if (/\b(cp|mv|install|tee|tar|rsync|ln)\b[^;|&]*\/opt\/pairnets\b/.test(text)) return 'local';
  return null;
}

function ssh(args, ctx, intents, depth) {
  let i = 0;
  for (; i < args.length; i++) {
    const a = args[i];
    if (a === '--') { i++; break; }
    if (!a.startsWith('-')) break;
    if (SSH_VALUES.has(a)) i++;
  }
  const host = args[i];
  const remote = args.slice(i + 1).join(' ');
  if (!host || !remote) return;
  const kind = remoteDeploy(remote);
  if (kind) intents.push({ type: 'remote', dir: ctx.cwd, host, kind, pairnets: /pairnets/i.test(remote) });
  // Commands sent to the server can also push or release from there.
  try {
    const nested = classify(remote, 'bash').intents.filter(x => x.type !== 'remote');
    for (const n of nested) intents.push({ ...n, host });
  } catch {
    // the remote text is reported above when it matters
  }
}

function copyToHost(name, args, ctx, intents) {
  const words = positionals(args, new Set(['-P', '-i', '-o', '-F', '-l', '-S', '-J', '-c', '-e', '--rsh', '--exclude', '--include', '--filter', '-f']));
  if (words.length < 2) return;
  const dest = words[words.length - 1];
  const sources = words.slice(0, -1);
  const remoteDest = /^[^/\\:]{2,}:/.test(dest) || /^[\w.-]+@/.test(dest);
  if (!remoteDest) return;
  if (sources.some(s => /(install|update|get)\.sh$|pairnets-server|pairnets[^/\\]*\.tar\.gz$/i.test(s) || /(^|[\\/])deploy[\\/]?$/.test(s)))
    intents.push({ type: 'remote', dir: ctx.cwd, host: dest.split(':')[0], kind: 'local', pairnets: true, via: name });
}

// ------------------------------------------------------------------ bash lexer

/**
 * Splits bash text into simple commands (arrays of words, quotes removed) and collects the text of every
 * $(...), `...`, <(...) and of heredocs fed to a shell, to be read as commands too.
 */
export function lexBash(text) {
  const commands = [];
  const nested = [];
  let words = [];
  let word = null;
  let i = 0;
  const pendingHeredocs = [];
  let lastProgram = null;

  const endWord = () => {
    if (word !== null) { words.push(word); word = null; }
  };
  const endCommand = () => {
    endWord();
    const cleaned = dropRedirections(words);
    if (cleaned.length) commands.push(cleaned);
    words = [];
  };
  const take = (ch) => { word = (word ?? '') + ch; };

  while (i < text.length) {
    const ch = text[i];
    if (ch === '\n') {
      endCommand();
      i++;
      // heredoc bodies start on the next line
      while (pendingHeredocs.length) {
        const h = pendingHeredocs.shift();
        let body = '';
        while (i <= text.length) {
          let end = text.indexOf('\n', i);
          if (end < 0) end = text.length;
          const line = text.slice(i, end);
          i = end + 1;
          const check = h.strip ? line.replace(/^\t+/, '') : line;
          if (check === h.delimiter) break;
          body += line + '\n';
          if (end >= text.length) break;
        }
        if (h.feedsShell) nested.push({ text: body, shell: h.feedsShell, onServer: h.onServer });
      }
      continue;
    }
    if (ch === ' ' || ch === '\t' || ch === '\r') { endWord(); i++; continue; }
    if (ch === '#' && word === null) {
      while (i < text.length && text[i] !== '\n') i++;
      continue;
    }
    if (ch === '\\') {
      if (text[i + 1] === '\n') { i += 2; continue; }
      if (i + 1 < text.length) take(text[i + 1]);
      i += 2;
      continue;
    }
    if (ch === "'") {
      const end = text.indexOf("'", i + 1);
      if (end < 0) throw new Error('unclosed single quote');
      take(text.slice(i + 1, end));
      i = end + 1;
      continue;
    }
    if (ch === '"') {
      i++;
      let s = '';
      for (;;) {
        if (i >= text.length) throw new Error('unclosed double quote');
        const c = text[i];
        if (c === '"') { i++; break; }
        if (c === '\\' && i + 1 < text.length && '"\\$`\n'.includes(text[i + 1])) { if (text[i + 1] !== '\n') s += text[i + 1]; i += 2; continue; }
        if (c === '$' && text[i + 1] === '(') {
          const end = matchParen(text, i + 1);
          nested.push({ text: text.slice(i + 2, end) });
          s += text.slice(i, end + 1);
          i = end + 1;
          continue;
        }
        if (c === '`') {
          const end = text.indexOf('`', i + 1);
          if (end < 0) throw new Error('unclosed backtick');
          nested.push({ text: text.slice(i + 1, end) });
          s += text.slice(i, end + 1);
          i = end + 1;
          continue;
        }
        s += c;
        i++;
      }
      take(s);
      continue;
    }
    if (ch === '$' && text[i + 1] === "'") {
      // ANSI-C quoting: keep the content, roughly unescaped
      let j = i + 2;
      let s = '';
      while (j < text.length && text[j] !== "'") {
        if (text[j] === '\\' && j + 1 < text.length) { s += text[j + 1] === 'n' ? '\n' : text[j + 1]; j += 2; continue; }
        s += text[j++];
      }
      if (j >= text.length) throw new Error('unclosed $\' quote');
      take(s);
      i = j + 1;
      continue;
    }
    if ((ch === '$' || ch === '<' || ch === '>') && text[i + 1] === '(') {
      const end = matchParen(text, i + 1);
      nested.push({ text: text.slice(i + 2, end) });
      take(text.slice(i, end + 1));
      i = end + 1;
      continue;
    }
    if (ch === '`') {
      const end = text.indexOf('`', i + 1);
      if (end < 0) throw new Error('unclosed backtick');
      nested.push({ text: text.slice(i + 1, end) });
      take(text.slice(i, end + 1));
      i = end + 1;
      continue;
    }
    if (ch === '<' && text[i + 1] === '<' && text[i + 2] !== '<') {
      // heredoc: <<WORD or <<-WORD (quotes around WORD allowed)
      endWord();
      let j = i + 2;
      const strip = text[j] === '-';
      if (strip) j++;
      while (text[j] === ' ' || text[j] === '\t') j++;
      let delimiter = '';
      while (j < text.length && !/[\s;&|<>()]/.test(text[j])) delimiter += text[j++];
      delimiter = delimiter.replace(/['"\\]/g, '');
      const program = programName((dropRedirections(words)[0]) ?? lastProgram ?? '');
      const feedsShell = ['bash', 'sh', 'zsh', 'dash', 'ssh'].includes(program) ? 'bash' : ['powershell', 'pwsh'].includes(program) ? 'powershell' : null;
      pendingHeredocs.push({ delimiter, strip, feedsShell, onServer: program === 'ssh' });
      i = j;
      continue;
    }
    if (ch === ';' || ch === '|' || ch === '&' || ch === '(' || ch === ')' || ch === '{' && word === null || ch === '}' && word === null) {
      if (ch === '&' && (text[i + 1] === '>' || (word !== null && /^\d*>$/.test(word)))) { take(ch); i++; continue; }
      if (ch === '&' && word !== null && /[<>]$/.test(word)) { take(ch); i++; continue; }
      lastProgram = dropRedirections(word !== null ? [...words, word] : words)[0] ?? lastProgram;
      endCommand();
      i += (text[i + 1] === ch && (ch === '&' || ch === '|' || ch === ';')) ? 2 : 1;
      continue;
    }
    take(ch);
    i++;
  }
  endCommand();
  return { commands, nested };
}

function matchParen(text, open) {
  let depth = 0;
  for (let i = open; i < text.length; i++) {
    const c = text[i];
    if (c === "'") { const e = text.indexOf("'", i + 1); if (e < 0) break; i = e; continue; }
    if (c === '"') {
      let j = i + 1;
      while (j < text.length && text[j] !== '"') { if (text[j] === '\\') j++; j++; }
      i = j;
      continue;
    }
    if (c === '(') depth++;
    else if (c === ')') { depth--; if (depth === 0) return i; }
  }
  throw new Error('unclosed parenthesis');
}

function dropRedirections(list) {
  const out = [];
  for (let i = 0; i < list.length; i++) {
    const w = list[i];
    if (/^[\d*]*(>>?|<)&(\d+|-)$/.test(w)) continue; // 2>&1, >&2: complete
    if (/^([\d*]*|&)(>>?|<|>\|)$/.test(w)) { i++; continue; } // operator alone: skip it and its target
    if (/^([\d*]*|&)(>>?|<)./.test(w)) continue; // operator glued to its target
    out.push(w);
  }
  return out;
}

// ------------------------------------------------------------------ PowerShell lexer

/** The same for PowerShell: backtick escapes, '' in single quotes, here-strings, $( ) and { } script blocks. */
export function lexPowerShell(text) {
  const commands = [];
  const nested = [];
  let words = [];
  let word = null;
  let i = 0;

  const endWord = () => {
    if (word !== null) { words.push(word); word = null; }
  };
  const endCommand = () => {
    endWord();
    const cleaned = dropRedirections(words);
    if (cleaned.length) commands.push(cleaned);
    words = [];
  };
  const take = (ch) => { word = (word ?? '') + ch; };

  while (i < text.length) {
    const ch = text[i];
    if (ch === '\n' || ch === ';' || ch === '{' || ch === '}' || ch === '(' || ch === ')') { endCommand(); i++; continue; }
    if (ch === ' ' || ch === '\t' || ch === '\r') { endWord(); i++; continue; }
    if (ch === '|' || (ch === '&' && text[i + 1] === '&')) {
      endCommand();
      i += (text[i + 1] === ch) ? 2 : 1;
      continue;
    }
    if (ch === '&' && word === null) {
      // call operator at the start of a command; at the end it starts a background job
      if (words.length === 0) { i++; continue; }
      endCommand();
      i++;
      continue;
    }
    if (ch === '<' && text[i + 1] === '#') {
      const end = text.indexOf('#>', i + 2);
      if (end < 0) throw new Error('unclosed block comment');
      i = end + 2;
      continue;
    }
    if (ch === '#' && word === null) {
      while (i < text.length && text[i] !== '\n') i++;
      continue;
    }
    if (ch === '`') {
      if (text[i + 1] === '\n' || (text[i + 1] === '\r' && text[i + 2] === '\n')) { i += text[i + 1] === '\r' ? 3 : 2; continue; }
      if (i + 1 < text.length) take(text[i + 1] === 'n' ? '\n' : text[i + 1]);
      i += 2;
      continue;
    }
    if (ch === '@' && (text[i + 1] === "'" || text[i + 1] === '"') && /^\r?\n/.test(text.slice(i + 2))) {
      const quote = text[i + 1];
      const close = text.indexOf('\n' + quote + '@', i + 2);
      if (close < 0) throw new Error('unclosed here-string');
      const body = text.slice(text.indexOf('\n', i) + 1, close).replace(/\r$/, '');
      if (quote === '"') collectPsSubexpressions(body, nested);
      take(body);
      i = close + 3;
      continue;
    }
    if (ch === "'") {
      let j = i + 1;
      let s = '';
      for (;;) {
        if (j >= text.length) throw new Error('unclosed single quote');
        if (text[j] === "'") {
          if (text[j + 1] === "'") { s += "'"; j += 2; continue; }
          break;
        }
        s += text[j++];
      }
      take(s);
      i = j + 1;
      continue;
    }
    if (ch === '"') {
      let j = i + 1;
      let s = '';
      for (;;) {
        if (j >= text.length) throw new Error('unclosed double quote');
        const c = text[j];
        if (c === '`' && j + 1 < text.length) { s += text[j + 1] === 'n' ? '\n' : text[j + 1]; j += 2; continue; }
        if (c === '"') {
          if (text[j + 1] === '"') { s += '"'; j += 2; continue; }
          break;
        }
        if (c === '$' && text[j + 1] === '(') {
          const end = matchParen(text, j + 1);
          nested.push({ text: text.slice(j + 2, end) });
          s += text.slice(j, end + 1);
          j = end + 1;
          continue;
        }
        s += c;
        j++;
      }
      take(s);
      i = j + 1;
      continue;
    }
    if (ch === '$' && text[i + 1] === '(') {
      const end = matchParen(text, i + 1);
      nested.push({ text: text.slice(i + 2, end) });
      take(text.slice(i, end + 1));
      i = end + 1;
      continue;
    }
    if (ch === ',' && word !== null) { take(ch); i++; continue; }
    take(ch);
    i++;
  }
  endCommand();
  return { commands, nested };
}

function collectPsSubexpressions(body, nested) {
  for (let i = 0; i < body.length; i++) {
    if (body[i] === '$' && body[i + 1] === '(') {
      try {
        const end = matchParen(body, i + 1);
        nested.push({ text: body.slice(i + 2, end) });
        i = end;
      } catch {
        return;
      }
    }
  }
}
