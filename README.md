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

`--lcov` may repeat to merge coverage from several test suites:

```sh
crap4net --lcov unit/coverage.info --lcov acceptance/coverage.info src/
```

Each tracefile is parsed on its own and merged per line with max-hits
semantics — the same rule used for duplicate `SF:` records within one
tracefile. Never concatenate tracefiles yourself: coverlet writes them
without a trailing newline, so `cat` fuses `end_of_record` with the next
`SF:` line and that record silently vanishes.

Methods whose file is missing from the tracefile — or with no instrumented
lines — count as **uncovered**: missing coverage must never look like
safety. A scan that finds **no methods at all** exits 1 (the scanned
directories are named on stderr): a mistyped source dir must never look
like a pass.

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
