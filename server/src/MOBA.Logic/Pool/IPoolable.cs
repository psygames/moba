// SPDX-License-Identifier: MIT
namespace MOBA.Logic;

/// <summary>Marker for objects that can be recycled by <see cref="Pool{T}"/>.</summary>
public interface IPoolable
{
    /// <summary>Reset state to defaults so the instance is ready to be handed out again.
    /// MUST NOT allocate.</summary>
    void Reset();
}
