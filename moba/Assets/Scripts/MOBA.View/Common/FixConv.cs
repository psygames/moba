using Box2DSharp.Common;
using UnityEngine;

namespace MOBA.View
{
    /// <summary>
    /// Helpers to bridge fixed-point logic types to Unity float / Vector3.
    /// XY plane is the world plane; we map Box2D X→Unity X, Box2D Y→Unity Z, leaving Y=0.
    /// </summary>
    public static class FixConv
    {
        public static float F(FP v) => (float)v;
        public static int   I(FP v) => (int)(long)v;

        public static Vector3 ToWorld(FVector2 v) => new Vector3((float)v.X, 0f, (float)v.Y);
        public static Vector2 ToVec2 (FVector2 v) => new Vector2((float)v.X, (float)v.Y);
    }
}
