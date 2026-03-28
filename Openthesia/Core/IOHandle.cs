using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Multimedia;
using Openthesia.Core.Midi;
using Openthesia.Core.Plugins;
using Openthesia.Settings;
using Openthesia.Ui;
using System.Numerics;

namespace Openthesia.Core;

public static class IOHandle
{
    public static List<int> PressedKeys { get; private set; } = new();
    private static Dictionary<int, Stack<bool>> _pressedKeyHands = new();
    private static Dictionary<int, Stack<float>> _pressedKeyVelocities = new();
    private static Dictionary<int, int> _activeNoteVoices = new();

    public static List<NoteRect> NoteRects = new();

    private static HashSet<int> _sustainedNotes = new(); // Keeps track of sustained notes

    private static bool _sustainPedalActive = false;
    public static bool SustainPedalActive => _sustainPedalActive;

    public struct NoteRect
    {
        public int KeyNum;
        public bool IsBlack;
        public float VelocityNorm;
        public float PY1;
        public float PY2;
        public float Time;
        public bool WasReleased;
        public float FinalTime;
    }

    public static Vector4 GetPressedKeyColor(int noteNumber)
    {
        const float minMidi = 21f;
        const float maxMidi = 108f;
        float t = Math.Clamp((noteNumber - minMidi) / (maxMidi - minMidi), 0f, 1f);

        Vector4 baseColor = ThemeManager.NoteFadeCol;
        Vector4 dark = new(baseColor.X * 0.48f, baseColor.Y * 0.48f, baseColor.Z * 0.48f, baseColor.W);
        Vector4 bright = Vector4.Lerp(baseColor, Vector4.One, 0.22f);
        Vector4 result = Vector4.Lerp(dark, bright, t);
        if (CoreSettings.UseVelocityAsNoteOpacity)
        {
            float velocityNorm = 1f;
            if (_pressedKeyVelocities.TryGetValue(noteNumber, out var velocityStack) && velocityStack.Count > 0)
                velocityNorm = Math.Clamp(velocityStack.Peek(), 0f, 1f);

            float shaped = MathF.Pow(velocityNorm, 1.25f);
            float brightness = 0.24f + 0.76f * shaped;
            result.X *= brightness;
            result.Y *= brightness;
            result.Z *= brightness;
            result.W = Math.Clamp(MathF.Pow(velocityNorm, 1.45f), 0.02f, 1f);
        }
        else
        {
            result.W = baseColor.W;
        }
        return result;
    }

    private static void OnKeyPressed(SevenBitNumber noteNumber, SevenBitNumber velocity, bool isBlack, bool? isRightHand)
    {
        // Check if sustain pedal is active
        if (_sustainPedalActive)
        {
            // add to sustained notes
            _sustainedNotes.Add(noteNumber);
        }

        if (WindowsManager.Window == Enums.Windows.PlayMode)
        {
            var note = new NoteRect()
            {
                KeyNum = noteNumber,
                IsBlack = isBlack,
                VelocityNorm = Math.Clamp((int)velocity / 127f, 0f, 1f),
                PY1 = PianoRenderer.P.Y,
                PY2 = PianoRenderer.P.Y,
                Time = 0f,
            };
            NoteRects.Add(note);
        }

        if (_activeNoteVoices.TryGetValue(noteNumber, out int activeVoices))
            _activeNoteVoices[noteNumber] = activeVoices + 1;
        else
            _activeNoteVoices[noteNumber] = 1;

        // Retrigger every NoteOn for proper articulation. StopNote is handled only when voice count reaches zero.
        MidiPlayer.SoundFontEngine?.PlayNote(0, noteNumber, velocity);
        if (!PressedKeys.Contains(noteNumber))
            PressedKeys.Add(noteNumber);

        if (!_pressedKeyHands.TryGetValue(noteNumber, out var handStack))
        {
            handStack = new Stack<bool>();
            _pressedKeyHands[noteNumber] = handStack;
        }

        handStack.Push(isRightHand ?? true);

        if (!_pressedKeyVelocities.TryGetValue(noteNumber, out var velocityStack))
        {
            velocityStack = new Stack<float>();
            _pressedKeyVelocities[noteNumber] = velocityStack;
        }
        velocityStack.Push(Math.Clamp((int)velocity / 127f, 0f, 1f));
    }

    private static int OnKeyReleased(SevenBitNumber noteNumber)
    {
        int remainingVoices = 0;
        if (_activeNoteVoices.TryGetValue(noteNumber, out int activeVoices))
        {
            activeVoices = Math.Max(activeVoices - 1, 0);
            if (activeVoices == 0)
                _activeNoteVoices.Remove(noteNumber);
            else
                _activeNoteVoices[noteNumber] = activeVoices;

            remainingVoices = activeVoices;
        }

        if (remainingVoices == 0)
        {
            if (_sustainPedalActive)
            {
                // If sustain pedal is active, keep note alive until pedal release.
                _sustainedNotes.Add(noteNumber);
            }
            else
            {
                MidiPlayer.SoundFontEngine?.StopNote(0, noteNumber);
            }
        }

        if (WindowsManager.Window == Enums.Windows.PlayMode)
        {
            int index = NoteRects.FindIndex(x => x.KeyNum == noteNumber && !x.WasReleased);
            if (index >= 0)
            {
                var n = NoteRects[index];
                n.WasReleased = true;
                n.FinalTime = n.Time;
                NoteRects[index] = n;
            }
        }

        if (remainingVoices == 0)
            PressedKeys.Remove(noteNumber);

        if (_pressedKeyHands.TryGetValue(noteNumber, out var handStack))
        {
            if (handStack.Count > 0)
                handStack.Pop();

            if (handStack.Count == 0)
                _pressedKeyHands.Remove(noteNumber);
        }

        if (_pressedKeyVelocities.TryGetValue(noteNumber, out var velocityStack))
        {
            if (velocityStack.Count > 0)
                velocityStack.Pop();

            if (velocityStack.Count == 0)
                _pressedKeyVelocities.Remove(noteNumber);
        }

        return remainingVoices;
    }

