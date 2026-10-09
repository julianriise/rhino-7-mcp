using System;
using System.Diagnostics;
using System.Threading;
using RhinoMCPPlugin.Functions;
using Xunit;

namespace SoftParam.Tests;

/// <summary>
/// Cancel during the AI read: the tool the read is waiting on stops at once,
/// with its children, and the read throws OperationCanceledException.
/// </summary>
public class ForskUvCancelTests
{
    [Fact]
    public void ACancelledRead_StopsTheToolWithinASecond()
    {
        using var cts = new CancellationTokenSource(300);
        var clock = Stopwatch.StartNew();
        using (ForskUv.Cancellable(cts.Token))
            Assert.Throws<OperationCanceledException>(() => ForskUv.Run("/bin/sleep", "30", "/", "sleep", 60000));
        Assert.True(clock.ElapsedMilliseconds < 2000, clock.ElapsedMilliseconds + " ms");
    }

    [Fact]
    public void ChildrenOfTheTool_StopToo()
    {
        var marker = "forsk-cancel-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        using var cts = new CancellationTokenSource(500);
        using (ForskUv.Cancellable(cts.Token))
            Assert.Throws<OperationCanceledException>(() =>
                ForskUv.Run("/bin/sh", "-c \"/bin/sleep 30 " + marker.Length + " ; true\"", "/", "sh", 60000));
        Thread.Sleep(300);
        var left = ForskUv.Run("/bin/sh", "-c \"/bin/ps -Ao command | /usr/bin/grep -c '^/bin/sleep 30 " + marker.Length + "' || true\"", "/", "ps", 5000);
        Assert.Equal("0", left.Stdout.Trim());
    }

    [Fact]
    public void GrandchildrenOfTheTool_StopToo()
    {
        // sh starts sh, which starts sleep: the read's tool, Python, and its worker.
        using var cts = new CancellationTokenSource(500);
        using (ForskUv.Cancellable(cts.Token))
            Assert.Throws<OperationCanceledException>(() =>
                ForskUv.Run("/bin/sh", "-c \"/bin/sh -c '/bin/sleep 31; true'; true\"", "/", "sh", 60000));
        Thread.Sleep(300);
        var left = ForskUv.Run("/bin/sh", "-c \"/bin/ps -Ao command | /usr/bin/grep -c '^/bin/sleep 31' || true\"", "/", "ps", 5000);
        Assert.Equal("0", left.Stdout.Trim());
    }

    [Fact]
    public void OutsideTheScope_ARunIsNotCancelled()
    {
        using (var cts = new CancellationTokenSource())
        using (ForskUv.Cancellable(cts.Token))
            cts.Cancel();
        Assert.Equal(0, ForskUv.Run("/usr/bin/true", "", "/", "true", 5000).Code);
    }
}
