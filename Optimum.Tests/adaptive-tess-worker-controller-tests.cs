using System.Threading;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class AdaptiveTessWorkerControllerTests
{
    [Fact]
    public void ZeroMaxCapStaysZero()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 0, evalInterval: 10);
        Assert.Equal(0, ctrl.ActiveWorkerCap);

        ctrl.ForceCapForTesting(3);
        Assert.Equal(0, ctrl.ActiveWorkerCap);

        for (int i = 0; i < 10; i++)
            ctrl.RecordChunk(backpressureTicks: 1000, workTicks: 1000);

        Assert.Equal(0, ctrl.Evaluate(queueDepth: 50));
        ctrl.Reset();
        Assert.Equal(0, ctrl.ActiveWorkerCap);
    }

    [Fact]
    public void HighBackpressureScalesDown()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.ForceCapForTesting(4);

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 8000, workTicks: 2000);

        int newCap = ctrl.Evaluate(queueDepth: 2);
        Assert.True(newCap < 4, $"Expected scale-down, got cap={newCap}");
    }

    [Fact]
    public void LowBackpressureScalesUp()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.ForceCapForTesting(2);

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 100, workTicks: 9000);

        int newCap = ctrl.Evaluate(queueDepth: 20);
        Assert.True(newCap > 2, $"Expected scale-up, got cap={newCap}");
    }

    [Fact]
    public void StarvationGuardPreventsScaleDown()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.ForceCapForTesting(3);

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 6000, workTicks: 4000);

        int newCap = ctrl.Evaluate(queueDepth: 100);
        Assert.Equal(3, newCap);
    }

    [Fact]
    public void NeverExceedsMaxWorkers()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 2, evalInterval: 5);
        ctrl.ForceCapForTesting(2);

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 0, workTicks: 10000);

        int newCap = ctrl.Evaluate(queueDepth: 20);
        Assert.True(newCap <= 2);
    }

    [Fact]
    public void SetMaxWorkersClampsCurrent()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        Assert.Equal(4, ctrl.ActiveWorkerCap);

        ctrl.SetMaxWorkers(1);
        Assert.Equal(1, ctrl.ActiveWorkerCap);
    }

    [Fact]
    public void ResetRestoresMaxWorkerCap()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 3, evalInterval: 5);
        ctrl.ForceCapForTesting(1);
        Assert.Equal(1, ctrl.ActiveWorkerCap);

        ctrl.Reset();
        Assert.Equal(3, ctrl.ActiveWorkerCap);
        Assert.Equal(0.0, ctrl.BackpressureRatio);
    }

    [Fact]
    public void EwmaSmoothsPeaks()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.ForceCapForTesting(3);

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 5000, workTicks: 5000);
        ctrl.Evaluate(queueDepth: 10);
        double first = ctrl.BackpressureRatio;

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 0, workTicks: 10000);
        ctrl.Evaluate(queueDepth: 10);
        double second = ctrl.BackpressureRatio;

        Assert.True(second < first, $"EWMA should smooth down: first={first}, second={second}");
        Assert.True(second > 0, "EWMA should not drop to zero after one good interval");
    }

    [Fact]
    public void OnCapChangedFires()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.ForceCapForTesting(4);

        int? reportedOld = null;
        int? reportedNew = null;
        ctrl.OnCapChanged = (old, @new, _) => { reportedOld = old; reportedNew = @new; };

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 9000, workTicks: 1000);
        ctrl.Evaluate(queueDepth: 2);

        Assert.NotNull(reportedOld);
        Assert.NotNull(reportedNew);
        Assert.True(reportedNew < reportedOld);
    }

    [Fact]
    public void NoScaleUpWithEmptyQueue()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.ForceCapForTesting(2);

        for (int i = 0; i < 5; i++)
            ctrl.RecordChunk(backpressureTicks: 0, workTicks: 10000);

        int newCap = ctrl.Evaluate(queueDepth: 0);
        Assert.Equal(2, newCap);
    }

    [Fact]
    public void ResetWithNewMax()
    {
        var ctrl = new AdaptiveTessWorkerController(maxWorkers: 4, evalInterval: 5);
        ctrl.Reset(newMax: 2);
        Assert.Equal(2, ctrl.ActiveWorkerCap);
    }
}
