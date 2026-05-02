// SPDX-License-Identifier: MIT
using System.Collections.Generic;

namespace MOBA.Server;

/// <summary>
/// Lightweight multi-room lifecycle manager. Owns a set of <see cref="RoomHost"/>s and
/// pumps them together in a single-threaded loop (same thread that calls
/// <see cref="Pump"/>). PRD §6.1 — single process, multiple rooms.
/// </summary>
public sealed class RoomManager
{
    private readonly Dictionary<uint, RoomHost> _rooms = new();

    public int TotalRooms  => _rooms.Count;
    public int ActiveRooms
    {
        get { int n = 0; foreach (var r in _rooms.Values) if (!r.MatchEnded) n++; return n; }
    }
    public IEnumerable<RoomHost> AllRooms => _rooms.Values;

    /// <summary>Returns the existing room or creates a new one on <paramref name="port"/>.</summary>
    public RoomHost GetOrCreate(uint roomId, ulong seed, ushort port)
    {
        if (_rooms.TryGetValue(roomId, out var existing)) return existing;
        var host = new RoomHost(roomId, seed, port);
        _rooms[roomId] = host;
        return host;
    }

    public bool TryGet(uint roomId, out RoomHost host) => _rooms.TryGetValue(roomId, out host);

    /// <summary>Pumps all managed rooms once. Call from a tight loop.</summary>
    public void Pump()
    {
        foreach (var host in _rooms.Values) host.Pump();
    }

    /// <summary>Stop all rooms (finalises replay files) and clear the registry.</summary>
    public void StopAll()
    {
        foreach (var host in _rooms.Values) host.Stop();
        _rooms.Clear();
    }
}
