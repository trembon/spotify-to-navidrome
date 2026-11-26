"""Exercise the packaged plugin against disposable Lidarr and Navidrome containers."""

import argparse
import functools
import http.server
import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import tempfile
import threading
import time
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlencode, urlsplit
from urllib.request import Request, urlopen
import uuid
from zipfile import ZipFile


LIDARR = ('docker.io/linuxserver/lidarr:nightly-4.0.0.5236-ls228'
          '@sha256:656a4d008a9a60ba32aba95c85b0fd19a06af8e2680f6748ef693956aa84ae33')
NAVIDROME = ('docker.io/deluan/navidrome:0.64.2'
             '@sha256:38dc2727bfcfd5ede290f8ada114fc90368146f265ae4701ddddbcbe2a44ee52')
PYTHON = ('docker.io/library/python:3.12-alpine'
          '@sha256:c5ba3deef00c7758561a0f03c26f44f10589263d40e732b8748e0d1f38f6dd89')
REPOSITORY = 'https://github.com/trembon/spotify-to-navidrome'
PASSWORD = 'disposable-integration-test'
ARTISTS = ['Fixture North', 'Fixture South', 'Fixture East']
TITLES = ['Paper Harbor', 'Quiet Horizon', 'Open Trail', 'Blue Bridge']
LISTS = ['A' * 22, 'B' * 22, 'C' * 22]
PLUGIN_FILES = {'Lidarr.Plugin.SpotifyNavidrome.dll', 'Lidarr.Plugin.SpotifyNavidrome.deps.json'}
TRACKS = ['D' * 22, 'E' * 22, 'F' * 22, 'G' * 22]
ARTIST_FOR_TRACK = [0, 0, 1, 2]


def mbid(name):
    return str(uuid.uuid5(uuid.NAMESPACE_OID, name))


def albums_for(artist):
    return [f'{artist} First Album', f'{artist} Second Album']


def artist_resource(artist, include_albums=False):
    resource = dict(id=mbid(artist), artistName=artist, artistAliases=[], oldIds=[], genres=[], images=[], links=[])
    if include_albums:
        resource['albums'] = [album_resource(artist, album) for album in albums_for(artist)]
    return resource


def album_resource(artist, album, include_release=False):
    data = dict(id=mbid(album), title=album, artistId=mbid(artist), type='Album', secondaryTypes=[],
                releaseStatuses=['Official'], releaseDate='2020-01-01T00:00:00Z', oldIds=[], images=[], links=[], genres=[])
    if include_release:
        data['artists'] = [artist_resource(artist)]
        data['releases'] = [dict(id=mbid(album + ' release'), title=album, status='Official', country=[], label=[], oldIds=[],
                                 media=[dict(name='', position=1, format='Digital Media')], tracks=[
                                     dict(trackName=album + ' Track', id=mbid(album + ' track'), oldIds=[],
                                          artistId=mbid(artist), mediumNumber=1, trackNumber='1', trackPosition=1,
                                          durationMs=12000, recordingId=mbid(album + ' recording'), oldRecordingIds=[])])]
    return data


