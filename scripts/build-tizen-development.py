#!/usr/bin/env python3
"""Build an emulator TPK with current SDK development signing, never NuGet's expired keys."""
import base64
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


try:
    # Keep both disposable SDK keys and configured Samsung keys outside source.
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
            raise RuntimeError('Expected one freshly signed TPK')
except Exception as error:
    print('::error::' + redact(str(error)), file=sys.stderr)
    sys.exit(1)
