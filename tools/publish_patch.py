"""Explicitly publish a checked patch to an EXISTING release; never create/merge a release."""
import argparse
import hashlib
import json
import os
import re
import subprocess
import urllib.error
import urllib.request

from release import ROOT, OUT, VERSION, BUILD_NUMBER, hash_file

REPOSITORY = 'repos/ivan22822/QemuM'
CHANNEL_BRANCH = 'codex/alpha-update-channel'



def ci_ready(checks, commit, environment=None):
    environment = os.environ if environment is None else environment
    def completed(item):
        return item['status'] == 'completed' and item['conclusion'] in ('success', 'neutral', 'skipped')
    def own_publication_job(item):
        run = environment.get('GITHUB_RUN_ID', '')
        return (environment.get('GITHUB_ACTIONS') == 'true' and environment.get('GITHUB_SHA') == commit
                and environment.get('GITHUB_JOB') == 'publish-alpha7-build1'
                and run.isdigit() and item.get('name') == 'publish-alpha7-build1'
                and item['status'] == 'in_progress'
                and item.get('details_url', '').startswith('https://github.com/ivan22822/QemuM/actions/runs/'+run+'/job/'))
    return bool(checks) and any(completed(item) for item in checks) and all(completed(item) or own_publication_job(item) for item in checks)