class Fixtures(http.server.BaseHTTPRequestHandler):
    def respond(self, data, status=200):
        is_html = isinstance(data, str)
        payload = (data if is_html else json.dumps(data)).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'text/html' if is_html else 'application/json')
        self.send_header('Content-Length', str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        path = urlsplit(self.path)
        item = path.path.rsplit('/', 1)[-1]
        if path.path.startswith('/embed/playlist/') and item in LISTS:
            if item == LISTS[0] and Path('/fixture/break-spotify').exists():
                self.respond('Temporary Spotify outage', 503)
                return
            index = LISTS.index(item)
            rows = [dict(uri=f'spotify:track:{TRACKS[i]}', title=TITLES[i],
                         subtitle=ARTISTS[ARTIST_FOR_TRACK[i]], duration=12000)
                    for i in [[1, 0, 0], [2], [3]][index]]
            entity = dict(uri=f'spotify:playlist:{item}', title=f'Fixture {index}', trackList=rows)
            payload = dict(props=dict(pageProps=dict(state=dict(data=dict(entity=entity)))))
            self.respond(f'<script id="__NEXT_DATA__" type="application/json">{json.dumps(payload)}</script>')
        elif path.path == f'/track/{TRACKS[2]}':
            self.respond(f'<meta property="og:description" content="{ARTISTS[1]} · {albums_for(ARTISTS[1])[0]} · Song · 2020">'
                         '<meta name="music:album" content="https://open.spotify.com/album/HHHHHHHHHHHHHHHHHHHHHH">')
        elif path.path == '/metadata/search':
            query = parse_qs(path.query)
            term = query['query'][0]
            if query['type'] == ['artist']:
                self.respond([artist_resource(artist) for artist in ARTISTS if artist.lower() == term])
            else:
                self.respond([album_resource(artist, album, include_release=True) for artist in ARTISTS
                              for album in albums_for(artist) if album.lower() == term])
        elif path.path.startswith('/metadata/artist/'):
            self.respond(next(artist_resource(artist, include_albums=True) for artist in ARTISTS if mbid(artist) == item))
        elif path.path.startswith('/metadata/album/'):
            self.respond(next(album_resource(artist, album, include_release=True) for artist in ARTISTS
                              for album in albums_for(artist) if mbid(album) == item))
        elif path.path.startswith('/metadata/recent/'):
            self.respond(dict(limited=True, items=[]))
        else:
            self.respond({}, 404)


def serve_fixtures():
    with http.server.ThreadingHTTPServer(('0.0.0.0', 8090), Fixtures) as metadata:
        threading.Thread(target=metadata.serve_forever, daemon=True).start()
        with http.server.ThreadingHTTPServer(('0.0.0.0', 443), Fixtures) as spotify:
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.load_cert_chain('/fixture/ca.crt', '/fixture/key.pem')
            spotify.socket = context.wrap_socket(spotify.socket, server_side=True)
            spotify.serve_forever()


def docker(*args, capture=False, check=True):
    return subprocess.run(['docker', *args], check=check, text=True, capture_output=capture, timeout=180).stdout


def api(base, headers, path, data=None, method=None):
    body = json.dumps(data).encode() if data is not None else None
    request = Request(base + path, data=body, headers={'Content-Type': 'application/json', **headers}, method=method)
    try:
        with urlopen(request, timeout=15) as response:
            raw = response.read()
            return json.loads(raw) if raw else None
    except HTTPError as error:
        raise RuntimeError(f'{request.get_method()} {path}: HTTP {error.code}, {error.read().decode()}') from error


def wait_for(probe, description, seconds=90):
    deadline = time.monotonic() + seconds
    error = None
    while time.monotonic() < deadline:
        try:
            result = probe()
            if result:
                return result
        except (RuntimeError, URLError, ConnectionError, TimeoutError) as exc:
            error = exc
        time.sleep(1)
    raise AssertionError(f'Timed out waiting for {description}: {error}')


def server_url(container, port):
    return 'http://' + docker('port', container, str(port), capture=True).strip().splitlines()[0]


def mount(directory, destination):
    return '--mount', f'type=bind,src={directory},dst={destination}'


def run_integration(work, archive, from_github):
    suffix = uuid.uuid4().hex[:10]
    network = 'spotify-navidrome-test-' + suffix
    lidarr, nav, fixture = (network + '-' + service for service in ('lidarr', 'navidrome', 'fixture'))
    lidarr_config, library, media, fixtures = (work / name for name in ('lidarr', 'library', 'media', 'fixtures'))
    for directory in (lidarr_config, library, media, fixtures):
        directory.mkdir()
    shutil.copyfile(__file__, fixtures / 'integration.py')
    openssl_config = fixtures / 'openssl.cnf'
    openssl_config.write_text('[req]\ndistinguished_name = dn\n[dn]\n')
    certificate = subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '2',
                                  '-config', str(openssl_config), '-keyout', str(fixtures / 'key.pem'),
                                  '-out', str(fixtures / 'ca.crt'), '-subj', '/CN=open.spotify.com',
                                  '-addext', 'subjectAltName=DNS:open.spotify.com',
                                  '-addext', 'basicConstraints=critical,CA:TRUE'], capture_output=True, text=True)
    if certificate.returncode:
        raise RuntimeError(f'Could not generate the fixture certificate: {certificate.stderr}')
    key = uuid.uuid4().hex
    (lidarr_config / 'config.xml').write_text(f'<Config><ApiKey>{key}</ApiKey><InstanceName>Lidarr test</InstanceName></Config>')
    plugins = lidarr_config / 'plugins' / 'trembon' / 'spotify-to-navidrome'
    if not from_github:
        plugins.mkdir(parents=True)
        with ZipFile(archive) as package:
            assert set(package.namelist()) == PLUGIN_FILES
            package.extractall(plugins)

    docker('network', 'create', network, capture=True)
    try:
        docker('run', '-d', '--name', fixture, '--network', network, '--network-alias', 'open.spotify.com',
               '--network-alias', 'fixture', *mount(fixtures, '/fixture'), PYTHON, 'python', '/fixture/integration.py', '--serve', capture=True)
        docker('run', '-d', '--name', nav, '--network', network, '--network-alias', 'navidrome', '-p', '127.0.0.1::4533',
               '-e', 'ND_SCANNER_SCHEDULE=0', '-e', 'ND_ENABLEINSIGHTSCOLLECTOR=false', *mount(media, '/music'), NAVIDROME, capture=True)
        ownership = ['-e', f'PUID={os.getuid()}', '-e', f'PGID={os.getgid()}'] if hasattr(os, 'getuid') else []
        docker('run', '-d', '--name', lidarr, '--network', network, '-p', '127.0.0.1::8686', *ownership,
               *mount(lidarr_config, '/config'), *mount(library, '/music'), *mount(fixtures, '/test-certs'), LIDARR, capture=True)

        lid = functools.partial(api, server_url(lidarr, 8686), {'X-Api-Key': key})
        nav_url = server_url(nav, 4533)
        nav_api = functools.partial(api, nav_url, {})

        def provider_schema():
            return next((item for item in lid('/api/v1/importlist/schema')
                         if item.get('implementation') == 'SpotifyNavidromeImportList'), None)

        status = wait_for(lambda: lid('/api/v1/system/status'), 'pinned Lidarr startup')
        assert status['version'] == '4.0.0.5236', status['version']
        assert status['runtimeVersion'] == '10.0.15', status['runtimeVersion']
        assert status['databaseVersion'] == '3.53.4', status['databaseVersion']
        print('Verified Lidarr 4.0.0.5236, runtime 10.0.15, SQLite 3.53.4.', flush=True)
        docker('exec', lidarr, 'sh', '-c',
               'cp /test-certs/ca.crt /usr/local/share/ca-certificates/spotify-fixture.crt && update-ca-certificates', capture=True)

        if from_github:
            lid('/api/v1/command', dict(name='InstallPlugin', githubUrl=REPOSITORY))
            wait_for(lambda: (plugins / 'Lidarr.Plugin.SpotifyNavidrome.dll').exists(), 'GitHub release download')
        else:
            wait_for(provider_schema, 'plugin registration on cold start')
        docker('restart', lidarr, capture=True)
        lid = functools.partial(api, server_url(lidarr, 8686), {'X-Api-Key': key})

        schema = wait_for(provider_schema, 'plugin registration after restart')
        assert schema['shouldMonitor'] == 'entireArtist', schema['shouldMonitor']
        assert schema['minRefreshInterval'] == '06:00:00', schema['minRefreshInterval']
        assert any(plugin['name'] == 'spotify-to-navidrome' for plugin in lid('/api/v1/system/plugins'))
        if from_github and archive is not None:
            with ZipFile(archive) as package:
                for name in PLUGIN_FILES:
                    assert (plugins / name).read_bytes() == package.read(name), f'Installed {name} differs from the tested ZIP'
            print('Verified that Lidarr installed the files from the tested ZIP.', flush=True)
        print('Loaded the plugin and verified the provider defaults.', flush=True)

        config = lid('/api/v1/config/metadataprovider')
        config['metadataSource'] = 'http://fixture:8090/metadata'
        lid('/api/v1/config/metadataprovider', config, 'PUT')
        quality = lid('/api/v1/qualityprofile')[0]['id']
        metadata = next(item['id'] for item in lid('/api/v1/metadataprofile') if item['name'] == 'Standard')
        lid('/api/v1/rootfolder', dict(name='Library', path='/music', defaultQualityProfileId=quality,
                                     defaultMetadataProfileId=metadata, defaultTags=[]))

        wait_for(lambda: nav_api('/rest/getOpenSubsonicExtensions?f=json'), 'Navidrome startup')
        admin = nav_api('/auth/createAdmin', dict(username='admin', password=PASSWORD))
        for username in ('sync', 'viewer'):
            api(nav_url, {'X-ND-Authorization': 'Bearer ' + admin['token']}, '/api/user',
                dict(userName=username, name=username, password=PASSWORD, isAdmin=False))

        def subsonic(method, user='sync', **options):
            answer = nav_api('/rest/' + method + '.view?' + urlencode(dict(u=user, p=PASSWORD, v='1.16.1',
                                                                           c='test-fixture', f='json', **options)))['subsonic-response']
            if answer['status'] != 'ok':
                raise RuntimeError(f'Navidrome {method}: {answer.get("error")}')
            return answer

        def add_track(index):
            artist = ARTISTS[ARTIST_FOR_TRACK[index]]
            docker('exec', nav, 'ffmpeg', '-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi',
                   '-i', 'anullsrc=r=8000:cl=mono', '-t', '12', '-metadata', f'title={TITLES[index]}',
                   '-metadata', f'artist={artist}', '-metadata', f'album={albums_for(artist)[0]}', f'/music/{index}.flac')

        def scan_for(index):
            subsonic('startScan', 'admin')
            wait_for(lambda: any(song['title'] == TITLES[index] for song in
                                 subsonic('search3', query=TITLES[index]).get('searchResult3', {}).get('song', [])),
                     'indexed Navidrome fixture ' + TITLES[index])

        for index in (0, 2, 3):
            add_track(index)
        scan_for(3)

        def refresh(list_id):
            command = lid('/api/v1/command', dict(name='ImportListSync', definitionId=list_id))
            finished = wait_for(lambda: terminal_command(command['id']), 'Lidarr import command')
            assert finished['status'] == 'completed', finished

        def terminal_command(command_id):
            command = lid(f'/api/v1/command/{command_id}')
            return command if command['status'] in ('completed', 'failed', 'aborted') else None

        ids = []
        for index, mode in enumerate(('entireArtist', 'specificAlbum', 'none')):
            settings = dict(playlists=[LISTS[index], f'https://open.spotify.com/playlist/{LISTS[index]}'],
                            navidromeUrl='http://navidrome:4533', username='sync', password=PASSWORD)
            definition = {**schema, 'name': 'Fixture ' + mode, 'shouldMonitor': mode, 'enableAutomaticAdd': True,
                          'rootFolderPath': '/music', 'qualityProfileId': quality, 'metadataProfileId': metadata,
                          'shouldSearch': False, 'shouldMonitorExisting': False,
                          'fields': [{**field, 'value': settings.get(field['name'], field.get('value'))}
                                     for field in schema['fields']]}
            lid('/api/v1/importlist/test', definition)
            if index == 0:
                assert not subsonic('getPlaylists')['playlists'].get('playlist'), 'Test wrote a playlist'
            ids.append(lid('/api/v1/importlist', definition)['id'])
            refresh(ids[-1])

        def verify_monitoring():
            artists = {artist['artistName']: artist for artist in lid('/api/v1/artist')}
            if set(artists) != set(ARTISTS):
                return False
            albums = lid('/api/v1/album')
            for index, artist in enumerate(ARTISTS):
                records = {album['title']: album['monitored'] for album in albums if album['artistId'] == artists[artist]['id']}
                if [records.get(title) for title in albums_for(artist)] != [[True, True], [True, False], [False, False]][index]:
                    return False
                assert artists[artist]['monitored'] == (index != 2), artists[artist]
            return True

        wait_for(verify_monitoring, 'all three Lidarr monitor modes')
        print('Verified artist and album monitoring in all three modes.', flush=True)

        playlist = next(item for item in subsonic('getPlaylists', 'viewer')['playlists']['playlist']
                        if item['name'] == '[Spotify] Fixture 0')
        playlist_id = playlist['id']
        assert playlist['public'] and '2/3' in playlist['comment'], playlist

        def titles_in_playlist():
            return [song['title'] for song in subsonic('getPlaylist', 'viewer', id=playlist_id)['playlist']['entry']]

        assert titles_in_playlist() == [TITLES[0], TITLES[0]]
        health = lambda: lid('/api/v1/health')
        wait_for(lambda: any('1 missing from Navidrome' in row['message'] for row in health()), 'missing-track warning')
        refresh(ids[0])
        assert titles_in_playlist() == [TITLES[0], TITLES[0]]
        assert len(subsonic('getPlaylists')['playlists']['playlist']) == 3

        (fixtures / 'break-spotify').touch()
        refresh(ids[0])
        wait_for(lambda: any('could not refresh' in row['message'] for row in health()), 'Spotify failure in health')
        assert titles_in_playlist() == [TITLES[0], TITLES[0]]
        (fixtures / 'break-spotify').unlink()
        add_track(1)
        scan_for(1)
        refresh(ids[0])
        assert titles_in_playlist() == [TITLES[1], TITLES[0], TITLES[0]]
        wait_for(lambda: all('Spotify playlists' not in row['message'] for row in health()), 'cleared health warnings')
        print('Verified visibility to a second user, stable ID, duplicate order, failure retention, and recovery.', flush=True)
    except Exception:
        for name in (lidarr, nav, fixture):
            docker('logs', '--tail', '75', name, check=False)
        raise
    finally:
        docker('rm', '-f', '-v', lidarr, nav, fixture, capture=True, check=False)
        docker('network', 'rm', network, capture=True, check=False)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--serve', action='store_true')
    parser.add_argument('--from-github', action='store_true')
    parser.add_argument('--archive', type=Path, help='Install this ZIP locally or compare it with the GitHub install.')
    args = parser.parse_args()
    if not args.from_github and args.archive is None:
        args.archive = Path('.artifacts/Lidarr.Plugin.SpotifyNavidrome.net10.0.zip')
    if args.serve:
        serve_fixtures()
    else:
        # Keep Docker bind mounts in the workspace when running on Windows.
        artifacts = Path('.artifacts').resolve()
        artifacts.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='integration-', dir=artifacts) as work:
            run_integration(Path(work), args.archive, args.from_github)
