#!/usr/bin/env python3
"""Run locally to save an existing Samsung profile as GitHub Actions secrets."""
import argparse
import base64
from datetime import datetime, timezone
import getpass
import os
from pathlib import Path
import re
import subprocess
import sys

REPOSITORY = 'abhishekbindlish1/Streamed'


class SetupError(Exception):
    pass


def run(arguments, *, data=None, environment=None):
    try:
        return subprocess.run(arguments, input=data, capture_output=True,
                              env=environment, timeout=60, check=False)
    except (OSError, subprocess.TimeoutExpired):
        # A child process can include credentials in an error. Never echo it.
        raise SetupError('Could not run ' + arguments[0] + '; check its installation and connection') from None


def read_profile(path):
    try:
        if not path.is_file() or path.stat().st_size > 5 * 1024 * 1024:
            raise SetupError('Each profile must be a P12 file smaller than 5 MiB')
        return path.read_bytes()
    except OSError:
        raise SetupError('Could not read one of the selected P12 files') from None


def validate_profile(contents, password, label):
    # Export public certificates only. The password is passed in the child's
    # environment, never in process arguments, logs, or a temporary file.
    environment = {**os.environ, 'STREAMED_PROFILE_PASSWORD': password, 'LC_ALL': 'C'}
    command = ['openssl', 'pkcs12', '-nokeys', '-passin', 'env:STREAMED_PROFILE_PASSWORD']
    result = run(command, data=contents, environment=environment)
    if result.returncode and b'unsupported' in result.stderr.lower():
        result = run(command + ['-legacy'], data=contents, environment=environment)
    if result.returncode:
        raise SetupError(label + ' P12 could not be opened; check the password and OpenSSL support')
    certificates = re.findall(rb'-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----', result.stdout, re.S)
    if not certificates:
        raise SetupError(label + ' P12 has no public certificates')
    now = datetime.now(timezone.utc)
    expires = []
    for certificate in certificates:
        dates = run(['openssl', 'x509', '-noout', '-dates'], data=certificate, environment=environment)
        try:
            values = dict(line.split('=', 1) for line in dates.stdout.decode('ascii').splitlines())
            start, end = [datetime.strptime(values[key], '%b %d %H:%M:%S %Y %Z').replace(tzinfo=timezone.utc)
                          for key in ['notBefore', 'notAfter']]
        except (KeyError, ValueError, UnicodeError):
            raise SetupError('Could not read ' + label + ' certificate validity') from None
        if dates.returncode or not start <= now < end:
            raise SetupError(label + ' contains an expired or not-yet-valid certificate; no secrets were updated')
        expires.append(end)
    return min(expires).date().isoformat()


def configure(author, distributor, *, repository=REPOSITORY, password_prompt=getpass.getpass):
    # Check access before asking for passwords or changing any existing secrets.
    access = run(['gh', 'secret', 'list', '--repo', repository, '--json', 'name'])
    if access.returncode:
        raise SetupError('Cannot access repository secrets. Run gh auth login with an account allowed to manage this repository')
    author_bytes, distributor_bytes = read_profile(author), read_profile(distributor)
    author_password = password_prompt('Author P12 password: ')
    distributor_password = password_prompt('Distributor P12 password: ')
    if not author_password or not distributor_password:
        raise SetupError('Both passwords are required; no secrets were updated')
    author_expiry = validate_profile(author_bytes, author_password, 'Author')
    distributor_expiry = validate_profile(distributor_bytes, distributor_password, 'Distributor')
    print('Public certificate dates checked: author expires ' + author_expiry + '; distributor expires ' + distributor_expiry)
    print('Samsung issuance and target authorization will be checked by the emulator install.')
    secrets = {
        'TIZEN_AUTHOR_P12_BASE64': base64.b64encode(author_bytes),
        'TIZEN_AUTHOR_PASSWORD': author_password.encode(),
        'TIZEN_DISTRIBUTOR_P12_BASE64': base64.b64encode(distributor_bytes),
        'TIZEN_DISTRIBUTOR_PASSWORD': distributor_password.encode(),
    }
    for name, value in secrets.items():
        result = run(['gh', 'secret', 'set', name, '--repo', repository], data=value)
        if result.returncode:
            raise SetupError('Secret upload failed at ' + name + '. Some secrets may have changed; rerun this entire helper before starting signing jobs')
        print('Saved ' + name)
    result = run(['gh', 'workflow', 'run', 'vendor-tv.yml', '--repo', repository, '--ref', 'main'])
    if result.returncode:
        raise SetupError('Profile saved, but runtime checks could not start. Run gh workflow run vendor-tv.yml --repo ' + repository + ' --ref main')
    print('Started main runtime checks: https://github.com/' + repository + '/actions/workflows/vendor-tv.yml')
    print('Profile configuration is complete. Installation is only verified when the Samsung job passes.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--author', type=Path, required=True, help='Existing Samsung author.p12')
    parser.add_argument('--distributor', type=Path, required=True, help='Samsung distributor.p12 authorizing the emulator')
    args = parser.parse_args()
    if not sys.stdin.isatty():
        raise SetupError('Run in an interactive terminal so password prompts do not echo')
    configure(args.author.expanduser(), args.distributor.expanduser())


if __name__ == '__main__':
    try:
        main()
    except (SetupError, EOFError, KeyboardInterrupt) as error:
        print('Signing setup stopped: ' + (str(error) or 'cancelled'), file=sys.stderr)
        sys.exit(1)
