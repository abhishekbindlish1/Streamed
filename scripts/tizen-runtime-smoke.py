#!/usr/bin/env python3
"""Launch the actual Samsung VM and check the built native TPK survives startup."""
import os
from pathlib import Path
import signal
import subprocess
import sys
import time
from PIL import ImageGrab

root = Path(__file__).resolve().parent.parent
output = root / 'test-results/tizen-emulator'
output.mkdir(parents=True, exist_ok=True)
sdk = Path(os.environ['TIZEN_SDK_DIRECTORY'])
data = Path(os.environ['TIZEN_SDK_DATA_DIRECTORY'])
env = {**os.environ, 'JAVA_TOOL_OPTIONS': '-Duser.home=' + str(data / 'user'), 'QT_QPA_PLATFORM': 'xcb', 'QT_DEBUG_PLUGINS': '1'}
emcli = sdk / 'tools/emulator/bin/em-cli'
tizen = sdk / 'tools/ide/bin/tizen'
sdb = sdk / 'tools/sdb'
launcher = None
serial = None


def command(arguments, name, timeout=45):
    result = subprocess.run([str(x) for x in arguments], capture_output=True, env=env, timeout=timeout)
    text = (result.stdout + result.stderr).decode(errors='replace')
    (output / (name + '.log')).write_text(text)
    if result.returncode:
        raise RuntimeError(name + ' failed: ' + text[-2200:])
    return text


try:
    if not os.access('/dev/kvm', os.R_OK | os.W_OK):
        raise RuntimeError('Samsung TV 10 x64 requires writable /dev/kvm; use the accelerated CI runner')
    command([emcli, 'create', '-n', 'StreamedSamsungCi', '-p', 'tv-samsung-10.0-x86_64', '-t', 'HD1080 TV', '-r', '1024', '-w', 'yes', '-g', 'no'], 'create-vm')
    launch_log = (output / 'launch-vm.log').open('wb')
    launcher = subprocess.Popen([str(emcli), 'launch', '-n', 'StreamedSamsungCi'], env=env,
                                stdout=launch_log, stderr=subprocess.STDOUT, start_new_session=True)
    deadline = time.monotonic() + 300
    while time.monotonic() < deadline:
        if launcher.poll() not in (None, 0):
            raise RuntimeError('Samsung emulator manager exited before boot: ' + (output / 'launch-vm.log').read_text(errors='replace')[-2500:])
        devices = command([sdb, 'devices'], 'sdb-devices', timeout=15)
        connected = [line.split()[0] for line in devices.splitlines()
                     if 'device' in line.split()[1:2] and ('emulator' in line or 'StreamedSamsungCi' in line)]
        if len(connected) == 1:
            serial = connected[0]
            break
        time.sleep(3)
    if not serial:
        raise RuntimeError('Samsung VM did not connect to SDB within five minutes')
    command([sdb, '-s', serial, 'capability'], 'sdb-capability')
    command([sdb, '-s', serial, 'shell', 'date', '-u'], 'device-clock')
    packages = list((root / 'apps/tizen-dotnet/bin/Release/tizen90').glob('*.tpk'))
    if len(packages) != 1:
        raise RuntimeError('Expected exactly one freshly built native TPK')
    # This is the built development package, not a re-signed or modified test copy.
    # The .NET first-app guide specifies SDB's native TPK installer. The TV
    # web CLI routes "tizen install" through wascmd instead of native pkgcmd.
    result = command([sdb, '-s', serial, 'install', packages[0]], 'install-native-tpk', timeout=120)
    print('::notice::Samsung TPK install: ' + result[-1800:].replace('\n', '%0A'), flush=True)
    command([tizen, 'run', '-p', 'com.streamed.tv.dotnet', '-s', serial], 'launch-native-tpk', timeout=90)
    time.sleep(15)
    # Samsung disables the general interactive shell. Use its advertised
    # appid2pid capability, as the SDK's own ApplicationUtil does.
    process = command([sdb, '-s', serial, 'shell', '/usr/bin/appid2pid', 'com.streamed.tv.dotnet'], 'native-processes')
    if not any(word.isdecimal() and int(word) > 1 for word in process.split()):
        raise RuntimeError('The native app did not remain running after launch')
    ImageGrab.grab(xdisplay=os.environ.get('DISPLAY')).save(output / 'tizen-native-startup.png')
    print('Samsung TV 10 emulator booted; the source TPK installed, launched and remained running. Media/provider acceptance is separate.')
except Exception as error:
    print('::error::Samsung runtime test: ' + str(error).replace('\n', '%0A'), flush=True)
    if serial:
        # The TV CLI can collapse native installer errors into "install app
        # failed". Preserve the device's actual package-manager diagnostics.
        probes = {
            'device-package-log': ['shell', 'dlogutil', '-d'],
            'device-journal': ['shell', 'journalctl', '-n', '120', '--no-pager'],
            'device-package-list': ['shell', 'pkgcmd', '-l'],
        }
        probes['device-clock'] = ['shell', 'date', '-u']
        for name, arguments in probes.items():
            try:
                text = command([sdb, '-s', serial, *arguments], name, timeout=20)
                lines = [line for line in text.splitlines() if any(word in line.lower() for word in
                         ['pkg', 'package', 'certificate', 'signature', 'streamed', 'dotnet', 'failed', 'error', 'denied', 'not support'])]
                excerpt = '\n'.join(lines[-60:]) or text[-1200:]
                print('::notice::Samsung ' + name + ': ' + excerpt.replace('%', '%25').replace('\n', '%0A'), flush=True)
            except Exception as probe_error:
                print('::notice::Samsung ' + name + ': ' + str(probe_error).replace('%', '%25').replace('\n', '%0A'), flush=True)
        capability = output / 'sdb-capability.log'
        if capability.exists():
            print('::notice::Samsung capabilities: ' + capability.read_text().replace('%', '%25').replace('\n', '%0A'), flush=True)
    for name in ['launch-vm.log', 'sdb-devices.log']:
        path = output / name
        if path.exists():
            text = path.read_text(errors='replace')[-4500:]
            print('::notice::Samsung ' + name + ': ' + text.replace('%', '%25').replace('\n', '%0A'), flush=True)
    for path in list(data.rglob('*.log'))[-6:]:
        if 'emulator' in str(path):
            full = path.read_text(errors='replace')
            important = [line for line in full.splitlines() if any(word in line.lower() for word in ['could not', 'cannot', 'not found', 'failed', 'xcb', 'plugin'])]
            text = '\n'.join(important[-22:]) + '\n' + full[-1800:]
            print('::notice::Samsung VM log: ' + text.replace('%', '%25').replace('\n', '%0A'), flush=True)
    try:
        ImageGrab.grab(xdisplay=os.environ.get('DISPLAY')).save(output / 'tizen-runtime-failure.png')
    except Exception:
        pass
    sys.exit(1)
finally:
    if launcher:
        try:
            os.killpg(launcher.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
