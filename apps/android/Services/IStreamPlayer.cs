using System.Threading;
using System.Threading.Tasks;
using StreamedTv.Models;

namespace StreamedTv.Services
{
    public interface IStreamPlayer
    {
        bool IsPlaying { get; }

        Task PlayAsync(StreamOption stream, CancellationToken cancellationToken);

        Task PauseAsync();

        Task ResumeAsync();

        Task StopAsync();
    }
}
