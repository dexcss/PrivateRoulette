using System;
using System.Collections.Generic;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

using PrivateRoulette.Configuration;
using PrivateRoulette.Protocol;
using PrivateRoulette.Services;

namespace PrivateRoulette.UI;

/// <summary>
/// In-game wheel window. Uses the same 28s timing/radius profile as the web
/// wheel: the rotor coasts, the ball rides the outer rim, drops onto the number
/// track for a long crawl with tiny fret bumps, then settles exactly into the
/// winning pocket.
/// </summary>
public sealed class WheelWindow : Window
{
    private const float Tau = MathF.PI * 2f;
    private const float Slot = Tau / 38f;
    private const double SpinMs = 28000;
    private const double IdleRadPerSec = 0.12;
    private const double RimRadius = 0.925;
    private const double TrackRadius = 0.862;
    private const double RestRadius = 0.69;

    private readonly PluginConfig config;
    private readonly RouletteSession session;
    private readonly IPluginLog log;

    private DateTime spinStart = DateTime.MinValue;
    private double wheelFrom;
    private double wheelTo;
    private double relFrom;
    private double relTo;
    private double[] wheelTable = Array.Empty<double>();
    private double[] ballTable = Array.Empty<double>();
    private bool animating;
    private int? pendingResult;

    private double idleWheelAngle;
    private DateTime lastIdleTick = DateTime.UtcNow;
    private DateTime hopStart = DateTime.MinValue;
    private int lastTickPocket = -1;
    private float currentBallSpeed;
    private readonly List<(Vector2 Pos, float Alpha)> trail = new();

    private int lastResult = -1;
    private string lastColor = "";
    private DateTime settledAt = DateTime.MinValue;

