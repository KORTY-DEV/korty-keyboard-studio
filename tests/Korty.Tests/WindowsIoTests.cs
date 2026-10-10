using System.IO.Pipes;
using Korty.Core.Protocol;
using Korty.Core.Tft;
using Korty.Hid.Windows;
using Xunit;

namespace Korty.Tests;

/// <summary>
/// Exercises the real overlapped WriteFile/ReadFile + timeout/cancel code of <see cref="WindowsHidIo"/> on a named pipe
/// (same Win32 calls, no HID device involved). HidD_* calls cannot be tested without a device and are covered by the
/// hardware acceptance stages.
/// </summary>
public class WindowsIoTests
{
    private static (NamedPipeServerStream server, string path) Pipe()
    {
        var name = "korty-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        return (server, @"\\.\pipe\" + name);
    }

    [Fact]
    public async Task WriteOutputSendsExactlyTheGivenBufferOverOverlappedIo()
    {
        var (server, path) = Pipe();
        using var _ = server;
        var connect = server.WaitForConnectionAsync();
        using var io = new WindowsHidIo();
        var buffer = Enumerable.Range(0, 4097).Select(i => (byte)(i * 7)).ToArray();
        buffer[0] = 0;                                                  // report id
        var read = Task.Run(async () =>
        {
            await connect;
            var got = new byte[4097]; int n = 0;
            while (n < got.Length) n += await server.ReadAsync(got.AsMemory(n));
            return got;
        });
        io.WriteOutput(path, buffer);
        Assert.Equal(buffer, await read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ReadInputTimesOutWithNullThenReturnsARealReport()
    {
        var (server, path) = Pipe();
        using var _ = server;
        var connect = server.WaitForConnectionAsync();
        using var io = new WindowsHidIo();
        // open the client side first via a harmless write so that the handle exists
        var hello = new byte[] { 0 };
        var drain = Task.Run(async () => { await connect; var b = new byte[1]; await server.ReadAsync(b); });
        io.WriteOutput(path, hello);
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(io.ReadInput(path, 65, 200));                       // nothing queued: must time out, cancel cleanly and return null
        Assert.InRange(sw.ElapsedMilliseconds, 150, 2000);

        var ack = new byte[65]; ack[1] = 0xAB; ack[64] = 0xCD;
        var send = server.WriteAsync(ack).AsTask();                     // zero-size pipe buffer: the write completes only while the client reads
        var got = io.ReadInput(path, 65, 1000);
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(got); Assert.Equal(ack, got);

        Assert.Null(io.ReadInput(path, 65, 100));                       // and it keeps working after a cancelled read
    }

    [Fact]
    public void OpeningAMissingInterfaceReportsAClearErrorAndDoesNotPoisonLaterCalls()
    {
        using var io = new WindowsHidIo();
        var ex = Assert.ThrowsAny<Exception>(() => io.WriteOutput(@"\\.\pipe\korty-does-not-exist-" + Guid.NewGuid().ToString("N"), new byte[4]));
        Assert.Contains("HID", ex.Message);
        Assert.ThrowsAny<Exception>(() => io.SetFeature(@"\\.\pipe\korty-does-not-exist", new byte[65]));
    }
}

public class GifPathTests
{
    [Theory]
    [InlineData(1, 9)] [InlineData(2, 17)] [InlineData(5, 41)] [InlineData(8, 65)]
    public async Task StillAndAnimatedUploadsRunFullSequenceOnRealShapedRig(int frames, int chunks)
    {
        using var rig = new RealShapedRig();
        rig.ActivateAndArm(Stages.Rgb, Stages.Png, Stages.Gif);
        var anim = new TftAnimation(Enumerable.Range(0, frames).Select(i => new TftFrame(Px.Solid((byte)(i * 30), 80, (byte)(255 - i * 20)), 100)).ToList());
        var plan = TftUploadPlanner.Plan(anim, TftEncodingOptions.For(ProtocolFamily.Feature04));
        Assert.Equal(chunks, plan.DataChunks); Assert.Equal(chunks + 3, plan.Packets.Count);
        var r = await rig.Service.SendAsync(plan.Packets, rig.Device, null, default);
        Assert.True(r.Success, r.Message);
        Assert.Equal(plan.Packets.Count, r.Sent); Assert.Equal(chunks, r.AcksExpected); Assert.Equal(0, r.AcksMissed);
        var writes = rig.Io.Ops.Where(o => o.Kind == "WriteOutput").ToList();
        Assert.Equal(chunks, writes.Count);
        Assert.All(writes, w => { Assert.Equal(4097, w.Data.Length); Assert.Equal(RealShapedRig.DataPath, w.Path); });
        Assert.Equal(Stages.ForFrames(frames), plan.Stage);
    }

    [Fact]
    public async Task SecondUploadAfterAnotherOneBehavesTheSame()
    {
        using var rig = new RealShapedRig();
        rig.ActivateAndArm(Stages.Rgb, Stages.Png, Stages.Gif);
        foreach (var n in new[] { 1, 8 })
        {
            var anim = new TftAnimation(Enumerable.Range(0, n).Select(i => new TftFrame(Px.Solid((byte)i, 1, 2), 100)).ToList());
            var r = await rig.Service.SendAsync(TftUploadPlanner.Plan(anim, TftEncodingOptions.For(ProtocolFamily.Feature04)).Packets, rig.Device, null, default);
            Assert.True(r.Success, r.Message);
        }
    }

    [Fact]
    public async Task SlowFirstChunkWhileTheKeyboardErasesFlashIsNotAborted()
    {
        // used to fail: write timeout was 3 s, the first chunk of a bigger image can stay pending longer
        var name = "korty-slow-" + Guid.NewGuid().ToString("N");
        using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        var connect = server.WaitForConnectionAsync();
        using var io = new WindowsHidIo();
        var reader = Task.Run(async () => { await connect; await Task.Delay(4000); var b = new byte[4097]; int n = 0; while (n < b.Length) n += await server.ReadAsync(b.AsMemory(n)); return n; });
        io.WriteOutput(@"\\.\pipe\" + name, new byte[4097]);
        Assert.Equal(4097, await reader.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Win32ErrorsCarryTheNumericCodeInTheMessage()
    {
        using var io = new WindowsHidIo();
        var ex = Assert.ThrowsAny<System.ComponentModel.Win32Exception>(() => io.WriteOutput(@"\\.\pipe\korty-missing-" + Guid.NewGuid().ToString("N"), new byte[4]));
        Assert.Matches(@"Win32 \d+ \(0x[0-9A-F]+\)", ex.Message);
    }
}
