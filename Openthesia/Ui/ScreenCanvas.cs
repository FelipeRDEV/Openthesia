using IconFonts;
using ImGuiNET;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Openthesia.Core;
using Openthesia.Core.Midi;
using Openthesia.Core.SoundFonts;
using Openthesia.Enums;
using Openthesia.Settings;
using Openthesia.Ui.Helpers;
using System.Numerics;
using Veldrid;
using ScreenRecorderLib;
using Note = Melanchall.DryWetMidi.Interaction.Note;
using static Openthesia.Core.ScreenCanvasControls;
using Openthesia.Core.Plugins;
using Openthesia.Core.FileDialogs;
using Vanara.PInvoke;

namespace Openthesia.Ui;

public class ScreenCanvas
{
    public static Vector2 CanvasPos { get; private set; }

    // controls state to handle top bar hiding
    private static bool _leftHandColorPicker;
    private static bool _rightHandColorPicker;
    private static bool _comboFallSpeed;
    private static bool _comboPlaybackSpeed;
    private static bool _comboSoundFont;
    private static bool _comboPlugins;

    private static Vector2 _rectStart;
    private static Vector2 _rectEnd;
    private static bool _isRectMode;
    private static bool _isRightRect;
    private static bool _isHoveringTextBtn;
    private static bool _isProgressBarHovered;
    private static float _panVelocity;
    private static bool _isProgressBarActive;
    private static readonly Dictionary<int, HitLineGlowState> _hitLineGlows = new();
    private static readonly List<int> _hitLineGlowKeys = new();
    private static readonly List<int> _hitLineGlowsToRemove = new();
    private static readonly List<HitLineParticle> _hitLineParticles = new();
    private static uint _hitLineParticleRng = 0x6E624EB7;
    private const float PreRollSeconds = 3f;
    private static bool _isPreRollActive;
    private static float _preRollRemaining;
    private static float _preRollBaseSeconds;
    private static bool _queueInitialPreRoll;
    private static bool _hasResumeAnchor;
    private static float _resumeAnchorSeconds;
    private static bool _preRollScrollVisual;
    private static bool _learningPausedByMissingNote;

    private struct HitLineGlowState
    {
        public float X1;
        public float X2;
        public Vector4 Color;
        public float Intensity;
        public float VerticalScale;
        public float ParticleCarry;
    }

