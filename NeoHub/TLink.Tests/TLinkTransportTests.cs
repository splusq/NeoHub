using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;

namespace DSC.TLink.Tests;

public class TLinkTransportTests
{
    private static (TLinkTransport Transport, PipeWriter PanelWrites) CreateTransport()
    {
        var toUs = new Pipe();
        var fromUs = new Pipe();
        var transport = new TLinkTransport(new DuplexPipe(toUs.Reader, fromUs.Writer), NullLogger<TLinkTransport>.Instance);
        return (transport, toUs.Writer);
    }

    private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }

    // header "AB" 0x7E payload 0x01 0x02 0x7F
    private static readonly byte[] ValidFrame = [0x41, 0x42, 0x7E, 0x01, 0x02, 0x7F];
    // no 0x7E header delimiter before 0x7F
    private static readonly byte[] MalformedFrame = [0x41, 0x42, 0x01, 0x7F];

    [Fact]
    public async Task MalformedFrame_IsReported_ThenReadingContinues()
    {
        var (transport, panel) = CreateTransport();
        await panel.WriteAsync(MalformedFrame);
        await panel.WriteAsync(ValidFrame);
        await panel.CompleteAsync();

        var results = new List<Result<TLinkMessage>>();
        await foreach (var result in transport.ReadAllAsync())
            results.Add(result);

        Assert.Equal(2, results.Count);
        Assert.Equal(TLinkErrorCode.FramingError, results[0].Error!.Value.Code);
        Assert.True(results[1].IsSuccess);
        Assert.Equal(new byte[] { 0x01, 0x02 }, results[1].Value.Payload.ToArray());
    }

    [Fact]
    public async Task GarbageWithNoDelimiter_IsDropped_AndStreamResynchronizes()
    {
        var (transport, panel) = CreateTransport();
        await using var reading = transport.ReadAllAsync().GetAsyncEnumerator();

        // More than MaxFrameBytes with no 0x7F: reported and discarded instead of buffered forever.
        await panel.WriteAsync(Enumerable.Repeat((byte)0x55, TLinkTransport.MaxFrameBytes + 1).ToArray());
        Assert.True(await reading.MoveNextAsync());
        Assert.Equal(TLinkErrorCode.FramingError, reading.Current.Error!.Value.Code);

        // The next frame parses cleanly: the dropped bytes didn't leak into it.
        await panel.WriteAsync(ValidFrame);
        Assert.True(await reading.MoveNextAsync());
        Assert.True(reading.Current.IsSuccess);
        Assert.Equal(new byte[] { 0x41, 0x42 }, reading.Current.Value.Header.ToArray());
        Assert.Equal(new byte[] { 0x01, 0x02 }, reading.Current.Value.Payload.ToArray());
    }

    [Fact]
    public async Task RemoteClose_EndsTheStream()
    {
        var (transport, panel) = CreateTransport();
        await panel.WriteAsync(ValidFrame);
        await panel.CompleteAsync();

        var results = new List<Result<TLinkMessage>>();
        await foreach (var result in transport.ReadAllAsync())
            results.Add(result);

        Assert.Single(results);
    }
}
