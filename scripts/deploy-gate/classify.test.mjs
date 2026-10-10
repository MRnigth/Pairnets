// node --test scripts/deploy-gate
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { classify, lexBash, lexPowerShell, tagMade, tampers } from './classify.mjs';

const types = (command, shell = 'bash') => classify(command, shell).intents.map(i => i.type);
const pushes = (command, shell = 'bash') => classify(command, shell).intents.filter(i => i.type === 'git-push');

test('ordinary commands are not deploys', () => {
  for (const c of [
    'git status', 'dotnet test Pairnets.sln', 'git commit -m "git push origin main"', 'echo "git push origin main"',
    "git log --grep='push'", 'git fetch origin', 'git pull --rebase', 'gh pr view 12', 'gh pr create --base main --fill',
    'gh release view latest', 'gh run list', 'gh workflow list', 'npx wrangler whoami', 'npx wrangler deploy --dry-run --outdir out',
    'ssh soro uptime', 'ssh soro systemctl status pairnets-server', 'ssh soro journalctl -u pairnets-server -n 50',
    'scp soro:/var/log/syslog .', 'grep -rn "push" src/', 'cat <<EOF > notes.txt\ngit push origin main\nEOF\ngit status',
    'npm publish --dry-run || true', 'gh api repos/MRnigth/Pairnets/releases', 'wrangler d1 migrations apply pairnets-id --local',
  ]) assert.deepEqual(types(c), [], c);
});

test('git push forms are found with their remote and refspecs', () => {
  assert.deepEqual(pushes('git push origin main')[0].refspecs, ['main']);
  assert.equal(pushes('git push origin main')[0].remote, 'origin');
  assert.deepEqual(pushes('git push')[0].refspecs, []);
  assert.deepEqual(pushes('git push origin HEAD:refs/heads/main')[0].refspecs, ['HEAD:refs/heads/main']);
  assert.deepEqual(pushes('git push -u origin feature/x')[0].refspecs, ['feature/x']);
  assert.deepEqual(pushes('git push origin tag v1.2.3')[0].refspecs, ['refs/tags/v1.2.3']);
  assert.equal(pushes('git push --tags origin')[0].tags, true);
  assert.equal(pushes('git push --follow-tags')[0].tags, true);
  assert.equal(pushes('git push --all origin')[0].all, true);
  assert.equal(pushes('git push --mirror backup')[0].mirror, true);
  assert.equal(pushes('git push --delete origin main')[0].deleting, true);
  assert.equal(pushes('git push -d origin old')[0].deleting, true);
  assert.equal(pushes('git push --no-verify origin main')[0].noVerify, true);
  assert.equal(pushes('git push -o ci.skip origin main')[0].remote, 'origin');
  assert.equal(pushes('git push --repo=origin main')[0].remote, 'origin');
  assert.equal(pushes('git push --force-with-lease origin main')[0].remote, 'origin');
});

test('pushes hidden inside other commands are found', () => {
  for (const c of [
    'git push origin main 2>&1 | tail -5', 'cd /c/work && git push', 'git add . && git commit -m x && git push origin main',
    'bash -c "git push origin main"', 'bash -lc \'git push origin main\'', 'sh -c "cd x; git push"',
    'powershell -NoProfile -Command "git push origin main"', 'echo $(git push origin main)', 'echo `git push`',
    'eval "git push origin main"', 'sudo -u someone git push origin main', 'env GIT_TRACE=1 git push', 'FOO=1 git push',
    'timeout 600 git push origin main', 'nohup git push &', 'bash <<EOF\ngit push origin main\nEOF', '(git push origin main)',
    'cmd /c "git push origin main"', 'git push origin main; echo done', 'true || git push origin main',
    '/usr/bin/git push origin main', '"/c/Program Files/Git/cmd/git.exe" push origin main', 'git -c user.name=x push origin main',
    'git --no-pager push origin main', 'xargs git push origin < list',
  ]) assert.equal(pushes(c).length, 1, c);
});

test('PowerShell commands are read as PowerShell', () => {
  for (const c of [
    'git push origin main', 'git push origin main; Write-Host done', "& 'C:\\Program Files\\Git\\cmd\\git.exe' push origin main",
    'git.exe push origin main | Out-Host', "Start-Process git -ArgumentList 'push','origin','main' -Wait",
    "Invoke-Expression 'git push origin main'", 'iex "git push origin main"', 'if ($?) { git push origin main }',
    "Set-Location C:\\work; git push", '$x = $(git push origin main)', 'git push origin main 2>&1 | Out-Null',
    "pwsh -c 'git push origin main'",
    `powershell -EncodedCommand ${Buffer.from('git push origin main', 'utf16le').toString('base64')}`,
  ]) assert.equal(pushes(c, 'powershell').length, 1, c);
  assert.deepEqual(types("Write-Host 'git push origin main'", 'powershell'), []);
  assert.deepEqual(types("git commit -m 'git push origin main'", 'powershell'), []);
  assert.deepEqual(types("git commit -m @'\ngit push origin main\n'@", 'powershell'), []);
  assert.equal(pushes('git push origin `\n  main', 'powershell')[0].refspecs[0], 'main');
});

