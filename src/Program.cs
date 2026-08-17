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
    var options = ParseArguments(args, out var exitCode);
    if (options is null)
      return exitCode;
    if (!InputsAreUsable(options))
      return 1;
    var scores = TryAnalyze(options);
    if (scores is null)
      return 1;
    return Gate(options, scores);
  }

  /// <summary>
  /// Parses the whole command line. Returns null when the run should end
  /// during parsing (help, or a bad argument), with the exit code in
  /// <paramref name="exitCode"/>.
  /// </summary>
  private static Options? ParseArguments(string[] args, out int exitCode)
  {
    var options = new Options();
    for (var i = 0; i < args.Length; i++)
    {
      var stop = ApplyArgument(args, ref i, options);
      if (stop is int code)
      {
        exitCode = code;
        return null;
      }
    }
    if (options.SourceDirs.Count == 0)
      options.SourceDirs.Add(".");
    exitCode = 0;
    return options;
  }

  /// <summary>
  /// Applies one argument to <paramref name="options"/>. A non-null
  /// return is the exit code the run must stop with; null means keep
  /// parsing. Arguments that consume a value advance <paramref name="i"/>.
  /// </summary>
  private static int? ApplyArgument(string[] args, ref int i, Options options)
  {
    switch (args[i])
    {
      case "--help" or "-h": Console.WriteLine(Usage); return 0;
      case "--all": options.ShowAll = true; return null;
      case "--json": options.Json = true; return null;
      default: return ApplyValueOrSourceDir(args, ref i, options);
    }
  }

  private static int? ApplyValueOrSourceDir(string[] args, ref int i, Options options)
  {
    switch (args[i])
    {
      case "--lcov":
        if (!TryTakeValue(args, ref i, out var tracefile))
          return MissingValue("--lcov");
        options.LcovPaths.Add(tracefile);
        return null;
      case "--threshold":
        return ApplyThreshold(args, ref i, options);
      case var flag when flag.StartsWith('-'):
        Console.Error.WriteLine($"Unknown option: {flag}\n\n{Usage}");
        return 1;
      default:
        options.SourceDirs.Add(args[i]);
        return null;
    }
  }

  private static int? ApplyThreshold(string[] args, ref int i, Options options)
  {
    if (!TryTakeValue(args, ref i, out var value))
      return MissingValue("--threshold");
    if (!IsUsableThreshold(value, out options.Threshold))
    {
      Console.Error.WriteLine(
          $"Invalid --threshold '{value}': must be a finite number greater than zero.");
      return 1;
    }
    return null;
  }

  /// <summary>
  /// Invariant culture so the gate reads "6.5" identically on every
  /// machine; NaN/Infinity parse successfully but poison the comparison
  /// (crap > NaN is always false), so only finite positive values pass.
  /// </summary>
  private static bool IsUsableThreshold(string value, out double threshold) =>
      double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold)
      && double.IsFinite(threshold)
      && threshold > 0;

  private static bool TryTakeValue(string[] args, ref int i, out string value)
  {
    if (i + 1 >= args.Length)
    {
      value = "";
      return false;
    }
    value = args[++i];
    return true;
  }

  private static int MissingValue(string flag)
  {
    Console.Error.WriteLine($"Missing value for {flag}.\n\n{Usage}");
    return 1;
  }

  /// <summary>
  /// Checks every input path up front with our own messages — exception
  /// text for a bad path varies by OS, and stderr must always name the
  /// offending path. Reports all offenders, then fails the run.
  /// </summary>
  private static bool InputsAreUsable(Options options)
  {
    if (options.LcovPaths.Count == 0)
    {
      Console.Error.WriteLine($"Missing required --lcov <tracefile>.\n\n{Usage}");
      return false;
    }
    return TracefilesExist(options.LcovPaths) && SourceDirsExist(options.SourceDirs);
  }

  private static bool TracefilesExist(List<string> paths)
  {
    var missing = paths.Where(path => !File.Exists(path)).ToList();
    foreach (var path in missing)
      Console.Error.WriteLine($"lcov tracefile not found: {path}");
    return missing.Count == 0;
  }

  private static bool SourceDirsExist(List<string> dirs)
  {
    var invalid = dirs.Where(dir => !Directory.Exists(dir)).ToList();
    foreach (var dir in invalid)
      Console.Error.WriteLine(File.Exists(dir)
          ? $"source path is a file, not a directory: {dir}"
          : $"source directory not found: {dir}");
    return invalid.Count == 0;
  }

  /// <summary>
  /// Reads the tracefiles and scores every method found under the source
  /// dirs. Unreadable input (permissions, paths vanishing mid-run) is an
  /// input error: reported on stderr, null returned — never a crash.
  /// </summary>
  private static List<CrapScore>? TryAnalyze(Options options)
  {
    try
    {
      var lcov = LcovParser.ParseMany(options.LcovPaths.Select(File.ReadAllText));
      return ScoreAllMethods(options.SourceDirs, lcov);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    {
      Console.Error.WriteLine($"crap4net: cannot read input: {error.Message}");
      return null;
    }
  }

  private static List<CrapScore> ScoreAllMethods(
      List<string> dirs, Dictionary<string, Dictionary<int, long>> lcov)
  {
    var scores = new List<CrapScore>();
    foreach (var file in SourceFiles(dirs))
    {
      var hits = LcovParser.ForFile(lcov, file);
      scores.AddRange(
          ComplexityWalker.Analyze(file, File.ReadAllText(file))
              .Select(method => CrapAnalyzer.Score(method, hits)));
    }
    return scores;
  }

  private static IEnumerable<string> SourceFiles(IEnumerable<string> dirs) =>
      dirs.SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
          .Where(f => !f.Split(Path.DirectorySeparatorChar, '/')
                        .Any(segment => segment is "bin" or "obj"))
          .Distinct();

  /// <summary>
  /// Reports the scores and applies the exit-code contract: 2 when any
  /// method exceeds the threshold, 1 when nothing was analyzed (an empty
  /// scan must never look like a pass), 0 otherwise.
  /// </summary>
  private static int Gate(Options options, List<CrapScore> scores)
  {
    var failures = scores.Where(s => s.Crap > options.Threshold)
                         .OrderByDescending(s => s.Crap)
                         .ToList();
    var listed = options.ShowAll ? scores.OrderByDescending(s => s.Crap).ToList() : failures;
    if (options.Json)
      ReportJson(options.Threshold, scores.Count, failures.Count, listed);
    else
      ReportText(options.Threshold, scores.Count, failures.Count, listed);
    if (scores.Count == 0)
    {
      Console.Error.WriteLine(
          "crap4net: no methods found — nothing was analyzed. Scanned: "
          + string.Join(", ", options.SourceDirs.Select(Path.GetFullPath)));
      return 1;
    }
    return failures.Count == 0 ? 0 : 2;
  }

  private static void ReportJson(
      double threshold, int methods, int failures, List<CrapScore> listed)
  {
    Console.WriteLine(JsonSerializer.Serialize(new
    {
      threshold,
      methods,
      failures,
      results = listed.Select(s => new
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
  }

  private static void ReportText(
      double threshold, int methods, int failures, List<CrapScore> listed)
  {
    foreach (var s in listed)
      Console.WriteLine(
          $"{s.Crap,8:F2}  comp {s.Method.Complexity,3}  cov {s.Coverage,6:P1}  " +
          $"{s.Method.File}:{s.Method.StartLine}  {s.Method.Name}" +
          (s.InstrumentedLines == 0 ? "  [no coverage data]" : ""));

    Console.WriteLine(
        $"crap4net: {methods} methods, threshold {threshold}, " + Summary(methods, failures));
  }

  private static string Summary(int methods, int failures) =>
      methods == 0 ? "NO METHODS ANALYZED."
      : failures == 0 ? "all within threshold."
      : $"{failures} ABOVE THRESHOLD.";

  /// <summary>Everything the command line said, with defaults applied.</summary>
  private sealed class Options
  {
    public readonly List<string> LcovPaths = new();
    public readonly List<string> SourceDirs = new();
    public double Threshold = 6;
    public bool ShowAll;
    public bool Json;
  }
}
