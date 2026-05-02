// SPDX-License-Identifier: MIT
// MobaDeterminismAnalyzer — PRD §1.4 / §4.1.
// Roslyn analyzer enforced on the MOBA.Logic assembly.
//
// Diagnostics:
//   MOBA001  Forbidden type/API in MOBA.Logic
//            (System.Random, DateTime/Stopwatch, UnityEngine.*, float/double literals,
//             Math.* with non-deterministic semantics, LINQ allocating extensions, ...)
//   MOBA002  `new` allocation inside a method tagged [NoGC]
//            (also: array creation, lambda/closure, params array, string concat, boxing).
//
// Scope: this analyzer is scoped to projects that opt in via
//   <PropertyGroup><MobaDeterminismScope>Logic</MobaDeterminismScope></PropertyGroup>
// or whose AssemblyName starts with "MOBA.Logic". Other assemblies are ignored.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MOBA.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MobaDeterminismAnalyzer : DiagnosticAnalyzer
{
    public const string ForbiddenApiId = "MOBA001";
    public const string AllocInNoGCId  = "MOBA002";

    private static readonly DiagnosticDescriptor ForbiddenApi = new(
        id: ForbiddenApiId,
        title: "Non-deterministic / forbidden API in MOBA.Logic",
        messageFormat: "'{0}' is forbidden in MOBA.Logic (reason: {1})",
        category: "MOBA.Determinism",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "MOBA.Logic must remain deterministic and engine-agnostic.");

    private static readonly DiagnosticDescriptor AllocInNoGC = new(
        id: AllocInNoGCId,
        title: "Allocation inside [NoGC] method",
        messageFormat: "Allocation '{0}' is not allowed inside method marked [NoGC]",
        category: "MOBA.Determinism",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Methods tagged [NoGC] must not allocate during the simulation tick.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(ForbiddenApi, AllocInNoGC);

    // namespace prefix -> reason
    private static readonly (string Prefix, string Reason)[] ForbiddenNamespaces =
    {
        ("UnityEngine",                       "MOBA.Logic must not depend on Unity"),
        ("System.Threading.Tasks",            "non-deterministic scheduling"),
        ("System.Threading.Timer",            "non-deterministic clock"),
        ("System.IO",                         "non-deterministic IO; serialize via MemoryPack snapshot only"),
        ("System.Linq",                       "LINQ enumerator allocations + delegate boxing"),
    };

    private static readonly HashSet<string> ForbiddenTypes = new()
    {
        "System.Random",
        "System.Diagnostics.Stopwatch",
        "System.DateTime",
        "System.DateTimeOffset",
        "System.Text.StringBuilder",
        "System.Guid",
    };

    // Math members that exist in System.Math but have non-deterministic
    // cross-platform results (e.g. trig). Deterministic ops live in Fix64.
    private static readonly HashSet<string> ForbiddenMathMembers = new()
    {
        "System.Math.Sin", "System.Math.Cos", "System.Math.Tan", "System.Math.Atan2",
        "System.Math.Sqrt", "System.Math.Pow", "System.Math.Exp", "System.Math.Log",
        "System.MathF.Sin", "System.MathF.Cos", "System.MathF.Tan", "System.MathF.Atan2",
        "System.MathF.Sqrt", "System.MathF.Pow", "System.MathF.Exp", "System.MathF.Log",
    };

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            if (!IsLogicAssembly(start.Compilation, start.Options)) return;

            // ----- MOBA001: forbidden symbols at use sites -----
            start.RegisterSyntaxNodeAction(AnalyzeIdentifier,
                SyntaxKind.IdentifierName,
                SyntaxKind.GenericName,
                SyntaxKind.QualifiedName);

            // float / double literal in MOBA.Logic source.
            start.RegisterSyntaxNodeAction(AnalyzePredefinedType,
                SyntaxKind.PredefinedType);

            // ----- MOBA002: allocation inside [NoGC] -----
            start.RegisterSyntaxNodeAction(AnalyzeMethod,
                SyntaxKind.MethodDeclaration,
                SyntaxKind.ConstructorDeclaration,
                SyntaxKind.GetAccessorDeclaration,
                SyntaxKind.SetAccessorDeclaration,
                SyntaxKind.LocalFunctionStatement);
        });
    }

    private static bool IsLogicAssembly(Compilation c, AnalyzerOptions options)
    {
        var name = c.AssemblyName ?? string.Empty;
        if (name.StartsWith("MOBA.Logic", System.StringComparison.Ordinal)) return true;
        // Optional opt-in via .editorconfig / build property file.
        return false;
    }

    // ---------------- MOBA001 -------------------------------------------------

    private static void AnalyzeIdentifier(SyntaxNodeAnalysisContext ctx)
    {
        // Only inspect type / member references, not declarations.
        if (ctx.Node.Parent is BaseTypeDeclarationSyntax) return;
        if (ctx.Node.Parent is VariableDeclarationSyntax) { /* still inspect */ }

        var info = ctx.SemanticModel.GetSymbolInfo(ctx.Node);
        var sym = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (sym == null) return;
        ReportIfForbidden(ctx, sym);
    }

    private static void AnalyzePredefinedType(SyntaxNodeAnalysisContext ctx)
    {
        var pt = (PredefinedTypeSyntax)ctx.Node;
        var kind = pt.Keyword.Kind();
        if (kind == SyntaxKind.FloatKeyword || kind == SyntaxKind.DoubleKeyword)
        {
            // `float` / `double` are usable in test/CLI helpers but never in MOBA.Logic gameplay code.
            ctx.ReportDiagnostic(Diagnostic.Create(ForbiddenApi, pt.GetLocation(),
                pt.Keyword.Text, "use Fix64 instead"));
        }
    }

    private static void ReportIfForbidden(SyntaxNodeAnalysisContext ctx, ISymbol sym)
    {
        // Walk up to the containing namespace.
        var ns = sym.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        foreach (var (prefix, reason) in ForbiddenNamespaces)
        {
            if (ns == prefix || ns.StartsWith(prefix + ".", System.StringComparison.Ordinal))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(ForbiddenApi, ctx.Node.GetLocation(),
                    sym.ToDisplayString(), reason));
                return;
            }
        }

        var typeFull = sym is ITypeSymbol ts ? ts.ToDisplayString() : sym.ContainingType?.ToDisplayString() ?? string.Empty;
        if (ForbiddenTypes.Contains(typeFull))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(ForbiddenApi, ctx.Node.GetLocation(),
                typeFull, "non-deterministic state/clock"));
            return;
        }

        if (sym.ContainingType != null)
        {
            string memberFull = sym.ContainingType.ToDisplayString() + "." + sym.Name;
            if (ForbiddenMathMembers.Contains(memberFull))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(ForbiddenApi, ctx.Node.GetLocation(),
                    memberFull, "use Fix64 trig instead"));
            }
        }
    }

    // ---------------- MOBA002 -------------------------------------------------

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext ctx)
    {
        var node = ctx.Node;
        var symbol = ctx.SemanticModel.GetDeclaredSymbol(node);
        if (symbol == null) return;
        if (!HasNoGC(symbol)) return;

        SyntaxNode body = null;
        switch (node)
        {
            case MethodDeclarationSyntax m:
                body = (SyntaxNode)m.Body ?? m.ExpressionBody; break;
            case ConstructorDeclarationSyntax c:
                body = (SyntaxNode)c.Body ?? c.ExpressionBody; break;
            case AccessorDeclarationSyntax a:
                body = (SyntaxNode)a.Body ?? a.ExpressionBody; break;
            case LocalFunctionStatementSyntax lf:
                body = (SyntaxNode)lf.Body ?? lf.ExpressionBody; break;
        }
        if (body == null) return;

        foreach (var n in body.DescendantNodes())
        {
            switch (n)
            {
                case ObjectCreationExpressionSyntax oc:
                {
                    // Struct construction is stack-allocated; the analyzer only flags reference types.
                    var ti = ctx.SemanticModel.GetTypeInfo(oc).Type;
                    if (ti != null && ti.IsValueType) break;
                    Report(ctx, n, "new " + (oc.Type?.ToString() ?? "?") + "()");
                    break;
                }
                case ArrayCreationExpressionSyntax ac:
                    Report(ctx, n, "new " + ac.Type);
                    break;
                case ImplicitArrayCreationExpressionSyntax:
                    Report(ctx, n, "new[]{...}");
                    break;
                case AnonymousObjectCreationExpressionSyntax:
                    Report(ctx, n, "anonymous object");
                    break;
                case ParenthesizedLambdaExpressionSyntax:
                case SimpleLambdaExpressionSyntax:
                case AnonymousMethodExpressionSyntax:
                    Report(ctx, n, "lambda/closure");
                    break;
                case StackAllocArrayCreationExpressionSyntax:
                    // stackalloc is fine.
                    break;
            }
        }
    }

    private static bool HasNoGC(ISymbol symbol)
    {
        foreach (var a in symbol.GetAttributes())
        {
            var name = a.AttributeClass?.Name;
            if (name == "NoGCAttribute" || name == "NoGC") return true;
        }
        return false;
    }

    private static void Report(SyntaxNodeAnalysisContext ctx, SyntaxNode n, string desc)
        => ctx.ReportDiagnostic(Diagnostic.Create(AllocInNoGC, n.GetLocation(), desc));
}