test('the working folder follows cd and git -C', () => {
  assert.equal(pushes('cd /c/work/repo && git push')[0].dir, '/c/work/repo');
  assert.equal(pushes('cd /c/work && cd repo && git push')[0].dir, '/c/work/repo');
  assert.equal(pushes('git -C /c/other push origin main')[0].dir, '/c/other');
  assert.equal(pushes('Set-Location C:\\x; git push', 'powershell')[0].dir, 'C:\\x');
  assert.equal(pushes('git push')[0].dir, null);
});

test('GitHub deploys', () => {
  const merge = classify('gh pr merge 12 --squash --delete-branch').intents[0];
  assert.equal(merge.type, 'gh-pr-merge');
  assert.equal(merge.selector, '12');
  assert.equal(classify('gh pr merge --auto -m').intents[0].selector, null);
  assert.equal(classify('gh pr merge -R MRnigth/Pairnets 7 -b "body text"').intents[0].repo, 'MRnigth/Pairnets');
  const release = classify('gh release create v1.2.3 dist/* --title "Pairnets v1.2.3" --target main').intents[0];
  assert.deepEqual([release.type, release.action, release.tag, release.target], ['gh-release', 'create', 'v1.2.3', 'main']);
  assert.equal(classify('gh release upload latest x.zip --clobber').intents[0].tag, 'latest');
  assert.equal(classify('gh release delete latest --yes').intents[0].action, 'delete');
  const wf = classify('gh workflow run release.yml --ref main -f x=y').intents[0];
  assert.deepEqual([wf.type, wf.workflow, wf.ref], ['gh-workflow-run', 'release.yml', 'main']);
  assert.equal(classify('gh run rerun 123456 --failed').intents[0].runId, '123456');
  assert.deepEqual(types('gh api -X POST repos/MRnigth/Pairnets/merges -f base=main -f head=x'), ['gh-api']);
  assert.deepEqual(types('gh api repos/MRnigth/Pairnets/pulls/3/merge -X PUT'), ['gh-api']);
  assert.deepEqual(types('gh api repos/o/r/git/refs/heads/main -f sha=abc -F force=true'), ['gh-api']);
  assert.deepEqual(types('gh api repos/o/r/actions/workflows/release.yml/dispatches -f ref=main'), ['gh-api']);
  assert.deepEqual(types('gh api repos/o/r/releases/latest'), []);
});

test('Cloudflare deploys', () => {
  for (const c of ['wrangler deploy', 'npx wrangler deploy', 'npx -y wrangler@3 deploy --env production', 'pnpm dlx wrangler deploy',
    'npm exec -- wrangler deploy', 'wrangler pages deploy site --project-name pairnets', 'wrangler versions upload',
    'wrangler versions deploy', 'wrangler rollback', 'wrangler d1 migrations apply pairnets-id --remote',
    'wrangler d1 execute pairnets-id --remote --command "DELETE FROM x"', 'cd cloud && npx wrangler deploy']) {
    assert.deepEqual(types(c), ['wrangler'], c);
  }
  assert.equal(classify('cd cloud && npx wrangler deploy').intents[0].dir, 'cloud');
  assert.equal(classify('npx wrangler deploy --cwd cloud').intents[0].dir, 'cloud');
});

test('installing or updating a real server', () => {
  const get = classify('ssh soro "curl -fsSL https://raw.githubusercontent.com/MRnigth/Pairnets/main/deploy/get.sh | sudo bash"').intents[0];
  assert.deepEqual([get.type, get.host, get.kind, get.pairnets], ['remote', 'soro', 'release', true]);
  assert.equal(classify('ssh -p 2222 -i key admin@box sudo /opt/pairnets/update.sh').intents[0].kind, 'release');
  assert.equal(classify('ssh box "sudo touch /var/lib/pairnets/update/request"').intents[0].kind, 'release');
  assert.equal(classify('ssh box "cd /tmp/pairnets-server-linux-x64 && sudo ./install.sh"').intents[0].kind, 'local');
  assert.equal(classify('ssh box sudo cp pairnets-server /opt/pairnets/').intents[0].kind, 'local');
  assert.equal(classify('ssh box <<EOF\nsudo bash /tmp/install.sh\nEOF').intents[0].type, 'remote');
  assert.deepEqual(types('scp dist/pairnets-server-linux-x64.tar.gz box:/tmp/'), ['remote']);
  assert.deepEqual(types('scp deploy/install.sh admin@box:'), ['remote']);
  assert.deepEqual(types('rsync -av deploy/ box:/tmp/deploy'), ['remote']);
  assert.deepEqual(types('scp box:/opt/pairnets/install.sh .'), []);
  assert.deepEqual(types('ssh box "cd repo && git push origin main"'), ['git-push']);
});

