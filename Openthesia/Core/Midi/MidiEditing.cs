using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using System.Xml.Serialization;
using Vanara.PInvoke;

namespace Openthesia.Core.Midi;

public static class MidiEditing
{
    private const int LeftSoftMaxPitch = 60;   // C4
    private const int LeftHardMaxPitch = 63;   // D#4
    private const int RightSoftMinPitch = 60;  // C4
    private const int RightHardMinPitch = 55;  // G3

    private readonly struct IndexedNote
    {
        public IndexedNote(Note note, int index)
        {
            Note = note;
            Index = index;
        }

        public Note Note { get; }
        public int Index { get; }
    }

    private readonly struct PitchHistory
    {
        public PitchHistory(bool isRightHand, long time)
        {
            IsRightHand = isRightHand;
            Time = time;
        }

        public bool IsRightHand { get; }
        public long Time { get; }
    }

    private readonly struct ActiveNote
    {
        public ActiveNote(int pitch, long endTime)
        {
            Pitch = pitch;
            EndTime = endTime;
        }

        public int Pitch { get; }
        public long EndTime { get; }
    }

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
        var notes = MidiFileData.Notes?.ToList() ?? new List<Note>();

        var indexedNotes = notes
            .Select((note, index) => new IndexedNote(note, index))
            .OrderBy(x => x.Note.Time)
            .ThenBy(x => (int)x.Note.NoteNumber)
            .ThenBy(x => x.Index)
            .ToList();

        if (indexedNotes.Count == 0)
        {
            LeftRightData.S_IsRightNote.Clear();
            return;
        }

        if (LeftRightData.S_IsRightNote.Count != notes.Count)
        {
            LeftRightData.S_IsRightNote = Enumerable.Repeat(true, notes.Count).ToList();
        }

        var groupedNotes = GroupNotesByStartTime(indexedNotes);
        int ticksPerQuarter = GetTicksPerQuarter();
        long continuityWindowTicks = Math.Max(1, ticksPerQuarter * 2L);

        float leftCenter = 50f;
        float rightCenter = 62f;
        var lastPitchHistory = new Dictionary<int, PitchHistory>();
        var activeLeftNotes = new List<ActiveNote>();
        var activeRightNotes = new List<ActiveNote>();

