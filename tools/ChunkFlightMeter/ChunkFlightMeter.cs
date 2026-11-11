using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ChunkFlightMeter;

/// <summary>
/// Records the time between frames and logs one window every five seconds of wall time in the grammar
/// that scripts/analyze-chunk-flight.py reads: frameUs=n/p50/p95/p99/max, over50ms, frameMeanUs and the
/// non-empty histogram buckets, plus the player position, the summed time of frames over 50 ms, the
/// loaded terrain ahead of the player in blocks (ahead=), the chunks the client received (chunks=), the chunk columns the integrated server loaded or generated
/// (cols=), the load average and the mean CPU frequency. The bucket layout matches OptimumHistogram,
/// four buckets per power of two.
///
/// When CHUNKFLIGHT_SETUP is "x,y,z,yawDegrees[,moveSpeed]" the mod also prepares the session after the
/// level loads: it closes the character creation dialog (which pauses a new survival world), switches
/// to creative mode with free movement and noclip, sets the fly speed, freezes time of day and weather,
/// teleports to the absolute block position and fixes the heading, so every run starts from the same
/// place facing the same way. Server replies are logged so the benchmark can prove each step.
/// </summary>
public sealed class ChunkFlightMeterSystem : ModSystem
{
    // The integrated server runs in the client process, so the server side can hand its count to the
    // client side through a static.
    internal static long ServerColumns;
    internal static long ClientChunks;

    public override bool ShouldLoad(EnumAppSide forSide) => true;

    public override void StartServerSide(ICoreServerAPI api)
    {
        api.Event.ChunkColumnLoaded += (_, _) => Interlocked.Increment(ref ServerColumns);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        var meter = new FrameMeter(api);
        api.Event.RegisterRenderer(meter, EnumRenderStage.Before, "chunkflightmeter");
        api.Event.LeaveWorld += meter.Flush;
        api.Event.ChunkDirty += (_, _, reason) =>
        {
            if (reason == EnumChunkDirtyReason.NewlyLoaded) Interlocked.Increment(ref ClientChunks);
        };

        string setup = Environment.GetEnvironmentVariable("CHUNKFLIGHT_SETUP");
        if (!string.IsNullOrWhiteSpace(setup))
        {
            api.Event.ChatMessage += meter.OnChat;
            api.Event.LevelFinalize += () => meter.BeginSetup(setup);
        }
    }
}

internal sealed class FrameMeter : IRenderer
{
    private const int SubBucketBits = 2;
    private const int SubBuckets = 1 << SubBucketBits;
    private const int MaxMostSignificantBit = 47;
    private const long StutterMicros = 50_000;
    private const double WindowSeconds = 5.0;

    private readonly ICoreClientAPI api;
    private readonly long[] buckets = new long[SubBuckets * MaxMostSignificantBit];
    private long lastTicks;
    private long windowStartTicks;
    private long count;
    private long sum;
    private long max;
    private long over33;
    private long over50;
    private long over100;
    private long hitchMicros;

    // Setup sequencer: steps run on the render thread against wall time, because game tick callbacks
    // stand still while the character dialog pauses the game.
    private readonly List<(double delaySeconds, Action action)> steps = new();
    private int nextStep;
    private long stepTicks;
    private bool setupActive;
    private float holdYaw = float.NaN;

    public FrameMeter(ICoreClientAPI api)
    {
        this.api = api;
    }

    public double RenderOrder => 0;

    public int RenderRange => 1;

    public void OnChat(int groupId, string message, EnumChatType chattype, string data)
    {
        if (setupActive && !string.IsNullOrEmpty(message))
        {
            api.Logger.Notification("ChunkFlightMeter: reply [" + chattype + "] " + message.Replace('\n', ' '));
        }
    }

