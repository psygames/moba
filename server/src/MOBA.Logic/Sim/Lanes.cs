// SPDX-License-Identifier: MIT
namespace MOBA.Logic.Sim;

/// <summary>
/// Hard-coded lane geometry for the M4 reference map.
/// Map is 100×100 m, blue base at (-45,-45), red base at (+45,+45).
/// Each lane is 6 waypoints from blue base toward red base; red minions
/// walk it in reverse (waypoint index N-1 → 0).
/// </summary>
public static class Lanes
{
    public const int LaneCount = 3;
    public const int WaypointsPerLane = 6;

    /// <summary>[lane, waypoint] → position. Blue→Red direction.</summary>
    public static readonly TSVector2[,] Waypoints = BuildWaypoints();

    /// <summary>Tower positions for a single side (blue). Mirror for red.</summary>
    public static readonly TSVector2[] BlueTowers = new TSVector2[]
    {
        // Per lane: outer (frontmost), then base.
        new((Fix64)(-30), (Fix64)(-45)), // top outer
        new((Fix64)(-45), (Fix64)(-45)), // top base
        new((Fix64)(-15), (Fix64)(-15)), // mid outer
        new((Fix64)(-30), (Fix64)(-30)), // mid base
        new((Fix64)(-45), (Fix64)(-30)), // bot outer
        new((Fix64)(-45), (Fix64)(-45)), // bot base
    };
    public static readonly TSVector2[] RedTowers = new TSVector2[]
    {
        new((Fix64)( 30), (Fix64)( 45)),
        new((Fix64)( 45), (Fix64)( 45)),
        new((Fix64)( 15), (Fix64)( 15)),
        new((Fix64)( 30), (Fix64)( 30)),
        new((Fix64)( 45), (Fix64)( 30)),
        new((Fix64)( 45), (Fix64)( 45)),
    };

    public const int TowersPerSide = 6;

    private static TSVector2[,] BuildWaypoints()
    {
        var w = new TSVector2[LaneCount, WaypointsPerLane];
        // Top: along y=-45 then up x=+45.
        w[0, 0] = new((Fix64)(-45), (Fix64)(-45));
        w[0, 1] = new((Fix64)(-25), (Fix64)(-45));
        w[0, 2] = new((Fix64)(  0), (Fix64)(-45));
        w[0, 3] = new((Fix64)( 25), (Fix64)(-45));
        w[0, 4] = new((Fix64)( 45), (Fix64)(-25));
        w[0, 5] = new((Fix64)( 45), (Fix64)( 45));
        // Mid: diagonal.
        w[1, 0] = new((Fix64)(-45), (Fix64)(-45));
        w[1, 1] = new((Fix64)(-27), (Fix64)(-27));
        w[1, 2] = new((Fix64)( -9), (Fix64)( -9));
        w[1, 3] = new((Fix64)(  9), (Fix64)(  9));
        w[1, 4] = new((Fix64)( 27), (Fix64)( 27));
        w[1, 5] = new((Fix64)( 45), (Fix64)( 45));
        // Bot: down x=-45 then along y=+45.
        w[2, 0] = new((Fix64)(-45), (Fix64)(-45));
        w[2, 1] = new((Fix64)(-45), (Fix64)(-25));
        w[2, 2] = new((Fix64)(-45), (Fix64)( 25));
        w[2, 3] = new((Fix64)(-25), (Fix64)( 45));
        w[2, 4] = new((Fix64)(  0), (Fix64)( 45));
        w[2, 5] = new((Fix64)( 45), (Fix64)( 45));
        return w;
    }
}
