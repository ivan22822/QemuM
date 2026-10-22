"""Explicitly publish a checked patch to an EXISTING release; never create/merge a release."""
import argparse
import hashlib
import json
import subprocess
import urllib.error
import urllib.request

from release import ROOT, OUT, VERSION, BUILD_NUMBER, hash_file

REPOSITORY = 'repos/ivan22822/QemuM'
CHANNEL_BRANCH = 'codex/alpha-update-channel'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--commit', required=True, help='exact tested full source commit')
    parser.add_argument('--publish', action='store_true', help='perform GitHub writes')
    args = parser.parse_args()
    git = lambda *parts: subprocess.check_output(['git', '-C', str(ROOT), *parts], text=True).strip()
    if git('rev-parse', 'HEAD') != args.commit or git('status', '--porcelain'):
        raise RuntimeError('Commit must be current and the tracked checkout must be clean')
    subprocess.run([__import__('sys').executable, str(ROOT/'tools/audit_release.py'), '--patch'], check=True)
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
    credential = subprocess.run(['git', 'credential', 'fill'], input='protocol=https\nhost=github.com\n\n',
                                capture_output=True, text=True, check=True, timeout=30)
    fields = dict(line.split('=', 1) for line in credential.stdout.splitlines() if '=' in line)
    headers = {'Authorization': 'Bearer '+fields['password'],
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
            return json.load(response)

    checks = api('commits/'+args.commit+'/check-runs')['check_runs']
    if not checks or any(item['status'] != 'completed' or item['conclusion'] not in ('success', 'neutral', 'skipped') for item in checks):
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
    upload(f'update-build.{BUILD_NUMBER}.json', feed_bytes)
    checksums = hashlib.sha256(payload).hexdigest()+'  '+package_name+'\n'
    checksums += hashlib.sha256(feed_bytes).hexdigest()+f'  update-build.{BUILD_NUMBER}.json\n'
    upload(f'SHA256SUMS-build.{BUILD_NUMBER}.txt', checksums.encode('ascii'))

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

    # The legacy loader only recognizes update.json in Releases. Keep its previous
    # feed/checksums, then publish the new feed under the recognized mutable name.
    old_feed = assets.get('update.json')
    if old_feed and old_feed.get('digest') != 'sha256:'+hashlib.sha256(feed_bytes).hexdigest():
        archived = f'update-before-build.{BUILD_NUMBER}.json'
        if archived in assets:
            raise RuntimeError('Legacy feed archive already exists; inspect before resuming')
        api('releases/assets/'+str(old_feed['id']), 'PATCH', {'name': archived})
        assets[archived] = dict(old_feed, name=archived); del assets['update.json']
    upload('update.json', feed_bytes)
    current_sums = ''.join(item['digest'].split(':', 1)[1]+'  '+name+'\n'
                           for name, item in sorted(assets.items()) if item.get('digest') and not name.startswith('SHA256SUMS'))
    sums_bytes = current_sums.encode('ascii')
    old_sums = assets.get('SHA256SUMS.txt')
    if old_sums and old_sums.get('digest') != 'sha256:'+hashlib.sha256(sums_bytes).hexdigest():
        archived = f'SHA256SUMS-before-build.{BUILD_NUMBER}.txt'
        if archived in assets:
            raise RuntimeError('Checksum archive already exists; inspect before resuming')
        api('releases/assets/'+str(old_sums['id']), 'PATCH', {'name': archived})
        del assets['SHA256SUMS.txt']
    upload('SHA256SUMS.txt', sums_bytes)
    receipt = dict(plan, channel_commit=commit['sha'], release=release['html_url'])
    (OUT/'publication.json').write_text(json.dumps(receipt, indent=2), encoding='utf-8')
    print(json.dumps(receipt, indent=2))


if __name__ == '__main__':
    main()