def archived_metadata(name):
    return re.fullmatch(r'(?:update-(?:before-)?build\.\d+\.json|SHA256SUMS-(?:before-)?build\.\d+\.txt)', name) is not None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--commit', required=True, help='exact tested full source commit')
    parser.add_argument('--publish', action='store_true', help='perform GitHub writes')
    parser.add_argument('--installer', action='store_true', help='also refresh the installer and bootstrap downloads')
    args = parser.parse_args()
    git = lambda *parts: subprocess.check_output(['git', '-C', str(ROOT), *parts], text=True).strip()
    if git('rev-parse', 'HEAD') != args.commit or git('status', '--porcelain'):
        raise RuntimeError('Commit must be current and the tracked checkout must be clean')
    subprocess.run([__import__('sys').executable, str(ROOT/'tools/audit_release.py')] + ([] if args.installer else ['--patch']), check=True)
    feed = json.loads((OUT/'update.json').read_text(encoding='utf-8'))
    if BUILD_NUMBER <= 0 or feed.get('build') != BUILD_NUMBER:
        raise RuntimeError('A positive, matching patch build is required')
    package = OUT/f'QemuM-{VERSION}-app.zip'
    package_name = f'QemuM-{VERSION}-build.{BUILD_NUMBER}-app.zip'
    expected_url = f'https://github.com/ivan22822/QemuM/releases/download/v{VERSION}/{package_name}'
    if feed['url'] != expected_url or feed['sha256'] != hash_file(package):
        raise RuntimeError('Unexpected feed URL or hash')
    plan = {'source_commit': args.commit, 'version': VERSION, 'build': BUILD_NUMBER,
            'channel_branch': CHANNEL_BRANCH, 'existing_release': 'v'+VERSION,
            'package': package_name, 'new_release': False, 'merge_main': False}
    print(json.dumps(plan, indent=2), flush=True)
    if not args.publish:
        return
    token = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
    if not token:
        credential = subprocess.run(['git', 'credential', 'fill'], input='protocol=https\nhost=github.com\n\n',
                                    capture_output=True, text=True, check=True, timeout=30)
        fields = dict(line.split('=', 1) for line in credential.stdout.splitlines() if '=' in line)
        token = fields['password']
    headers = {'Authorization': 'Bearer '+token,
               'Accept': 'application/vnd.github+json', 'User-Agent': 'QemuM-patch-publisher'}

    def api(path, method='GET', body=None, content=None):
        url = path if path.startswith('https://') else 'https://api.github.com/'+REPOSITORY+'/'+path
        options = dict(headers)
        payload = None
        if body is not None:
            payload = json.dumps(body).encode('utf-8'); options['Content-Type'] = 'application/json'
        if content is not None:
            payload = content; options['Content-Type'] = 'application/octet-stream'
        with urllib.request.urlopen(urllib.request.Request(url, headers=options, data=payload, method=method), timeout=90) as response:
            return None if response.status == 204 else json.load(response)

    checks = api('commits/'+args.commit+'/check-runs')['check_runs']
    if not ci_ready(checks, args.commit):
        raise RuntimeError('Successful GitHub CI for the exact commit is required')
    release = api('releases/tags/v'+VERSION)
    if release['draft'] or release['prerelease']:
        raise RuntimeError('Expected the existing published normal alpha release')
    assets = {item['name']: item for item in release['assets']}

    def upload(name, content):
        digest = 'sha256:'+hashlib.sha256(content).hexdigest()
        item = assets.get(name)
        if item is None:
            item = api(release['upload_url'].split('{')[0]+'?name='+name, 'POST', content=content)
            assets[name] = item
        if item.get('digest') != digest or item['size'] != len(content):
            raise RuntimeError('Published asset mismatch: '+name)
        print('Verified asset: '+name, flush=True)
        return item

    payload = package.read_bytes()
    upload(package_name, payload)
    feed_bytes = (OUT/'update.json').read_bytes()
    def backup_asset(item):
        # Backups are preserved in the CI artifact, never added as release downloads.
        target = OUT/'replaced-assets'/item['name']
        target.parent.mkdir(parents=True, exist_ok=True)
        with urllib.request.urlopen(item['browser_download_url'], timeout=90) as response:
            value = response.read(32*1024*1024+1)
        if len(value) != item['size'] or len(value) > 32*1024*1024:
            raise RuntimeError('Release backup size mismatch: '+item['name'])
        if item.get('digest') and item['digest'] != 'sha256:'+hashlib.sha256(value).hexdigest():
            raise RuntimeError('Release backup checksum mismatch: '+item['name'])
        target.write_bytes(value)

    def replace_asset(name, content):
        item = assets.get(name)
        if item and item.get('digest') != 'sha256:'+hashlib.sha256(content).hexdigest():
            backup_asset(item)
            api('releases/assets/'+str(item['id']), 'DELETE')
            del assets[name]
        return upload(name, content)

    if args.installer:
        for name in [f'QemuM-{VERSION}-Setup.exe', 'QemuM.exe', 'QemuM-Refresh.exe']:
            replace_asset(name, (OUT/name).read_bytes())

    # Publish a metadata-only tree. Source and compiled binaries stay off this branch.
    old = None
    try:
        old = api('git/ref/heads/'+CHANNEL_BRANCH)['object']['sha']
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
    if old:
        previous = api('contents/alpha.json?ref='+old)
        import base64
        prior_feed = json.loads(base64.b64decode(previous['content']))
        version_key = lambda text: tuple(int(part) for part in text.replace('-alpha.', '.').split('.'))
        prior_key = (version_key(prior_feed['version']), prior_feed.get('build', 0))
        next_key = (version_key(VERSION), BUILD_NUMBER)
        if prior_key > next_key or (prior_key == next_key and prior_feed != feed):
            raise RuntimeError('Channel cannot be downgraded or an existing build reused')
    tree_items = []
    for name, value in [('alpha.json', feed_bytes.decode('utf-8')),
                        ('README.md', '# QemuM update channel\n\nMetadata only. Sources: '+args.commit+'.\nNo main merge or new release is required for patch delivery.\n')]:
        blob = api('git/blobs', 'POST', {'content': value, 'encoding': 'utf-8'})
        tree_items.append({'path': name, 'mode': '100644', 'type': 'blob', 'sha': blob['sha']})
    tree = api('git/trees', 'POST', {'tree': tree_items})
    commit = api('git/commits', 'POST', {'message': f'updates: publish {VERSION} build {BUILD_NUMBER}',
                 'tree': tree['sha'], 'parents': [old] if old else []})
    if old:
        api('git/refs/heads/'+CHANNEL_BRANCH, 'PATCH', {'sha': commit['sha'], 'force': False})
    else:
        api('git/refs', 'POST', {'ref': 'refs/heads/'+CHANNEL_BRANCH, 'sha': commit['sha']})
    print('Patch channel advanced without force push', flush=True)

    # Keep stable legacy feed URLs. Old package ZIPs remain available to cached feeds.
    replace_asset('update.json', feed_bytes)
    for name, item in list(assets.items()):
        if archived_metadata(name):
            backup_asset(item)
            api('releases/assets/'+str(item['id']), 'DELETE')
            del assets[name]
            print('Archived obsolete metadata: '+name, flush=True)
    sums_bytes = ''.join(item['digest'].split(':', 1)[1]+'  '+name+'\n'
                         for name, item in sorted(assets.items())
                         if item.get('digest') and name != 'SHA256SUMS.txt').encode('ascii')
    replace_asset('SHA256SUMS.txt', sums_bytes)
    if args.installer:
        body = ('## QemuM '+VERSION+' · сборка '+str(BUILD_NUMBER)+'\n\n'
                '[**Установить QemuM для Windows x64**](https://github.com/ivan22822/QemuM/releases/download/v'+VERSION+'/QemuM-'+VERSION+'-Setup.exe)\n\n'
                'Для существующей установки: **Сервис → Обновления**.\n\n'
                '[Пакет обновления — сборка '+str(BUILD_NUMBER)+']('+expected_url+') · '
                '[Контрольные суммы](https://github.com/ivan22822/QemuM/releases/download/v'+VERSION+'/SHA256SUMS.txt)\n\n'
                'Плавные диалоги и переходы, исправление запуска из панели задач, кнопка запуска после обновления, '
                'компактный обновлятор и окно сервиса без прокрутки.\n\n'
                'Установщик содержит ту же сборку, что и канал обновлений. Старые ZIP сохранены для действующих ссылок. '
                'Длительный тест стендов остаётся приостановлен; полный GUI-прогон не повторялся по запросу пользователя.\n')
        api('releases/'+str(release['id']), 'PATCH', {'body': body})
    receipt = dict(plan, channel_commit=commit['sha'], release=release['html_url'])
    (OUT/'publication.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    print(json.dumps(receipt, indent=2))


if __name__ == '__main__':
    main()
