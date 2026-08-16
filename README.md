# crap4net

CRAP scores for C#: `complexity² × (1 − coverage)³ + complexity`, computed
per method by joining Roslyn syntax analysis (cyclomatic complexity, method
line spans) with an lcov tracefile (line hits, e.g. from coverlet).

```sh
dotnet test --collect:"XPlat Code Coverage;Format=lcov"
crap4net --lcov tests/TestResults/<run>/coverage.info src/
```

Exit 0 when every method is within `--threshold` (default 6, the swarm's
hardener bar); exit 2 lists offenders, worst first. `--all` shows every
method, `--json` for machines.

Methods whose file is missing from the tracefile — or with no instrumented
lines — count as **uncovered**: missing coverage must never look like
safety.

## Install

```sh
git clone https://github.com/bandoyer/crap4net
dotnet pack crap4net/src -c Release -o crap4net/nupkg
dotnet tool install --global crap4net --add-source crap4net/nupkg
```

## Provenance

Built as part of [swarm-forge-herdr](https://github.com/bandoyer/swarm-forge-herdr)
— the .NET member of the tool family pioneered by
[crap4clj](https://github.com/unclebob/crap4clj). MIT licensed.