    public void BeginSetup(string setup)
    {
        string[] parts = setup.Split(',');
        if (parts.Length < 4)
        {
            api.Logger.Warning("ChunkFlightMeter: CHUNKFLIGHT_SETUP needs x,y,z,yaw");
            return;
        }

        string x = parts[0].Trim(), y = parts[1].Trim(), z = parts[2].Trim();
        float yaw = float.Parse(parts[3], CultureInfo.InvariantCulture) * (float)Math.PI / 180f;
        string speed = parts.Length > 4 ? parts[4].Trim() : null;

        // "/" commands go to the server, "." commands are client commands and only run through the chat line path.
        steps.Add((2.0, () => api.SendChatMessage("/gamemode creative")));
        steps.Add((1.5, () => api.TriggerChatMessage(".freemove 1")));
        steps.Add((0.5, () => api.TriggerChatMessage(".noclip 1")));
        if (speed != null)
        {
            steps.Add((0.5, () => api.TriggerChatMessage(".movespeed " + speed)));
        }

        steps.Add((0.5, () => api.SendChatMessage("/time set 12:00")));
        steps.Add((0.5, () => api.SendChatMessage("/time stop")));
        steps.Add((0.5, () => api.SendChatMessage("/weather acp 0")));
        steps.Add((0.5, () => api.SendChatMessage("/weather setprecip -1")));
        steps.Add((0.5, () => api.SendChatMessage("/tp =" + x + " =" + y + " =" + z)));
        steps.Add((2.0, () => holdYaw = yaw));
        steps.Add((1.5, () =>
        {
            var data = api.World.Player.WorldData;
            var pos = api.World.Player.Entity.Pos;
            api.Logger.Notification(string.Create(CultureInfo.InvariantCulture,
                $"ChunkFlightMeter: setup complete {Position()} yawDeg={pos.Yaw * 180 / Math.PI:0.0} pitchDeg={pos.Pitch * 180 / Math.PI:0.0} mode={data.CurrentGameMode} freeMove={data.FreeMove} noClip={data.NoClip} moveSpeed={data.MoveSpeedMultiplier:0.##} paused={api.IsGamePaused}"));
            setupActive = false;
        }));
        nextStep = 0;
        stepTicks = Stopwatch.GetTimestamp();
        setupActive = true;
    }

    private void RunSetup(long now)
    {
        if (api.World.Player?.Entity == null)
        {
            stepTicks = now;
            return;
        }

        // A new survival world opens the character dialog and pauses the game until it closes.
        foreach (GuiDialog dialog in api.Gui.OpenedGuis.ToArray())
        {
            if (dialog.GetType().Name.Contains("CreateCharacter"))
            {
                api.Logger.Notification("ChunkFlightMeter: closing " + dialog.GetType().Name);
                dialog.TryClose();
                stepTicks = now;
                return;
            }
        }

        // The character system pauses the game from a queued task, which can land after the dialog closed.
        // A paused integrated server does not process commands, so no step runs while paused.
        if (api.IsGamePaused)
        {
            if ((now - stepTicks) / (double)Stopwatch.Frequency >= 1.0)
            {
                api.Logger.Notification("ChunkFlightMeter: unpausing");
                api.PauseGame(false);
                stepTicks = now;
            }

            return;
        }

        if (nextStep >= steps.Count || (now - stepTicks) / (double)Stopwatch.Frequency < steps[nextStep].delaySeconds)
        {
            return;
        }

        steps[nextStep].action();
        nextStep++;
        stepTicks = now;
    }

    private string Position()
    {
        var pos = api.World.Player?.Entity?.Pos;
        return pos == null
            ? "pos=0,0,0"
            : string.Create(CultureInfo.InvariantCulture, $"pos={pos.X:0.0},{pos.Y:0.0},{pos.Z:0.0}");
    }

    /// <summary>
    /// How many blocks of loaded terrain lie ahead of the player along the heading, probed one chunk at a
    /// time up to 1024 blocks. Zero means the player has outrun chunk loading and looks into the void.
    /// </summary>
    private int LoadedAhead()
    {
        var pos = api.World.Player?.Entity?.Pos;
        if (pos == null)
        {
            return 0;
        }

        double dx = Math.Sin(pos.Yaw), dz = Math.Cos(pos.Yaw);
        var probe = new BlockPos(0, api.World.SeaLevel - 1, 0, pos.Dimension);
        int ahead = 0;
        for (int distance = 0; distance <= 1024; distance += 32)
        {
            probe.X = (int)(pos.X + dx * distance);
            probe.Z = (int)(pos.Z + dz * distance);
            if (api.World.BlockAccessor.GetChunkAtBlockPos(probe) == null)
            {
                break;
            }

            ahead = distance;
        }

        return ahead;
    }