    public WheelWindow(PluginConfig config, RouletteSession session, IPluginLog log)
        : base("Private Roulette — Wheel###prwheel")
    {
        this.config = config;
        this.session = session;
        this.log = log;

        this.Size = new Vector2(620, 660);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 400),
            MaximumSize = new Vector2(1400, 1400),
        };
        this.RespectCloseHotkey = true;

        session.SpinReceived += this.OnSpin;
    }

    private void OnSpin(SpinResult spin)
    {
        this.lastColor = spin.Color;
        if (this.config.AutoOpenWheelOnSpin)
            this.IsOpen = true;
        this.BeginSpin(spin.Result);
    }

    public void BeginSpin(int result)
    {
        var now = DateTime.UtcNow;
        var wheelNow = this.CurrentWheelAngle(now);

        var rng = new Random();
        var pocket = Wheel.PositionOf(result) * Slot - MathF.PI / 2f;

        this.wheelFrom = wheelNow;
        this.wheelTo = wheelNow + (14 + rng.NextDouble() * 3) * Tau;

        // The ball is launched several turns ahead of its landing pocket, then
        // bleeds speed through the numbers. The integrated profile is
        // normalised so the final position is exactly the pocket.
        var relEnd = pocket;
        var relTurns = 8 + rng.NextDouble() * 2;
        this.relFrom = relEnd + relTurns * Tau;
        this.relTo = relEnd;

        this.wheelTable = BuildTable(WheelVelocity);
        this.ballTable = BuildTable(BallVelocity);

        this.pendingResult = result;
        this.spinStart = now;
        this.animating = true;
        this.settledAt = DateTime.MinValue;
        this.hopStart = DateTime.MinValue;
        this.lastTickPocket = -1;
        this.currentBallSpeed = 0;
        this.trail.Clear();
    }

    /// <summary>Slow idle rotation between spins (persistent, so there is no post-settle jump).</summary>
    private double CurrentWheelAngle(DateTime now)
    {
        var dt = Math.Clamp((now - this.lastIdleTick).TotalSeconds, 0, 0.1);
        this.lastIdleTick = now;
        this.idleWheelAngle = (this.idleWheelAngle + IdleRadPerSec * dt) % Tau;
        return this.idleWheelAngle;
    }

    private static double WheelVelocity(double t)
    {
        if (t < 0.05) return t / 0.05;
        if (t <= 0.5) return 1;
        var u = (1 - t) / 0.5;
        return Math.Pow(Math.Max(0, u), 1.5);
    }

    private static double BallVelocity(double t)
    {
        if (t < 0.03) return t / 0.03;
        if (t < 0.42) return 1 - (0.55 * (t - 0.03)) / 0.39;
        if (t < 0.62) return 0.45 - (0.3 * (t - 0.42)) / 0.2;
        if (t < 0.9) return 0.15 - (0.13 * (t - 0.62)) / 0.28;
        return 0.02 * (1 - (t - 0.9) / 0.1);
    }

    /// <summary>Integrates a velocity profile into a normalised progress table.</summary>
    private static double[] BuildTable(Func<double, double> velocity)
    {
        const int n = 240;
        var table = new double[n + 1];
        var acc = 0.0;
        const double dt = 1.0 / n;
        for (var i = 1; i <= n; i++)
        {
            acc += Math.Max(0, velocity((i - 0.5) * dt)) * dt;
            table[i] = acc;
        }

        if (acc <= 0) return table;
        for (var i = 0; i <= n; i++) table[i] /= acc;
        return table;
    }

    private static double SampleTable(double[] table, double t)
    {
        if (table.Length < 2) return Math.Clamp(t, 0, 1);
        var x = Math.Clamp(t, 0, 1) * (table.Length - 1);
        var idx = (int)Math.Floor(x);
        var frac = x - idx;
        var a = table[idx];
        var b = table[Math.Min(idx + 1, table.Length - 1)];
        return a + (b - a) * frac;
    }

    private static double Smoothstep(double x)
    {
        var c = Math.Clamp(x, 0, 1);
        return c * c * (3 - 2 * c);
    }

    /// <summary>Gentle, fully-enveloped settle wobble; exactly zero at t = 1.</summary>
    private static double SettleWobble(double t)
    {
        if (t < 0.85) return 0;
        var u = (t - 0.85) / 0.15;
        var envelope = Math.Sin(Math.PI * Math.Min(1, u));
        return Slot * 0.10 * envelope * Math.Sin(u * Math.PI * 4.5);
    }

    /// <summary>Rim → number track → pocket, smoothstepped at every transition.</summary>
    private static double BallRadiusAt(double t)
    {
        if (t < 0.4) return RimRadius;
        if (t < 0.52) return RimRadius + (TrackRadius - RimRadius) * Smoothstep((t - 0.4) / 0.12);
        if (t < 0.88) return TrackRadius;
        if (t < 0.98) return TrackRadius + (RestRadius - TrackRadius) * Smoothstep((t - 0.88) / 0.1);
        return RestRadius;
    }

    private float Hop(DateTime now)
    {
        if (!this.animating || this.hopStart == DateTime.MinValue) return 0;
        var age = (now - this.hopStart).TotalMilliseconds;
        if (age < 0 || age >= 240) return 0;
        return (float)Math.Sin(Math.PI * age / 240.0);
    }

    private (double wheel, double rel, float ballR) Sample(DateTime now)
    {
        if (!this.animating)
        {
            var pocket = this.lastResult >= 0
                ? Wheel.PositionOf(this.lastResult) * Slot - MathF.PI / 2f
                : MathF.PI;
            return (this.CurrentWheelAngle(now), pocket, (float)RestRadius);
        }

        var total = (now - this.spinStart).TotalMilliseconds;
        var t = Math.Clamp(total / SpinMs, 0, 1);

        var wheel = this.wheelFrom + (this.wheelTo - this.wheelFrom) * SampleTable(this.wheelTable, t);
        var rel = this.relFrom + (this.relTo - this.relFrom) * SampleTable(this.ballTable, t) + SettleWobble(t);
        var radius = BallRadiusAt(t);
        var speed = BallVelocity(t);
        this.currentBallSpeed = (float)speed;

        // Fret ticks: crossing a pocket boundary while slow starts one tiny hop.
        var relNorm = ((rel % Tau) + Tau) % Tau;
        var pocketIndex = (int)(relNorm / Slot);
        if (pocketIndex != this.lastTickPocket)
        {
            this.lastTickPocket = pocketIndex;
            if (speed < 0.22) this.hopStart = now;
        }

        if (t >= 1)
        {
            this.animating = false;
            this.lastResult = this.pendingResult ?? -1;
            this.pendingResult = null;
            this.settledAt = now;
            this.hopStart = DateTime.MinValue;
            this.idleWheelAngle = this.wheelTo % Tau;
            this.lastIdleTick = now;
            return (this.wheelTo, this.relTo, (float)RestRadius);
        }

        return (wheel, rel, (float)radius);
    }

    public override void Draw()
    {
        var now = DateTime.UtcNow;
        var (wheelAngle, relAngle, ballRadius) = this.Sample(now);
        var hop = this.Hop(now);

        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var size = MathF.Max(260, MathF.Min(avail.X, avail.Y - 60));
        var center = origin + new Vector2(avail.X / 2, size / 2 + 4);
        var r = size / 2 - 6;

        // ---- rim ----
        draw.AddCircleFilled(center, r, U32(0x24160C), 72);
        draw.AddCircleFilled(center, r * 0.985f, U32(0x4A2F1B), 72);
        draw.AddCircleFilled(center, r * 0.962f, U32(0x7A4F2D), 72);
        draw.AddCircleFilled(center, r * 0.94f, U32(0x50331C), 72);
        // gold inlay
        draw.AddCircle(center, r * 0.921f, U32(0xE8CF86), 72, r * 0.012f);
        draw.AddCircle(center, r * 0.907f, U32(0xA9832B), 72, r * 0.014f);
        // ball track groove
        draw.AddCircle(center, r * 0.905f, U32(0xE8CF86, 0.28f), 72, MathF.Max(1f, r * 0.006f));
        draw.AddCircle(center, r * 0.878f, U32(0x000000, 0.35f), 72, MathF.Max(1f, r * 0.006f));
        // inner rim shadow
        draw.AddCircleFilled(center, r * 0.9f, U32(0x1A120B), 72);

        // ---- pockets ----
        var pocketOuter = r * 0.885f;
        var pocketInner = r * 0.46f;
        for (var i = 0; i < 38; i++)
        {
            var n = Wheel.Order[i];
            var a0 = (float)(wheelAngle + i * Slot - Slot / 2 - Math.PI / 2);
            var a1 = a0 + Slot;

            var (fill, edge) = Wheel.ColorOf(n) switch
            {
                "red" => (0xC0392B, 0x8E2F24),
                "black" => (0x24272E, 0x14161B),
                _ => (0x0F9D58, 0x0A5C33),
            };

            draw.PathClear();
            draw.PathLineTo(center);
            draw.PathArcTo(center, pocketOuter, a0, a1, 6);
            draw.PathFillConvex(U32(fill));

            // outer edge shading
            draw.PathClear();
            draw.PathLineTo(center + Dir(a0) * pocketOuter);
            draw.PathArcTo(center, pocketOuter, a0, a1, 6);
            draw.PathArcTo(center, pocketOuter - r * 0.02f, a1, a0, 6);
            draw.PathFillConvex(U32(edge, 0.55f));

            // separator
            draw.AddLine(center + Dir(a0) * pocketInner, center + Dir(a0) * pocketOuter, U32(0xE8CF86, 0.55f), 1.1f);

            // number (scaled font)
            var mid = a0 + Slot / 2;
            var tp = center + Dir(mid) * (pocketOuter - r * 0.058f);
            var text = Wheel.Display(n);
            var fontSize = MathF.Max(9f, r * 0.052f);
            var textSize = ImGui.CalcTextSize(text) * (fontSize / ImGui.GetFontSize());
            draw.AddText(ImGui.GetFont(), fontSize, tp - textSize / 2f + new Vector2(0.5f, 1f), U32(0x000000, 0.5f), text);
            draw.AddText(ImGui.GetFont(), fontSize, tp - textSize / 2f, U32(0xF6EFDD), text);

            // fret stud on the ball track
            var fa = (float)(wheelAngle + i * Slot - Math.PI / 2);
            draw.AddCircleFilled(center + Dir(fa) * (r * 0.905f), MathF.Max(2f, r * 0.012f), U32(0xF0D98A), 12);
        }

        // ---- inner cone ----
        draw.AddCircleFilled(center, pocketInner, U32(0x12151B), 72);
        draw.AddCircleFilled(center, pocketInner * 0.985f, U32(0x0C0E13), 72);
        for (var i = 0; i < 8; i++)
        {
            var a = (float)(wheelAngle + i * Tau / 8);
            draw.AddLine(center + Dir(a) * r * 0.075f, center + Dir(a) * r * 0.44f, U32(0xE8CF86, 0.35f), MathF.Max(1.5f, r * 0.006f));
        }

        // ---- hub ----
        draw.AddCircleFilled(center, r * 0.17f, U32(0xF0D98A), 40);
        draw.AddCircleFilled(center, r * 0.14f, U32(0xA9832B), 40);
        draw.AddCircleFilled(center, r * 0.12f, U32(0x1E1808), 40);
        var monoSize = MathF.Max(9f, r * 0.04f);
        var mono = "PR";
        var monoSz = ImGui.CalcTextSize(mono) * (monoSize / ImGui.GetFontSize());
        draw.AddText(ImGui.GetFont(), monoSize, center - monoSz / 2f, U32(0xE8CF86), mono);

        // ---- ball (screen space: wheel + relative) ----
        var ballAngle = wheelAngle + relAngle;
        var hopLift = 0.008f * hop;
        var ballPos = center + Dir((float)ballAngle) * (r * ballRadius * (1 + hopLift));
        var ballRadiusPx = MathF.Max(5f, r * 0.033f) * (1 + 0.07f * hop);

        // motion streak while the ball is moving fast
        if (this.animating)
        {
            this.trail.Add((ballPos, Math.Clamp(this.currentBallSpeed * 1.4f, 0, 1)));
            if (this.trail.Count > 6) this.trail.RemoveAt(0);
            for (var i = 0; i < this.trail.Count; i++)
            {
                var p = this.trail[i];
                var f = (float)i / this.trail.Count;
                draw.AddCircleFilled(p.Pos, ballRadiusPx * (0.55f + 0.45f * f), U32(0xFFFFFF, p.Alpha * 0.28f * f), 16);
            }
        }
        else
        {
            this.trail.Clear();
        }

        var shadowScale = 1 - 0.15f * hop;
        draw.AddCircleFilled(
            ballPos + new Vector2(ballRadiusPx * (0.28f + 0.12f * hop), ballRadiusPx * (0.4f + 0.1f * hop)),
            ballRadiusPx * 0.92f * shadowScale,
            U32(0x000000, 0.5f - 0.08f * hop), 24);
        draw.AddCircleFilled(ballPos, ballRadiusPx, U32(0xFFFFFF), 24);
        draw.AddCircleFilled(ballPos - new Vector2(ballRadiusPx * 0.34f, ballRadiusPx * 0.4f), ballRadiusPx * 0.2f, U32(0xFFFFFF, 0.95f), 16);
        draw.AddCircle(ballPos, ballRadiusPx, U32(0x000000, 0.45f), 24, 1.2f);

        // ---- winning pocket pulse ----
        if (!this.animating && this.lastResult >= 0)
        {
            var age = (now - this.settledAt).TotalMilliseconds;
            if (age >= 0 && age < 2600)
            {
                var idx = Wheel.PositionOf(this.lastResult);
                var a0 = (float)(wheelAngle + idx * Slot - Slot / 2 - Math.PI / 2);
                var alpha = (float)(0.9 * (1 - age / 2600));
                draw.PathClear();
                draw.PathArcTo(center, pocketOuter, a0, a0 + Slot, 8);
                draw.PathStroke(U32(0xFFEBAA, alpha), ImDrawFlags.None, 3f);
            }
        }

        // ---- result plate ----
        if (!this.animating && this.lastResult >= 0)
        {
            var plate = new Vector2(r * 0.34f, r * 0.15f);
            var p0 = center + new Vector2(-plate.X / 2, r * 0.28f);
            var p1 = p0 + plate;
            draw.AddRectFilled(p0, p1, U32(0x0A0C10, 0.88f), plate.Y / 2f);
            var col = Wheel.ColorOf(this.lastResult) switch
            {
                "red" => 0xE74C3C,
                "black" => 0xCFD4DC,
                _ => 0x2ECC71,
            };
            draw.AddRect(p0, p1, U32(col), plate.Y / 2f, ImDrawFlags.None, 2f);
            var label = Wheel.Display(this.lastResult);
            var plateSize = MathF.Max(12f, plate.Y * 0.62f);
            var labelSz = ImGui.CalcTextSize(label) * (plateSize / ImGui.GetFontSize());
            draw.AddText(ImGui.GetFont(), plateSize, p0 + (plate - labelSz) / 2f + new Vector2(0, 1), U32(col), label);
        }

        ImGui.Dummy(new Vector2(avail.X, size + 8));

        // ---- footer ----
        var st = this.session.State;
        if (st != null)
            ImGui.TextDisabled($"game {this.session.GameId} · control #{st.ControlNumber} · {st.SpinMode} spin");

        if (this.animating)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "spinning…");
        }
        else if (this.lastResult >= 0)
        {
            var col = this.lastColor switch
            {
                "red" => new Vector4(0.91f, 0.30f, 0.24f, 1f),
                "black" => new Vector4(0.78f, 0.80f, 0.84f, 1f),
                _ => new Vector4(0.18f, 0.80f, 0.44f, 1f),
            };
            ImGui.TextColored(col, $"last result: {Wheel.Display(this.lastResult)} {this.lastColor.ToUpperInvariant()}");
            var spin = this.session.LastSpin;
            if (spin?.Proof != null && spin.Proof.Hash != "manual")
            {
                var (valid, _, seed, hash) = Fairness.Verify(spin.Proof.DiceRoll ?? -1, spin.Proof.ControlNumber, spin.Result);
                ImGui.TextDisabled($"proof: seed {seed} · hash {hash}");
                ImGui.TextColored(valid ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
                    valid ? "fairness verified ✓" : "PROOF MISMATCH");
            }
        }
        else
        {
            ImGui.TextDisabled("waiting for the first spin…");
        }
    }

    private static Vector2 Dir(float angle) => new(MathF.Cos(angle), MathF.Sin(angle));

    private static uint U32(int rgb, float alpha = 1f)
    {
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return ImGui.GetColorU32(new Vector4(r / 255f, g / 255f, b / 255f, alpha));
    }
}
