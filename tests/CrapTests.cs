using Crap4Net;
using Xunit;

namespace Crap4Net.Tests;

public class FormulaTests
{
  [Theory]
  [InlineData(1, 1.0, 1.0)]     // trivial, fully covered
  [InlineData(1, 0.0, 2.0)]     // trivial, uncovered: 1 + 1
  [InlineData(5, 1.0, 5.0)]     // full coverage collapses to complexity
  [InlineData(5, 0.0, 30.0)]    // 25 + 5
  [InlineData(4, 0.5, 6.0)]     // 16 * 0.125 + 4
  public void MatchesDefinition(int complexity, double coverage, double expected) =>
      Assert.Equal(expected, CrapScore.Formula(complexity, coverage), precision: 10);
}

public class ComplexityTests
{
  private static int ComplexityOf(string methodBody)
  {
    var source = $"class C {{ void M() {{ {methodBody} }} }}";
    var method = Assert.Single(ComplexityWalker.Analyze("test.cs", source));
    return method.Complexity;
  }

  [Fact]
  public void StraightLineCodeIsOne() =>
      Assert.Equal(1, ComplexityOf("var x = 1; System.Console.WriteLine(x);"));

  [Fact]
  public void EachBranchAddsOne() =>
      Assert.Equal(3, ComplexityOf("if (true) { } if (false) { }"));

  [Fact]
  public void LoopsCatchAndLogicalOperatorsCount() =>
      // while + catch + && + ?? = 4 decision points
      Assert.Equal(5, ComplexityOf("""
            while (true) { break; }
            try { } catch { }
            var b = 1 > 0 && 2 > 1;
            object? o = null; var q = o ?? "x";
            """));

  [Fact]
  public void SwitchArmsCount() =>
      Assert.Equal(4, ComplexityOf("var y = 1 switch { 1 => 1, 2 => 2, _ => 3 };"));

  [Fact]
  public void LocalFunctionsGetTheirOwnEntry()
  {
    var source = """
            class C
            {
                void M()
                {
                    if (true) { }
                    int Local(int x) { return x > 0 ? x : -x; }
                    Local(1);
                }
            }
            """;
    var methods = ComplexityWalker.Analyze("test.cs", source);
    Assert.Equal(2, methods.Count);
    Assert.Equal(2, methods.Single(m => m.Name == "C.M").Complexity);
    Assert.Equal(2, methods.Single(m => m.Name == "C.Local (local)").Complexity);
  }

  [Fact]
  public void ExpressionBodiedMembersAndAccessorsAreFound()
  {
    var source = """
            class C
            {
                int _x;
                int X { get => _x > 0 ? _x : 0; set { _x = value; } }
                int Double(int n) => n * 2;
            }
            """;
    var names = ComplexityWalker.Analyze("test.cs", source).Select(m => m.Name).ToHashSet();
    Assert.Contains("C.X.get", names);
    Assert.Contains("C.X.set", names);
    Assert.Contains("C.Double", names);
  }
}

public class LcovParserTests
{
  private const string Sample = """
        SF:/repo/src/Widget.cs
        DA:3,5
        DA:4,0
        DA:7,2
        end_of_record
        SF:/repo/src/Other.cs
        DA:1,1
        end_of_record
        """;

  [Fact]
  public void ParsesHitsPerFile()
  {
    var files = LcovParser.Parse(Sample);
    Assert.Equal(2, files.Count);
    // Parse normalizes its keys, which re-roots "/repo/..." to
    // "<drive>:/repo/..." on Windows; normalize the expectation through
    // the same function so both sides re-root identically everywhere.
    var widget = files[LcovParser.NormalizePath("/repo/src/Widget.cs")];
    Assert.Equal(5, widget[3]);
    Assert.Equal(0, widget[4]);
  }

  [Fact]
  public void SuffixMatchFindsRelocatedCheckouts()
  {
    var files = LcovParser.Parse(Sample);
    var hits = LcovParser.ForFile(files, "/elsewhere/src/Widget.cs");
    Assert.NotNull(hits);
    Assert.Equal(2, hits![7]);
  }

