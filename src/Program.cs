using System.Globalization;
using System.Text.Json;

namespace Crap4Net;

internal static class Program
{
  private const string Usage = """
        crap4net — CRAP scores (complexity² × (1−coverage)³ + complexity) for C#.

        Usage: crap4net --lcov <tracefile> [options] [source-dir ...]

        Options:
          --lcov <file>       lcov tracefile (e.g. from coverlet). Required; repeat
                              to merge several tracefiles (max hits per line).
          --threshold <n>     failure bar, a finite number > 0; any method above it
                              fails the run (default 6)
          --all               list every method, not just those above the threshold
          --json              machine-readable output
        Source dirs default to the current directory; bin/ and obj/ are skipped.

        Exit codes: 0 ok; 1 usage/input error, or no methods found to analyze;
        2 methods above the threshold.
        """;

  internal static int Main(string[] args)
  {
    var lcovPaths = new List<string>();
    double threshold = 6;
    var showAll = false;
    var json = false;
    var sourceDirs = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
      switch (args[i])
      {
        case "--lcov":
          {
            if (i + 1 >= args.Length)
            {
              Console.Error.WriteLine($"Missing value for --lcov.\n\n{Usage}");
              return 1;
            }
            lcovPaths.Add(args[++i]);
            break;
          }
        case "--threshold":
          {
            if (i + 1 >= args.Length)
            {
              Console.Error.WriteLine($"Missing value for --threshold.\n\n{Usage}");
              return 1;
            }
            // Invariant culture so the gate reads "6.5" identically on every
            // machine; NaN/Infinity parse successfully but poison the
            // comparison (crap > NaN is always false), so only finite
            // positive values may pass.
            var value = args[++i];
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold)
                || !double.IsFinite(threshold) || threshold <= 0)
            {
              Console.Error.WriteLine(
                  $"Invalid --threshold '{value}': must be a finite number greater than zero.");
              return 1;
            }
            break;
          }
        case "--all": showAll = true; break;
        case "--json": json = true; break;
        case "--help" or "-h": Console.WriteLine(Usage); return 0;
        case var flag when flag.StartsWith('-'):
          Console.Error.WriteLine($"Unknown option: {flag}\n\n{Usage}"); return 1;
        default: sourceDirs.Add(args[i]); break;
      }
    }

    if (lcovPaths.Count == 0)
    {
      Console.Error.WriteLine($"Missing required --lcov <tracefile>.\n\n{Usage}");
      return 1;
    }
    var missingTracefiles = lcovPaths.Where(path => !File.Exists(path)).ToList();
    if (missingTracefiles.Count > 0)
    {
      foreach (var path in missingTracefiles)
        Console.Error.WriteLine($"lcov tracefile not found: {path}");
      return 1;
    }
    if (sourceDirs.Count == 0)
      sourceDirs.Add(".");
    // Validate up front with our own messages: exception text for a bad
    // path varies by OS, and stderr must always name the offending path.
    var invalidDirs = sourceDirs.Where(dir => !Directory.Exists(dir)).ToList();
    if (invalidDirs.Count > 0)
    {
      foreach (var dir in invalidDirs)
        Console.Error.WriteLine(File.Exists(dir)
            ? $"source path is a file, not a directory: {dir}"
            : $"source directory not found: {dir}");
      return 1;
    }

    var scores = new List<CrapScore>();
    try
    {
      var lcov = LcovParser.ParseMany(lcovPaths.Select(File.ReadAllText));
      foreach (var file in SourceFiles(sourceDirs))
      {
        var hits = LcovParser.ForFile(lcov, file);
        scores.AddRange(
            ComplexityWalker.Analyze(file, File.ReadAllText(file))
                .Select(method => CrapAnalyzer.Score(method, hits)));
      }
    }
    // Unreadable tracefiles or source (permissions, paths vanishing
    // mid-run) are input errors: exit 1, never an unhandled crash.
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
      Console.Error.WriteLine($"crap4net: cannot read input: {error.Message}");
      return 1;
    }

    var failures = scores.Where(s => s.Crap > threshold)
                         .OrderByDescending(s => s.Crap)
                         .ToList();
    Report(json, showAll, threshold, scores, failures);
    // An empty scan means the gate measured nothing — a mistyped source
    // dir (or a scan of generated-only trees) must fail, not pass.
    if (scores.Count == 0)
    {
      Console.Error.WriteLine(
          "crap4net: no methods found — nothing was analyzed. Scanned: "
          + string.Join(", ", sourceDirs.Select(Path.GetFullPath)));
      return 1;
    }
    return failures.Count == 0 ? 0 : 2;
  }

  private static IEnumerable<string> SourceFiles(IEnumerable<string> dirs) =>
      dirs.SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
          .Where(f => !f.Split(Path.DirectorySeparatorChar, '/')
                        .Any(segment => segment is "bin" or "obj"))
          .Distinct();

  private static void Report(bool json, bool showAll, double threshold,
      List<CrapScore> scores, List<CrapScore> failures)
  {
    if (json)
    {
      Console.WriteLine(JsonSerializer.Serialize(new
      {
        threshold,
        methods = scores.Count,
        failures = failures.Count,
        results = (showAll ? scores.OrderByDescending(s => s.Crap).ToList() : failures)
              .Select(s => new
              {
                file = s.Method.File,
                method = s.Method.Name,
                line = s.Method.StartLine,
                complexity = s.Method.Complexity,
                coverage = Math.Round(s.Coverage, 4),
                instrumentedLines = s.InstrumentedLines,
                // crap is rounded for reading; the gate compares crapExact,
                // so the report can never look within a threshold it failed.
                crap = Math.Round(s.Crap, 2),
                crapExact = s.Crap
              })
      }, new JsonSerializerOptions { WriteIndented = true }));
      return;
    }

    foreach (var s in showAll ? scores.OrderByDescending(s => s.Crap).ToList() : failures)
      Console.WriteLine(
          $"{s.Crap,8:F2}  comp {s.Method.Complexity,3}  cov {s.Coverage,6:P1}  " +
          $"{s.Method.File}:{s.Method.StartLine}  {s.Method.Name}" +
          (s.InstrumentedLines == 0 ? "  [no coverage data]" : ""));

    Console.WriteLine($"crap4net: {scores.Count} methods, threshold {threshold}, " +
                      (scores.Count == 0 ? "NO METHODS ANALYZED."
                       : failures.Count == 0 ? "all within threshold."
                       : $"{failures.Count} ABOVE THRESHOLD."));
  }
}
