using System.Threading.Channels;
using WrapperApi.Models;

namespace WrapperApi.Services;

public interface IBackgroundJobQueue
{
    void Enqueue(Job job, string dxValue);
    IAsyncEnumerable<(Job job, string dxValue)> ReadAllAsync(CancellationToken cancellationToken);
}

public class BackgroundJobQueue : IBackgroundJobQueue
{
    private readonly Channel<(Job job, string dxValue)> _channel;

    public BackgroundJobQueue()
    {
        var options = new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _channel = Channel.CreateBounded<(Job, string)>(options);
    }

    public void Enqueue(Job job, string dxValue)
    {
        if (!_channel.Writer.TryWrite((job, dxValue)))
        {
            throw new InvalidOperationException("Unable to enqueue job. Channel is full.");
        }
    }

    public IAsyncEnumerable<(Job job, string dxValue)> ReadAllAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}