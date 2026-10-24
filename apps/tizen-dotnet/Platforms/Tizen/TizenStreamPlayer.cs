using System;
using System.Threading;
using System.Threading.Tasks;
using StreamedTv.Models;
using StreamedTv.Services;

#if TIZEN
using Tizen.Multimedia;
using TvPlayer = Tizen.Multimedia.Player;
#endif

namespace StreamedTv.Platforms.Tizen
{
    public sealed class TizenStreamPlayer : IStreamPlayer
    {
#if TIZEN
        private TvPlayer _player;
#endif
        public bool IsPlaying { get; private set; }

        public async Task PlayAsync(StreamOption stream, CancellationToken cancellationToken)
        {
            if (stream == null || string.IsNullOrWhiteSpace(stream.PlaybackUrl))
            {
                throw new ArgumentException("A stream with a playable URL is required.");
            }

            await StopAsync();

#if TIZEN
            cancellationToken.ThrowIfCancellationRequested();
            _player = new TvPlayer();
            _player.SetSource(new MediaUriSource(stream.PlaybackUrl));
            await _player.PrepareAsync();
            cancellationToken.ThrowIfCancellationRequested();
            _player.Start();
            IsPlaying = true;
#else
            await Task.Delay(1, cancellationToken);
            throw new NotSupportedException("Tizen TV playback requires a Tizen .NET TV build with Tizen.TV.Multimedia available.");
#endif
        }

        public Task PauseAsync()
        {
#if TIZEN
            if (_player != null)
            {
                _player.Pause();
                IsPlaying = false;
            }
#endif
            return Task.CompletedTask;
        }

        public Task ResumeAsync()
        {
#if TIZEN
            if (_player != null)
            {
                _player.Start();
                IsPlaying = true;
            }
#endif
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
#if TIZEN
            if (_player != null)
            {
                try
                {
                    _player.Stop();
                }
                catch
                {
                    // The TV player can throw if the stream never reached prepared state.
                }

                _player.Dispose();
                _player = null;
            }
#endif
            IsPlaying = false;
            return Task.CompletedTask;
        }
    }
}
