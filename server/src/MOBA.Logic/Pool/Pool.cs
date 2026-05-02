// SPDX-License-Identifier: MIT
// Generic stack-backed object pool. All allocation happens during Prewarm
// (before the simulation starts); Get / Return are O(1) and zero-alloc.
//
// Thread-safety: NOT thread-safe. The MOBA logic runs single-threaded per
// room so this is fine; explicit choice over locking to keep Get/Return at
// a few cycles.

using System;

namespace MOBA.Logic;

public sealed class Pool<T> where T : class, IPoolable, new()
{
    private T[] _items;
    private int _count;
    private readonly int _maxRetain;

    /// <summary>Number of currently free objects in the pool.</summary>
    public int Count => _count;
    /// <summary>Hard cap on retained instances. Returns above this are dropped.</summary>
    public int MaxRetain => _maxRetain;

    public Pool(int initialCapacity, int maxRetain)
    {
        if (initialCapacity < 0) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        if (maxRetain < initialCapacity) throw new ArgumentOutOfRangeException(nameof(maxRetain));
        _items = new T[Math.Max(initialCapacity, 4)];
        _maxRetain = maxRetain;
    }

    /// <summary>Allocate up to <paramref name="count"/> instances and stash them. Run once at room init.</summary>
    public void Prewarm(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        EnsureCapacity(_count + count);
        for (int i = 0; i < count; i++) _items[_count++] = new T();
    }

    [NoGC]
    public T Get()
    {
        if (_count == 0) return AllocOverflow();   // escape hatch — production rooms must size up front
        int idx = --_count;
        var item = _items[idx];
        _items[idx] = null;
        return item;
    }

    /// <summary>Out-of-pool allocation. Marked separately so the [NoGC] hot path stays clean for the analyzer.</summary>
    private static T AllocOverflow() => new T();

    [NoGC]
    public void Return(T item)
    {
        if (item == null) return;
        item.Reset();
        if (_count >= _maxRetain) return;          // drop on the floor; let GC reclaim
        if (_count >= _items.Length) GrowTo(_items.Length * 2);
        _items[_count++] = item;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _items.Length) return;
        int cap = _items.Length;
        while (cap < needed) cap *= 2;
        GrowTo(cap);
    }

    private void GrowTo(int cap)
    {
        var bigger = new T[cap];
        Array.Copy(_items, bigger, _count);
        _items = bigger;
    }
}
