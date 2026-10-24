#!/usr/bin/env python3
"""Build an emulator TPK with current SDK development signing, never NuGet's expired keys."""
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parent.parent
sdk = Path(os.environ['TIZEN_SDK_DIRECTORY']).resolve()
distributor = sdk / 'tools/certificate-generator/certificates/distributor/sdk-public/tizen-distributor-signer-new.p12'
if not distributor.is_file():
    sys.exit('Current Tizen Studio public distributor certificate is missing; run setup-tv-sdks.py tizen-cli first')

# This password belongs to the SDK's public development fixture, not a user key.
distributor_password = 'tizenpkcs12passfordsigner'
password = secrets.token_urlsafe(32)


def run(command, env, timeout):
    result = subprocess.run([str(item) for item in command], cwd=root, env=env,
                            capture_output=True, text=True, timeout=timeout)
    output = (result.stdout + result.stderr).replace(password, '[development password]').replace(distributor_password, '[SDK fixture password]')
    print(output, end='', flush=True)
    if result.returncode:
        raise RuntimeError('Tizen development build command failed')


try:
    # Each CI build gets a disposable author key outside the checkout. Secure
    # stable Samsung profiles belong in a separate device/release signing flow.
    with tempfile.TemporaryDirectory(prefix='streamed-tizen-signing-') as temporary:
        signing = Path(temporary)
        environment = {**os.environ, 'JAVA_TOOL_OPTIONS': '-Duser.home=' + str(signing / 'user')}
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
except Exception as error:
    print('::error::' + str(error).replace(password, '[development password]'), file=sys.stderr)
    sys.exit(1)
