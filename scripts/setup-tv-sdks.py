#!/usr/bin/env python3
"""Prepare official, pinned Linux TV SDKs without writing into a user's home."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tarfile
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('platform', choices=['webos', 'tizen'])
parser.add_argument('--root', type=Path, required=True)
parser.add_argument('--cache', type=Path, required=True)
args = parser.parse_args()
root, cache = args.root.resolve(), args.cache.resolve()
root.mkdir(parents=True, exist_ok=True)
cache.mkdir(parents=True, exist_ok=True)
versions = json.loads(Path(__file__).with_name('tv-sdk-versions.json').read_text())


def download(name, url, digest):
    target = cache / name
    if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() == digest:
        return target
    print('Fetching official SDK artifact: ' + name, flush=True)
    request = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0', 'Referer': 'https://webostv.developer.lge.com/'})
    temporary = target.with_suffix('.partial')
    with urllib.request.urlopen(request, timeout=45) as response, temporary.open('wb') as output:
        while block := response.read(1024 * 1024):
            output.write(block)
    if hashlib.sha256(temporary.read_bytes()).hexdigest() != digest:
        temporary.unlink()
        raise ValueError('SDK checksum mismatch: ' + name)
    temporary.replace(target)
    return target


def unpack_zip(archive, destination, prefix=''):
    destination = destination.resolve()
    with zipfile.ZipFile(archive) as bundle:
        if bundle.testzip() is not None:
            raise ValueError('Corrupt SDK ZIP')
        for item in bundle.infolist():
            if not item.filename.startswith(prefix):
                continue
            relative = Path(item.filename[len(prefix):])
            if relative.is_absolute() or '..' in relative.parts:
                raise ValueError('SDK archive path escapes its directory')
            path = destination / relative
            if item.is_dir():
                path.mkdir(parents=True, exist_ok=True)
                continue
            path.parent.mkdir(parents=True, exist_ok=True)
            if not path.parent.resolve().is_relative_to(destination):
                raise ValueError('SDK archive parent escapes its directory')
            if path.is_symlink():
                path.unlink()
            mode = item.external_attr >> 16
            if stat.S_ISLNK(mode):
                link = bundle.read(item).decode()
                if not (path.parent / link).resolve().is_relative_to(destination):
                    raise ValueError('SDK symlink escapes its directory')
                path.unlink(missing_ok=True)
                path.symlink_to(link)
            else:
                path.write_bytes(bundle.read(item))
                if mode & 0o777:
                    path.chmod(mode & 0o777)


def webos():
    spec = versions['webos']
    archive = cache / 'webos26.zip'
    if not archive.exists() or hashlib.sha256(archive.read_bytes()).hexdigest() != spec['sha256']:
        headers = {'Content-Type': 'application/json', 'Origin': 'https://webostv.developer.lge.com',
                   'Referer': 'https://webostv.developer.lge.com/', 'X-Requested-With': 'XMLHttpRequest'}
        request = urllib.request.Request('https://webostv.developer.lge.com/api/downloadSDK',
                                        data=json.dumps({'fileId': spec['fileId']}).encode(), headers=headers, method='POST')
        with urllib.request.urlopen(request, timeout=30) as response:
            encrypted = json.load(response)['fileIdEnc']
        endpoint = 'https://developer.lge.com/common/file/DownloadFilePath.ajax?' + urllib.parse.urlencode({'fileId': encrypted})
        with urllib.request.urlopen(urllib.request.Request(endpoint, data=b'', headers=headers, method='POST'), timeout=30) as response:
            metadata = ET.fromstring(response.read())
        url = next(element.attrib['gftsUrl'] for element in metadata.iter() if element.attrib.get('gftsUrl'))
        archive = download('webos26.zip', url, spec['sha256'])
    sdk = root / 'webos'
    unpack_zip(archive, sdk)
    appimages = list(sdk.rglob('*.AppImage'))
    if len(appimages) != 1:
        raise ValueError('Expected one LG Simulator AppImage')
    appimage = appimages[0]
    appimage.chmod(0o755)
    subprocess.run([str(appimage), '--appimage-extract'], cwd=appimage.parent, stdout=subprocess.DEVNULL, check=True)
    appdir = appimage.parent / 'squashfs-root'
    return {'WEBOS_SIMULATOR_APPDIR': str(appdir), 'WEBOS_SIMULATOR_VERSION': spec['version']}


def tizen():
    spec = versions['tizen']
    installer = download('tizen-web-cli-6.1.bin', spec['url'], spec['sha256']).read_bytes()
    length = int(re.search(rb'ORI_FILE_LEN="(\d+)"', installer[:1000])[1])
    checksum = re.search(rb'ORI_FILE_md5sum="([a-f0-9]+)"', installer[:1000])[1].decode()
    payload = installer.split(b'\n', length - 1)[length - 1]
    if hashlib.md5(payload).hexdigest() != checksum:
        raise ValueError('Tizen installer internal checksum mismatch')
    sdk = root / 'tizen-studio'
    sdk.mkdir(exist_ok=True)
    with tarfile.open(fileobj=io.BytesIO(payload), mode='r:gz') as bundle:
        member = bundle.extractfile('tizen-sdk.zip')
        if member is None:
            raise ValueError('Tizen SDK payload is missing')
        unpack_zip(io.BytesIO(member.read()), sdk)
    tv = download('samsung-tv10.zip', spec['tvUrl'], spec['tvSha256'])
    with zipfile.ZipFile(tv) as bundle:
        for block in bundle.read('pkg_list_ubuntu-64').decode().strip().split('\n\n'):
            record = dict(line.split(' : ', 1) for line in block.splitlines() if ' : ' in line)
            if 'Path' not in record:
                continue
            data = bundle.read(record['Path'].lstrip('/'))
            if hashlib.sha256(data).hexdigest() != record['SHA256']:
                raise ValueError('Samsung TV SDK vendor checksum mismatch')
            unpack_zip(io.BytesIO(data), sdk, prefix='data/')
    for package in spec['auxiliary']:
        archive = download(package['name'] + '.zip', package['url'], package['sha256'])
        unpack_zip(archive, sdk, prefix='data/')
    data = root / 'tizen-data'
    data.mkdir(exist_ok=True)
    (sdk / 'sdk.info').write_text(f'TIZEN_SDK_INSTALLED_PATH={sdk}\nTIZEN_SDK_DATA_PATH={data}\nJDK_PATH={sdk}/jdk\nTIZEN_SDK_VERSION=6.1\n')
    return {'TIZEN_SDK_DIRECTORY': str(sdk), 'TIZEN_SDK_DATA_DIRECTORY': str(data)}


try:
    configuration = webos() if args.platform == 'webos' else tizen()
    print(json.dumps(configuration, indent=2))
    if os.environ.get('GITHUB_ENV'):
        with open(os.environ['GITHUB_ENV'], 'a') as output:
            for name, value in configuration.items():
                output.write(name + '=' + value + '\n')
except Exception as error:
    # SDK download links can contain temporary tokens. Keep them out of logs.
    message = re.sub(r'https?://\S+', '[SDK URL]', str(error))
    print('::error::TV SDK setup: ' + message, file=sys.stderr)
    sys.exit(1)
