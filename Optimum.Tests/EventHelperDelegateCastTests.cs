#if !NO_DONOR
using System;
using Vintagestory.API.Common;
using Xunit;

namespace Optimum.Tests;

// The decompiler emitted System.Func for EventHelper's cancellable-handler
// casts, but the vanilla IL (and every cancellable event declaration, e.g.
// BeforeActiveSlotChanged) uses the API's own Vintagestory.API.Common.Func
// delegates. The recompiled build then failed every handler with
// InvalidCastException and logged an error per invocation while the event
// silently proceeded as if unhandled. These tests subscribe handlers of the
// API delegate type and assert they actually run and can cancel.
public class EventHelperDelegateCastTests
{
    private sealed class SilentLogger : LoggerBase
    {
        public int ErrorCount;

        protected override void LogImpl(EnumLogType logType, string format, params object[] args)
        {
            if (logType == EnumLogType.Error)
            {
                ErrorCount++;
            }
        }
    }

    [Fact]
    public void InvokeSafeCancellableRunsApiFuncHandlerWithOneArg()
    {
        var logger = new SilentLogger();
        int calls = 0;
        Delegate ev = (Vintagestory.API.Common.Func<int, EnumHandling>)(arg =>
        {
            calls++;
            Assert.Equal(42, arg);
            return EnumHandling.PassThrough;
        });

        bool notCancelled = ev.InvokeSafeCancellable(logger, "test", 42);

        Assert.True(notCancelled);
        Assert.Equal(1, calls);
        Assert.Equal(0, logger.ErrorCount);
    }

    [Fact]
    public void InvokeSafeCancellableRunsApiFuncHandlerWithTwoArgs()
    {
        var logger = new SilentLogger();
        int calls = 0;
        Delegate ev = (Vintagestory.API.Common.Func<string, int, EnumHandling>)((a, b) =>
        {
            calls++;
            return EnumHandling.PreventDefault;
        });

        bool notCancelled = ev.InvokeSafeCancellable(logger, "test", "slot", 7);

        Assert.False(notCancelled);
        Assert.Equal(1, calls);
        Assert.Equal(0, logger.ErrorCount);
    }
}
#endif
