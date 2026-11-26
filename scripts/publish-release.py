"""Publish the tested plugin ZIP from a successful main-branch Actions run."""

import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET


PROJECT = Path('src/Plugin/Lidarr.Plugin.SpotifyNavidrome.csproj')
ARCHIVE = Path('.artifacts/Lidarr.Plugin.SpotifyNavidrome.net10.0.zip')
STABLE_VERSION = re.compile(r'(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){2}')


def github_api(endpoint, *options):
    output = subprocess.check_output(['gh', 'api', endpoint, *options], text=True, encoding='utf-8')
    return json.loads(output)


def check_tag(tag, commit, draft_exists):
    ref = f'refs/tags/{tag}'
    result = subprocess.run(['git', 'ls-remote', '--exit-code', 'origin', ref, f'{ref}^{{}}'],
                            text=True, stdout=subprocess.PIPE)
    if result.returncode == 2 and not draft_exists:
        subprocess.run(['git', 'push', 'origin', f'{commit}:{ref}'], check=True)
        return
    result.check_returncode()
    refs = {name: sha for sha, name in (line.split() for line in result.stdout.splitlines())}
    if refs.get(f'{ref}^{{}}', refs.get(ref)) != commit:
        raise RuntimeError(f'{tag} already points to a different commit. Rerun CI for the tagged commit.')


def publish(repository, commit):
    project = ET.parse(PROJECT)
    version = project.findtext('./PropertyGroup/Version', '').strip()
    if not STABLE_VERSION.fullmatch(version):
        raise ValueError(f'Expected a stable MAJOR.MINOR.PATCH plugin version, got {version!r}.')
    minimum = project.findtext('./PropertyGroup/MinimumLidarrVersion', '').strip()
    if not re.fullmatch(r'[0-9]+(?:\.[0-9]+){3}', minimum):
        raise ValueError('Expected a four-part MinimumLidarrVersion in the plugin project.')
    tag = f'v{version}'
    notes = f'Minimum Lidarr Version: {minimum}'
    endpoint = f'repos/{repository}/releases'
    releases = [release for page in github_api(f'{endpoint}?per_page=100', '--paginate', '--slurp')
                for release in page]
    if any(release['tag_name'] == tag and not release['draft'] for release in releases):
        print(f'{tag} is already published.')
        return
    version_parts = tuple(map(int, version.split('.')))
    for release in releases:
        published = release['tag_name'].removeprefix('v')
        if (not release['draft'] and not release['prerelease'] and STABLE_VERSION.fullmatch(published)
                and tuple(map(int, published.split('.'))) > version_parts):
            print(f'Skipping {tag} because {release["tag_name"]} is already published.')
            return

    with ARCHIVE.open('rb') as archive:
        checksum = 'sha256:' + hashlib.file_digest(archive, 'sha256').hexdigest()

    drafts = [release for release in releases if release['tag_name'] == tag and release['draft']]
    if len(drafts) > 1:
        raise RuntimeError(f'Multiple drafts exist for {tag}.')
    draft = drafts[0] if drafts else None
    if draft and (draft['author']['login'] != 'github-actions[bot]' or draft['target_commitish'] != 'main'
                  or draft['prerelease'] or notes not in (draft['body'] or '').splitlines()):
        raise RuntimeError(f'Existing draft for {tag} does not match this workflow.')
    check_tag(tag, commit, draft is not None)
    if draft is None:
        draft = github_api(endpoint, '--method', 'POST', '-f', f'tag_name={tag}', '-f', 'target_commitish=main',
                           '-f', f'name=Spotify to Navidrome {tag}', '-f', f'body={notes}',
                           '-F', 'draft=true', '-F', 'generate_release_notes=true')
    if draft['target_commitish'] != 'main' or not draft['draft']:
        raise RuntimeError(f'Expected {tag} to be an unpublished release targeting main.')

    release_endpoint = f'{endpoint}/{draft["id"]}'
    asset = next((asset for asset in draft['assets'] if asset['name'] == ARCHIVE.name), None)
    if asset is None or asset.get('digest') != checksum or asset['state'] != 'uploaded':
        upload = ['gh', 'release', 'upload', tag, str(ARCHIVE), '--repo', repository]
        if asset is not None:
            upload.append('--clobber')
        subprocess.run(upload, check=True)
    assets = github_api(f'{release_endpoint}/assets')
    if not any(asset['name'] == ARCHIVE.name and asset['state'] == 'uploaded' and asset.get('digest') == checksum
               for asset in assets):
        raise RuntimeError(f'GitHub did not report the expected checksum for {ARCHIVE.name}.')
    release = github_api(release_endpoint, '--method', 'PATCH', '-F', 'draft=false', '-f', 'make_latest=true')
    print(f'Published {release["html_url"]} with {checksum}.')


if __name__ == '__main__':
    publish(os.environ['GITHUB_REPOSITORY'], os.environ['GITHUB_SHA'])