    private struct HitLineParticle
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public Vector4 Color;
        public float Life;
        public float MaxLife;
        public float Size;
    }

    private sealed class ChordGuideMarker
    {
        public readonly HashSet<int> PitchClasses = new();
        public float AttackY;
        public float StartSeconds;
        public float LongestLengthSeconds;
    }

    private readonly struct ChordPattern
    {
        public readonly string Suffix;
        public readonly int[] Intervals;
        public readonly int Priority;

        public ChordPattern(string suffix, int[] intervals, int priority)
        {
            Suffix = suffix;
            Intervals = intervals;
            Priority = priority;
        }
    }

    private static readonly string[] _pitchClassNames = new[]
    {
        "C", "C#", "D", "D#", "E", "F",
        "F#", "G", "G#", "A", "A#", "B"
    };

    private static readonly ChordPattern[] _chordPatterns = new[]
    {
        new ChordPattern("maj7", new[] { 0, 4, 7, 11 }, 1),
        new ChordPattern("7", new[] { 0, 4, 7, 10 }, 2),
        new ChordPattern("m7", new[] { 0, 3, 7, 10 }, 3),
        new ChordPattern("mMaj7", new[] { 0, 3, 7, 11 }, 4),
        new ChordPattern("dim7", new[] { 0, 3, 6, 9 }, 5),
        new ChordPattern("m7b5", new[] { 0, 3, 6, 10 }, 6),
        new ChordPattern("aug", new[] { 0, 4, 8 }, 7),
        new ChordPattern("dim", new[] { 0, 3, 6 }, 8),
        new ChordPattern("sus4", new[] { 0, 5, 7 }, 9),
        new ChordPattern("sus2", new[] { 0, 2, 7 }, 10),
        new ChordPattern("m", new[] { 0, 3, 7 }, 11),
        new ChordPattern("", new[] { 0, 4, 7 }, 12),
    };

    private static void RenderGrid()
    {
        var drawList = ImGui.GetWindowDrawList();
        for (int key = 0; key < 52; key++)
        {
            if (key % 7 == 2)
            {
                drawList.AddLine(CanvasPos + new Vector2(key * PianoRenderer.Width, 0),
                    new(PianoRenderer.P.X + key * PianoRenderer.Width, PianoRenderer.P.Y), ImGui.GetColorU32(new Vector4(Vector3.One, 0.08f)), 2);
            }
            else if (key % 7 == 5)
            {
                drawList.AddLine(CanvasPos + new Vector2(key * PianoRenderer.Width, 0),
                    new(PianoRenderer.P.X + key * PianoRenderer.Width, PianoRenderer.P.Y), ImGui.GetColorU32(new Vector4(Vector3.One, 0.06f)));
            }
        }
    }

    private static bool IsRectInside(Vector2 aMin, Vector2 aMax, Vector2 bMin, Vector2 bMax)
    {
        return aMin.X >= bMin.X && aMax.X <= bMax.X && aMin.Y >= bMin.Y && aMax.Y <= bMax.Y;
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    private static void CancelPreRoll()
    {
        _isPreRollActive = false;
        _preRollRemaining = 0f;
        _preRollScrollVisual = true;
    }

    private static void BeginPreRoll()
    {
        if (MidiPlayer.Playback is null)
            return;

        float durationSeconds = (float)MidiFileData.MidiFile.GetDuration<MetricTimeSpan>().TotalSeconds;
        float timelineFromTimer = FallSpeedVal <= 0f ? MidiPlayer.Seconds : MidiPlayer.Timer / (100f * FallSpeedVal);
        if (!float.IsFinite(timelineFromTimer))
            timelineFromTimer = MidiPlayer.Seconds;

        float baseSeconds = _hasResumeAnchor ? _resumeAnchorSeconds : timelineFromTimer;
        _preRollBaseSeconds = Math.Clamp(baseSeconds, 0f, durationSeconds);
        MidiPlayer.Seconds = _preRollBaseSeconds;
        _preRollRemaining = PreRollSeconds;
        _isPreRollActive = true;
        _preRollScrollVisual = !_hasResumeAnchor;
        _learningPausedByMissingNote = false;
        MidiPlayer.IsTimerRunning = false;
        MidiPlayer.Playback.Stop();
        MidiPlayer.SoundFontEngine?.StopAllNote(0);
        _hitLineGlows.Clear();
        _hitLineParticles.Clear();

        float visualSeconds = _preRollScrollVisual ? (_preRollBaseSeconds - PreRollSeconds) : _preRollBaseSeconds;
        MidiPlayer.Timer = visualSeconds * 100f * FallSpeedVal;
    }

    private static float CapturePlaybackSeconds()
    {
        if (MidiPlayer.Playback is null)
            return Math.Max(0f, MidiPlayer.Seconds);

        float captured = MidiPlayer.Seconds;
        try
        {
            captured = (float)MidiPlayer.Playback.GetCurrentTime<MetricTimeSpan>().TotalSeconds;
        }
        catch
        {
            // Keep the last known timeline value when device time is not available.
        }

        if (!float.IsFinite(captured))
            captured = MidiPlayer.Seconds;

        return Math.Max(0f, captured);
    }

    private static void PausePlaybackAtCurrentPosition(bool stopAllNotes)
    {
        if (MidiPlayer.Playback is null)
            return;

        float durationSeconds = (float)MidiFileData.MidiFile.GetDuration<MetricTimeSpan>().TotalSeconds;
        float timerSeconds = 0f;
        if (FallSpeedVal > 0f)
        {
            timerSeconds = MidiPlayer.Timer / (100f * FallSpeedVal);
            if (!float.IsFinite(timerSeconds))
                timerSeconds = 0f;
        }
        float pauseSeconds = Math.Max(CapturePlaybackSeconds(), Math.Max(MidiPlayer.Seconds, timerSeconds));
        pauseSeconds = Math.Clamp(pauseSeconds, 0f, durationSeconds);

        CancelPreRoll();
        MidiPlayer.IsTimerRunning = false;
        MidiPlayer.Playback.Stop();
        if (stopAllNotes)
            MidiPlayer.SoundFontEngine?.StopAllNote(0);

        MidiPlayer.Seconds = pauseSeconds;
        MidiPlayer.Timer = pauseSeconds * 100f * FallSpeedVal;
        _resumeAnchorSeconds = pauseSeconds;
        _hasResumeAnchor = true;
        _learningPausedByMissingNote = false;
    }

    private static void StartPlaybackAfterPreRoll()
    {
        if (MidiPlayer.Playback is null)
            return;

        long microseconds = (long)(_preRollBaseSeconds * 1_000_000f);
        MidiPlayer.Seconds = _preRollBaseSeconds;
        MidiPlayer.Timer = _preRollBaseSeconds * 100f * FallSpeedVal;
        if (IsLearningMode)
        {
            MidiPlayer.Playback.Stop();
        }
        else
        {
            MidiPlayer.Playback.MoveToTime(new MetricTimeSpan(microseconds));
            MidiPlayer.Playback.Start();
        }
        MidiPlayer.StartTimer();
        _hasResumeAnchor = false;
        _learningPausedByMissingNote = false;
        CancelPreRoll();
    }

    private static void UpdatePreRoll()
    {
        if (!_isPreRollActive || MidiPlayer.Playback is null)
            return;

        float dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0.001f, 0.05f);
        float speed = Math.Clamp((float)MidiPlayer.Playback.Speed, 0.25f, 4f);
        _preRollRemaining = Math.Max(0f, _preRollRemaining - dt);
        if (_preRollScrollVisual)
            MidiPlayer.Timer += dt * 100f * speed * FallSpeedVal;

        if (_preRollRemaining <= 0f)
            StartPlaybackAfterPreRoll();
    }

    private static void DrawPreRollCountdown()
    {
        if (!_isPreRollActive)
            return;

        float remaining = Math.Max(0f, _preRollRemaining);
        float progress = Math.Clamp((PreRollSeconds - remaining) / PreRollSeconds, 0f, 1f);
        int countdownValue = Math.Max(1, (int)MathF.Ceiling(remaining));
        string text = countdownValue.ToString();
        float pulse = 1f + 0.04f * MathF.Sin(progress * MathF.PI * 8f);
        float fontSize = MathF.Max(58f, ImGui.GetFontSize() * 3.9f) * pulse;
        Vector2 center = new(ImGui.GetIO().DisplaySize.X * 0.5f, PianoRenderer.P.Y * 0.18f);
        Vector2 panelSize = ImGuiUtils.FixedSize(new Vector2(120f, 92f)) * pulse;
        Vector2 panelMin = center - panelSize * 0.5f;
        Vector2 panelMax = center + panelSize * 0.5f;

        float textScale = fontSize / MathF.Max(1f, ImGui.GetFontSize());
        Vector2 textSize = ImGui.CalcTextSize(text) * textScale;
        Vector2 pos = center - textSize * 0.5f;

        var drawList = ImGui.GetWindowDrawList();
        Vector4 accent = new(
            Math.Clamp(ThemeManager.NoteFadeCol.X * 1.4f, 0f, 1f),
            Math.Clamp(ThemeManager.NoteFadeCol.Y * 1.4f, 0f, 1f),
            Math.Clamp(ThemeManager.NoteFadeCol.Z * 1.4f, 0f, 1f),
            0.90f);
        drawList.AddRectFilled(panelMin, panelMax, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.58f)), 18f);
        drawList.AddRect(panelMin, panelMax, ImGui.GetColorU32(accent), 18f, ImDrawFlags.None, 2f);
        drawList.AddText(ImGui.GetFont(), fontSize, pos + new Vector2(2f, 2f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.88f)), text);
        drawList.AddText(ImGui.GetFont(), fontSize, pos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.98f)), text);
    }

    public static void QueueInitialPreRollStart()
    {
        _queueInitialPreRoll = true;
        _hasResumeAnchor = false;
        _resumeAnchorSeconds = 0f;
        _learningPausedByMissingNote = false;
    }
    
    private static bool IsNoteEnabled(int index) => true;

    private static float GetNoteGradientT(int midiNote)
    {
        const float minMidi = 21f;
        const float maxMidi = 108f;
        return Math.Clamp((midiNote - minMidi) / (maxMidi - minMidi), 0f, 1f);
    }

    private static Vector4 GetGradientNoteColor(int midiNote)
    {
        Vector4 baseColor = ThemeManager.NoteFadeCol;
        float t = GetNoteGradientT(midiNote);
        Vector4 dark = new(baseColor.X * 0.48f, baseColor.Y * 0.48f, baseColor.Z * 0.48f, baseColor.W);
        Vector4 bright = Vector4.Lerp(baseColor, Vector4.One, 0.22f);
        Vector4 result = Vector4.Lerp(dark, bright, t);
        result.W = baseColor.W;
        return result;
    }

    private static Vector4 GetNoteColor(int index, int midiNote)
    {
        return IsNoteEnabled(index) ? GetGradientNoteColor(midiNote) : ThemeManager.MainBgCol;
    }

    private static Vector4 ScaleRgb(Vector4 color, float factor)
    {
        return new Vector4(color.X * factor, color.Y * factor, color.Z * factor, color.W);
    }

    private static Vector4 ApplyVelocityVisualToColor(Vector4 baseColor, float velocityNorm)
    {
        if (!CoreSettings.UseVelocityAsNoteOpacity)
            return baseColor;

        float v = Math.Clamp(velocityNorm, 0f, 1f);
        float shaped = MathF.Pow(v, 1.25f);
        float brightness = 0.24f + 0.76f * shaped;
        Vector4 c = ScaleRgb(baseColor, brightness);
        c.W = Math.Clamp(MathF.Pow(v, 1.45f), 0.02f, 1f);
        return c;
    }

    private static void GetNoteBodyGradient(Vector4 baseColor, bool isBlack, out Vector4 leftCol, out Vector4 rightCol)
    {
        float alpha = baseColor.W;
        float leftMul = isBlack ? 0.72f : 0.84f;
        float rightBright = isBlack ? 0.26f : 0.44f;
        leftCol = ScaleRgb(baseColor, leftMul);
        rightCol = Vector4.Lerp(baseColor, Vector4.One, rightBright);
        leftCol.W = alpha;
        rightCol.W = alpha;
    }

    private static void DrawHorizontalGradientNoteBody(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        Vector4 leftCol,
        Vector4 rightCol,
        float rounding)
    {
        float width = MathF.Max(1f, max.X - min.X);
        float safeRounding = MathF.Min(rounding, MathF.Max(0f, width * 0.5f - 0.5f));

        // Solid rounded body avoids center seam artifacts.
        Vector4 baseCol = Vector4.Lerp(leftCol, rightCol, 0.5f);
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(baseCol), safeRounding, ImDrawFlags.RoundCornersAll);

        // Stronger/saturated contour as requested.
        Vector4 border = Vector4.Lerp(leftCol, rightCol, 0.82f);
        border.X = Math.Clamp(border.X * 1.3f, 0f, 1f);
        border.Y = Math.Clamp(border.Y * 1.3f, 0f, 1f);
        border.Z = Math.Clamp(border.Z * 1.3f, 0f, 1f);
        border.W = Math.Clamp(baseCol.W * 1.1f + 0.2f, 0.35f, 1f);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), safeRounding, ImDrawFlags.RoundCornersAll, 1.2f);
    }

    private static void DrawNoteLabel(ImDrawListPtr drawList, Vector2 min, Vector2 max, int midiNote)
    {
        string noteLabel = KeysUtils.GetMidiNoteLabel(midiNote);
        float maxWidth = max.X - min.X - 2f;
        float maxHeight = max.Y - min.Y - 4f;
        if (maxWidth <= 2f || maxHeight <= 2f)
            return;

        if (!TryGetFittedTextSize(noteLabel, maxWidth, maxHeight, out float fontSize, out Vector2 textSize))
            return;

        Vector2 textPos = new(
            min.X + (max.X - min.X - textSize.X) / 2f,
            min.Y + (max.Y - min.Y - textSize.Y) / 2f);

        drawList.AddText(ImGui.GetFont(), fontSize, textPos + new Vector2(1), ImGui.GetColorU32(new Vector4(0, 0, 0, 1)), noteLabel);
        drawList.AddText(ImGui.GetFont(), fontSize, textPos, ImGui.GetColorU32(Vector4.One), noteLabel);
    }

    private static bool TryGetFittedTextSize(string text, float maxWidth, float maxHeight, out float fontSize, out Vector2 textSize)
    {
        const float minFontSize = 6f;
        float baseFontSize = ImGui.GetFontSize();
        Vector2 baseTextSize = ImGui.CalcTextSize(text);

        if (baseTextSize.X <= 0f || baseTextSize.Y <= 0f || baseFontSize <= 0f)
        {
            fontSize = 0f;
            textSize = Vector2.Zero;
            return false;
        }

        float widthScale = maxWidth / baseTextSize.X;
        float heightScale = maxHeight / baseTextSize.Y;
        float scale = MathF.Min(1f, MathF.Min(widthScale, heightScale));

        if (scale <= 0f)
        {
            fontSize = 0f;
            textSize = Vector2.Zero;
            return false;
        }

        fontSize = MathF.Max(minFontSize, baseFontSize * scale);
        float realScale = fontSize / baseFontSize;
        textSize = baseTextSize * realScale;

        return true;
    }

    private static float NextHitLineRandom()
    {
        _hitLineParticleRng ^= _hitLineParticleRng << 13;
        _hitLineParticleRng ^= _hitLineParticleRng >> 17;
        _hitLineParticleRng ^= _hitLineParticleRng << 5;
        return (_hitLineParticleRng & 0x00FFFFFF) / 16777216f;
    }

    private static bool TryGetChordName(HashSet<int> pitchClasses, out string chordName)
    {
        chordName = string.Empty;
        if (pitchClasses.Count < 3)
            return false;

        bool[] active = new bool[12];
        foreach (int pitchClass in pitchClasses)
        {
            active[(pitchClass % 12 + 12) % 12] = true;
        }

        int bestScore = int.MaxValue;
        string best = string.Empty;
        int activeCount = pitchClasses.Count;
        for (int root = 0; root < 12; root++)
        {
            if (!active[root])
                continue;

            foreach (ChordPattern pattern in _chordPatterns)
            {
                if (!TryMatchChordPattern(active, root, pattern, activeCount, out int score))
                    continue;

                if (score >= bestScore)
                    continue;

                bestScore = score;
                best = _pitchClassNames[root] + pattern.Suffix;
            }
        }

        if (bestScore == int.MaxValue)
            return false;

        chordName = best;
        return true;
    }

    private static bool TryMatchChordPattern(bool[] active, int root, ChordPattern pattern, int activeCount, out int score)
    {
        score = int.MaxValue;
        bool[] required = new bool[12];
        for (int i = 0; i < pattern.Intervals.Length; i++)
        {
            int target = (root + pattern.Intervals[i]) % 12;
            required[target] = true;
            if (!active[target])
                return false;
        }

        int extras = 0;
        for (int i = 0; i < 12; i++)
        {
            if (active[i] && !required[i])
                extras++;
        }

        // Keep it readable: allow little color tones, ignore overly dense clusters.
        if (extras > 2)
            return false;

        int densityPenalty = activeCount > 5 ? (activeCount - 5) * 2 : 0;
        score = extras * 20 + pattern.Priority + densityPenalty;
        return true;
    }

    private static void DrawChordGuideLines(ImDrawListPtr drawList, Dictionary<long, ChordGuideMarker> markers)
    {
        if (markers.Count == 0)
            return;

        const float longChordMinSeconds = 0.42f;
        List<ChordGuideMarker> ordered = new(markers.Count);
        foreach (ChordGuideMarker marker in markers.Values)
        {
            if (marker.LongestLengthSeconds < longChordMinSeconds || marker.PitchClasses.Count < 3)
                continue;
            ordered.Add(marker);
        }

        if (ordered.Count == 0)
            return;

        ordered.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));

        float left = PianoRenderer.P.X;
        float right = PianoRenderer.P.X + PianoRenderer.Width * 52f;
        Vector4 baseColor = Vector4.Lerp(ThemeManager.NoteFadeCol, Vector4.One, 0.25f);
        Vector4 lineColor = new(baseColor.X, baseColor.Y, baseColor.Z, 0.085f);
        Vector4 textColor = new(baseColor.X, baseColor.Y, baseColor.Z, 0.7f);
        Vector4 chipBgColor = new(0f, 0f, 0f, 0.24f);
        float labelFontSize = MathF.Max(10f, ImGui.GetFontSize() * 0.78f);
        float minY = -8f;
        float maxY = PianoRenderer.P.Y + 6f;

        string lastChord = string.Empty;
        float lastChordSecond = float.NegativeInfinity;

        foreach (ChordGuideMarker marker in ordered)
        {
            if (marker.AttackY < minY || marker.AttackY > maxY)
                continue;

            if (!TryGetChordName(marker.PitchClasses, out string chordName))
                continue;

            if (chordName == lastChord && marker.StartSeconds - lastChordSecond < 0.6f)
                continue;

            drawList.AddLine(new Vector2(left, marker.AttackY), new Vector2(right, marker.AttackY), ImGui.GetColorU32(lineColor), 1f);

            Vector2 textSize = ImGui.CalcTextSize(chordName);
            float scale = labelFontSize / MathF.Max(1f, ImGui.GetFontSize());
            Vector2 scaledTextSize = textSize * scale;
            Vector2 chipMin = new(right - scaledTextSize.X - 12f, marker.AttackY - scaledTextSize.Y - 3f);
            Vector2 chipMax = new(right - 4f, marker.AttackY - 1f);
            drawList.AddRectFilled(chipMin, chipMax, ImGui.GetColorU32(chipBgColor), 4f, ImDrawFlags.RoundCornersAll);
            drawList.AddText(ImGui.GetFont(), labelFontSize, new Vector2(chipMin.X + 5f, chipMin.Y + 1f), ImGui.GetColorU32(textColor), chordName);

            lastChord = chordName;
            lastChordSecond = marker.StartSeconds;
        }
    }

    private static void DrawHitLineConsumeEffect(ImDrawListPtr drawList, float x1, float x2, Vector4 color, float intensity, float verticalScale)
    {
        float lineY = PianoRenderer.P.Y - 1f;
        float velScale = CoreSettings.UseVelocityAsNoteOpacity
            ? Math.Clamp(color.W, 0.12f, 1f)
            : 1f;
        Vector3 sourceRgb = new(color.X, color.Y, color.Z);
        Vector3 rgb = Vector3.Lerp(sourceRgb, Vector3.One, 0.5f);
        float width = MathF.Max(6f, x2 - x1);
        float glowBoost = 1.22f + 0.25f * velScale;
        float energy = Math.Clamp(intensity * glowBoost, 0.12f, 2.05f);
        float riseScale = Math.Clamp(verticalScale, 0.5f, 1.1f);
        float roundnessBase = Math.Clamp(width * 0.28f, 2f, 6f);
        Vector3 hot = Vector3.Lerp(rgb, Vector3.One, 0.8f);

        // Keep horizontal spread very close to key width.
        float corePadX = Math.Clamp(width * 0.012f, 0f, 0.24f);
        float auraPadX = corePadX + Math.Clamp(width * 0.008f, 0.04f, 0.2f);

        // Compact upward bloom with smooth vertical softness.
        float upReach = (8f + 24f * energy) * riseScale;
        float coreDown = 0.6f + 0.24f * energy;
        float auraUp = upReach + (8f + 12f * energy) * riseScale;
        float auraDown = 1.0f + 0.28f * energy;
        float farAuraUp = auraUp + (10f + 16f * energy) * riseScale;
        float farAuraDown = auraDown + 0.9f;

        drawList.AddRectFilledMultiColor(
            new Vector2(x1 - auraPadX - 0.25f, lineY - farAuraUp),
            new Vector2(x2 + auraPadX + 0.25f, lineY + farAuraDown),
                ImGui.GetColorU32(new Vector4(rgb, 0f)),
                ImGui.GetColorU32(new Vector4(rgb, 0f)),
                ImGui.GetColorU32(new Vector4(rgb, Math.Clamp(0.26f * energy, 0.08f, 0.38f))),
                ImGui.GetColorU32(new Vector4(rgb, Math.Clamp(0.26f * energy, 0.08f, 0.38f))));

        drawList.AddRectFilledMultiColor(
            new Vector2(x1 - auraPadX, lineY - auraUp),
            new Vector2(x2 + auraPadX, lineY + auraDown),
                ImGui.GetColorU32(new Vector4(rgb, 0f)),
                ImGui.GetColorU32(new Vector4(rgb, 0f)),
                ImGui.GetColorU32(new Vector4(rgb, Math.Clamp(0.38f * energy, 0.14f, 0.64f))),
                ImGui.GetColorU32(new Vector4(rgb, Math.Clamp(0.38f * energy, 0.14f, 0.64f))));

        drawList.AddRectFilledMultiColor(
            new Vector2(x1 - corePadX, lineY - upReach),
            new Vector2(x2 + corePadX, lineY + coreDown),
                ImGui.GetColorU32(new Vector4(hot, 0f)),
                ImGui.GetColorU32(new Vector4(hot, 0f)),
                ImGui.GetColorU32(new Vector4(hot, Math.Clamp(1.25f * energy, 0.3f, 1f))),
                ImGui.GetColorU32(new Vector4(hot, Math.Clamp(1.25f * energy, 0.3f, 1f))));

        float flashHalfHeight = 1.25f + 0.85f * energy;
        drawList.AddRectFilled(
            new Vector2(x1 - corePadX, lineY - flashHalfHeight),
            new Vector2(x2 + corePadX, lineY + flashHalfHeight),
            ImGui.GetColorU32(new Vector4(hot, Math.Clamp(1.2f * energy, 0.36f, 1f))),
            roundnessBase + 1.2f,
            ImDrawFlags.RoundCornersAll);

        float coreThickness = 1.8f + energy * 1.4f;
        drawList.AddLine(
            new Vector2(x1 - corePadX, lineY),
            new Vector2(x2 + corePadX, lineY),
            ImGui.GetColorU32(new Vector4(hot, Math.Clamp(1.35f * energy, 0.5f, 1f))),
            coreThickness);

        // Keep only one core stroke to avoid "double glow" look on the hit line.
    }

    private static void SpawnHitLineParticles(float x1, float x2, Vector4 noteColor, int spawnCount, float edgeBurst, float intensity)
    {
        if (spawnCount <= 0)
            return;

        int maxParticles = 760;
        int available = maxParticles - _hitLineParticles.Count;
        if (available <= 0)
            return;

        spawnCount = Math.Min(spawnCount, available);
        float width = MathF.Max(6f, x2 - x1);
        float lineY = PianoRenderer.P.Y - 1f;
        float opacityScale = CoreSettings.UseVelocityAsNoteOpacity
            ? Math.Clamp(MathF.Pow(noteColor.W, 1.05f), 0.08f, 1f)
            : 1f;
        Vector4 particleColor = Vector4.Lerp(noteColor, Vector4.One, 0.72f);
        particleColor.W = opacityScale;

        for (int i = 0; i < spawnCount; i++)
        {
            float t = NextHitLineRandom();
            float speedUp = 92f + 150f * NextHitLineRandom() + intensity * 34f + edgeBurst * 30f;
            float speedSide = (NextHitLineRandom() - 0.5f) * (54f + width * 0.5f);
            float life = 0.2f + 0.16f * NextHitLineRandom() + edgeBurst * 0.1f;
            float size = 1.2f + 1.7f * NextHitLineRandom() + edgeBurst * 0.45f;

            _hitLineParticles.Add(new HitLineParticle
            {
                Position = new Vector2(Lerp(x1, x2, t), lineY + 0.5f),
                Velocity = new Vector2(speedSide, -speedUp),
                Color = particleColor,
                Life = life,
                MaxLife = life,
                Size = size
            });
        }
    }

    private static void DrawHitLineTransientEffects(ImDrawListPtr drawList)
    {
        float dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0.001f, 0.05f);
        float playbackSpeed = MidiPlayer.Playback is null ? 1f : (float)MidiPlayer.Playback.Speed;
        playbackSpeed = Math.Clamp(playbackSpeed, 0.25f, 4f);
        float speedScale = playbackSpeed;
        float glowDecay = MathF.Exp(-dt * (12.5f + 5.5f * speedScale));

        _hitLineGlowKeys.Clear();
        foreach (var key in _hitLineGlows.Keys)
        {
            _hitLineGlowKeys.Add(key);
        }

        _hitLineGlowsToRemove.Clear();
        foreach (int key in _hitLineGlowKeys)
        {
            if (!_hitLineGlows.TryGetValue(key, out var glow))
                continue;

            glow.Intensity *= glowDecay;
            if (glow.Intensity < 0.13f)
            {
                _hitLineGlowsToRemove.Add(key);
                continue;
            }

            _hitLineGlows[key] = glow;
            DrawHitLineConsumeEffect(drawList, glow.X1, glow.X2, glow.Color, glow.Intensity, glow.VerticalScale);
        }

        foreach (int key in _hitLineGlowsToRemove)
        {
            _hitLineGlows.Remove(key);
        }

        float particleDrag = MathF.Exp(-dt * (3.9f + 1.2f * speedScale));
        float particleLifeStep = dt * (1.05f + 0.55f * speedScale);
        for (int i = _hitLineParticles.Count - 1; i >= 0; i--)
        {
            HitLineParticle particle = _hitLineParticles[i];
            particle.Life -= particleLifeStep;
            if (particle.Life <= 0f)
            {
                _hitLineParticles.RemoveAt(i);
                continue;
            }

            particle.Position += particle.Velocity * dt;
            particle.Velocity *= particleDrag;
            float life01 = particle.Life / particle.MaxLife;
            float alpha = MathF.Pow(life01, 1.2f) * 1.35f * particle.Color.W;
            float size = particle.Size * (0.65f + life01 * 1.05f);
            Vector4 color = new Vector4(particle.Color.X, particle.Color.Y, particle.Color.Z, alpha);

            drawList.AddCircleFilled(particle.Position, size, ImGui.GetColorU32(color));
            drawList.AddLine(
                particle.Position,
                particle.Position - particle.Velocity * (0.012f + 0.008f * life01),
                ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, alpha)),
                1.35f);

            _hitLineParticles[i] = particle;
        }
    }

    private static void RegisterHitLineNoteEffect(int noteId, float py1, float py2, float x1, float x2, Vector4 noteColor)
    {
        bool intersectsHitLine = py1 <= PianoRenderer.P.Y && py2 >= PianoRenderer.P.Y - 0.5f;
        if (!intersectsHitLine)
            return;

        float dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0.001f, 0.05f);
        float lineY = PianoRenderer.P.Y;
        float edgeDistance = MathF.Abs(py2 - lineY);
        float edgeBurst = MathF.Exp(-edgeDistance / 8f);
        float sustain = 0.05f;
        float velocityOpacityScale = CoreSettings.UseVelocityAsNoteOpacity
            ? Math.Clamp(MathF.Pow(noteColor.W, 1.2f), 0.08f, 1f)
            : 1f;
        float targetIntensity = Math.Clamp((sustain + edgeBurst * 1.2f) * (0.3f + 0.7f * velocityOpacityScale), 0.18f, 1.5f);
        float noteVisualHeight = MathF.Max(2f, py2 - py1);
        float targetVerticalScale = Math.Clamp((noteVisualHeight + 10f) / 68f, 0.5f, 1.1f);

        if (_hitLineGlows.TryGetValue(noteId, out var glow))
        {
            glow.X1 = Lerp(glow.X1, x1, 0.45f);
            glow.X2 = Lerp(glow.X2, x2, 0.45f);
            Vector4 glowColor = Vector4.Lerp(noteColor, Vector4.One, 0.22f);
            glowColor.W = noteColor.W;
            glow.Color = Vector4.Lerp(glow.Color, glowColor, 0.32f);
            glow.Intensity = MathF.Max(glow.Intensity * 0.84f + targetIntensity * 0.55f, targetIntensity);
            glow.VerticalScale = MathF.Max(glow.VerticalScale * 0.82f + targetVerticalScale * 0.48f, targetVerticalScale);

            float width = MathF.Max(6f, x2 - x1);
            float emitRate = (34f + width * 0.42f) * (0.68f + targetIntensity * 1.14f) * (0.5f + 0.5f * velocityOpacityScale);
            if (edgeBurst > 0.55f)
                emitRate += 54f * edgeBurst;
            glow.ParticleCarry += emitRate * dt;
            int spawnCount = Math.Min(24, (int)glow.ParticleCarry);
            glow.ParticleCarry -= spawnCount;
            _hitLineGlows[noteId] = glow;
            SpawnHitLineParticles(x1, x2, noteColor, spawnCount, edgeBurst, targetIntensity);
        }
        else
        {
            float width = MathF.Max(6f, x2 - x1);
            float emitRate = (34f + width * 0.42f) * (0.68f + targetIntensity * 1.14f) * (0.5f + 0.5f * velocityOpacityScale);
            float carry = emitRate * dt;
            int spawnCount = Math.Min(24, Math.Max(1, (int)carry));
            carry = MathF.Max(0f, carry - spawnCount);
            Vector4 initialGlowColor = Vector4.Lerp(noteColor, Vector4.One, 0.22f);
            initialGlowColor.W = noteColor.W;

            _hitLineGlows[noteId] = new HitLineGlowState
            {
                X1 = x1,
                X2 = x2,
                Color = initialGlowColor,
                Intensity = targetIntensity * 0.9f,
                VerticalScale = targetVerticalScale,
                ParticleCarry = carry
            };
            SpawnHitLineParticles(x1, x2, noteColor, spawnCount, edgeBurst, targetIntensity);
        }
    }

    private static void ApplyMasterVolume(float volume)
    {
        CoreSettings.SetMasterVolume(volume);

        if (CoreSettings.SoundEngine == SoundEngine.SoundFonts)
        {
            MidiPlayer.SoundFontEngine?.SetVolume(CoreSettings.MasterVolume);
        }
        else if (CoreSettings.SoundEngine == SoundEngine.Plugins)
        {
            VstPlayer.SetVolume(CoreSettings.MasterVolume);
        }
    }

    private static void DrawMasterVolumeControl(float yOffset, string id)
    {
        ImGui.SetCursorScreenPos(new(ImGui.GetIO().DisplaySize.X - ImGuiUtils.FixedSize(new Vector2(220)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(yOffset)).Y));
        ImGui.PushItemWidth(ImGuiUtils.FixedSize(new Vector2(220)).X);

        float volumePercent = CoreSettings.MasterVolume * 100f;
        if (ImGui.SliderFloat($"##MasterVolume{id}", ref volumePercent, 0f, 1000f, "Volume %.0f%%", ImGuiSliderFlags.AlwaysClamp))
        {
            ApplyMasterVolume(volumePercent / 100f);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Volume master. Acima de 100 pode distorcer.");
        }

        ImGui.PopItemWidth();
    }

    private static void DrawInputNotes()
    {
        var speed = 100f * ImGui.GetIO().DeltaTime * FallSpeedVal;
        var drawList = ImGui.GetWindowDrawList();

        int index = 0;
        List<IOHandle.NoteRect> toRemove = new();
        foreach (var note in IOHandle.NoteRects.ToArray())
        {
            float py1;
            float py2;

            //int idx = IOHandle.NoteRects.IndexOf(note);

            var n = IOHandle.NoteRects[index];
            n.Time += speed;
            IOHandle.NoteRects[index] = n;

            var length = note.WasReleased ? note.FinalTime : note.Time;

            py1 = note.PY1 - note.Time;
            py2 = note.PY2 + length - note.Time;

            if (py2 < 0)
            {
                toRemove.Add(note);
                //IOHandle.NoteRects.Remove(note);
                index++;
                continue;
            }

            if (note.IsBlack)
            {
                float inputVelocityNorm = note.VelocityNorm <= 0f ? 1f : note.VelocityNorm;

                if (CoreSettings.NeonFx)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float thickness = i * 2;
                        float alpha = 0.2f + (3 - i) * 0.2f;
                        Vector4 playNoteColor = ApplyVelocityVisualToColor(GetGradientNoteColor(note.KeyNum), inputVelocityNorm);
                        uint color = ImGui.GetColorU32(new Vector4(playNoteColor.X, playNoteColor.Y, playNoteColor.Z, alpha) * 0.5f * 0.7f);
                        drawList.AddRect(
                            new(PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width + PianoRenderer.Width * 3 / 4 - 1, py1 - 1),
                            new(PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width + PianoRenderer.Width * 5 / 4 + 1, py2 + 1),
                            color,
                            CoreSettings.NoteRoundness,
                            0,
                            thickness
                        );
                    }
                }
                else
                {
                        uint color = ImGui.GetColorU32(new Vector4(Vector3.Zero, 1f) * 0.5f);
                    drawList.AddRect(
                        new Vector2(PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width + PianoRenderer.Width * 3 / 4 - 1, py1 - 1),
                        new Vector2(PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width + PianoRenderer.Width * 5 / 4 + 1, py2 + 1),
                        color,
                        CoreSettings.NoteRoundness,
                        0,
                        1f
                    );
                }

                float bx1 = PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width + PianoRenderer.Width * 3 / 4;
                float bx2 = PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width + PianoRenderer.Width * 5 / 4;
                Vector4 blackBase = ApplyVelocityVisualToColor(ImGuiUtils.DarkenColor(GetGradientNoteColor(note.KeyNum), 0.2f), inputVelocityNorm);
                GetNoteBodyGradient(blackBase, true, out var blackLeft, out var blackRight);
                DrawHorizontalGradientNoteBody(drawList, new Vector2(bx1, py1), new Vector2(bx2, py2), blackLeft, blackRight, CoreSettings.NoteRoundness);

                DrawNoteLabel(
                    drawList,
                    new Vector2(PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width + PianoRenderer.Width * 3 / 4, py1),
                    new Vector2(PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width + PianoRenderer.Width * 5 / 4, py2),
                    note.KeyNum);
            }
            else
            {
                float inputVelocityNorm = note.VelocityNorm <= 0f ? 1f : note.VelocityNorm;

                if (CoreSettings.NeonFx)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float thickness = i * 2;
                        float alpha = 0.2f + (3 - i) * 0.2f;
                        Vector4 playNoteColor = ApplyVelocityVisualToColor(GetGradientNoteColor(note.KeyNum), inputVelocityNorm);
                        uint color = ImGui.GetColorU32(new Vector4(playNoteColor.X, playNoteColor.Y, playNoteColor.Z, alpha) * 0.5f);
                        drawList.AddRect(
                            new(PianoRenderer.P.X + PianoRenderer.WhiteNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width - 1, py1 - 1),
                            new(PianoRenderer.P.X + PianoRenderer.WhiteNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width + PianoRenderer.Width + 1, py2 + 1),
                            color,
                            CoreSettings.NoteRoundness,
                            0,
                            thickness
                        );
                    }
                }
                else
                {
                    uint color = ImGui.GetColorU32(new Vector4(Vector3.Zero, 1f) * 0.5f);
                    drawList.AddRect(
                        new Vector2(PianoRenderer.P.X + PianoRenderer.WhiteNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width - 1, py1 - 1),
                        new Vector2(PianoRenderer.P.X + PianoRenderer.WhiteNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width + PianoRenderer.Width + 1, py2 + 1),
                        color,
                        CoreSettings.NoteRoundness,
                        0,
                        1f
                    );
                }

                float wx1 = PianoRenderer.P.X + PianoRenderer.WhiteNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width;
                float wx2 = wx1 + PianoRenderer.Width;
                Vector4 whiteBase = ApplyVelocityVisualToColor(GetGradientNoteColor(note.KeyNum), inputVelocityNorm);
                GetNoteBodyGradient(whiteBase, false, out var whiteLeft, out var whiteRight);
                DrawHorizontalGradientNoteBody(drawList, new Vector2(wx1, py1), new Vector2(wx2, py2), whiteLeft, whiteRight, CoreSettings.NoteRoundness);

                DrawNoteLabel(
                    drawList,
                    new Vector2(PianoRenderer.P.X + PianoRenderer.WhiteNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width, py1),
                    new Vector2(PianoRenderer.P.X + PianoRenderer.WhiteNoteToKey.GetValueOrDefault((SevenBitNumber)note.KeyNum, 0) * PianoRenderer.Width + PianoRenderer.Width, py2),
                    note.KeyNum);
            }
            index++;
        }

        if (toRemove.Count > 0)
        {
            IOHandle.NoteRects.RemoveRange(0, toRemove.Count - 1);
            IOHandle.NoteRects.RemoveAt(0);
        }
    }

    private static void DrawPlaybackNotes()
    {
        var drawList = ImGui.GetWindowDrawList();

        UpdatePreRoll();
        if (MidiPlayer.IsTimerRunning && !_isPreRollActive)
        {
            MidiPlayer.Timer += ImGui.GetIO().DeltaTime * 100f * (float)MidiPlayer.Playback.Speed * FallSpeedVal;
            if (IsLearningMode && FallSpeedVal > 0f)
            {
                float durationSeconds = (float)MidiFileData.MidiFile.GetDuration<MetricTimeSpan>().TotalSeconds;
                float timelineSeconds = MidiPlayer.Timer / (100f * FallSpeedVal);
                if (!float.IsFinite(timelineSeconds))
                    timelineSeconds = MidiPlayer.Seconds;

                timelineSeconds = Math.Clamp(timelineSeconds, 0f, durationSeconds);
                MidiPlayer.Seconds = timelineSeconds;

                if (timelineSeconds >= durationSeconds)
                {
                    MidiPlayer.StopTimer();
                    MidiPlayer.Playback.Stop();
                }
            }
        }

        int index = 0;
        var notes = MidiFileData.Notes;
        bool missingNote = false;
        Dictionary<long, ChordGuideMarker> chordGuideMarkers = new();
        foreach (Note note in notes)
        {
            var time = (float)note.TimeAs<MetricTimeSpan>(MidiFileData.TempoMap).TotalSeconds * FallSpeedVal;
            float lengthSeconds = (float)note.LengthAs<MetricTimeSpan>(MidiFileData.TempoMap).TotalSeconds;
            var length = lengthSeconds * FallSpeedVal;
            var col = GetNoteColor(index, note.NoteNumber);
            
            // color opacity based on note velocity
            if (CoreSettings.UseVelocityAsNoteOpacity)
            {
                float velocity01 = Math.Clamp(note.Velocity / 127f, 0f, 1f);
                col = ApplyVelocityVisualToColor(col, velocity01);
            }

            float py1;
            float py2;
            if (UpDirection && !IsLearningMode && !IsEditMode)
            {
                py1 = PianoRenderer.P.Y + time * 100 - MidiPlayer.Timer;
                py2 = PianoRenderer.P.Y + time * 100 + length * 100 - MidiPlayer.Timer;

                // skip notes outside of screen to save performance
                if (py1 > PianoRenderer.P.Y || py2 < 0)
                {
                    index++;
                    continue;
                }
            }
            else
            {
                py1 = PianoRenderer.P.Y - time * 100 + MidiPlayer.Timer;
                py2 = PianoRenderer.P.Y - time * 100 + length * 100 + MidiPlayer.Timer;

                py1 -= length * 100;
                py2 -= length * 100;

                if (IsLearningMode)
                {
                    float hitLineStep = MathF.Abs(ImGui.GetIO().DeltaTime * 100f * (float)MidiPlayer.Playback.Speed * FallSpeedVal);
                    float hitLineWindow = MathF.Max(1.5f, hitLineStep + 1f);
                    if (py2 >= PianoRenderer.P.Y - hitLineWindow && py2 <= PianoRenderer.P.Y + hitLineWindow)
                    {
                        if (IsNoteEnabled(index) && !IOHandle.PressedKeys.Contains(note.NoteNumber))
                        {
                            missingNote = true;
                            if (!_learningPausedByMissingNote)
                            {
                                MidiPlayer.StopTimer();
                                MidiPlayer.Playback.Stop();
                                _learningPausedByMissingNote = true;
                            }

                            if (note.NoteName.ToString().EndsWith("Sharp"))
                            {
                                var v3 = new Vector3(col.X, col.Y, col.Z);
                                ImGui.GetForegroundDrawList().AddCircleFilled(new(PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault(note.NoteNumber, 0) * PianoRenderer.Width + PianoRenderer.Width * 3 / 4 + 10,
                                    py2 + PianoRenderer.Height / 1.7f), 7, ImGui.GetColorU32(new Vector4(v3, 1)));
                            }
                            else
                            {
                                ImGui.GetForegroundDrawList().AddCircleFilled(new(PianoRenderer.P.X + PianoRenderer.WhiteNoteToKey.GetValueOrDefault(note.NoteNumber, 0) * PianoRenderer.Width + PianoRenderer.Width / 2,
                                    py2 + PianoRenderer.Height / 1.2f), 7, ImGui.GetColorU32(col));
                            }
                        }
                    }
                }

                if (IsEditMode && !_isProgressBarHovered && !_isProgressBarActive)
                {
                    if (ImGui.GetIO().KeyCtrl && ImGui.IsMouseDown(ImGuiMouseButton.Left) && !_isRectMode)
                    {
                        _rectStart = ImGui.GetMousePos();
                        _isRightRect = false;
                        _isRectMode = true;
                    }

                    if (ImGui.GetIO().KeyCtrl && ImGui.IsMouseDown(ImGuiMouseButton.Right) && !_isRectMode)
                    {
                        _rectStart = ImGui.GetMousePos();
                        _isRightRect = true;
                        _isRectMode = true;
                    }

                    if (_isRectMode)
                    {
                        // only allow rect going top-left
                        if (ImGui.GetMousePos().Y > _rectStart.Y || ImGui.GetMousePos().X > _rectStart.X)
                        {
                            _isRectMode = false;
                        }

                        Vector4 rectCol = _isRightRect ? ThemeManager.RightHandCol : ThemeManager.LeftHandCol;
                        var v3 = new Vector3(rectCol.X, rectCol.Y, rectCol.Z);
                        ImGui.GetWindowDrawList().AddRectFilled(_rectStart, ImGui.GetMousePos(), ImGui.GetColorU32(new Vector4(v3, .005f)));

                        float rpx1;
                        float rpx2;
                        if (note.NoteName.ToString().EndsWith("Sharp"))
                        {
                            rpx1 = PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault(note.NoteNumber, 0) * PianoRenderer.Width + PianoRenderer.Width * 3 / 4;
                            rpx2 = PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault(note.NoteNumber, 0) * PianoRenderer.Width + PianoRenderer.Width * 5 / 4;
                        }
                        else
                        {
                            rpx1 = PianoRenderer.P.X + PianoRenderer.WhiteNoteToKey.GetValueOrDefault(note.NoteNumber, 0) * PianoRenderer.Width;
                            rpx2 = PianoRenderer.P.X + PianoRenderer.WhiteNoteToKey.GetValueOrDefault(note.NoteNumber, 0) * PianoRenderer.Width + PianoRenderer.Width;
                        }

                        bool isInside = IsRectInside(_rectStart, ImGui.GetMousePos(), new(rpx1, py1), new(rpx2, py2));
                        if (isInside)
                        {
                            MidiEditing.SetRightHand(index, _isRightRect);
                        }
                    }

                    if ((ImGui.IsMouseReleased(ImGuiMouseButton.Left) || ImGui.IsMouseReleased(ImGuiMouseButton.Right)) && _isRectMode)
                    {
                        MidiEditing.SaveData();
                        _rectEnd = ImGui.GetMousePos();
                        _isRectMode = false;
                    }

                    if (note.NoteName.ToString().EndsWith("Sharp"))
                    {
                        if (ImGui.IsMouseHoveringRect(new(PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault(note.NoteNumber, 0) * PianoRenderer.Width + PianoRenderer.Width * 3 / 4, py1),
                            new(PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault(note.NoteNumber, 0) * PianoRenderer.Width + PianoRenderer.Width * 5 / 4, py2)))
                        {
                            if (ShowTextNotes)
                            {
                                Drawings.NoteTooltip($"Note: {note.NoteName}\nOctave: {note.Octave}\nVelocity: {note.Velocity}" +
                                    $"\nNumber: {note.NoteNumber}\nRight Hand: {LeftRightData.S_IsRightNote[index]}");
                            }

                            if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && !_isRectMode)
                            {
                                // set left
                                MidiEditing.SetRightHand(index, false);
                                MidiEditing.SaveData();
                            }
                            else if (ImGui.IsMouseDown(ImGuiMouseButton.Right) && !_isRectMode)
                            {
                                // set right
                                MidiEditing.SetRightHand(index, true);
                                MidiEditing.SaveData();
                            }
                        }
                    }
                    else
                    {
                        if (ImGui.IsMouseHoveringRect(new(PianoRenderer.P.X + PianoRenderer.WhiteNoteToKey.GetValueOrDefault(note.NoteNumber, 0) * PianoRenderer.Width, py1),
                            new(PianoRenderer.P.X + PianoRenderer.WhiteNoteToKey.GetValueOrDefault(note.NoteNumber, 0) * PianoRenderer.Width + PianoRenderer.Width, py2)))
                        {
                            if (ShowTextNotes)
                            {
                                Drawings.NoteTooltip($"Note: {note.NoteName}\nOctave: {note.Octave}\nVelocity: {note.Velocity}" +
                                    $"\nNumber: {note.NoteNumber}\nRight Hand: {LeftRightData.S_IsRightNote[index]}");
                            }

                            if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && !_isRectMode)
                            {
                                // set left
                                MidiEditing.SetRightHand(index, false);
                                MidiEditing.SaveData();
                            }
                            else if (ImGui.IsMouseDown(ImGuiMouseButton.Right) && !_isRectMode)
                            {
                                // set right
                                MidiEditing.SetRightHand(index, true);
                                MidiEditing.SaveData();
                            }
                        }
                    }
                }
                else
                {
                    // Disable rect mode when the progress bar is hovered or active
                    _isRectMode = false;
                }

                // skip notes outside of screen to save performance
                if (py2 < 0 || py1 > PianoRenderer.P.Y)
                {
                    index++;
                    continue;
                }
            }

            if (lengthSeconds >= 0.42f)
            {
                float attackY = (UpDirection && !IsLearningMode && !IsEditMode) ? py1 : py2;
                if (!chordGuideMarkers.TryGetValue(note.Time, out var marker))
                {
                    marker = new ChordGuideMarker
                    {
                        AttackY = attackY,
                        StartSeconds = time / MathF.Max(FallSpeedVal, 0.0001f),
                        LongestLengthSeconds = lengthSeconds
                    };
                    chordGuideMarkers[note.Time] = marker;
                }

                marker.AttackY = attackY;
                marker.LongestLengthSeconds = MathF.Max(marker.LongestLengthSeconds, lengthSeconds);
                marker.PitchClasses.Add(note.NoteNumber % 12);
            }

            if (note.NoteName.ToString().EndsWith("Sharp"))
            {
                float x1 = PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault(note.NoteNumber, 0) * PianoRenderer.Width + PianoRenderer.Width * 3 / 4;
                float x2 = PianoRenderer.P.X + PianoRenderer.BlackNoteToKey.GetValueOrDefault(note.NoteNumber, 0) * PianoRenderer.Width + PianoRenderer.Width * 5 / 4;

                if (CoreSettings.NeonFx)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float thickness = i * 2;
                        float alpha = 0.2f + (3 - i) * 0.2f;
                        uint color = ImGui.GetColorU32(new Vector4(col.X, col.Y, col.Z, alpha) * 0.5f * 0.7f);
                        drawList.AddRect(
                            new(x1 - 1, py1 - 1),
                            new(x2 + 1, py2 + 1),
                            color,
                            CoreSettings.NoteRoundness,
                            0,
                            thickness
                        );
                    }
                }
                else
                {
                    uint color = ImGui.GetColorU32(new Vector4(Vector3.Zero, 1f) * 0.5f);
                    drawList.AddRect(
                        new Vector2(x1 - 1, py1 - 1),
                        new Vector2(x2 + 1, py2 + 1),
                        color,
                        CoreSettings.NoteRoundness,
                        0,
                        1f
                    );
                }

                Vector4 sharpBase = ImGuiUtils.DarkenColor(col, 0.2f);
                GetNoteBodyGradient(sharpBase, true, out var sharpLeft, out var sharpRight);
                DrawHorizontalGradientNoteBody(drawList, new Vector2(x1, py1), new Vector2(x2, py2), sharpLeft, sharpRight, CoreSettings.NoteRoundness);
                DrawNoteLabel(
                    drawList,
                    new Vector2(x1, py1),
                    new Vector2(x2, py2),
                    note.NoteNumber);

                if (!_isPreRollActive)
                    RegisterHitLineNoteEffect(index, py1, py2, x1, x2, col);
            }
            else
            {
                float x1 = PianoRenderer.P.X + PianoRenderer.WhiteNoteToKey.GetValueOrDefault(note.NoteNumber, 0) * PianoRenderer.Width;
                float x2 = x1 + PianoRenderer.Width;

                if (CoreSettings.NeonFx)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float thickness = i * 2;
                        float alpha = 0.2f + (3 - i) * 0.2f;
                        uint color = ImGui.GetColorU32(new Vector4(col.X, col.Y, col.Z, alpha) * 0.5f);
                        drawList.AddRect(
                            new(x1 - 1, py1 - 1),
                            new(x2 + 1, py2 + 1),
                            color,
                            CoreSettings.NoteRoundness,
                            0,
                            thickness
                        );
                    }
                }
                else
                {
                    uint color = ImGui.GetColorU32(new Vector4(Vector3.Zero, 1f) * 0.5f);
                    drawList.AddRect(
                        new Vector2(x1 - 1, py1 - 1),
                        new Vector2(x2 + 1, py2 + 1),
                        color,
                        CoreSettings.NoteRoundness,
                        0,
                        1f
                    );
                }

                GetNoteBodyGradient(col, false, out var whiteLeftCol, out var whiteRightCol);
                DrawHorizontalGradientNoteBody(drawList, new Vector2(x1, py1), new Vector2(x2, py2), whiteLeftCol, whiteRightCol, CoreSettings.NoteRoundness);
                DrawNoteLabel(
                    drawList,
                    new Vector2(x1, py1),
                    new Vector2(x2, py2),
                    note.NoteNumber);

                if (!_isPreRollActive)
                    RegisterHitLineNoteEffect(index, py1, py2, x1, x2, col);
            }
            index++;
        }

        DrawChordGuideLines(drawList, chordGuideMarkers);
        DrawHitLineTransientEffects(drawList);

        if (IsLearningMode && _learningPausedByMissingNote && !MidiPlayer.IsTimerRunning && !missingNote)
        {
            if (!_isPreRollActive)
                MidiPlayer.StartTimer();
            _learningPausedByMissingNote = false;
        }
    }

    private static void GetPlaybackInputs()
    {
        if (!IsLearningMode && !_isHoveringTextBtn)
        {
            if (ImGui.GetIO().MouseWheel < 0)
            {
                float speed = (float)(MidiPlayer.Playback.Speed - 0.25f);
                float cValue = Math.Clamp(speed, 0.25f, 4);
                MidiPlayer.Playback.Speed = cValue;
            }
            else if (ImGui.GetIO().MouseWheel > 0)
            {
                float speed = (float)(MidiPlayer.Playback.Speed + 0.25f);
                float cValue = Math.Clamp(speed, 0.25f, 4);
                MidiPlayer.Playback.Speed = cValue;
            }
        }

        var panButton = IsEditMode ? ImGuiMouseButton.Middle : ImGuiMouseButton.Right;
        if (ImGui.IsMouseHoveringRect(Vector2.Zero, new(ImGui.GetIO().DisplaySize.X, PianoRenderer.P.Y)) && ImGui.IsMouseDown(panButton))
        {
            if (_isPreRollActive)
                CancelPreRoll();
            _hasResumeAnchor = false;

            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
            const float interpolationFactor = 0.05f;
            const float decelerationFactor = 0.75f;
            float mouseDeltaY = ImGui.GetIO().MouseDelta.Y;
            if (UpDirection) mouseDeltaY = -mouseDeltaY;
            _panVelocity = Lerp(_panVelocity, mouseDeltaY, interpolationFactor);
            _panVelocity *= decelerationFactor;
            float targetTime = Math.Clamp(MidiPlayer.Seconds + _panVelocity, 0, (float)MidiPlayer.Playback.GetDuration<MetricTimeSpan>().TotalSeconds);
            var newTime = Lerp(MidiPlayer.Seconds, targetTime, interpolationFactor);
            long ms = (long)(newTime * 1000000);
            MidiPlayer.Playback.MoveToTime(new MetricTimeSpan(ms));
            MidiPlayer.Seconds = newTime;
            MidiPlayer.Timer = MidiPlayer.Seconds * 100 * FallSpeedVal;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
        {
            if (MidiPlayer.IsTimerRunning || _isPreRollActive)
            {
                PausePlaybackAtCurrentPosition(true);
            }
            else
            {
                BeginPreRoll();
            }
        }

        if (ImGui.IsKeyPressed(ImGuiKey.R, false) && !CoreSettings.KeyboardInput && !IsLearningMode && !IsEditMode)
        {
            SetUpDirection(!UpDirection);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.T, false) && !CoreSettings.KeyboardInput)
        {
            SetTextNotes(!ShowTextNotes);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
        {
            if (_isPreRollActive)
                CancelPreRoll();
            _hasResumeAnchor = false;

            float n = ImGui.GetIO().KeyCtrl ? 0.1f : 1f;
            var newTime = Math.Clamp(MidiPlayer.Seconds + n, 0, (float)MidiFileData.MidiFile.GetDuration<MetricTimeSpan>().TotalSeconds);
            long ms = (long)(newTime * 1000000);
            MidiPlayer.Playback.MoveToTime(new MetricTimeSpan(ms));
            MidiPlayer.Timer = newTime * 100 * FallSpeedVal;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
        {
            if (_isPreRollActive)
                CancelPreRoll();
            _hasResumeAnchor = false;

            float n = ImGui.GetIO().KeyCtrl ? 0.1f : 1f;
            var newTime = Math.Clamp(MidiPlayer.Seconds - n, 0, (float)MidiFileData.MidiFile.GetDuration<MetricTimeSpan>().TotalSeconds);
            long ms = (long)(newTime * 1000000);
            MidiPlayer.Playback.MoveToTime(new MetricTimeSpan(ms));
            MidiPlayer.Timer = newTime * 100 * FallSpeedVal;
        }
    }

    private static void GetInputs()
    {
        if (CoreSettings.KeyboardInput)
        {
            VirtualKeyboard.ListenForKeyPresses();
        }

        if (ImGui.IsKeyPressed(ImGuiKey.G, false) && !CoreSettings.KeyboardInput)
        {
            CoreSettings.SetNeonFx(!CoreSettings.NeonFx);
        }

        if (!IsLearningMode)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.UpArrow, false))
            {
                switch (FallSpeed)
                {
                    case FallSpeeds.Slow:
                        SetFallSpeed(FallSpeeds.Default);
                        break;
                    case FallSpeeds.Default:
                        SetFallSpeed(FallSpeeds.Fast);
                        break;
                    case FallSpeeds.Fast:
                        SetFallSpeed(FallSpeeds.Faster);
                        break;
                }
            }

            if (ImGui.IsKeyPressed(ImGuiKey.DownArrow, false))
            {
                switch (FallSpeed)
                {
                    case FallSpeeds.Faster:
                        SetFallSpeed(FallSpeeds.Fast);
                        break;
                    case FallSpeeds.Fast:
                        SetFallSpeed(FallSpeeds.Default);
                        break;
                    case FallSpeeds.Default:
                        SetFallSpeed(FallSpeeds.Slow);
                        break;
                }
            }
        }
    }

    public static void RenderCanvas(bool playMode = false)
    {
        using (AutoFont font22 = new(FontController.GetFontOfSize(22)))
        {
            CanvasPos = ImGui.GetWindowPos();

            if (!playMode && _queueInitialPreRoll && MidiPlayer.Playback is not null)
            {
                _queueInitialPreRoll = false;
                MidiPlayer.Seconds = 0f;
                MidiPlayer.Timer = 0f;
                _learningPausedByMissingNote = false;
                BeginPreRoll();
            }

            RenderGrid();

            if (CoreSettings.FpsCounter)
            {
                var fps = $"{ImGui.GetIO().Framerate:0 FPS}";
                ImGui.GetWindowDrawList().AddText(new(ImGui.GetIO().DisplaySize.X - ImGui.CalcTextSize(fps).X - 5, ImGui.GetContentRegionAvail().Y - 25),
                    ImGui.GetColorU32(Vector4.One), fps);
            }

            if (playMode)
                DrawInputNotes();
            else
                DrawPlaybackNotes();

            DrawPreRollCountdown();
            GetInputs();

            var showTopBar = ImGui.IsMouseHoveringRect(Vector2.Zero, new(ImGui.GetIO().DisplaySize.X, 300));
            if (_comboFallSpeed || _comboPlaybackSpeed || _leftHandColorPicker || _rightHandColorPicker || _comboSoundFont || _comboPlugins)
                showTopBar = true;

            if (playMode)
            {
                if (showTopBar || LockTopBar)
                {
                    DrawPlayModeControls();
                    DrawPlayModeRightControls();
                }
            }

            if (!playMode)
            {
                GetPlaybackInputs();

                if (showTopBar || LockTopBar)
                {
                    DrawProgressBar();
                    DrawPlaybackControls();
                    DrawPlaybackRightControls();
                }
            }

            DrawSharedControls(showTopBar, playMode);
        }
    }

    private static void DrawProgressBar()
    {
        ImGui.SetNextItemWidth(ImGui.GetIO().DisplaySize.X);

        var pBarBg = new Vector3(ThemeManager.MainBgCol.X, ThemeManager.MainBgCol.Y, ThemeManager.MainBgCol.Z);
        var oldFrameBg = ImGuiTheme.Style.Colors[(int)ImGuiCol.FrameBg];
        var oldFrameBgHovered = ImGuiTheme.Style.Colors[(int)ImGuiCol.FrameBgHovered];
        var oldFrameBgActive = ImGuiTheme.Style.Colors[(int)ImGuiCol.FrameBgActive];
        var oldSliderGrab = ImGuiTheme.Style.Colors[(int)ImGuiCol.SliderGrab];
        var oldSliderGrabActive = ImGuiTheme.Style.Colors[(int)ImGuiCol.SliderGrabActive];

        ImGuiTheme.Style.Colors[(int)ImGuiCol.FrameBg] = new Vector4(pBarBg, 0.8f);
        ImGuiTheme.Style.Colors[(int)ImGuiCol.FrameBgHovered] = new Vector4(pBarBg, 0.8f);
        ImGuiTheme.Style.Colors[(int)ImGuiCol.FrameBgActive] = new Vector4(pBarBg, 0.8f);
        ImGuiTheme.Style.Colors[(int)ImGuiCol.SliderGrab] = ThemeManager.RightHandCol;
        ImGuiTheme.Style.Colors[(int)ImGuiCol.SliderGrabActive] = ThemeManager.RightHandCol;

        if (ImGui.SliderFloat("##Progress slider", ref MidiPlayer.Seconds, 0, (float)MidiFileData.MidiFile.GetDuration<MetricTimeSpan>().TotalSeconds, "%.1f",
            ImGuiSliderFlags.NoRoundToFormat | ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.NoInput))
        {
            if (_isPreRollActive)
                CancelPreRoll();
            _hasResumeAnchor = false;

            long ms = (long)(MidiPlayer.Seconds * 1000000);
            MidiPlayer.Playback.MoveToTime(new MetricTimeSpan(ms));
            MidiPlayer.Timer = MidiPlayer.Seconds * 100 * FallSpeedVal;
        }
        _isProgressBarActive = ImGui.IsItemActive();
        _isProgressBarHovered = ImGui.IsItemHovered();
        if (_isProgressBarActive && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        }

        var pBarHeight = ImGui.GetItemRectSize().Y;
        var playbackPercentage = MidiPlayer.Seconds * 100 / (float)MidiFileData.MidiFile.GetDuration<MetricTimeSpan>().TotalSeconds;
        var pBarWidth = ImGui.GetIO().DisplaySize.X * playbackPercentage / 100;
        var v3 = new Vector3(ThemeManager.RightHandCol.X, ThemeManager.RightHandCol.Y, ThemeManager.RightHandCol.Z);
        ImGui.GetWindowDrawList().AddRectFilled(Vector2.Zero, new Vector2(pBarWidth, pBarHeight), ImGui.GetColorU32(new Vector4(v3, 0.2f)));

        ImGuiTheme.Style.Colors[(int)ImGuiCol.FrameBg] = oldFrameBg;
        ImGuiTheme.Style.Colors[(int)ImGuiCol.FrameBgHovered] = oldFrameBgHovered;
        ImGuiTheme.Style.Colors[(int)ImGuiCol.FrameBgActive] = oldFrameBgActive;
        ImGuiTheme.Style.Colors[(int)ImGuiCol.SliderGrab] = oldSliderGrab;
        ImGuiTheme.Style.Colors[(int)ImGuiCol.SliderGrabActive] = oldSliderGrabActive;
    }

    private static void DrawPlaybackControls()
    {
        ImGui.SetNextWindowPos(new Vector2(ImGui.GetIO().DisplaySize.X / 2 - ImGuiUtils.FixedSize(new Vector2(110)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(50)).Y));
        if (ImGui.BeginChild("Player controls", ImGuiUtils.FixedSize(new Vector2(220, 50)), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var playColor = (!MidiPlayer.IsTimerRunning && !_isPreRollActive) ? Vector4.One : ThemeManager.RightHandCol;

            // PLAY BUTTON
            ImGui.PushFont(FontController.Font16_Icon16);
            ImGuiTheme.Style.Colors[(int)ImGuiCol.Text] = playColor;
            if (ImGui.Button($"{FontAwesome6.Play}", new(ImGuiUtils.FixedSize(new Vector2(50)).X, ImGui.GetWindowSize().Y)))
            {
                BeginPreRoll();
            }
            ImGuiTheme.Style.Colors[(int)ImGuiCol.Text] = Vector4.One;
            var pauseColor = (MidiPlayer.IsTimerRunning || _isPreRollActive) ? Vector4.One : new(0.70f, 0.22f, 0.22f, 1);
            ImGui.SameLine();
            // PAUSE BUTTON
            ImGuiTheme.Style.Colors[(int)ImGuiCol.Text] = pauseColor;
            if (ImGui.Button($"{FontAwesome6.Pause}", new(ImGuiUtils.FixedSize(new Vector2(50)).X, ImGui.GetWindowSize().Y)))
            {
                PausePlaybackAtCurrentPosition(true);
            }
            ImGuiTheme.Style.Colors[(int)ImGuiCol.Text] = Vector4.One;
            ImGui.SameLine();
            // STOP BUTTON
            if (ImGui.Button($"{FontAwesome6.Stop}", new(ImGuiUtils.FixedSize(new Vector2(50)).X, ImGui.GetWindowSize().Y)) || ImGui.IsKeyPressed(ImGuiKey.Backspace, false))
            {
                CancelPreRoll();
                _hasResumeAnchor = false;
                MidiPlayer.SoundFontEngine?.StopAllNote(0);
                MidiPlayer.Playback.Stop();
                MidiPlayer.Playback.MoveToStart();
                MidiPlayer.IsTimerRunning = false;
                MidiPlayer.Timer = 0;
            }
            ImGui.SameLine();
            // RECORD SCREEN BUTTON
            ImGui.PushStyleColor(ImGuiCol.Text, ScreenRecorder.Status == RecorderStatus.Recording ? new Vector4(0.08f, 0.80f, 0.27f, 1) : Vector4.One);
            if (ImGui.Button($"{FontAwesome6.Video}", new(ImGuiUtils.FixedSize(new Vector2(50)).X, ImGui.GetWindowSize().Y))
                || (ImGui.IsKeyDown(ImGuiKey.ModCtrl) && ImGui.IsKeyPressed(ImGuiKey.R)))
            {
                switch (ScreenRecorder.Status)
                {
                    case RecorderStatus.Idle:
                        ScreenRecorder.StartRecording();
                        if (CoreSettings.VideoRecStartsPlayback)
                        {
                            BeginPreRoll();
                        }
                        break;
                    case RecorderStatus.Recording:
                        CancelPreRoll();
                        _hasResumeAnchor = false;
                        ScreenRecorder.EndRecording();
                        MidiPlayer.SoundFontEngine?.StopAllNote(0);
                        MidiPlayer.Playback.Stop();
                        MidiPlayer.Playback.MoveToStart();
                        MidiPlayer.IsTimerRunning = false;
                        MidiPlayer.Timer = 0;
                        break;
                }
            }
            ImGui.PopStyleColor();

            ImGui.PopFont();
            ImGui.EndChild();
        }
    }

    private static void DrawPlaybackRightControls()
    {
        var directionIcon = UpDirection ? FontAwesome6.ArrowUp : FontAwesome6.ArrowDown;
        var icon = LockTopBar ? FontAwesome6.Lock : FontAwesome6.LockOpen;
        var showTextIcon = ShowTextNotes ? FontAwesome6.TextHeight : FontAwesome6.TextSlash;

        if (!IsLearningMode && !IsEditMode)
        {
            // NOTES DIRECTION BUTTON
            ImGui.PushFont(FontController.Font16_Icon16);
            ImGui.SetCursorScreenPos(new(ImGui.GetIO().DisplaySize.X - ImGuiUtils.FixedSize(new Vector2(220)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(50)).Y));
            if (ImGui.Button(directionIcon, ImGuiUtils.FixedSize(new Vector2(50))))
            {
                SetUpDirection(!UpDirection);
            }
            ImGui.PopFont();
        }

        // NOTES NOTATION BUTTON
        ImGui.PushFont(FontController.Font16_Icon16);
        ImGui.SetCursorScreenPos(new(ImGui.GetIO().DisplaySize.X - ImGuiUtils.FixedSize(new Vector2(160)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(50)).Y));
        if (ImGui.Button(showTextIcon, ImGuiUtils.FixedSize(new Vector2(50))))
        {
            SetTextNotes(!ShowTextNotes);
        }
        ImGui.PopFont();
        _isHoveringTextBtn = ImGui.IsItemHovered();
        if (_isHoveringTextBtn)
        {
            if (ImGui.GetIO().MouseWheel > 0)
            {
                switch (TextType)
                {
                    case TextTypes.Octave:
                        SetTextType(TextTypes.Velocity);
                        break;
                    case TextTypes.Velocity:
                        SetTextType(TextTypes.NoteName);
                        break;
                }
            }
            else if (ImGui.GetIO().MouseWheel < 0)
            {
                switch (TextType)
                {
                    case TextTypes.NoteName:
                        SetTextType(TextTypes.Velocity);
                        break;
                    case TextTypes.Velocity:
                        SetTextType(TextTypes.Octave);
                        break;
                }
            }

            ImGui.SetCursorScreenPos(new(ImGui.GetIO().DisplaySize.X - ImGuiUtils.FixedSize(new Vector2(160)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(250)).Y));
            ImGui.BeginGroup();
            foreach (var textType in Enum.GetValues<TextTypes>())
            {
                var selected = textType == TextType;
                ImGui.Selectable(textType.ToString(), selected);
            }
            ImGui.EndGroup();
        }

        // LOCK BUTTON
        ImGui.PushFont(FontController.Font16_Icon16);
        ImGui.SetCursorScreenPos(new(ImGui.GetIO().DisplaySize.X - ImGuiUtils.FixedSize(new Vector2(100)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(50)).Y));
        if (ImGui.Button(icon, ImGuiUtils.FixedSize(new Vector2(50))))
        {
            SetLockTopBar(!LockTopBar);
        }
        ImGui.PopFont();

        var fullScreenIcon = Program._window.WindowState == WindowState.BorderlessFullScreen ? FontAwesome6.Minimize : FontAwesome6.Expand;

        // FULLSCREEN BUTTON
        ImGui.PushFont(FontController.Font16_Icon16);
        ImGui.SetCursorScreenPos(new(ImGui.GetIO().DisplaySize.X - ImGuiUtils.FixedSize(new Vector2(40)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(50)).Y));
        if (ImGui.Button(fullScreenIcon, ImGuiUtils.FixedSize(new Vector2(25))))
        {
            var windowsState = Program._window.WindowState == WindowState.BorderlessFullScreen ? WindowState.Normal : WindowState.BorderlessFullScreen;
            Program._window.WindowState = windowsState;
        }
        ImGui.PopFont();

        if (!IsLearningMode)
        {
            // FALLSPEED DROPDOWN LIST
            ImGui.SetCursorScreenPos(new(ImGui.GetIO().DisplaySize.X - ImGuiUtils.FixedSize(new Vector2(220)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(110)).Y));
            if (ImGui.BeginCombo("##Fall speed", $"{FallSpeed}",
                ImGuiComboFlags.WidthFitPreview | ImGuiComboFlags.HeightLarge))
            {
                _comboFallSpeed = true;
                foreach (var speed in Enum.GetValues(typeof(FallSpeeds)))
                {
                    if (ImGui.Selectable(speed.ToString()))
                    {
                        SetFallSpeed((FallSpeeds)speed);
                    }
                }
                ImGui.EndCombo();
            }
            else
                _comboFallSpeed = false;

            // PLAYBACK SPEED DROPDOWN LIST
            ImGui.SetCursorScreenPos(new(ImGui.GetIO().DisplaySize.X - ImGuiUtils.FixedSize(new Vector2(220)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(155)).Y));
            if (ImGui.BeginCombo("##Playback speed", $"{MidiPlayer.Playback.Speed}x",
                ImGuiComboFlags.WidthFitPreview | ImGuiComboFlags.HeightLarge))
            {
                _comboPlaybackSpeed = true;
                for (float i = 0.25f; i <= 4; i += 0.25f)
                {
                    if (ImGui.Selectable($"{i}x"))
                    {
                        MidiPlayer.Playback.Speed = i;
                    }
                }
                ImGui.EndCombo();
            }
            else
                _comboPlaybackSpeed = false;

            DrawMasterVolumeControl(205, "Playback");
        }
        else
        {
            DrawMasterVolumeControl(110, "PlaybackLearning");
        }
    }

    private static void DrawSharedControls(bool showTopBar, bool playMode)
    {
        if (!showTopBar && !LockTopBar)
            return;

        // BACK BUTTON
        ImGui.PushFont(FontController.Font16_Icon16);
        ImGui.BeginDisabled(ScreenRecorder.Status == RecorderStatus.Recording);
        ImGui.SetCursorScreenPos(new(ImGuiUtils.FixedSize(new Vector2(25)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(50)).Y));
        if (ImGui.Button(FontAwesome6.ArrowLeftLong, ImGuiUtils.FixedSize(new Vector2(100, 50))) || ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            CancelPreRoll();
            _hasResumeAnchor = false;
            MidiPlayer.Playback?.Stop();
            MidiPlayer.Playback?.MoveToStart();
            MidiPlayer.IsTimerRunning = false;
            MidiPlayer.Timer = 0;
            SetLearningMode(false);
            var route = playMode ? Enums.Windows.Home : Enums.Windows.MidiBrowser;
            WindowsManager.SetWindow(route);
        }
        ImGui.EndDisabled();
        ImGui.PopFont();

        var neonIcon = CoreSettings.NeonFx ? FontAwesome6.Lightbulb : FontAwesome6.PowerOff;

        // GLOW BUTTON
        ImGui.PushFont(FontController.Font16_Icon16);
        ImGui.SetCursorScreenPos(new(ImGuiUtils.FixedSize(new Vector2(25)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(110)).Y));
        if (ImGui.Button(neonIcon, ImGuiUtils.FixedSize(new Vector2(35))))
        {
            CoreSettings.SetNeonFx(!CoreSettings.NeonFx);
        }
        ImGui.PopFont();

        // NOTE FADE COLOR PICKER (single source for notes/glow/particles/keypress)
        ImGui.SetCursorScreenPos(new(ImGuiUtils.FixedSize(new Vector2(85)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(110)).Y));
        Vector4 fadeColor = ThemeManager.NoteFadeCol;
        if (ImGui.ColorEdit4("Note Fade Color", ref fadeColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel
            | ImGuiColorEditFlags.NoDragDrop | ImGuiColorEditFlags.NoOptions | ImGuiColorEditFlags.NoAlpha))
        {
            ThemeManager.SetNoteFadeColor(fadeColor);
        }

        bool fadeColorPopupOpen = ImGui.IsPopupOpen("Note Fade Colorpicker");
        _leftHandColorPicker = fadeColorPopupOpen;
        _rightHandColorPicker = fadeColorPopupOpen;

        // Hand L/R toggles removed: single visual style now.

        if (CoreSettings.SoundEngine == SoundEngine.SoundFonts)
        {
            // SOUNDFONTS DROPDOWN LIST
            ImGui.SetCursorScreenPos(new(ImGuiUtils.FixedSize(new Vector2(140)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(50)).Y));
            if (ImGui.BeginCombo("##SoundFont", SoundFontPlayer.ActiveSoundFont, ImGuiComboFlags.HeightLargest | ImGuiComboFlags.WidthFitPreview))
            {
                _comboSoundFont = true;
                var seenSoundFontNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var folderPath in SoundFontsPathsManager.SoundFontsPaths)
                {
                    if (!Directory.Exists(folderPath))
                        continue;

                    foreach (var soundFontPath in Directory.EnumerateFiles(folderPath, "*.sf2", SearchOption.TopDirectoryOnly))
                    {
                        string soundFontName = Path.GetFileNameWithoutExtension(soundFontPath);
                        if (!seenSoundFontNames.Add(soundFontName))
                            continue;

                        bool isSelected = string.Equals(SoundFontPlayer.ActiveSoundFont, soundFontName, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable(soundFontName, isSelected))
                        {
                            MidiPlayer.SoundFontEngine?.StopAllNote(0);
                            SoundFontPlayer.LoadSoundFont(soundFontPath);
                        }

                        if (isSelected)
                            ImGui.SetItemDefaultFocus();
                    }
                }
                ImGui.EndCombo();
            }
            else
                _comboSoundFont = false;
        }
        else if (CoreSettings.SoundEngine == SoundEngine.Plugins)
        {
            var instrument = VstPlayer.PluginsChain?.PluginInstrument;
            var name = instrument == null ? "No Plugin Instrument" : instrument.PluginName;

            // PLUGINS DROPDOWN LIST
            ImGui.SetCursorScreenPos(new(ImGuiUtils.FixedSize(new Vector2(140)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(50)).Y));
            if (ImGui.BeginCombo("##Plugins", name, ImGuiComboFlags.HeightLargest | ImGuiComboFlags.WidthFitPreview))
            {
                _comboPlugins = true;

                ImGui.SeparatorText("Instrument");

                ImGui.Text(name);
                ImGui.SameLine();
                if (ImGui.SmallButton($"{FontAwesome6.ScrewdriverWrench}##tweak_instrument") && instrument is VstPlugin vstInstrument)
                {
                    vstInstrument.OpenPluginWindow();
                }
                ImGui.SameLine();
                if (ImGui.SmallButton($"{FontAwesome6.FolderOpen}##change_instrument"))
                {
                    var dialog = new OpenFileDialog()
                    {
                        Title = "Select a VST2 plugin instrument",
                        Filter = "vst plugin (*.dll)|*.dll"
                    };
                    dialog.ShowOpenFileDialog();

                    if (dialog.Success)
                    {
                        var file = new FileInfo(dialog.Files.First());
                        var plugin = new VstPlugin(file.FullName);
                        if (plugin.PluginType != PluginType.Instrument)
                        {
                            plugin.Dispose();
                            User32.MessageBox(IntPtr.Zero, "Plugin is not an instrument.", "Error Loading Plugin",
                                User32.MB_FLAGS.MB_ICONERROR | User32.MB_FLAGS.MB_TOPMOST);
                        }
                        else
                        {
                            VstPlayer.PluginsChain.AddPlugin(plugin);
                            PluginsPathManager.LoadValidInstrumentPath(file.FullName);
                        }
                    }
                }

                ImGui.Spacing();
                ImGui.SeparatorText("Effects");

                foreach (var effect in VstPlayer.PluginsChain.FxPlugins.ToList())
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text(effect.PluginName);
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"{FontAwesome6.ScrewdriverWrench}##tweak_effect{effect.PluginId}") && effect is VstPlugin vstEffect)
                    {
                        vstEffect.OpenPluginWindow();
                    }
                    bool enabled = effect.Enabled;
                    string state = enabled ? "ON" : "OFF";
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"{state}##{effect.PluginId}"))
                    {
                        effect.Enabled = !effect.Enabled;
                    }
                }

                ImGui.EndCombo();
            }
            else
                _comboPlugins = false;
        }

        // SUSTAIN PEDAL BUTTON
        // ImageButton padding according to https://github.com/ocornut/imgui/issues/6901#issuecomment-1749178625
        var imagePadding = ImGui.GetStyle().FramePadding * 2.0f;
        ImGui.SetCursorPos(ImGui.GetWindowSize() - ImGuiUtils.FixedSize(new Vector2(65) + imagePadding));
        if (ImGui.ImageButton("SustainBtn", IOHandle.SustainPedalActive ? Drawings.SustainPedalOn : Drawings.SustainPedalOff, 
                ImGuiUtils.FixedSize(new Vector2(50))))
        {
            IOHandle.OnEventReceived(null, new Melanchall.DryWetMidi.Multimedia.MidiEventReceivedEventArgs(
                new ControlChangeEvent(ControlUtilities.AsSevenBitNumber(ControlName.DamperPedal),
                new SevenBitNumber((byte)(IOHandle.SustainPedalActive ? 0 : 100)))));
            DevicesManager.ODevice?.SendEvent(new ControlChangeEvent(new SevenBitNumber(64), new SevenBitNumber((byte)(IOHandle.SustainPedalActive ? 0 : 100))));
        }
    }

    private static void DrawPlayModeControls()
    {
        ImGui.SetNextWindowPos(new Vector2(ImGui.GetIO().DisplaySize.X / 2 - ImGuiUtils.FixedSize(new Vector2(110)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(50)).Y));
        if (ImGui.BeginChild("Player controls", ImGuiUtils.FixedSize(new Vector2(220, 50)), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var recordColor = MidiRecording.IsRecording() ? new Vector4(1, 0, 0, 1) : Vector4.One;

            // RECORD BUTTON
            ImGui.PushFont(FontController.Font16_Icon16);
            ImGuiTheme.Style.Colors[(int)ImGuiCol.Text] = recordColor;
            if (ImGui.Button($"{FontAwesome6.CircleDot}", new(ImGuiUtils.FixedSize(new Vector2(50)).X, ImGui.GetWindowSize().Y)))
            {
                MidiRecording.StartRecording();
            }
            ImGuiTheme.Style.Colors[(int)ImGuiCol.Text] = Vector4.One;
            ImGui.SameLine();
            // STOP BUTTON
            ImGuiTheme.Style.Colors[(int)ImGuiCol.Text] = new(0.70f, 0.22f, 0.22f, 1);
            if (ImGui.Button($"{FontAwesome6.Stop}", new(ImGuiUtils.FixedSize(new Vector2(50)).X, ImGui.GetWindowSize().Y)))
            {
                MidiRecording.StopRecording();
            }
            ImGuiTheme.Style.Colors[(int)ImGuiCol.Text] = Vector4.One;
            ImGui.SameLine();
            // SAVE RECORDING BUTTON
            if (ImGui.Button($"{FontAwesome6.SdCard}", new(ImGuiUtils.FixedSize(new Vector2(50)).X, ImGui.GetWindowSize().Y)))
            {
                MidiRecording.SaveRecordingToFile();
            }
            ImGui.SameLine();
            // RECORD SCREEN BUTTON
            ImGui.PushStyleColor(ImGuiCol.Text, ScreenRecorder.Status == RecorderStatus.Recording ? new Vector4(0.08f, 0.80f, 0.27f, 1) : Vector4.One);
            if (ImGui.Button($"{FontAwesome6.Video}", new(ImGuiUtils.FixedSize(new Vector2(50)).X, ImGui.GetWindowSize().Y))
                || (ImGui.IsKeyDown(ImGuiKey.ModCtrl) && ImGui.IsKeyPressed(ImGuiKey.R)))
            {
                switch (ScreenRecorder.Status)
                {
                    case RecorderStatus.Idle:
                        MidiPlayer.ClearPlayback();
                        ScreenRecorder.StartRecording();
                        break;
                    case RecorderStatus.Recording:
                        ScreenRecorder.EndRecording();
                        break;
                }
            }
            ImGui.PopStyleColor();
            ImGui.PopFont();
            ImGui.EndChild();
        }      
    }

    private static void DrawPlayModeRightControls()
    {
        var icon = LockTopBar ? FontAwesome6.Lock : FontAwesome6.LockOpen;

        // LOCK BUTTON
        ImGui.PushFont(FontController.Font16_Icon16);
        ImGui.SetCursorScreenPos(new(ImGui.GetIO().DisplaySize.X - ImGuiUtils.FixedSize(new Vector2(280)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(50)).Y));
        if (ImGui.Button(icon, ImGuiUtils.FixedSize(new Vector2(50))))
        {
            SetLockTopBar(!LockTopBar);
        }
        ImGui.PopFont();

        if (!MidiRecording.IsRecording())
        {
            // VIEW LAST RECORDING BUTTON
            ImGui.SetCursorScreenPos(new(ImGui.GetIO().DisplaySize.X - ImGuiUtils.FixedSize(new Vector2(220)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(50)).Y));
            if (ImGui.Button("View last recording", ImGuiUtils.FixedSize(new Vector2(180, 50))))
            {
                var recordedMidi = MidiRecording.GetRecordedMidi();
                if (recordedMidi != null)
                {
                    MidiFileHandler.LoadMidiFile(recordedMidi);
                    LeftRightData.S_IsRightNote = Enumerable.Repeat(true, MidiFileData.Notes.Count()).ToList();
                    MidiEditing.AutoAssignHands();
                    MidiEditing.RebuildNoteIndexMap();
                    WindowsManager.SetWindow(Enums.Windows.MidiPlayback);
                }
            }

            // FALLSPEED DROPDOWN LIST
            ImGui.SetCursorScreenPos(new(ImGui.GetIO().DisplaySize.X - ImGuiUtils.FixedSize(new Vector2(220)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(110)).Y));
            if (ImGui.BeginCombo("##Fall speed", $"{FallSpeed}",
                ImGuiComboFlags.WidthFitPreview | ImGuiComboFlags.HeightLarge))
            {
                foreach (var speed in Enum.GetValues(typeof(FallSpeeds)))
                {
                    if (ImGui.Selectable(speed.ToString()))
                    {
                        SetFallSpeed((FallSpeeds)speed);
                    }
                }
                ImGui.EndCombo();
            }

            DrawMasterVolumeControl(155, "PlayMode");

            var fullScreenIcon = Program._window.WindowState == WindowState.BorderlessFullScreen ? FontAwesome6.Minimize : FontAwesome6.Expand;

            // FULLSCREEN BUTTON
            ImGui.PushFont(FontController.Font16_Icon16);
            ImGui.SetCursorScreenPos(new(ImGui.GetIO().DisplaySize.X - ImGuiUtils.FixedSize(new Vector2(30)).X, CanvasPos.Y + ImGuiUtils.FixedSize(new Vector2(50)).Y));
            if (ImGui.Button(fullScreenIcon, ImGuiUtils.FixedSize(new Vector2(25))))
            {
                var windowsState = Program._window.WindowState == WindowState.BorderlessFullScreen ? WindowState.Normal : WindowState.BorderlessFullScreen;
                Program._window.WindowState = windowsState;
            }
            ImGui.PopFont();
        }
    }
}