    private static void OnNoteOn(NoteOnEvent ev, bool? isRightHand = null)
    {
        SevenBitNumber velocity = ev.Velocity;
        if (CoreSettings.VelocityZeroIsNoteOff && velocity == 0)
        {
            int remainingVoices = OnKeyReleased(ev.NoteNumber);
            if (CoreSettings.SoundEngine == Enums.SoundEngine.Plugins && remainingVoices == 0)
            {
                VstPlayer.PluginsChain?.PluginInstrument?.ReceiveMidiEvent(new NoteOffEvent(ev.NoteNumber, (SevenBitNumber)0));
            }
        }
        else
        {
            bool isBlack = ev.GetNoteName().ToString().EndsWith("Sharp");
            OnKeyPressed(ev.NoteNumber, velocity, isBlack, isRightHand);
            if (CoreSettings.SoundEngine == Enums.SoundEngine.Plugins)
            {
                VstPlayer.PluginsChain?.PluginInstrument?.ReceiveMidiEvent(ev);
            }
        }
    }

    private static void OnNoteOff(NoteOffEvent ev)
    {
        int remainingVoices = OnKeyReleased(ev.NoteNumber);
        if (CoreSettings.SoundEngine == Enums.SoundEngine.Plugins && remainingVoices == 0)
        {
            VstPlayer.PluginsChain?.PluginInstrument?.ReceiveMidiEvent(ev);
        }
    }

    private static void OnSustainPedalOn()
    {
        _sustainPedalActive = true;
    }

    private static void OnSustainPedalOff()
    {
        _sustainPedalActive = false;

        // Stop sustained notes that are no longer actively held.
        foreach (var note in _sustainedNotes)
        {
            if (!_activeNoteVoices.TryGetValue(note, out int activeVoices) || activeVoices == 0)
            {
                MidiPlayer.SoundFontEngine?.StopNote(0, note);
            }
        }
        _sustainedNotes.Clear();
    }

    private static bool ResolveHandByCPosition(SevenBitNumber noteNumber)
    {
        // C Position convention: Middle C (C4=60) and above => right hand.
        return noteNumber >= 60;
    }

    private static bool ResolveHandFromMetadata(object metadata, SevenBitNumber noteNumber)
    {
        if (metadata is Note note)
        {
            string key = $"{note.NoteNumber}_{note.Time}";
            if (LeftRightData.S_NoteIndexMap.TryGetValue(key, out var indices) && indices.Count > 0)
            {
                int idx = indices[0];
                if (idx >= 0 && idx < LeftRightData.S_IsRightNote.Count)
                    return LeftRightData.S_IsRightNote[idx];
            }
        }

        return ResolveHandByCPosition(noteNumber);
    }

    public static void OnEventReceived(object sender, MidiEventReceivedEventArgs e)
    {
        var eType = e.Event.EventType;

        switch (eType)
        {
            case MidiEventType.NoteOn:
                OnNoteOn((NoteOnEvent)e.Event, null);
                break;
            case MidiEventType.NoteOff:
                OnNoteOff((NoteOffEvent)e.Event);
                break;
            case MidiEventType.ControlChange:
                var controlChangeEvent = (ControlChangeEvent)e.Event;
                if (CoreSettings.SoundEngine == Enums.SoundEngine.Plugins)
                {
                    VstPlayer.PluginsChain?.PluginInstrument?.ReceiveMidiEvent(controlChangeEvent);
                }
                if (controlChangeEvent.ControlNumber == 64) // 64 is the sustain pedal
                {
                    if (controlChangeEvent.ControlValue > 63)  // Sustain pedal ON (value greater than 63)
                    {
                        OnSustainPedalOn();
                    }
                    else  // Sustain pedal OFF (value <= 63)
                    {
                        OnSustainPedalOff();
                    }
                }
                break;
        }
    }

    public static void OnEventReceived(object sender, MidiEventPlayedEventArgs e)
    {
        // return in learning mode to prevent key presses
        if (ScreenCanvasControls.IsLearningMode)
            return;

        var eType = e.Event.EventType;

        switch (eType)
        {
            case MidiEventType.NoteOn:
                var noteOn = (NoteOnEvent)e.Event;
                bool isRightHand = ResolveHandFromMetadata(e.Metadata, noteOn.NoteNumber);
                OnNoteOn(noteOn, isRightHand);
                break;
            case MidiEventType.NoteOff:
                OnNoteOff((NoteOffEvent)e.Event);
                break;
            case MidiEventType.ControlChange:
                var controlChangeEvent = (ControlChangeEvent)e.Event;
                if (CoreSettings.SoundEngine == Enums.SoundEngine.Plugins)
                {
                    VstPlayer.PluginsChain?.PluginInstrument?.ReceiveMidiEvent(controlChangeEvent);
                }
                if (controlChangeEvent.ControlNumber == 64) // 64 is the sustain pedal
                {
                    if (controlChangeEvent.ControlValue > 63)  // Sustain pedal ON (value greater than 63)
                    {
                        OnSustainPedalOn();
                    }
                    else  // Sustain pedal OFF (value <= 63)
                    {
                        OnSustainPedalOff();
                    }
                }
                break;
        }
    }

    public static void OnEventSent(object sender, MidiEventSentEventArgs e)
    {
        var midiDevice = (MidiDevice)sender;
        //Console.WriteLine($"Event sent to '{midiDevice.Name}' at {DateTime.Now}: {e.Event}");
    }
}
