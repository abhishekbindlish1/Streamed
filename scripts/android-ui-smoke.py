#!/usr/bin/env python3
"""Exercise sandbox packages with remote keys and the actual Android UI."""
import argparse
import os
from pathlib import Path
import re
import subprocess
import time
import urllib.request
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--pairing', action='store_true')
args = parser.parse_args()
adb = os.environ.get('ADB', 'adb')
package = 'com.streamed.tv.android.sandbox' + ('.pairing' if args.pairing else '')
output = Path(os.environ.get('SCREENSHOT_DIR', 'test-results/android'))
output.mkdir(parents=True, exist_ok=True)


def command(*arguments, timeout=40):
    return subprocess.run([adb, *arguments], check=True, capture_output=True, timeout=timeout).stdout


def key(code):
    command('shell', 'input', 'keyevent', str(code))


def snapshot(name):
    (output / name).write_bytes(command('exec-out', 'screencap', '-p'))


def ui():
    # Android may momentarily have no accessibility root during focus or page
    # transitions. Retry fresh snapshots, never reuse an old hierarchy.
    for attempt in range(3):
        try:
            command('shell', 'rm', '-f', '/sdcard/streamed-smoke.xml')
            command('shell', 'uiautomator', 'dump', '/sdcard/streamed-smoke.xml')
            data = command('exec-out', 'cat', '/sdcard/streamed-smoke.xml')
            root = ET.fromstring(data)
            (output / 'android-ui.xml').write_bytes(data)
            return root
        except (ET.ParseError, subprocess.SubprocessError):
            if attempt == 2:
                raise
            time.sleep(1)


def has_text(root, text):
    return any(text.casefold() in node.get('text', '').casefold() for node in root.iter('node'))


def wait_for(predicate, description, timeout=120):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            root = ui()
            if predicate(root):
                return root
        except (ET.ParseError, subprocess.SubprocessError):
            pass
        time.sleep(1)
    raise AssertionError(description)


def launch():
    command('shell', 'am', 'force-stop', package)
    command('shell', 'monkey', '-p', package, '-c', 'android.intent.category.LAUNCHER', '1')


def focus_match(name):
    for _ in range(12):
        root = ui()
        if any(node.get('focused') == 'true' and name.casefold() in node.get('text', '').casefold() for node in root.iter('node')):
            return
        key(20)  # DPAD_DOWN; match selection must work without touch.
    raise AssertionError('Remote could not focus match: ' + name)


def playing_position(root):
    for node in root.iter('node'):
        match = re.search(r'Playing · (\d+):(\d+)', node.get('text', ''))
        if match:
            return int(match[1]) * 60 + int(match[2])
    return 0


def assert_bounded_page_layout():
    size = command('shell', 'wm', 'size').decode(errors='replace')
    dimensions = re.findall(r'(\d+)x(\d+)', size)
    assert dimensions, 'Android did not report a display size'
    limit = max(map(int, dimensions[-1])) * 10
    hierarchy = command('shell', 'dumpsys', 'activity', 'top').decode(errors='replace')
    for line in hierarchy.splitlines():
        if 'ContentViewGroup{' not in line and 'ShellPageContainer{' not in line and 'BoundedPageView{' not in line:
            continue
        bounds = re.search(r'(-?\d+),(-?\d+)-(-?\d+),(-?\d+)', line)
        if bounds:
            left, top, right, bottom = map(int, bounds.groups())
            assert right - left < limit and bottom - top < limit, 'Page layout escaped the device viewport: ' + line.strip()


def test_playback(name, label):
    focus_match(name)
    command('logcat', '-c')
    key(23)  # DPAD_CENTER
    first = wait_for(lambda root: playing_position(root) > 0, label + ' never started native playback')
    position = playing_position(first)
    wait_for(lambda root: playing_position(root) > 0 and playing_position(root) != position,
             label + ' playback position did not advance')
    logs = command('logcat', '-d', '-s', 'StreamedTv:I').decode(errors='replace')
    assert 'Native video rendering: 320x180' in logs, 'No native video dimensions were reported'
    snapshot('android-' + label + '-player.png')
    key(127)  # MEDIA_PAUSE
    wait_for(lambda root: has_text(root, 'Paused ·'), 'Remote pause did not pause ' + label)
    key(126)  # MEDIA_PLAY
    wait_for(lambda root: playing_position(root) > 0, 'Remote play did not resume ' + label)
    command('logcat', '-c')
    key(4)  # BACK
    wait_for(lambda root: has_text(root, 'SANDBOX') and has_text(root, 'Metro Strikers'),
             'Back did not return to the catalog')
    logs = command('logcat', '-d', '-s', 'StreamedTv:I').decode(errors='replace')
    assert 'Player stopped' in logs, 'Playback did not stop on leaving the player'
    print(label + ': remote selection, decoded video, advancing time, pause/resume and Back passed', flush=True)


def fixture_action(action):
    request = urllib.request.Request('http://127.0.0.1:8080/__test/pair/' + action, data=b'{}', method='POST')
    with urllib.request.urlopen(request, timeout=10) as response:
        assert response.status == 200


try:
    key(224)
    key(82)
    command('logcat', '-c')
    if args.pairing:
        # Reset only the separately identified fixture package, never a real account.
        command('shell', 'pm', 'clear', package)
        fixture_action('fail-next-init')
        launch()
        wait_for(lambda root: has_text(root, 'Retry pairing'), 'Pairing network failure had no Retry action')
        key(23)
        wait_for(lambda root: has_text(root, '123 456'), 'Pairing Retry did not obtain a code')
        snapshot('android-pairing.png')
        key(4)
        root = ui()
        assert not has_text(root, 'Metro Strikers'), 'Back bypassed the authentication gate'
        launch()
        wait_for(lambda root: has_text(root, '123 456'), 'Sign-in was not restored after Back')
        fixture_action('complete')
        wait_for(lambda root: has_text(root, 'SANDBOX') and has_text(root, 'Metro Strikers'),
                 'Completed pairing did not open the catalog')
        launch()
        wait_for(lambda root: has_text(root, 'Metro Strikers'), 'The paired fixture session did not survive restart')
        print('Pairing: retry, Back gate, completion and persisted-session restart passed', flush=True)
    else:
        launch()
        wait_for(lambda root: has_text(root, 'SANDBOX') and has_text(root, 'Metro Strikers'),
                 'Sandbox did not render fixture matches')
        assert_bounded_page_layout()
        snapshot('android-catalog.png')
        test_playback('Metro Strikers', 'hls')
        test_playback('Northern XI', 'mp4')
except Exception as error:
    message = str(error).replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
    print('::error::Android UI test: ' + message, flush=True)
    try:
        root = ui()
        text = ' | '.join(node.get('text', '') for node in root.iter('node') if node.get('text'))[:2500]
        print('::notice::Visible Android UI: ' + text.replace('%', '%25').replace('\n', ' '), flush=True)
    except (ET.ParseError, subprocess.SubprocessError):
        pass
    try:
        snapshot('android-failure.png')
    except subprocess.SubprocessError:
        pass
    raise
