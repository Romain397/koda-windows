using System;
using System.Collections.Generic;
using System.Linq;
using Koda.Core;

namespace Koda.Playback;

/// <summary>One occurrence of a song in the queue. The same song can be queued twice, so
/// anything planned across an await (a skip, a removal, a jump) anchors on this id.</summary>
public sealed record QueueItem(Guid Id, Song Song, PlaylistSource? Source = null)
{
    public static QueueItem Of(Song song, PlaylistSource? source = null) => new(Guid.NewGuid(), song, source);
}

public sealed record PlaylistSource(string ProfileKey, PlaylistReference Playlist);

public enum RepeatMode
{
    Off,
    All,
    One,
}

/// <summary>
/// The play queue, ported from KodaCore PlayQueue.swift: what plays now, what came before
/// and what comes next. <see cref="Items"/> is always the order playback follows. Shuffle
/// reorders it and remembers the order it replaced; turning shuffle off puts that order
/// back with the current song still current.
/// </summary>
public sealed class PlayQueue
{
    private List<QueueItem> _items = [];
    /// <summary>The order before shuffle, by occurrence. Empty while not shuffled.</summary>
    private List<Guid> _unshuffled = [];
    private Saved? _savedItems;

    public IReadOnlyList<QueueItem> Items => _items;
    public int ItemsVersion { get; private set; }
    public int? CurrentIndex { get; private set; }
    public bool IsShuffled { get; private set; }
    public bool IsPlaylist { get; private set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    public QueueItem? Current => CurrentIndex is { } i ? _items[i] : null;
    public bool IsEmpty => _items.Count == 0;

    public int UpcomingStart => (CurrentIndex ?? -1) + 1;
    public int UpcomingCount => _items.Count - UpcomingStart;

    /// <summary>What plays after the current song, in order.</summary>
    public IReadOnlyList<QueueItem> Upcoming => _items.Skip(UpcomingStart).ToArray();

    // MARK: Replacing

    /// <summary>Start with the selected song, or pick one at random when shuffling without a selection.</summary>
    public void Replace(IReadOnlyList<Song> songs, int? start = null, bool shuffled = false, PlaylistSource? source = null)
    {
        _items = [.. songs.Select(song => QueueItem.Of(song, source))];
        _unshuffled = [];
        IsShuffled = false;
        IsPlaylist = source is not null;
        CurrentIndex = _items.Count == 0 ? null
            : Math.Clamp(start ?? (shuffled ? Random.Shared.Next(_items.Count) : 0), 0, _items.Count - 1);
        if (shuffled) SetShuffle(true);
        else ItemsChanged();
    }

    public void Clear()
    {
        _items = [];
        _unshuffled = [];
        IsShuffled = false;
        IsPlaylist = false;
        CurrentIndex = null;
        ItemsChanged();
    }

    // MARK: Adding and removing

    /// <summary>Insert right after the current song, in the order given.</summary>
    public void PlayNext(IReadOnlyList<Song> songs, PlaylistSource? source = null)
    {
        if (songs.Count == 0) return;
        if (CurrentIndex is not { } current)
        {
            Append(songs, source);
            return;
        }

        var added = songs.Select(song => QueueItem.Of(song, source)).ToList();
        _items.InsertRange(current + 1, added);
        if (IsShuffled && _unshuffled.IndexOf(_items[current].Id) is >= 0 and var anchor)
        {
            _unshuffled.InsertRange(anchor + 1, added.Select(a => a.Id));
        }
        ItemsChanged();
    }

    /// <summary>Add to the end of the queue.</summary>
    public void Append(IReadOnlyList<Song> songs, PlaylistSource? source = null)
    {
        if (songs.Count == 0) return;
        var added = songs.Select(song => QueueItem.Of(song, source)).ToList();
        _items.AddRange(added);
        if (IsShuffled) _unshuffled.AddRange(added.Select(a => a.Id));
        if (CurrentIndex is null && _items.Count > 0) CurrentIndex = 0;
        ItemsChanged();
    }

    /// <summary>Remove an occurrence. The current song cannot be removed this way; skip it instead.</summary>
    public bool Remove(Guid id)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0 || index == CurrentIndex) return false;
        _items.RemoveAt(index);
        _unshuffled.Remove(id);
        if (CurrentIndex is { } current && index < current) CurrentIndex = current - 1;
        ItemsChanged();
        return true;
    }

    public sealed record ClearedUpcoming(Guid Current, int Version, QueueItem[] Items, Guid[]? OriginalOrder);

    /// <summary>Drop everything after the current song and keep what is needed to undo it.</summary>
    public ClearedUpcoming? ClearUpcoming()
    {
        if (UpcomingCount == 0) return null;
        if (CurrentIndex is not { } current)
        {
            Clear();
            return null;
        }

        var cleared = _items.Skip(current + 1).ToArray();
        var originalOrder = IsShuffled ? _unshuffled.ToArray() : null;
        var dropped = cleared.Select(item => item.Id).ToHashSet();
        _items.RemoveRange(current + 1, _items.Count - current - 1);
        _unshuffled.RemoveAll(dropped.Contains);
        ItemsChanged();
        return new ClearedUpcoming(_items[current].Id, ItemsVersion, cleared, originalOrder);
    }

    public bool CanRestoreUpcoming(ClearedUpcoming cleared) =>
        Current?.Id == cleared.Current && ItemsVersion == cleared.Version;

    /// <summary>Restore exact queue occurrences, including their playlist source.</summary>
    public bool RestoreUpcoming(ClearedUpcoming cleared)
    {
        if (!CanRestoreUpcoming(cleared)) return false;
        _items.AddRange(cleared.Items);
        if (cleared.OriginalOrder is { } order) _unshuffled = [.. order];
        ItemsChanged();
        return true;
    }

    /// <summary>Move one upcoming song; offsets count from the first upcoming song, as a list of them does.</summary>
    public void MoveUpcoming(int from, int to)
    {
        var start = UpcomingStart;
        var a = start + from;
        var b = start + to;
        if (from == to || a < start || a >= _items.Count || b < start || b >= _items.Count) return;
        var item = _items[a];
        _items.RemoveAt(a);
        _items.Insert(b, item);
        ItemsChanged();
    }

    /// <summary>Keep the playlist available for another round and limit unrelated playback history.</summary>
    public void TrimHistory(int keeping)
    {
        if (CurrentIndex is not { } current || current <= keeping) return;
        var drop = current - keeping;
        var dropped = _items.Take(drop)
            .Where(item => !IsPlaylist || item.Source is null)
            .Select(item => item.Id).ToHashSet();
        if (dropped.Count == 0) return;
        _items.RemoveAll(item => dropped.Contains(item.Id));
        CurrentIndex = current - dropped.Count;
        if (IsShuffled) _unshuffled.RemoveAll(dropped.Contains);
        ItemsChanged();
    }

    // MARK: Moving through the queue

    public bool Jump(Guid id)
    {
        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0) return false;
        CurrentIndex = index;
        return true;
    }

    /// <summary>The index that would play next. A song ending on its own repeats under repeat
    /// one; a skip always moves on.</summary>
    public int? NextIndex(bool automatic)
    {
        if (CurrentIndex is not { } current) return _items.Count == 0 ? null : 0;
        if (automatic && Repeat == RepeatMode.One) return current;
        if (current + 1 < _items.Count) return current + 1;
        return Repeat == RepeatMode.Off ? null : 0;
    }

    public QueueItem? PeekNext(bool automatic) => NextIndex(automatic) is { } i ? _items[i] : null;

    /// <summary>Move to the next song; null at the end of a queue that does not repeat, the queue unchanged.</summary>
    public QueueItem? Advance(bool automatic)
    {
        if (NextIndex(automatic) is not { } next) return null;
        CurrentIndex = next;
        return _items[next];
    }

    /// <summary>Move to the previous song; at the start it wraps under repeat and otherwise stays.</summary>
    public QueueItem? Retreat()
    {
        if (CurrentIndex is not { } current) return null;
        if (current > 0) CurrentIndex = current - 1;
        else if (Repeat != RepeatMode.Off && _items.Count > 1) CurrentIndex = _items.Count - 1;
        return Current;
    }

    // MARK: Shuffle

    /// <summary>On: the current song stays current and every other song follows in random order.
    /// Off: the order from before shuffle comes back, songs added meanwhile kept where they were added.</summary>
    public void SetShuffle(bool on)
    {
        if (on == IsShuffled) return;
        if (on)
        {
            _unshuffled = [.. _items.Select(i => i.Id)];
            if (CurrentIndex is not { } current)
            {
                var all = _items.ToArray();
                Random.Shared.Shuffle(all);
                _items = [.. all];
            }
            else
            {
                var playing = _items[current];
                var rest = _items.Where((_, i) => i != current).ToArray();
                Random.Shared.Shuffle(rest);
                _items = [playing, .. rest];
                CurrentIndex = 0;
            }
        }
        else
        {
            var currentId = Current?.Id;
            var byId = _items.ToDictionary(i => i.Id);
            var restored = _unshuffled.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            // Anything the shuffled order gained that the old order never saw goes at the end.
            var known = _unshuffled.ToHashSet();
            restored.AddRange(_items.Where(i => !known.Contains(i.Id)));
            _items = restored;
            CurrentIndex = currentId is { } id && _items.FindIndex(i => i.Id == id) is >= 0 and var at ? at : (_items.Count == 0 ? null : 0);
            _unshuffled = [];
        }

        IsShuffled = on;
        ItemsChanged();
    }

    // MARK: Saving (persistQueue on the Mac)

    public sealed record Saved(Song[] Songs, int? Current, bool Shuffled, RepeatMode Repeat,
        PlaylistSource?[]? Sources = null, int[]? OriginalOrder = null, bool? IsPlaylist = null);

    public Saved Save() => (_savedItems ??= SnapshotItems()) with { Current = CurrentIndex, Repeat = Repeat };

    private void ItemsChanged()
    {
        ItemsVersion++;
        _savedItems = null;
    }

    private Saved SnapshotItems()
    {
        int[]? originalOrder = null;
        if (IsShuffled)
        {
            var indices = _items.Select((item, index) => (item.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
            originalOrder = [.. _unshuffled.Select(id => indices[id])];
        }

        return new Saved([.. _items.Select(i => i.Song)], CurrentIndex, IsShuffled, Repeat,
            _items.Any(item => item.Source is not null) ? [.. _items.Select(item => item.Source)] : null,
            originalOrder, IsPlaylist);
    }

    public static PlayQueue Restore(Saved saved)
    {
        var queue = new PlayQueue { Repeat = saved.Repeat };
        var sources = saved.Sources?.Length == saved.Songs.Length ? saved.Sources : null;
        queue._items = [.. saved.Songs.Select((song, index) => QueueItem.Of(song, sources?[index]))];
        queue.CurrentIndex = queue._items.Count == 0 ? null : Math.Clamp(saved.Current ?? 0, 0, queue._items.Count - 1);
        queue.IsPlaylist = saved.IsPlaylist ?? (queue._items.FirstOrDefault()?.Source is not null);
        queue.IsShuffled = saved.Shuffled;
        if (saved.Shuffled)
        {
            var order = saved.OriginalOrder;
            // Older saves keep only the playback order, which can serve as their original order.
            queue._unshuffled = order is not null && order.Length == queue._items.Count
                && order.All(index => index >= 0 && index < queue._items.Count) && order.Distinct().Count() == order.Length
                ? [.. order.Select(index => queue._items[index].Id)]
                : [.. queue._items.Select(item => item.Id)];
        }
        return queue;
    }
}
