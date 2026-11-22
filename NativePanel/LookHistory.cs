using System;
using System.Collections.Generic;

// Session undo history of Spektrafilm looks, one list per variant (2026-11-19). Capture One's own Undo does not
// cover the panel, because looks are saved by the panel next to C1's settings. Snapshots are serialized looks,
// so entries are immutable and equal looks compare as equal strings.
//
// A run of changes closer together than the coalescing window (a slider drag, typing a value) becomes one step.
// Discrete actions (Defaults, presets, versions, undo itself) are recorded as their own step and seal it, so the
// next change starts a new step. The history lives only while the document's panel is open.
internal sealed class LookHistory
{
    internal static readonly TimeSpan DefaultCoalescing = TimeSpan.FromMilliseconds(800);
    internal const int DefaultLimit = 200;

    private readonly Dictionary<Guid, Track> tracks = new();
    private readonly TimeSpan coalescing;
    private readonly int limit;

    internal LookHistory() : this(DefaultCoalescing, DefaultLimit) { }

    internal LookHistory(TimeSpan coalescing, int limit)
    {
        if (coalescing < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(coalescing));
        if (limit < 2) throw new ArgumentOutOfRangeException(nameof(limit), "The history needs room for a step back.");
        this.coalescing = coalescing;
        this.limit = limit;
    }

    /// <summary>
    /// Records the variant's current look. Returns true when it became a new step or replaced the step in progress.
    /// </summary>
    internal bool Record(Guid variant, string snapshot, DateTime now, bool discrete = false)
    {
        if (variant == Guid.Empty) throw new ArgumentException("A history entry needs a variant.", nameof(variant));
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!tracks.TryGetValue(variant, out var track))
        {
            tracks[variant] = new Track(snapshot, now);
            return true;
        }
        if (track.Current == snapshot)
        {
            if (discrete) track.Sealed = true;
            return false;
        }
        var gap = now - track.LastChange;
        var continuing = !discrete && !track.Sealed && track.Index == track.Entries.Count - 1
            && gap >= TimeSpan.Zero && gap < coalescing;
        if (continuing)
            track.Entries[track.Index] = snapshot;
        else
        {
            track.Entries.RemoveRange(track.Index + 1, track.Entries.Count - track.Index - 1);
            track.Entries.Add(snapshot);
            if (track.Entries.Count > limit)
                track.Entries.RemoveRange(0, track.Entries.Count - limit);
            track.Index = track.Entries.Count - 1;
        }
        track.LastChange = now;
        track.Sealed = discrete;
        return true;
    }

    /// <summary>
    /// Replaces the current step after the panel re-applied it (normalised values), without touching redo.
    /// </summary>
    internal void Settle(Guid variant, string snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (tracks.TryGetValue(variant, out var track))
        {
            track.Entries[track.Index] = snapshot;
            track.Sealed = true;
        }
    }

    internal bool CanUndo(Guid variant) => tracks.TryGetValue(variant, out var track) && track.Index > 0;
    internal bool CanRedo(Guid variant) => tracks.TryGetValue(variant, out var track) && track.Index < track.Entries.Count - 1;

    internal string? Undo(Guid variant) => Move(variant, -1, commit: true);
    internal string? Redo(Guid variant) => Move(variant, +1, commit: true);

    /// <summary>The look one step back or forward, without moving. Commit the step once it has been applied.</summary>
    internal string? Peek(Guid variant, bool redo) => Move(variant, redo ? +1 : -1, commit: false);
    internal void Commit(Guid variant, bool redo) => Move(variant, redo ? +1 : -1, commit: true);

    private string? Move(Guid variant, int step, bool commit)
    {
        if (!tracks.TryGetValue(variant, out var track)) return null;
        var target = track.Index + step;
        if (target < 0 || target >= track.Entries.Count) return null;
        if (commit)
        {
            track.Index = target;
            track.Sealed = true;
        }
        return track.Entries[target];
    }

    internal string? Current(Guid variant) => tracks.TryGetValue(variant, out var track) ? track.Current : null;

    private sealed class Track
    {
        internal Track(string first, DateTime now)
        {
            Entries.Add(first);
            LastChange = now;
            Sealed = true; // The first entry is the look as found; the first edit must not overwrite it.
        }

        internal List<string> Entries { get; } = new();
        internal int Index { get; set; }
        internal DateTime LastChange { get; set; }
        internal bool Sealed { get; set; }
        internal string Current => Entries[Index];
    }
}
