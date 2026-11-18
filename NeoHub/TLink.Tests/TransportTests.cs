using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;

namespace DSC.TLink.Tests;

/// <summary>
/// TLink framing over real pipes. Every wait is on bytes the test wrote itself; nothing depends
/// on timing.
/// </summary>
public class TransportTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);   // hang guard only

    private sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }

    private readonly Pipe _toTransport = new();
    private readonly Pipe _fromTransport = new();
    private readonly TLinkTransport _transport;

    public TransportTests()
        => _transport = new TLinkTransport(new Duplex(_toTransport.Reader, _fromTransport.Writer), NullLogger<TLinkTransport>.Instance);

    private static byte[] Frame(string header, string payload)
        => [.. Encoding.ASCII.GetBytes(header), 0x7E, .. Encoding.ASCII.GetBytes(payload), 0x7F];

    private async Task Arrive(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
            await _toTransport.Writer.WriteAsync(chunk);
    }

    [Fact]
    public async Task SentFrame_ReadsBackIdentically_IncludingBytesThatNeedStuffing()
    {
        byte[] payload = [0x01, 0x7D, 0x7E, 0x7F, 0x02];
        Assert.True((await _transport.SendAsync(Encoding.ASCII.GetBytes("HDR"), payload)).IsSuccess);

        // Loop what was sent back in.
        var sent = await _fromTransport.Reader.ReadAsync().AsTask().WaitAsync(Guard);
        await Arrive(sent.Buffer.ToArray());
        _fromTransport.Reader.AdvanceTo(sent.Buffer.End);

        var result = await _transport.ReadMessageAsync().WaitAsync(Guard);
        Assert.True(result.IsSuccess);
        Assert.Equal("HDR", Encoding.ASCII.GetString(result.Value.Header.Span));
        Assert.Equal(payload, result.Value.Payload.ToArray());
    }

    [Fact]
    public async Task MalformedFrame_IsReported_ThenReadingContinues()
    {
        await Arrive([.. Encoding.ASCII.GetBytes("no header delimiter"), 0x7F], Frame("HDR", "ok"));

        await using var frames = _transport.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(Guard));
        Assert.Equal(TLinkErrorCode.FramingError, frames.Current.Error!.Value.Code);
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(Guard));
        Assert.Equal("ok", Encoding.ASCII.GetString(frames.Current.Value.Payload.Span));
    }

    /// <summary>
    /// Two frames in one read (one TCP segment): the second must not wait for more bytes.
    /// Before the fix it stayed buffered until the panel sent something else.
    /// </summary>
    [Fact]
    public async Task TwoFramesInOneRead_BothArrive_WithoutWaitingForMoreData()
    {
        await Arrive([.. Frame("HDR", "first"), .. Frame("HDR", "second")]);

        var first = await _transport.ReadMessageAsync().WaitAsync(Guard);
        var second = await _transport.ReadMessageAsync().WaitAsync(Guard);
        Assert.Equal("first", Encoding.ASCII.GetString(first.Value.Payload.Span));
        Assert.Equal("second", Encoding.ASCII.GetString(second.Value.Payload.Span));
    }

    [Fact]
    public async Task GarbageWithNoDelimiter_IsDropped_AndTheStreamResynchronizes()
    {
        await Arrive(Enumerable.Repeat((byte)0x41, TLinkTransport.MaxFrameBytes + 1).ToArray());

        var dropped = await _transport.ReadMessageAsync().WaitAsync(Guard);
        Assert.Equal(TLinkErrorCode.FramingError, dropped.Error!.Value.Code);

        await Arrive(Frame("HDR", "after"));
        var next = await _transport.ReadMessageAsync().WaitAsync(Guard);
        Assert.Equal("after", Encoding.ASCII.GetString(next.Value.Payload.Span));
    }

    [Fact]
    public async Task RemoteClosing_EndsTheStream_AfterTheFramesAlreadyReceived()
    {
        await Arrive(Frame("HDR", "last"));
        await _toTransport.Writer.CompleteAsync();

        var payloads = new List<string>();
        await foreach (var frame in _transport.ReadAllAsync().WithCancellation(CancellationToken.None))
            payloads.Add(Encoding.ASCII.GetString(frame.Value.Payload.Span));

        Assert.Equal(["last"], payloads);
    }
}
