#!/usr/bin/env python3
"""Build an emulator TPK with current SDK development signing, never NuGet's expired keys."""
import base64
import os
from pathlib import Path
import pty
import re
import secrets
import select
import shutil
import signal
import subprocess
import sys
import tempfile
import termios
import time

root = Path(__file__).resolve().parent.parent
sdk = Path(os.environ['TIZEN_SDK_DIRECTORY']).resolve()
distributor = sdk / 'tools/certificate-generator/certificates/distributor/sdk-public/tizen-distributor-signer-new.p12'
if not distributor.is_file():
    sys.exit('Current Tizen Studio public distributor certificate is missing; run setup-tv-sdks.py tizen-cli first')

# This password belongs to the SDK's public development fixture, not a user key.
signing_names = ['TIZEN_AUTHOR_P12_BASE64', 'TIZEN_AUTHOR_PASSWORD',
                 'TIZEN_DISTRIBUTOR_P12_BASE64', 'TIZEN_DISTRIBUTOR_PASSWORD']
configured = [bool(os.environ.get(name)) for name in signing_names]
if any(configured) and not all(configured):
    sys.exit('Supply all four TIZEN author/distributor signing variables, or none')
custom_profile = all(configured)
distributor_password = os.environ['TIZEN_DISTRIBUTOR_PASSWORD'] if custom_profile else 'tizenpkcs12passfordsigner'
password = os.environ['TIZEN_AUTHOR_PASSWORD'] if custom_profile else secrets.token_urlsafe(32)


def redact(text):
    for value in [password, distributor_password, *[os.environ.get(name, '') for name in signing_names]]:
        if value:
            text = text.replace(value, '[signing value]')
    return text


def run(command, env, timeout):
    result = subprocess.run([str(item) for item in command], cwd=root, env=env,
                            capture_output=True, text=True, timeout=timeout)
    output = redact(result.stdout + result.stderr)
    print(output, end='', flush=True)
    if result.returncode:
        raise RuntimeError('Tizen development build command failed')


def package_with_sdk(package, destination, profile, env):
    # Without a desktop password manager, Studio reads passwords from a console.
    # Answer only its known signing prompts; never save passwords to the SDK.
    master, slave = pty.openpty()
    attributes = termios.tcgetattr(slave)
    attributes[3] &= ~termios.ECHO
    termios.tcsetattr(slave, termios.TCSANOW, attributes)
    process = subprocess.Popen([str(sdk / 'tools/ide/bin/tizen'), 'package', '-t', 'tpk',
                                '-s', profile, '-o', str(destination), '--', str(package)],
                               env=env, stdin=slave, stdout=slave, stderr=slave, start_new_session=True)
    os.close(slave)
    output, cursor = b'', 0
    prompts = re.compile(rb'Author password:|Distributor\d* password:|Yes: \(Y\), No: \(N\) \?', re.I)
    deadline = time.monotonic() + 90
    try:
        while time.monotonic() < deadline:
            ready, _, _ = select.select([master], [], [], 0.5)
            if ready:
                try:
                    block = os.read(master, 65536)
                except OSError:
                    break
                if not block:
                    break
                output += block
                for match in prompts.finditer(output, cursor):
                    prompt = match.group().lower()
                    answer = password if prompt.startswith(b'author') else distributor_password if prompt.startswith(b'distributor') else 'N'
                    os.write(master, (answer + '\n').encode())
                    cursor = match.end()
            if process.poll() is not None:
                break
        if process.poll() is None:
            process.wait(timeout=5)
        if process.returncode or not (destination / package.name).is_file():
            raise RuntimeError('Tizen Studio did not produce the signed TPK')
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGTERM)
            process.wait(timeout=5)
        os.close(master)
        print(redact(output.decode(errors='replace')), flush=True)


try:
    # Each CI build gets a disposable author key outside the checkout. Secure
    # stable Samsung profiles belong in a separate device/release signing flow.
    with tempfile.TemporaryDirectory(prefix='streamed-tizen-signing-') as temporary:
        signing = Path(temporary)
        environment = {name: value for name, value in os.environ.items() if name not in signing_names}
        environment['JAVA_TOOL_OPTIONS'] = '-Duser.home=' + str(signing / 'user')
        if custom_profile:
            for name, filename in [('TIZEN_AUTHOR_P12_BASE64', 'author.p12'),
                                   ('TIZEN_DISTRIBUTOR_P12_BASE64', 'distributor.p12')]:
                target = signing / filename
                target.write_bytes(base64.b64decode(''.join(os.environ[name].split()), validate=True))
                target.chmod(0o600)
            distributor = signing / 'distributor.p12'
        else:
            run([sdk / 'tools/ide/bin/tizen', 'certificate', '-a', 'StreamedDevelopment',
                 '-p', password, '-n', 'Streamed Development', '-o', 'Streamed',
                 '-f', 'author', '--', signing], environment, 90)
        environment.update(AuthorPath=str(signing / 'author.p12'), AuthorPass=password,
                           DistributorPath=str(distributor), DistributorPass=distributor_password)
        # Remove only generated TPKs: a manifest version change must not leave a
        # stale, differently signed package eligible for release collection.
        for package in (root / 'apps/tizen-dotnet/bin/Release/tizen90').glob('*.tpk'):
            package.unlink()
        run(['dotnet', 'build', 'apps/tizen-dotnet/StreamedTv.csproj', '-c', 'Release'], environment, 300)
        packages = list((root / 'apps/tizen-dotnet/bin/Release/tizen90').glob('*.tpk'))
        if len(packages) != 1:
            raise RuntimeError('Expected one fresh TPK before Studio signing')
        profile = 'StreamedDevelopment' + secrets.token_hex(6)
        try:
            run([sdk / 'tools/ide/bin/tizen', 'security-profiles', 'add', '-n', profile,
                 '-a', signing / 'author.p12', '-p', password, '-d', distributor,
                 '-dp', distributor_password, '-f'], environment, 60)
            destination = signing / 'packages'
            destination.mkdir()
            package_with_sdk(packages[0], destination, profile, environment)
            staged = packages[0].with_suffix('.tpk.signed')
            shutil.copyfile(destination / packages[0].name, staged)
            staged.replace(packages[0])
        finally:
            run([sdk / 'tools/ide/bin/tizen', 'security-profiles', 'remove', '-n', profile], environment, 60)
except Exception as error:
    print('::error::' + redact(str(error)), file=sys.stderr)
    sys.exit(1)
