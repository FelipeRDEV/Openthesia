namespace Openthesia.Ui.Helpers;

public class KeysUtils
{
    private static readonly string[] _noteNames =
    {
        "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"
    };

    public static bool HasBlack(int key)
    {
        return !((key - 1) % 7 == 0 || (key - 1) % 7 == 3) && key != 51;
    }

    public static string GetMidiNoteLabel(int midiNote, bool includeOctave = false)
    {
        int noteIndex = ((midiNote % 12) + 12) % 12;
        string noteName = _noteNames[noteIndex];

        if (!includeOctave)
            return noteName;

        int octave = midiNote / 12 - 1;
        return $"{noteName}{octave}";
    }
}
