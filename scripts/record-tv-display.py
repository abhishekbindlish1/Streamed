#!/usr/bin/env python3
"""Record an Xvfb SDK session, including failures, without changing its exit status."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--size', default='2560x1440')
parser.add_argument('command', nargs=argparse.REMAINDER)
args = parser.parse_args()
command = args.command[1:] if args.command[:1] == ['--'] else args.command
if not command or not os.environ.get('DISPLAY'):
    parser.error('Run under Xvfb with a command after --')
if not shutil.which('ffmpeg') or not shutil.which('ffprobe'):
    parser.error('Install ffmpeg and ffprobe before recording')

args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.unlink(missing_ok=True)
recorder = None
status = 1
try:
    with args.output.with_suffix('.ffmpeg.log').open('wb') as log:
        recorder = subprocess.Popen([
            'ffmpeg', '-hide_banner', '-loglevel', 'warning', '-y',
            '-f', 'x11grab', '-video_size', args.size, '-framerate', '8',
            '-i', os.environ['DISPLAY'], '-an', '-vf', 'scale=1280:-2',
            '-c:v', 'libx264', '-threads', '1', '-preset', 'veryfast', '-crf', '28',
            '-pix_fmt', 'yuv420p', '-movflags', '+frag_keyframe+empty_moov+default_base_moof',
            '-t', '600', str(args.output)
        ], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=log)
        deadline = time.monotonic() + 10
        while not args.output.exists() and recorder.poll() is None and time.monotonic() < deadline:
            time.sleep(0.05)
        if recorder.poll() is not None or not args.output.exists():
            raise RuntimeError('Display recorder could not start; see its ffmpeg log')
        try:
            status = subprocess.run(command).returncode
        finally:
            if recorder.poll() is None:
                try:
                    recorder.communicate(input=b'q\n', timeout=15)
                except subprocess.TimeoutExpired:
                    recorder.kill()
                    recorder.wait(timeout=5)
        if recorder.returncode:
            raise RuntimeError('Display recorder failed; see its ffmpeg log')
        probe = subprocess.run([
            'ffprobe', '-v', 'error', '-select_streams', 'v:0',
            '-show_entries', 'stream=width,height:format=duration', '-of', 'json', str(args.output)
        ], capture_output=True, text=True, check=True)
        info = json.loads(probe.stdout)
        if not info.get('streams') or float(info['format'].get('duration', '0')) <= 0:
            raise RuntimeError('The SDK session recording contains no playable video')
        stream = info['streams'][0]
        print(f"::notice::SDK session video: {args.output} ({stream['width']}x{stream['height']}, {float(info['format']['duration']):.1f}s). Download the job's results artifact to watch it.", flush=True)
except Exception as error:
    print('::error::Display recording: ' + str(error), file=sys.stderr)
    if status == 0:
        status = 1
finally:
    if recorder and recorder.poll() is None:
        recorder.kill()
        recorder.wait(timeout=5)
sys.exit(status if status >= 0 else 128 - status)