test('tampering with the pass records or the hook is caught', () => {
  assert.equal(tampers('echo x > "$LOCALAPPDATA/Pairnets-predeploy/stamps/abc.pass"'), true);
  assert.equal(tampers("Set-Content $env:LOCALAPPDATA\\Pairnets-predeploy\\stamps\\a.pass 'x'"), true);
  assert.equal(tampers('rm ~/.claude/hooks/pairnets-deploy-gate/deploy-gate.mjs'), true);
  assert.equal(tampers('ls "$LOCALAPPDATA/Pairnets-predeploy/stamps"'), false);
  assert.equal(tampers('cat "$LOCALAPPDATA/Pairnets-predeploy/reports/x/report.txt"'), false);
  assert.equal(tampers('ls "$LOCALAPPDATA/Pairnets-predeploy/stamps"; gh run list 2>&1 | cat'), false);
  assert.equal(tampers('ls "$LOCALAPPDATA/Pairnets-predeploy/stamps" 2>/dev/null'), false);
  assert.equal(tampers('Get-ChildItem $env:LOCALAPPDATA\\Pairnets-predeploy\\stamps 2>$null'), false);
  assert.equal(tampers('ls x 2>&1 > "$LOCALAPPDATA/Pairnets-predeploy/stamps/a.pass"'), true);
  assert.equal(tampers('echo x >/dev/nullx "$LOCALAPPDATA/Pairnets-predeploy/stamps/a.pass"'), true);
  assert.deepEqual(types('cp fake.pass "$LOCALAPPDATA/Pairnets-predeploy/stamps/"'), ['tamper']);
});

test('what cannot be read safely is a deploy when it mentions one', () => {
  assert.deepEqual(types('git push "origin main'), ['unparsed']);
  assert.deepEqual(types('echo "unclosed'), []);
});

test('aliases and odd git subcommands are handed to the gate', () => {
  assert.deepEqual(types('git p origin main'), ['git-alias']);
  assert.deepEqual(types('git send-pack origin main'), ['unparsed']);
  assert.deepEqual(types('git subtree push --prefix cloud origin main'), ['git-push']);
});

test('a tag made in the same command as a push is found', () => {
  assert.deepEqual(types('git tag v1.2.3 && git push origin v1.2.3'), ['git-tag', 'git-push']);
  assert.equal(tagMade(['v1.2.3']), 'v1.2.3');
  assert.equal(tagMade(['-a', '-m', 'the release', 'v1.2.3', 'HEAD~1']), 'v1.2.3');
  assert.equal(tagMade(['-am', 'the release', 'v1.2.3']), 'v1.2.3');
  assert.equal(tagMade(['-f', '--message=x', 'v1.2.3']), 'v1.2.3');
  assert.equal(tagMade([]), null);
  assert.equal(tagMade(['-l', 'v*']), null);
  assert.equal(tagMade(['-d', 'v1.2.3']), null);
  assert.equal(tagMade(['--contains', 'HEAD']), null);
  assert.equal(tagMade(['-n5']), null);
});

test('the lexers', () => {
  assert.deepEqual(lexBash('a "b c" d\\ e \'f g\' > out.txt 2>&1').commands, [['a', 'b c', 'd e', 'f g']]);
  assert.deepEqual(lexBash('a && b || c; d | e & f').commands, [['a'], ['b'], ['c'], ['d'], ['e'], ['f']]);
  assert.deepEqual(lexBash('a # comment git push\nb').commands, [['a'], ['b']]);
  assert.deepEqual(lexBash('echo $(git push) "$(git status)"').nested.map(n => n.text), ['git push', 'git status']);
  assert.deepEqual(lexPowerShell("a 'it''s' \"x`\"y\" | b").commands, [['a', "it's", 'x"y'], ['b']]);
  assert.deepEqual(lexPowerShell('a <# git push #> b').commands, [['a', 'b']]);
  assert.deepEqual(lexPowerShell('if ($x) { git push } else { git fetch }').commands.map(c => c[0]), ['if', '$x', 'git', 'else', 'git']);
});
