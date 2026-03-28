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

        List<int> notesSorted = indexedNotes
            .Select(x => (int)x.Note.NoteNumber)
            .OrderBy(n => n)
            .ToList();

        float medianPitch = notesSorted[notesSorted.Count / 2];
        float splitPitch = Math.Clamp(medianPitch, 55f, 67f);

        int? lastLeft = null;
        int? lastRight = null;
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
            AssignGroupHands(group, splitPitch, ref lastLeft, ref lastRight);

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

    private static void AssignGroupHands(
        List<(Melanchall.DryWetMidi.Interaction.Note Note, int Index)> group,
        float splitPitch,
        ref int? lastLeft,
        ref int? lastRight)
    {
        if (group.Count == 1)
        {
            AssignSingle(group[0], splitPitch, ref lastLeft, ref lastRight);
            return;
        }

        int middle = group.Count / 2;
        for (int i = 0; i < group.Count; i++)
        {
            int midiNote = group[i].Note.NoteNumber;
            bool isRight;

            if (group.Count % 2 == 0)
            {
                isRight = i >= middle;
            }
            else if (i < middle)
            {
                isRight = false;
            }
            else if (i > middle)
            {
                isRight = true;
            }
            else
            {
                isRight = ChooseClosestHand(midiNote, splitPitch, lastLeft, lastRight);
            }

            LeftRightData.S_IsRightNote[group[i].Index] = isRight;
            if (isRight)
                lastRight = midiNote;
            else
                lastLeft = midiNote;
        }
    }

    private static void AssignSingle(
        (Melanchall.DryWetMidi.Interaction.Note Note, int Index) entry,
        float splitPitch,
        ref int? lastLeft,
        ref int? lastRight)
    {
        int midiNote = entry.Note.NoteNumber;
        bool isRight = ChooseClosestHand(midiNote, splitPitch, lastLeft, lastRight);
        LeftRightData.S_IsRightNote[entry.Index] = isRight;

        if (isRight)
            lastRight = midiNote;
        else
            lastLeft = midiNote;
    }

    private static bool ChooseClosestHand(int midiNote, float splitPitch, int? lastLeft, int? lastRight)
    {
        float leftAnchor = lastLeft ?? (splitPitch - 7f);
        float rightAnchor = lastRight ?? (splitPitch + 7f);

        float leftScore = MathF.Abs(midiNote - leftAnchor);
        float rightScore = MathF.Abs(midiNote - rightAnchor);

        if (midiNote > splitPitch)
        {
            leftScore += (midiNote - splitPitch) * 1.5f;
        }
        else if (midiNote < splitPitch)
        {
            rightScore += (splitPitch - midiNote) * 1.5f;
        }

        return rightScore <= leftScore;
    }
}
