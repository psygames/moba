// SPDX-License-Identifier: MIT
// Determinism guard attribute. Any method tagged [NoGC] is scanned by
// MobaDeterminismAnalyzer and may not contain `new` (object/array allocation),
// boxing conversions, params arrays, LINQ, string concatenation, lambdas, or
// closures. See server/src/MOBA.Analyzers/.
//
// Marker only — has zero runtime effect.

using System;

namespace MOBA.Logic;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property, Inherited = false)]
public sealed class NoGCAttribute : Attribute { }
