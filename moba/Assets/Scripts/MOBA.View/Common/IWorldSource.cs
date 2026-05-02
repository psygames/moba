using MOBA.Logic.Sim;

namespace MOBA.View
{
    /// <summary>Anything that owns a <see cref="DeterministicWorld"/> (NetClient or ReplayPlayer).</summary>
    public interface IWorldSource
    {
        DeterministicWorld World { get; }
    }
}
