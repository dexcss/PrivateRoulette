using System;
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
/// In-game wheel window: layered rim, gold inlay, scaled numerals and a ball
/// that settles exactly in the winning pocket (rotor-relative animation).
/// </summary>
public sealed class WheelWindow : Window
{
    private const float Tau = MathF.PI * 2f;
    private const float Slot = Tau / 38f;

    private readonly PluginConfig config;
    private readonly RouletteSession session;
    private readonly IPluginLog log;

    private DateTime spinStart = DateTime.MinValue;
    private double durationMs = 24400;
    private double wheelFrom;
    private double wheelTo;
    private double relFrom;
    private double relTo;
    private bool animating;
    private int? pendingResult;

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
        this.relFrom = pocket + (9 + rng.NextDouble() * 2) * Tau; // relative to rotor
        this.relTo = pocket;

        this.pendingResult = result;
        this.spinStart = now;
        this.animating = true;
        this.settledAt = DateTime.MinValue;
    }

    private double CurrentWheelAngle(DateTime now)
    {
        if (this.animating)
        {
            var t = Math.Clamp((now - this.spinStart).TotalMilliseconds / this.durationMs, 0, 1);
            return this.wheelFrom + (this.wheelTo - this.wheelFrom) * Ease(t);
        }

        return now.TimeOfDay.TotalSeconds * 0.12 % Tau;
    }

    /// <summary>Same velocity profile as the web wheel.</summary>
    private static double Ease(double t)
    {
        // velocity: ramp 0..0.05, cruise to 0.5, convex brake to 0
        double v(double x) => x < 0.05 ? x / 0.05 : x <= 0.5 ? 1 : Math.Pow(Math.Max(0, (1 - x) / 0.5), 1.5);
        // numeric integral, normalized
        const int n = 200;
        Span<double> table = stackalloc double[n + 1];
        var acc = 0.0;
        for (var i = 1; i <= n; i++)
        {
            acc += v((i - 0.5) / n) * (1.0 / n);
            table[i] = acc;
        }

        var x = Math.Clamp(t, 0, 1) * n;
        var idx = (int)Math.Floor(x);
        var frac = x - idx;
        var a = table[idx] / acc;
        var b = table[Math.Min(idx + 1, n)] / acc;
        return a + (b - a) * frac;
    }

    private (double wheel, double rel, float ballR) Sample(DateTime now)
    {
        if (!this.animating)
        {
            var pocket = this.lastResult >= 0 ? Wheel.PositionOf(this.lastResult) * Slot - MathF.PI / 2f : MathF.PI;
            return (this.CurrentWheelAngle(now), pocket, 0.665f);
        }

        var total = (now - this.spinStart).TotalMilliseconds;
        var tWheel = Math.Clamp(total / this.durationMs, 0, 1);
        var wheel = this.wheelFrom + (this.wheelTo - this.wheelFrom) * Ease(tWheel);

        var preRoll = 2400.0;
        var tBall = Math.Clamp((total - preRoll) / (this.durationMs - preRoll), 0, 1);
        var rel = this.relFrom + (this.relTo - this.relFrom) * Ease(tBall);

        // settle wobble, zero at t=1
        if (tBall > 0.9)
        {
            var p = (tBall - 0.9) / 0.1;
            rel += Math.Sin(p * Math.PI * 1.5) * Slot * 0.16 * Math.Pow(1 - p, 3);
        }

        // radius: rim → well with a soft hop
        float radius;
        if (tBall < 0.62) radius = 0.925f;
        else if (tBall < 0.78)
        {
            var u = (float)((tBall - 0.62) / 0.16);
            radius = 0.925f + (0.665f - 0.925f) * (u * u * (3 - 2 * u));
        }
        else
        {
            var p = (tBall - 0.78) / 0.22;
            radius = 0.665f + MathF.Sin((float)Math.Min(1, p) * MathF.PI) * 0.02f * (float)Math.Pow(1 - p, 2);
        }

        if (tBall >= 1)
        {
            this.animating = false;
            this.lastResult = this.pendingResult ?? -1;
            this.pendingResult = null;
            this.settledAt = now;
            return (this.wheelTo, this.relTo, 0.665f);
        }

        return (wheel, rel, radius);
    }

    public override void Draw()
    {
        var now = DateTime.UtcNow;
        var (wheelAngle, relAngle, ballRadius) = this.Sample(now);

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
        var ballPos = center + Dir((float)ballAngle) * (r * ballRadius);
        var ballRadiusPx = MathF.Max(5f, r * 0.034f);
        draw.AddCircleFilled(ballPos + new Vector2(ballRadiusPx * 0.25f, ballRadiusPx * 0.35f), ballRadiusPx * 0.95f, U32(0x000000, 0.4f), 24);
        draw.AddCircleFilled(ballPos, ballRadiusPx, U32(0xFFFFFF), 24);
        draw.AddCircleFilled(ballPos - new Vector2(ballRadiusPx * 0.3f, ballRadiusPx * 0.35f), ballRadiusPx * 0.35f, U32(0xFFFFFF, 0.95f), 16);
        draw.AddCircle(ballPos, ballRadiusPx, U32(0x000000, 0.45f), 24, 1.2f);

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
