# Build notes — bugs caught during AI-assisted development

Running log of real defects found in AI-generated code while building ShiftSense, and the test
that catches each one. This feeds the README's "AI-assisted development" section. Only real
findings go here.

## 1. Seed roster left no headroom for any swap (2026-11-12)

- **Where:** `src/ShiftSense.Domain/SeedData.cs`, first version of `BuildShifts`.
- **Symptom:** three scheduling tests failed at once: only 49 of 212 seed shifts had a single
  eligible swap candidate, and the coverage solver could not fill the open US shift. Every
  employee worked 5×8h = 40h in a fixed slot, so every US employee was already at the 40h cap,
  and the 11h-rest rule correctly rejected "evening then next-morning" swaps.
- **Fix:** 4 shifts per week (32h) so the cap leaves room for exactly one extra shift. The
  rest-gap behaviour was *right* and was kept.
- **Tests:** `SwapCandidateFinderTests.Seed_roster_has_candidates_for_most_shifts`,
  `CoverageSolverTests.Solves_the_seed_rosters_open_shifts`.
- **Lesson:** the algorithms were fine; the generated test data violated the domain's own
  constraints. Corpus-level tests over the seed caught what unit tests on toy rosters could not.

## 2. Test fixture violated the rule it wasn't testing (2026-11-12)

- **Where:** `SwapCandidateFinderTests.Rejects_weekly_hours_cap`.
- **Symptom:** the "relaxed cap" half of the test failed because the fixture's Thursday 06:00
  shift sat only 8h after the Wednesday 14–22 target — the rest rule fired, not the cap rule.
- **Fix:** move the fixture's shifts to 10–18 so the only violation under test is the weekly cap.
- **Lesson:** a constraint test must isolate one constraint; the generated fixture tripped a
  neighbouring one.

## 3. OpenTelemetry 1.21 silently breaks both Microsoft.Extensions.AI and the MCP SDK on .NET 8 (2026-11-12)

- **Where:** first in `ChatClientFactory.Create` → `UseOpenTelemetry(...)`; later the MCP server
  itself (`ModelContextProtocol.Diagnostics` static constructor) with the identical error.
- **Symptom:** `MissingMethodException: InstrumentAdvice<T>.set_HistogramBucketBoundaries(...)`.
  Every agent test failed; later every MCP `initialize` hung or returned 500. It compiled
  cleanly; only executing it revealed the problem.
- **Diagnosis (five steps, three wrong guesses):**
  1. Guess: the runtime's DiagnosticSource 8.0 wins over the package. Wrong — a throwaway
     diagnostic test printed the loaded assembly: `System.Diagnostics.DiagnosticSource` 11.0.0.0
     from the app folder, and its setter carries modreq `IsExternalInit` (init-only).
  2. Guess: M.E.AI 11.0 vs Microsoft.Agents.AI 1.28 skew. Aligning to M.E.AI 10.11 (what
     Agents.AI 1.28 declares) changed nothing.
  3. Workaround: drop `UseOpenTelemetry()` and hand-roll spans. Tests went green — until the MCP
     server failed the same way, which proved the problem was not M.E.AI-specific.
  4. `dotnet nuget why` showed the chain: **OpenTelemetry 1.21.0 → Microsoft.Extensions.* 11.0.0
     → DiagnosticSource 11.0.0**. MCP 2.4.0 and M.E.AI 10.11 themselves only ask for 10.x.
  5. Root cause: DiagnosticSource 11's net8 asset made that property init-only, changing the
     setter's signature, so anything compiled against 10.x cannot bind it.
- **Fix:** pin `OpenTelemetry.Extensions.Hosting` / `OpenTelemetry.Exporter.Console` to 1.20.1
  (declares DiagnosticSource 10.0.0). The Api now resolves DiagnosticSource 10.0.14 and both
  `UseOpenTelemetry()` (restored) and the MCP server work. Upgrading OpenTelemetry later requires
  M.E.AI and MCP builds that target the 11 line.
- **Tests:** `ShiftSenseAgentTests.*`, `ApiTests.*`, `McpServerTests.*`, plus a Kestrel smoke
  script that drives `/mcp` with raw JSON-RPC.
- **Lesson:** "verify package versions against NuGet" (the spec's own warning) is necessary but
  not sufficient — transitive *runtime* compatibility needs an executed path, and when a fix
  "works" by routing around a symptom, the same cause will resurface elsewhere. Measure (which
  assembly loaded, who pulled it in) before the next guess.