  [Fact]
  public void BareFilenameCollisionDoesNotMatch()
  {
    var files = LcovParser.Parse(Sample);
    Assert.Null(LcovParser.ForFile(files, "/unrelated/Widget.cs"));
  }

  [Fact]
  public void ParseManyMergesPerLineWithMaxHits()
  {
    // Neither text ends with a newline — coverlet writes tracefiles that
    // way, which is exactly why concatenating them externally is unsafe.
    var merged = LcovParser.ParseMany(new[]
    {
      "SF:/repo/src/Widget.cs\nDA:3,5\nDA:4,0\nend_of_record",
      "SF:/repo/src/Widget.cs\nDA:3,1\nDA:4,2\nDA:9,7\nend_of_record\n"
          + "SF:/repo/src/New.cs\nDA:1,1\nend_of_record"
    });
    var widget = merged[LcovParser.NormalizePath("/repo/src/Widget.cs")];
    Assert.Equal(5, widget[3]);   // max(5, 1)
    Assert.Equal(2, widget[4]);   // max(0, 2)
    Assert.Equal(7, widget[9]);   // only in the second tracefile
    Assert.Equal(1, merged[LcovParser.NormalizePath("/repo/src/New.cs")][1]);
  }
}

public class AnalyzerTests
{
  [Fact]
  public void JoinsCoverageBySpanAndScores()
  {
    var method = new MethodInfo("f.cs", "C.M", StartLine: 10, EndLine: 14, Complexity: 4);
    var hits = new Dictionary<int, long>
    {
      [10] = 1,
      [11] = 0,
      [12] = 0,
      [13] = 0,  // 25% covered
      [99] = 1                                  // outside span, ignored
    };
    var score = CrapAnalyzer.Score(method, hits);
    Assert.Equal(0.25, score.Coverage);
    Assert.Equal(4, score.InstrumentedLines);
    Assert.Equal(CrapScore.Formula(4, 0.25), score.Crap);
  }

  [Fact]
  public void MissingCoverageCountsAsUncovered()
  {
    var method = new MethodInfo("f.cs", "C.M", 1, 5, Complexity: 3);
    Assert.Equal(0.0, CrapAnalyzer.Score(method, null).Coverage);
    Assert.Equal(0.0, CrapAnalyzer.Score(method, new Dictionary<int, long>()).Coverage);
  }
}

/// <summary>
/// End-to-end exit-code contract tests: Main runs in-process with captured
/// console. All console-redirecting tests live in this one class so xUnit
/// never runs two redirections in parallel.
/// </summary>
public sealed class ProgramTests : IDisposable
{
  private readonly List<string> tempDirs = new();

  public void Dispose()
  {
    foreach (var dir in tempDirs)
      Directory.Delete(dir, recursive: true);
  }

  private string CreateTempDir()
  {
    var dir = Directory.CreateTempSubdirectory("crap4net-tests-").FullName;
    tempDirs.Add(dir);
    return dir;
  }

  /// <summary>
  /// Writes a one-method source file plus a tracefile that fully covers
  /// it, so a default run over the pair exits 0.
  /// </summary>
  private (string SourceDir, string LcovPath) CoveredFixture()
  {
    var dir = CreateTempDir();
    var source = Path.Combine(dir, "Sample.cs");
    File.WriteAllText(source, "class Sample { int Twice(int n) { return n * 2; } }");
    var lcov = Path.Combine(dir, "coverage.info");
    File.WriteAllText(lcov, $"SF:{source}\nDA:1,1\nend_of_record\n");
    return (dir, lcov);
  }

  private static (int Exit, string Out, string Err) Run(params string[] args)
  {
    var originalOut = Console.Out;
    var originalError = Console.Error;
    using var stdout = new StringWriter();
    using var stderr = new StringWriter();
    Console.SetOut(stdout);
    Console.SetError(stderr);
    try
    {
      return (Program.Main(args), stdout.ToString(), stderr.ToString());
    }
    finally
    {
      Console.SetOut(originalOut);
      Console.SetError(originalError);
    }
  }

  [Fact]
  public void CoveredProjectWithinThresholdExitsZero()
  {
    var (sourceDir, lcovPath) = CoveredFixture();
    var (exit, _, _) = Run("--lcov", lcovPath, "--threshold", "6", sourceDir);
    Assert.Equal(0, exit);
  }