        foreach (var group in groupedNotes)
        {
            long groupTime = group[0].Note.Time;
            PruneFinishedActiveNotes(activeLeftNotes, groupTime);
            PruneFinishedActiveNotes(activeRightNotes, groupTime);

            int bestSplit = SelectBestSplit(
                group,
                leftCenter,
                rightCenter,
                lastPitchHistory,
                continuityWindowTicks,
                activeLeftNotes,
                activeRightNotes);

            ApplySplit(group, bestSplit, lastPitchHistory, activeLeftNotes, activeRightNotes);
            UpdateHandCenters(group, bestSplit, ref leftCenter, ref rightCenter);
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

    private static List<List<IndexedNote>> GroupNotesByStartTime(List<IndexedNote> orderedNotes)
    {
        var groups = new List<List<IndexedNote>>();
        int i = 0;

        while (i < orderedNotes.Count)
        {
            long groupTime = orderedNotes[i].Note.Time;
            int j = i + 1;

            while (j < orderedNotes.Count && orderedNotes[j].Note.Time == groupTime)
                j++;

            groups.Add(orderedNotes.GetRange(i, j - i));
            i = j;
        }

        return groups;
    }

    private static int GetTicksPerQuarter()
    {
        if (MidiFileData.MidiFile?.TimeDivision is TicksPerQuarterNoteTimeDivision tpq)
            return Math.Max(1, (int)tpq.TicksPerQuarterNote);

        return 480;
    }

    private static int SelectBestSplit(
        List<IndexedNote> group,
        float leftCenter,
        float rightCenter,
        Dictionary<int, PitchHistory> lastPitchHistory,
        long continuityWindowTicks,
        List<ActiveNote> activeLeftNotes,
        List<ActiveNote> activeRightNotes)
    {
        var candidateSplits = new List<int>();
        int n = group.Count;
        for (int split = 0; split <= n; split++)
        {
            if (IsSplitReachable(group, split))
                candidateSplits.Add(split);
        }

        if (candidateSplits.Count == 0)
        {
            for (int split = 0; split <= n; split++)
                candidateSplits.Add(split);
        }

        float bestCost = float.MaxValue;
        int bestSplit = group.Count;
        foreach (int split in candidateSplits)
        {
            float cost = EvaluateSplitCost(
                group,
                split,
                leftCenter,
                rightCenter,
                lastPitchHistory,
                continuityWindowTicks,
                activeLeftNotes,
                activeRightNotes);
            if (cost < bestCost)
            {
                bestCost = cost;
                bestSplit = split;
            }
        }

        return bestSplit;
    }

    private static bool IsSplitReachable(List<IndexedNote> group, int split)
    {
        for (int i = 0; i < split; i++)
        {
            int pitch = group[i].Note.NoteNumber;
            if (pitch > LeftHardMaxPitch + 1)
                return false;
        }

        for (int i = split; i < group.Count; i++)
        {
            int pitch = group[i].Note.NoteNumber;
            if (pitch < RightHardMinPitch - 1)
                return false;
        }

        return true;
    }

    private static float EvaluateSplitCost(
        List<IndexedNote> group,
        int split,
        float leftCenter,
        float rightCenter,
        Dictionary<int, PitchHistory> lastPitchHistory,
        long continuityWindowTicks,
        List<ActiveNote> activeLeftNotes,
        List<ActiveNote> activeRightNotes)
    {
        const int maxFingersPerHand = 5;
        int n = group.Count;
        long groupTime = group[0].Note.Time;
        float cost = 0f;
        float groupSpan = group[n - 1].Note.NoteNumber - group[0].Note.NoteNumber;

        int leftTotalSimultaneous = activeLeftNotes.Count + split;
        int rightTotalSimultaneous = activeRightNotes.Count + (n - split);
        if (leftTotalSimultaneous > maxFingersPerHand)
            cost += 95f * (leftTotalSimultaneous - maxFingersPerHand);
        if (rightTotalSimultaneous > maxFingersPerHand)
            cost += 95f * (rightTotalSimultaneous - maxFingersPerHand);

        if (n > 1 && (split == 0 || split == n))
        {
            cost += 35f + groupSpan * 4f + (n >= 3 ? 25f : 0f);
            if (groupSpan >= 9f)
                cost += 80f;
        }

        if (split > 0 && split < n)
        {
            int leftTop = group[split - 1].Note.NoteNumber;
            int rightBottom = group[split].Note.NoteNumber;
            float separation = rightBottom - leftTop;
            if (separation < 1f)
                cost += (1f - separation) * 12f;
        }

        float leftSum = 0f;
        float rightSum = 0f;
        int leftCount = 0;
        int rightCount = 0;

        for (int i = 0; i < n; i++)
        {
            int pitch = group[i].Note.NoteNumber;
            bool isRight = i >= split;
            bool isLowest = i == 0;
            bool isHighest = i == n - 1;

            cost += EvaluateAssignmentCost(
                pitch,
                isRight,
                isLowest,
                isHighest,
                n,
                leftCenter,
                rightCenter,
                groupTime,
                lastPitchHistory,
                continuityWindowTicks);

            cost += EvaluateCrossingWithActiveHandsPenalty(
                pitch,
                isRight,
                activeLeftNotes,
                activeRightNotes);

            if (isRight)
            {
                rightSum += pitch;
                rightCount++;
            }
            else
            {
                leftSum += pitch;
                leftCount++;
            }
        }

        if (leftCount > 0 && rightCount > 0)
        {
            float leftMean = leftSum / leftCount;
            float rightMean = rightSum / rightCount;
            if (leftMean >= rightMean)
                cost += 25f;
        }

        cost += EvaluateHandSpanPenalty(activeLeftNotes, group, 0, split);
        cost += EvaluateHandSpanPenalty(activeRightNotes, group, split, n);

        return cost;
    }

    private static float EvaluateAssignmentCost(
        int pitch,
        bool isRight,
        bool isLowest,
        bool isHighest,
        int groupCount,
        float leftCenter,
        float rightCenter,
        long groupTime,
        Dictionary<int, PitchHistory> lastPitchHistory,
        long continuityWindowTicks)
    {
        float center = isRight ? rightCenter : leftCenter;
        float distance = MathF.Abs(pitch - center);
        float cost = distance * 0.34f + MathF.Max(0f, distance - 10f) * 0.9f;

        if (isRight)
        {
            if (pitch < RightSoftMinPitch)
            {
                float deficit = RightSoftMinPitch - pitch;
                cost += deficit * deficit * 1.9f + deficit * 4f;
            }

            if (pitch < RightHardMinPitch)
            {
                float deficit = RightHardMinPitch - pitch;
                cost += 500f + deficit * deficit * 45f;
            }

            if (pitch >= 60 && pitch <= 72)
                cost -= 1.4f;
        }
        else
        {
            if (pitch > LeftSoftMaxPitch)
            {
                float excess = pitch - LeftSoftMaxPitch;
                cost += excess * excess * 2.1f + excess * 4f;
            }

            if (pitch > LeftHardMaxPitch)
            {
                float excess = pitch - LeftHardMaxPitch;
                cost += 500f + excess * excess * 45f;
            }

            if (pitch >= 40 && pitch <= 59)
                cost -= 1.4f;
        }

        if (groupCount > 1)
        {
            if (isLowest && isRight)
                cost += 3.2f;
            if (isHighest && !isRight)
                cost += 3.2f;
        }

        if (lastPitchHistory.TryGetValue(pitch, out PitchHistory history) && history.IsRightHand != isRight)
        {
            long dt = Math.Abs(groupTime - history.Time);
            float proximity = dt <= continuityWindowTicks
                ? 1f
                : continuityWindowTicks / (float)(dt + 1);

            proximity = Math.Clamp(proximity, 0.15f, 1f);
            cost += 26f * proximity;
        }

        return cost;
    }

    private static void ApplySplit(
        List<IndexedNote> group,
        int split,
        Dictionary<int, PitchHistory> lastPitchHistory,
        List<ActiveNote> activeLeftNotes,
        List<ActiveNote> activeRightNotes)
    {
        long groupTime = group[0].Note.Time;
        for (int i = 0; i < group.Count; i++)
        {
            bool isRight = i >= split;
            var entry = group[i];
            LeftRightData.S_IsRightNote[entry.Index] = isRight;
            lastPitchHistory[entry.Note.NoteNumber] = new PitchHistory(isRight, groupTime);

            long endTime = GetNoteEndTime(entry.Note);
            if (isRight)
                activeRightNotes.Add(new ActiveNote(entry.Note.NoteNumber, endTime));
            else
                activeLeftNotes.Add(new ActiveNote(entry.Note.NoteNumber, endTime));
        }
    }

    private static void UpdateHandCenters(List<IndexedNote> group, int split, ref float leftCenter, ref float rightCenter)
    {
        if (split > 0)
        {
            float leftAvg = group.Take(split).Average(x => (float)x.Note.NoteNumber);
            leftCenter = Lerp(leftCenter, leftAvg, 0.24f);
        }
        else
        {
            leftCenter = Lerp(leftCenter, 50f, 0.09f);
        }

        if (split < group.Count)
        {
            float rightAvg = group.Skip(split).Average(x => (float)x.Note.NoteNumber);
            rightCenter = Lerp(rightCenter, rightAvg, 0.24f);
        }
        else
        {
            rightCenter = Lerp(rightCenter, 64f, 0.09f);
        }

        leftCenter = Math.Clamp(leftCenter, 36f, 58f);
        rightCenter = Math.Clamp(rightCenter, 60f, 88f);

        if (leftCenter > rightCenter - 5f)
        {
            float mid = (leftCenter + rightCenter) * 0.5f;
            leftCenter = mid - 2.5f;
            rightCenter = mid + 2.5f;
        }
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * Math.Clamp(t, 0f, 1f);
    }

    private static float EvaluateCrossingWithActiveHandsPenalty(
        int pitch,
        bool isRight,
        List<ActiveNote> activeLeftNotes,
        List<ActiveNote> activeRightNotes)
    {
        float cost = 0f;

        if (isRight && activeLeftNotes.Count > 0)
        {
            int leftTop = activeLeftNotes.Max(n => n.Pitch);
            if (pitch <= leftTop + 1)
            {
                float weight = (pitch >= 59 && pitch <= 62) ? 6f : 16f;
                cost += (leftTop + 2 - pitch) * weight;
            }
        }
        else if (!isRight && activeRightNotes.Count > 0)
        {
            int rightBottom = activeRightNotes.Min(n => n.Pitch);
            if (pitch >= rightBottom - 1)
            {
                float weight = (pitch >= 59 && pitch <= 62) ? 6f : 16f;
                cost += (pitch - (rightBottom - 2)) * weight;
            }
        }

        return cost;
    }

    private static void PruneFinishedActiveNotes(List<ActiveNote> activeNotes, long time)
    {
        for (int i = activeNotes.Count - 1; i >= 0; i--)
        {
            if (activeNotes[i].EndTime <= time)
                activeNotes.RemoveAt(i);
        }
    }

    private static float EvaluateHandSpanPenalty(List<ActiveNote> activeNotes, List<IndexedNote> group, int start, int end)
    {
        if (start >= end && activeNotes.Count == 0)
            return 0f;

        int minPitch = int.MaxValue;
        int maxPitch = int.MinValue;

        foreach (var active in activeNotes)
        {
            if (active.Pitch < minPitch) minPitch = active.Pitch;
            if (active.Pitch > maxPitch) maxPitch = active.Pitch;
        }

        for (int i = start; i < end; i++)
        {
            int pitch = group[i].Note.NoteNumber;
            if (pitch < minPitch) minPitch = pitch;
            if (pitch > maxPitch) maxPitch = pitch;
        }

        if (minPitch == int.MaxValue || maxPitch == int.MinValue)
            return 0f;

        int span = maxPitch - minPitch;
        if (span <= 12)
            return 0f;

        float excess = span - 12f;
        return excess * excess * 0.7f + excess * 1.4f;
    }

    private static long GetNoteEndTime(Note note)
    {
        long length = Math.Max(1L, note.Length);
        return note.Time + length;
    }
}
