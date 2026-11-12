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

## 3. `UseOpenTelemetry()` from Microsoft.Extensions.AI crashes at runtime on .NET 8 (2026-11-12)

- **Where:** `ChatClientFactory.Create` → `chat.AsBuilder().UseOpenTelemetry(...)`.
- **Symptom:** every agent and API test failed with
  `MissingMethodException: InstrumentAdvice<T>.set_HistogramBucketBoundaries(IReadOnlyList<T>)`.
  It compiled cleanly; only running it revealed the problem.
- **Diagnosis (took three attempts):** first guess was "the runtime's DiagnosticSource 8.0 is
  loading" — wrong, a diagnostic test showed `System.Diagnostics.DiagnosticSource` **11.0.0.0**
  was loaded from the app folder (pulled transitively by `OpenTelemetry.*` 1.21). Second guess was
  "M.E.AI 11.0 vs Agents.AI 1.28 skew" — aligning everything to M.E.AI 10.11.0 (what Agents.AI
  1.28 declares) didn't help either. The real cause: in DiagnosticSource 11 that property setter
  became `init`-only (modreq `IsExternalInit`), which changes the method signature, so the
  net8.0 builds of M.E.AI 10.11 *and* 11.0 (both compiled against DiagnosticSource 10.0.0.0)
  cannot bind it. A binary break two packages away.
- **Fix:** don't use `UseOpenTelemetry()`; emit our own spans. The agent already created an
  `agent.run` span with token counts; each tool call now gets a `tool.<name>` span with
  authorized/succeeded tags. Same information, no fragile dependency.
- **Tests:** `ShiftSenseAgentTests.*`, `ApiTests.*` — they run the real `ChatClientAgent` with a
  fake model, which is exactly why this surfaced in CI instead of in the demo.
- **Lesson:** "verify package versions against NuGet" (the spec's own warning) is necessary but
  not sufficient; transitive *runtime* compatibility needs an executed path. Also: when the first
  fix doesn't work, measure (which assembly actually loaded?) before guessing again.
