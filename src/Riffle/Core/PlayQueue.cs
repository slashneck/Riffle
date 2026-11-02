namespace Riffle.Core;

public sealed class QueueState
{
    public List<long> Order { get; set; } = [];
    public int Position { get; set; }
    public int Round { get; set; } = 1;
    public List<long> Pinned { get; set; } = [];
}

// One round plays every song exactly once. Only then is a new round shuffled.
public sealed class PlayQueue
{
    const int RoundSeam = 12;

    readonly Func<IReadOnlyCollection<long>, List<long>> shuffle;
    List<long> order = [];
    readonly List<long> pinned = [];

    public PlayQueue(Func<IReadOnlyCollection<long>, List<long>> shuffle) => this.shuffle = shuffle;

    public int Position { get; private set; }
    public int Round { get; private set; } = 1;
    public IReadOnlyList<long> Order => order;
    public IReadOnlyList<long> Pinned => pinned;
    public int Count => order.Count;
    public long? Current => Position >= 0 && Position < order.Count ? order[Position] : null;
    public int Played => Math.Min(Position, order.Count);

    public IEnumerable<long> Upcoming => pinned.Concat(order.Skip(Position + 1).Where(id => !pinned.Contains(id)));
    public IEnumerable<long> History => order.Take(Math.Max(0, Position)).Reverse();

    public void Start(IReadOnlyCollection<long> pool)
    {
        order = shuffle(pool);
        pinned.Clear();
        Position = 0;
        Round = 1;
    }

    public long? PeekNext()
    {
        if (pinned.Count > 0)
            return pinned[0];
        if (order.Count == 0)
            return null;
        return Position + 1 < order.Count ? order[Position + 1] : null;
    }

    public long? Advance()
    {
        if (order.Count == 0)
            return null;

        if (pinned.Count > 0)
        {
            var next = pinned[0];
            pinned.RemoveAt(0);
            // the pinned song plays now and counts as played for this round
            int from = order.IndexOf(next);
            if (from >= 0)
            {
                order.RemoveAt(from);
                if (from <= Position)
                    Position--;
            }
            Position = Math.Min(Position + 1, order.Count);
            order.Insert(Position, next);
            return next;
        }

        if (Position + 1 < order.Count)
            return order[++Position];

        NewRound();
        return Current;
    }

    public long? Back()
    {
        if (Position <= 0)
            return Current;
        return order[--Position];
    }

    public void Jump(long id)
    {
        int index = order.IndexOf(id);
        if (index < 0)
            return;
        pinned.Remove(id);
        // songs that get jumped over stay in this round: only the picked one moves
        order.RemoveAt(index);
        int target = Math.Min(index < Position ? Position : Position + 1, order.Count);
        order.Insert(target, id);
        Position = target;
    }

    public void PlayNext(long id)
    {
        if (!order.Contains(id) || id == Current)
            return;
        pinned.Remove(id);
        pinned.Insert(0, id);
    }

    public void Remove(long id)
    {
        pinned.Remove(id);
        int index = order.IndexOf(id);
        if (index < 0)
            return;
        order.RemoveAt(index);
        if (index < Position)
            Position--;
        if (Position >= order.Count && order.Count > 0)
            NewRound();
    }

    // Reshuffles what hasn't played yet, without touching what already has.
    public void ReshuffleUpcoming()
    {
        if (Position + 1 >= order.Count)
            return;
        var rest = shuffle(order.Skip(Position + 1).ToList());
        order = order.Take(Position + 1).Concat(rest).ToList();
    }

    // New songs land at a random spot in what's left of this round. Removed ones just go.
    public void Merge(IReadOnlyCollection<long> pool)
    {
        var wanted = new HashSet<long>(pool);
        var current = Current;
        var kept = order.Where((id, i) => wanted.Contains(id) || i == Position).ToList();
        Position = current is { } c ? kept.IndexOf(c) : 0;
        order = kept;
        pinned.RemoveAll(id => !wanted.Contains(id));

        var known = new HashSet<long>(order);
        foreach (var id in pool.Where(id => !known.Contains(id)))
        {
            int spot = Position + 1 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(order.Count - Position);
            order.Insert(spot, id);
        }
    }

    void NewRound()
    {
        var last = order.TakeLast(Math.Min(RoundSeam, order.Count / 3)).ToHashSet();
        var next = shuffle(order);

        // only at the seam between rounds: don't let a song that just played come right back
        for (int i = 0; i < Math.Min(last.Count, next.Count); i++)
        {
            if (!last.Contains(next[i]))
                continue;
            for (int k = last.Count; k < next.Count; k++)
            {
                if (last.Contains(next[k]))
                    continue;
                (next[i], next[k]) = (next[k], next[i]);
                break;
            }
        }

        order = next;
        Position = 0;
        Round++;
    }

    public QueueState Save() => new() { Order = order.ToList(), Position = Position, Round = Round, Pinned = pinned.ToList() };

    public void Restore(QueueState state)
    {
        order = state.Order.ToList();
        Position = Math.Clamp(state.Position, 0, Math.Max(0, order.Count - 1));
        Round = Math.Max(1, state.Round);
        pinned.Clear();
        pinned.AddRange(state.Pinned);
    }
}
