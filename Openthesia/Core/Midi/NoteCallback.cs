using Melanchall.DryWetMidi.Multimedia;

namespace Openthesia.Core.Midi;

public static class NoteCallback
{
    public static NotePlaybackData HandMutingNoteCallback(NotePlaybackData rawNoteData, long rawTime, long rawLength, TimeSpan playbackTime)
    {
        // Hand L/R muting is disabled (single-color mode keeps all notes active).
        return rawNoteData;
    }

}