  [Theory]
  [InlineData("NaN")]        // parses, then poisons every comparison to false
  [InlineData("Infinity")]   // parses, nothing can ever exceed it
  [InlineData("-Infinity")]
  [InlineData("0")]
  [InlineData("-1")]
  [InlineData("six")]
  public void UnusableThresholdIsUsageErrorEvenWhenInputsAreValid(string value)
  {
    var (sourceDir, lcovPath) = CoveredFixture();
    var (exit, _, err) = Run("--lcov", lcovPath, "--threshold", value, sourceDir);
    Assert.Equal(1, exit);
    Assert.Contains("--threshold", err);
    Assert.Contains(value, err);
  }

  [Fact]
  public void ThresholdWithoutValueIsUsageError()
  {
    var (exit, _, err) = Run("--threshold");
    Assert.Equal(1, exit);
    Assert.Contains("--threshold", err);
  }

  [Fact]
  public void ZeroMethodsAnalyzedIsAnErrorNamingTheScannedDirs()
  {
    var emptyDir = CreateTempDir();
    var (_, lcovPath) = CoveredFixture();
    var (exit, _, err) = Run("--lcov", lcovPath, emptyDir);
    Assert.Equal(1, exit);
    Assert.Contains(Path.GetFullPath(emptyDir), err);
  }

  /// <summary>
  /// A complexity-5 method with two instrumented lines, plus two
  /// tracefiles that each cover only one of them: either file alone is
  /// half coverage (crap 8.125, above the default bar), their union is
  /// full coverage (crap 5, within it).
  /// </summary>
  private (string SourceDir, string LcovA, string LcovB) SplitCoverageFixture()
  {
    var dir = CreateTempDir();
    var source = Path.Combine(dir, "Gnarly.cs");
    File.WriteAllText(source, """
        class Gnarly
        {
          static int Classify(int n)
          {
            if (n > 100) { return 4; }
            if (n > 10) { return 3; }
            if (n > 1) { return 2; }
            if (n > 0) { return 1; }
            return 0;
          }
        }
        """);
    // No trailing newline after end_of_record, faithful to coverlet.
    var lcovA = Path.Combine(dir, "a.info");
    File.WriteAllText(lcovA, $"SF:{source}\nDA:5,1\nDA:6,0\nend_of_record");
    var lcovB = Path.Combine(dir, "b.info");
    File.WriteAllText(lcovB, $"SF:{source}\nDA:5,0\nDA:6,1\nend_of_record");
    return (dir, lcovA, lcovB);
  }

  [Fact]
  public void SingleTracefileStillGatesOnItsOwnCoverage()
  {
    var (dir, lcovA, _) = SplitCoverageFixture();
    var (exit, _, _) = Run("--lcov", lcovA, dir);
    Assert.Equal(2, exit);
  }

  [Fact]
  public void RepeatedLcovFlagsMergeCoverageAcrossTracefiles()
  {
    var (dir, lcovA, lcovB) = SplitCoverageFixture();
    var (exit, _, _) = Run("--lcov", lcovA, "--lcov", lcovB, dir);
    Assert.Equal(0, exit);
  }

  [Fact]
  public void EveryMissingTracefileIsNamed()
  {
    var (dir, lcovA, _) = SplitCoverageFixture();
    var ghost = Path.Combine(dir, "ghost.info");
    var (exit, _, err) = Run("--lcov", lcovA, "--lcov", ghost, dir);
    Assert.Equal(1, exit);
    Assert.Contains(ghost, err);
  }

  [Fact]
  public void ZeroMethodsStillEmitsTheStableJsonShape()
  {
    var emptyDir = CreateTempDir();
    var (_, lcovPath) = CoveredFixture();
    var (exit, output, _) = Run("--lcov", lcovPath, "--json", emptyDir);
    Assert.Equal(1, exit);
    using var report = System.Text.Json.JsonDocument.Parse(output);
    Assert.Equal(0, report.RootElement.GetProperty("methods").GetInt32());
    Assert.Equal(0, report.RootElement.GetProperty("failures").GetInt32());
  }
}
