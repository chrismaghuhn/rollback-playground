using System.Reflection;
using System.Text.RegularExpressions;

namespace Core.Sim.Tests;

/// <summary>
/// Determinism regression audit — four automated guards that enforce the
/// Core.Sim determinism contract described in docs/ASSUMPTIONS.md §2.
///
/// These tests are intentionally broad (reflection + source scan) so that an
/// accidental addition of a forbidden type or API anywhere in Core.Sim is
/// caught before it reaches CI, even if it hides in a private helper.
///
/// ── Why audit tests pass immediately ─────────────────────────────────────────
///
/// Audit/regression tests verify invariants of existing code.  A test that
/// passes on first run here means the codebase already satisfies the
/// constraint — which is the desired outcome.  The tests are meaningful
/// because they would FAIL if a violation were introduced later:
///   • Add a field of type float   → A1 test (or the existing SimTypesTests
///                                   field audit) fails immediately.
///   • Add a method returning double → A2 (method-signature) test fails.
///   • Reference "Godot.dll"       → A3 (assembly reference) test fails.
///   • Write `DateTime.Now`        → B (source-scan) test fails.
/// </summary>
public class DeterminismAuditTests
{
    // ── Shared state ──────────────────────────────────────────────────────────

    private static readonly Assembly CoreSimAssembly = typeof(SimConstants).Assembly;

    // The three C# floating-point / fixed-point-alternative types that must
    // never appear in Core.Sim signatures.
    private static readonly HashSet<Type> ForbiddenFloatTypes =
    [
        typeof(float),
        typeof(double),
        typeof(decimal),
    ];

    // Engine assembly name prefixes (case-insensitive).
    private static readonly string[] EngineAssemblyPrefixes =
    [
        "Godot",
        "UnityEngine",
        "MonoGame",
        "Microsoft.Xna",
    ];

    // ── A1) Method signatures: return types + parameter types ────────────────