    private static string Environmental()
    {
        string load = "0";
        double mhz = 0;
        try
        {
            string[] fields = File.ReadAllText("/proc/loadavg").Split(' ');
            load = fields[0];
            int n = 0;
            // One level only: a recursive search under /sys follows its symlink loops and never returns.
            foreach (string policy in Directory.GetDirectories("/sys/devices/system/cpu/cpufreq", "policy*"))
            {
                mhz += double.Parse(File.ReadAllText(policy + "/scaling_cur_freq").Trim(), CultureInfo.InvariantCulture) / 1000.0;
                n++;
            }

            mhz = n == 0 ? 0 : mhz / n;
        }
        catch (Exception)
        {
            // Not Linux, or the files are unreadable: report zero.
        }

        return string.Create(CultureInfo.InvariantCulture, $"load1={load}, cpuMhz={mhz:0}");
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        long now = Stopwatch.GetTimestamp();
        if (setupActive)
        {
            RunSetup(now);
        }

        // The heading is held until a key or the mouse could change it; nothing moves the mouse in a run.
        if (!float.IsNaN(holdYaw))
        {
            var pos = api.World.Player?.Entity?.Pos;
            if (pos != null)
            {
                pos.Yaw = holdYaw;
                pos.Pitch = (float)Math.PI;
                api.Input.MouseYaw = holdYaw;
                api.Input.MousePitch = (float)Math.PI;
            }
        }

        if (lastTicks != 0)
        {
            long micros = (long)((now - lastTicks) * (1_000_000.0 / Stopwatch.Frequency));
            Record(micros);
        }
        else
        {
            windowStartTicks = now;
        }

        lastTicks = now;
        if ((now - windowStartTicks) / (double)Stopwatch.Frequency >= WindowSeconds)
        {
            windowStartTicks = now;
            Flush();
        }
    }

    private void Record(long micros)
    {
        lock (buckets)
        {
            buckets[BucketIndex(micros)]++;
            count++;
            sum += micros;
            if (micros > max) max = micros;
            if (micros > 33_333) over33++;
            if (micros > StutterMicros)
            {
                over50++;
                hitchMicros += micros;
            }
            if (micros > 100_000) over100++;
        }
    }

    public void Flush()
    {
        string line;
        lock (buckets)
        {
            if (count == 0)
            {
                return;
            }

            var sb = new StringBuilder(512);
            sb.Append("ChunkFlightMeter: frameUs=n").Append(count)
                .Append("/p50 ").Append(Percentile(0.50))
                .Append("/p95 ").Append(Percentile(0.95))
                .Append("/p99 ").Append(Percentile(0.99))
                .Append("/max ").Append(max)
                .Append(", over33ms=").Append(over33)
                .Append(", over50ms=").Append(over50)
                .Append(", over100ms=").Append(over100)
                .Append(", hitchMs=").Append((hitchMicros / 1000).ToString(CultureInfo.InvariantCulture))
                .Append(", ").Append(Position())
                .Append(", ahead=").Append(LoadedAhead())
                .Append(", chunks=").Append(Interlocked.Exchange(ref ChunkFlightMeterSystem.ClientChunks, 0))
                .Append(", cols=").Append(Interlocked.Exchange(ref ChunkFlightMeterSystem.ServerColumns, 0))
                .Append(", ").Append(Environmental())
                .Append(", frameMeanUs=").Append((sum / count).ToString(CultureInfo.InvariantCulture))
                .Append(", frameBuckets=");
            bool first = true;
            for (int i = 0; i < buckets.Length; i++)
            {
                if (buckets[i] == 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(i).Append(':').Append(buckets[i]);
            }

            line = sb.ToString();
            Array.Clear(buckets);
            count = sum = max = over33 = over50 = over100 = hitchMicros = 0;
        }

        api.Logger.Notification(line);
    }

    private long Percentile(double fraction)
    {
        long rank = Math.Max(1, (long)Math.Ceiling(fraction * count));
        long seen = 0;
        for (int i = 0; i < buckets.Length; i++)
        {
            seen += buckets[i];
            if (seen >= rank)
            {
                return Math.Min(BucketUpperBound(i), max);
            }
        }

        return max;
    }

    private static int BucketIndex(long value)
    {
        if (value < SubBuckets)
        {
            return value < 0 ? 0 : (int)value;
        }

        int msb = 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)value);
        if (msb > MaxMostSignificantBit)
        {
            return SubBuckets * MaxMostSignificantBit - 1;
        }

        int sub = (int)((value >> (msb - SubBucketBits)) & (SubBuckets - 1));
        return ((msb - 1) << SubBucketBits) + sub;
    }

    private static long BucketUpperBound(int index)
    {
        if (index < SubBuckets)
        {
            return index;
        }

        int msb = (index >> SubBucketBits) + 1;
        int sub = index & (SubBuckets - 1);
        int shift = msb - SubBucketBits;
        return ((long)(SubBuckets + sub) << shift) + (1L << shift) - 1;
    }

    public void Dispose()
    {
    }
}
