# Native playback fixture

These files are a generated test pattern and a quiet sine tone, not a captured
stream. They exercise H.264/AAC decoding, MP4 byte ranges and HLS segments without
external services or credentials. Sandbox builds loop the short clip.

Regenerate with FFmpeg (the MP4 and HLS files are committed so CI does not need it):

```sh
ffmpeg -y -f lavfi -i testsrc2=size=320x180:rate=10 \
  -f lavfi -i sine=frequency=440:sample_rate=22050 -t 12 \
  -filter:a volume=0.02 -c:v libx264 -profile:v baseline -pix_fmt yuv420p \
  -g 20 -b:v 100k -c:a aac -b:a 24k -movflags +faststart -threads 2 \
  tests/fixtures/media/native-test.mp4
ffmpeg -y -i tests/fixtures/media/native-test.mp4 -c copy -hls_time 2 \
  -hls_list_size 0 -hls_segment_filename tests/fixtures/media/native-test-%02d.ts \
  tests/fixtures/media/native-test.m3u8
```