    /// <summary>
    /// No method in Core.Sim may return float, double, or decimal, nor accept
    /// any such type as a parameter (including <c>in</c>/<c>ref</c>/<c>out</c>
    /// variants, which are unwrapped before the check).
    ///
    /// Extends the existing field-type test in SimTypesTests to cover the full
    /// public and private method surface.
    /// </summary>
    [Fact]
    public void CoreSim_MethodSignatures_ContainNoFloatDoubleOrDecimal()
    {
        var violations = new List<string>();

        foreach (Type type in CoreSimAssembly.GetTypes())
        {
            if (IsCompilerGenerated(type)) continue;

            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public    | BindingFlags.NonPublic |
                BindingFlags.Static    | BindingFlags.Instance  |
                BindingFlags.DeclaredOnly))
            {
                Type returnType = UnwrapRef(method.ReturnType);
                if (ForbiddenFloatTypes.Contains(returnType))
                    violations.Add(
                        $"  {type.FullName}.{method.Name}() → returns {returnType.Name}");

                foreach (ParameterInfo param in method.GetParameters())
                {
                    Type paramType = UnwrapRef(param.ParameterType);
                    if (ForbiddenFloatTypes.Contains(paramType))
                        violations.Add(
                            $"  {type.FullName}.{method.Name}(" +
                            $"param '{param.Name}': {paramType.Name})");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Float/double/decimal found in Core.Sim method signatures:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    // ── A2) Property types ────────────────────────────────────────────────────

    /// <summary>
    /// No property in Core.Sim may have type float, double, or decimal.
    /// Complements the field-type test (which already covers backing fields
    /// individually) by auditing the public API surface explicitly.
    /// </summary>
    [Fact]
    public void CoreSim_PropertyTypes_ContainNoFloatDoubleOrDecimal()
    {
        var violations = new List<string>();

        foreach (Type type in CoreSimAssembly.GetTypes())
        {
            if (IsCompilerGenerated(type)) continue;

            foreach (PropertyInfo prop in type.GetProperties(
                BindingFlags.Public    | BindingFlags.NonPublic |
                BindingFlags.Static    | BindingFlags.Instance  |
                BindingFlags.DeclaredOnly))
            {
                if (ForbiddenFloatTypes.Contains(prop.PropertyType))
                    violations.Add(
                        $"  {type.FullName}.{prop.Name} : {prop.PropertyType.Name}");
            }
        }

        Assert.True(violations.Count == 0,
            $"Float/double/decimal properties found in Core.Sim:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    // ── A3) Engine assembly references ────────────────────────────────────────

    /// <summary>
    /// Core.Sim must not reference any game-engine assembly.
    /// If the compiler ever resolves a Godot/Unity/MonoGame type in Core.Sim,
    /// the output assembly will carry that reference and this test will catch it.
    /// </summary>
    [Fact]
    public void CoreSim_HasNoEngineAssemblyReferences()
    {
        var violations = CoreSimAssembly
            .GetReferencedAssemblies()
            .Select(r => r.Name ?? string.Empty)
            .Where(name => EngineAssemblyPrefixes.Any(
                prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(violations.Count == 0,
            $"Engine assembly references found in Core.Sim:" +
            $"{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations.Select(v => $"  {v}")));
    }

    // ── B) Source scan: forbidden tokens in src/Core.Sim/**/*.cs ─────────────

    /// <summary>
    /// Scans every <c>*.cs</c> file under <c>src/Core.Sim/</c> (excluding
    /// generated <c>obj/</c> and <c>bin/</c> subtrees) for tokens that would
    /// violate the determinism contract if used in production code.
    ///
    /// Plain-string tokens (exact substring match):
    ///   DateTime, DateTimeOffset, Stopwatch, Environment.TickCount,
    ///   TickCount64, Thread.Sleep, Task.Delay, Godot, UnityEngine, MonoGame
    ///
    /// Word-boundary tokens (<c>\btoken\b</c>):
    ///   float, double, decimal
    ///
    /// Note: comments and string literals are not excluded — the rule is that
    /// these identifiers must not appear at all in the Core.Sim source tree
    /// (docs/ASSUMPTIONS.md §2 and §9).
    /// </summary>
    [Fact]
    public void CoreSimSourceFiles_ContainNoForbiddenTokens()
    {
        string repoRoot  = FindRepoRoot();
        string corSimSrc = Path.Combine(repoRoot, "src", "Core.Sim");

        string[] files = Directory
            .GetFiles(corSimSrc, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsGeneratedOutputPath(f))
            .ToArray();

        Assert.True(files.Length > 0,
            $"No .cs files found under {corSimSrc} — is the path correct?");

        // ── Plain substring tokens ────────────────────────────────────────────
        string[] plainTokens =
        [
            "DateTime", "DateTimeOffset", "Stopwatch",
            "Environment.TickCount", "TickCount64",
            "Thread.Sleep", "Task.Delay",
            "Godot", "UnityEngine", "MonoGame",
        ];

        // ── Word-boundary tokens (C# type keywords) ───────────────────────────
        string[] wordTokens = ["float", "double", "decimal"];
        Regex[]  wordRegexes = wordTokens
            .Select(t => new Regex($@"\b{Regex.Escape(t)}\b", RegexOptions.Compiled))
            .ToArray();

        var violations = new List<string>();

        foreach (string filePath in files)
        {
            string[] lines   = File.ReadAllLines(filePath);
            string   relPath = Path.GetRelativePath(repoRoot, filePath);

            for (int i = 0; i < lines.Length; i++)
            {
                string line    = lines[i];
                int    lineNum = i + 1;

                foreach (string token in plainTokens)
                {
                    if (line.Contains(token, StringComparison.Ordinal))
                        violations.Add($"  {relPath}:{lineNum}  ← contains '{token}'");
                }

                for (int t = 0; t < wordTokens.Length; t++)
                {
                    if (wordRegexes[t].IsMatch(line))
                        violations.Add($"  {relPath}:{lineNum}  ← contains word '{wordTokens[t]}'");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Forbidden tokens found in Core.Sim source files " +
            $"(see docs/ASSUMPTIONS.md §2 for rationale):" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Walks from <see cref="AppContext.BaseDirectory"/> up the directory tree
    /// until it finds the directory that contains <c>RollbackPlayground.sln</c>.
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RollbackPlayground.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate repo root: walked up from " +
            $"'{AppContext.BaseDirectory}' without finding RollbackPlayground.sln.");
    }

    /// <summary>
    /// Returns <see langword="true"/> for paths that contain <c>/obj/</c> or
    /// <c>/bin/</c> directory segments — these are generated output trees and
    /// must not be scanned.
    /// </summary>
    private static bool IsGeneratedOutputPath(string fullPath)
    {
        char sep = Path.DirectorySeparatorChar;
        return fullPath.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
            || fullPath.Contains($"{sep}bin{sep}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns <see langword="true"/> for compiler-generated types
    /// (e.g. <c>&lt;PrivateImplementationDetails&gt;</c>) whose names start
    /// with <c>'&lt;'</c>.
    /// </summary>
    private static bool IsCompilerGenerated(Type type) => type.Name.StartsWith('<');

    /// <summary>
    /// Unwraps a by-ref type (<c>T&amp;</c>) to its element type <c>T</c>.
    /// Needed so that <c>in SimState</c> / <c>ref PlayerState</c> parameters
    /// are compared against the underlying value type rather than the managed
    /// pointer type, which is never in <see cref="ForbiddenFloatTypes"/>.
    /// </summary>
    private static Type UnwrapRef(Type type) =>
        type.IsByRef ? type.GetElementType()! : type;
}
