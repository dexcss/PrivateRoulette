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
/// The showpiece: a procedurally drawn European wheel with a physically-flavoured
/// spin animation, driven by real spin results from the backend.
/// </summary>
public sealed class WheelWindow : Window
{
    private const float Tau = MathF.PI * 2f;
    private const float Pocket = Tau / 38f;

    private readonly PluginConfig config;
    private readonly RouletteSession session;
    private readonly IPluginLog log;

    // animation state
    private DateTime spinStart = DateTime.MinValue;
    private double durationMs = 6500;
    private double wheelFrom;
    private double wheelTo;
    private double ballFrom;
    private double ballTo;
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

        this.Size = new Vector2(560, 600);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 360),
            MaximumSize = new Vector2(1200, 1200),
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

    /// <summary>Starts the animation toward the given pocket.</summary>
    public void BeginSpin(int result)
    {
        var now = DateTime.UtcNow;
        var (wheelNow, _) = this.CurrentAngles(now);

        var rng = new Random();
        this.wheelFrom = wheelNow;
        this.wheelTo = wheelNow + (3 + rng.NextDouble() * 1.5) * Tau;

        var pocketAngle = Wheel.PositionOf(result) * Pocket;
        this.ballFrom = this.wheelFrom + Math.PI;
        this.ballTo = this.wheelTo + pocketAngle + (6 + rng.NextDouble() * 2) * Tau;

        this.pendingResult = result;
        this.spinStart = now;
        this.animating = true;
        this.settledAt = DateTime.MinValue;
    }

    private (double wheel, double ball) CurrentAngles(DateTime now)
    {
        if (this.animating)
        {
            var t = Math.Clamp((now - this.spinStart).TotalMilliseconds / this.durationMs, 0, 1);
            if (t >= 1)
            {
                this.animating = false;
                this.lastResult = this.pendingResult ?? -1;
                this.pendingResult = null;
                this.settledAt = now;
                return (this.wheelTo, this.ballTo);
            }

            var wheel = this.wheelFrom + (this.wheelTo - this.wheelFrom) * EaseOutCubic(t);

            double ball;
            if (t < 0.85)
            {
                var bt = t / 0.85;
                ball = this.ballFrom + (this.ballTo - this.ballFrom) * (1 - Math.Pow(1 - bt, 2.2));
            }
            else
            {
                var baseAngle = this.ballTo;
                var phase = (t - 0.85) / 0.15;
                var wobble = Math.Sin(phase * Math.PI * 3) * Pocket * 0.9 * (1 - phase);
                ball = baseAngle + wobble;
            }

            return (wheel, ball);
        }

        // idle: slow drift, ball resting in the last pocket
        var idleWheel = now.TimeOfDay.TotalSeconds * 0.12 % Tau;
        var pocket = this.lastResult >= 0 ? Wheel.PositionOf(this.lastResult) * Pocket : Math.PI;
        return (idleWheel, idleWheel + pocket);
    }

    private static double EaseOutCubic(double x) => 1 - Math.Pow(1 - x, 3);

    public override void Draw()
    {
        var now = DateTime.UtcNow;
        var (wheelAngle, ballAngle) = this.CurrentAngles(now);

        var draw = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var size = MathF.Max(220, MathF.Min(avail.X, avail.Y - 34));
        var center = origin + new Vector2(avail.X / 2, size / 2 + 4);
        var r = size / 2 - 6;

        // ---- outer rim ----
        draw.AddCircleFilled(center, r, U32(0x3c2517), 64);
        draw.AddCircleFilled(center, r * 0.965f, U32(0x7a4f2d), 64);

        // ---- pockets ----
        var pocketR = r * 0.78f;
        var innerR = r * 0.55f;
        for (var i = 0; i < 37; i++)
        {
            var n = Wheel.Order[i];
            var a0 = (float)(wheelAngle + i * Pocket - Pocket / 2 - Math.PI / 2);
            var a1 = a0 + Pocket;

            var color = Wheel.ColorOf(n) switch
            {
                "red" => 0xC0392B,
                "black" => 0x1B1B1F,
                _ => 0x0F9D58,
            };

            // pie slice
            draw.PathClear();
            draw.PathLineTo(center);
            draw.PathArcTo(center, pocketR, a0, a1, 6);
            draw.PathFillConvex(U32(color));

            // separator
            var p0 = center + Dir(a0) * innerR;
            var p1 = center + Dir(a0) * pocketR;
            draw.AddLine(p0, p1, U32(0xDAB371, 0.55f), 1f);

            // number
            var mid = a0 + Pocket / 2;
            var tp = center + Dir(mid) * (pocketR - r * 0.065f);
            var text = Wheel.Display(n);
            var textSize = ImGui.CalcTextSize(text);
            draw.AddText(tp - textSize / 2, U32(0xF5F0E6), text);
        }

        // ---- inner cone + hub ----
        draw.AddCircleFilled(center, innerR, U32(0x14161B), 64);
        draw.AddCircleFilled(center, innerR * 0.98f, U32(0x1E232C), 64);
        draw.AddCircleFilled(center, r * 0.1f, U32(0xC9A227), 32);
        for (var i = 0; i < 8; i++)
        {
            var a = (float)(wheelAngle + i * Tau / 8);
            draw.AddLine(center + Dir(a) * r * 0.12f, center + Dir(a) * r * 0.5f, U32(0xDAB371, 0.3f), 2f);
        }

        // ---- ball ----
        var progress = this.animating
            ? (float)Math.Clamp((now - this.spinStart).TotalMilliseconds / this.durationMs, 0, 1)
            : 1f;
        var ballRadius = r * (0.9f - 0.28f * (float)EaseOutCubic(progress));
        var ballPos = center + Dir((float)ballAngle) * ballRadius;
        draw.AddCircleFilled(ballPos, MathF.Max(4f, r * 0.028f), U32(0xFFFFFF), 24);
        draw.AddCircle(ballPos, MathF.Max(4f, r * 0.028f), U32(0x000000, 0.35f), 24, 1.5f);

        // ---- result overlay ----
        if (!this.animating && this.lastResult >= 0)
        {
            var color = Wheel.ColorOf(this.lastResult) switch
            {
                "red" => 0xE74C3C,
                "black" => 0x9AA0AA,
                _ => 0x2ECC71,
            };
            var label = Wheel.Display(this.lastResult);
            var textSize = ImGui.CalcTextSize(label) * 3.4f;
            draw.AddCircleFilled(center, r * 0.19f, U32(0x000000, 0.55f), 48);
            draw.AddText(center - textSize / 2, U32(color, 1f), label);
            // scale trick: draw bigger via font size would need a font; this is fine at default scale
        }

        // reserve space for the canvas
        ImGui.Dummy(new Vector2(avail.X, size + 8));

        // ---- footer info ----
        var st = this.session.State;
        if (st != null)
        {
            ImGui.TextDisabled($"game {this.session.GameId} · control #{st.ControlNumber} · {st.SpinMode} spin");
        }

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
