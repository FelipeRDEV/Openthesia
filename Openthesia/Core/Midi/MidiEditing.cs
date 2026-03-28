using System.Xml.Serialization;
using Vanara.PInvoke;

namespace Openthesia.Core.Midi;

public static class MidiEditing
{
    public static void SetRightHand(int noteIndex, bool isRightHand)
    {
        LeftRightData.S_IsRightNote[noteIndex] = isRightHand;
    }

    public static bool ReadData()
    {
        string filePath = Path.Combine(ProgramData.HandsDataPath, MidiFileData.FileName.Replace(".mid", string.Empty) + ".xml");
        if (!File.Exists(filePath))
            return false;

        try
        {
            using (FileStream fileStream = new(filePath, FileMode.Open))
            {
                XmlSerializer xmlSerializer = new(typeof(LeftRightData));
                LeftRightData leftRightData = (LeftRightData)xmlSerializer.Deserialize(fileStream);

                int notesCount = MidiFileData.Notes.Count();
                if (leftRightData.IsRightNote.Count != notesCount)
                {
                    return false;
                }

                LeftRightData.S_IsRightNote = leftRightData.IsRightNote;
                return true;
            }
        }
        catch (Exception ex)
        {
            User32.MessageBox(IntPtr.Zero, $"{ex.Message}", "Couldn't read hands data", User32.MB_FLAGS.MB_ICONERROR | User32.MB_FLAGS.MB_TOPMOST);
            return false;
        }
    }

    public static void SaveData()
    {
        string filePath = Path.Combine(ProgramData.HandsDataPath, MidiFileData.FileName.Replace(".mid", string.Empty) + ".xml");
        LeftRightData leftRightData = new()
        {
            IsRightNote = LeftRightData.S_IsRightNote
        };

        try
        {
            using (FileStream fileStream = new FileStream(filePath, FileMode.Create))
            {
                XmlSerializer xmlSerializer = new XmlSerializer(typeof(LeftRightData));
                xmlSerializer.Serialize(fileStream, leftRightData);
            }
        }
        catch (Exception ex)
        {
            User32.MessageBox(IntPtr.Zero, $"{ex.Message}", "Couldn't save hands data", User32.MB_FLAGS.MB_ICONERROR | User32.MB_FLAGS.MB_TOPMOST);
        }
    }

    public static void AutoAssignHands()
    {
        var indexedNotes = MidiFileData.Notes
            .Select((note, index) => (Note: note, Index: index))
            .OrderBy(x => x.Note.Time)
            .ThenBy(x => (int)x.Note.NoteNumber)
            .ToList();

        if (indexedNotes.Count == 0)
        {
            LeftRightData.S_IsRightNote.Clear();
            return;
        }

        if (LeftRightData.S_IsRightNote.Count != indexedNotes.Count)
        {
            LeftRightData.S_IsRightNote = Enumerable.Repeat(true, indexedNotes.Count).ToList();
        }

        int groupStart = 0;

        while (groupStart < indexedNotes.Count)
        {
            long groupTime = indexedNotes[groupStart].Note.Time;
            int groupEnd = groupStart;

            while (groupEnd < indexedNotes.Count && indexedNotes[groupEnd].Note.Time == groupTime)
            {
                groupEnd++;
            }

            var group = indexedNotes.GetRange(groupStart, groupEnd - groupStart);
            AssignGroupHandsByCPosition(group);

            groupStart = groupEnd;
        }
    }

    public static void RebuildNoteIndexMap()
    {
        LeftRightData.S_NoteIndexMap = new Dictionary<string, List<int>>();

        foreach (var (note, i) in MidiFileData.Notes.Select((note, i) => (note, i)))
        {
            var key = $"{note.NoteNumber}_{note.Time}";
            if (!LeftRightData.S_NoteIndexMap.TryGetValue(key, out var indexList))
            {
                indexList = new List<int>();
                LeftRightData.S_NoteIndexMap[key] = indexList;
            }

            indexList.Add(i);
        }
    }

    private static void AssignGroupHandsByCPosition(
        List<(Melanchall.DryWetMidi.Interaction.Note Note, int Index)> group)
    {
        // "C Position" convention:
        // Left hand around/below B3, right hand from Middle C (C4) and above.
        const int middleC = 60;
        foreach (var entry in group)
        {
            int midiNote = entry.Note.NoteNumber;
            bool isRight = midiNote >= middleC;
            LeftRightData.S_IsRightNote[entry.Index] = isRight;
        }
    }
}
