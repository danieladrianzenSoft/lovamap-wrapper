using System.Threading.Channels;
using WrapperApi.Models;

namespace WrapperApi.Services
{
    public interface IBackgroundJobQueue
    {
        void Enqueue(Job job, string dxValue, string? uploadUrl = null, string? uploadToken = null);
        IAsyncEnumerable<(Job job, string dxValue, string? uploadUrl, string? uploadToken)> ReadAllAsync(CancellationToken cancellationToken);
    }

    public class BackgroundJobQueue : IBackgroundJobQueue
    {
        private readonly Channel<(Job job, string dxValue, string? uploadUrl, string? uploadToken)> _channel;

        public BackgroundJobQueue()
        {
            var options = new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait
            };
            _channel = Channel.CreateBounded<(Job, string, string?, string?)>(options);
        }

        public void Enqueue(Job job, string dxValue, string? uploadUrl = null, string? uploadToken = null)
        {
            if (!_channel.Writer.TryWrite((job, dxValue, uploadUrl, uploadToken)))
            {
                throw new InvalidOperationException("Unable to enqueue job. Channel is full.");
            }
        }

        public IAsyncEnumerable<(Job job, string dxValue, string? uploadUrl, string? uploadToken)> ReadAllAsync(CancellationToken cancellationToken)
        {
            return _channel.Reader.ReadAllAsync(cancellationToken);
        }
    }
}