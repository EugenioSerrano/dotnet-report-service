using PageAccountingCheck;
using PaginationFixtures;

namespace StrictAccountingCheck;

/// <summary>
/// The "probe" command: the strict check, the character check and the geometry
/// observation on the four committed PDFs of the grid experiment of
/// spikes/08-pagedjs-pre-split, with each PDF's source fixture. Its known answers
/// are only what that experiment established: on the three PDFs with the split,
/// every row printed (rule 1) and the printed chain closes on the block total
/// (rule 3); on the giant-row PDF without the split, every row after row 50 is
/// absent. Everything else here is an observation.
/// </summary>
internal static class Probe
{
    private static readonly (string File, string Fixture, bool Split)[] Pdfs =
    [
        ("G200-split-t1.00-f0.90-on-r1.pdf", "G200", true),
        ("X1M-split-t1.00-f0.90-on-r1.pdf", "X1M", true),
        ("W2a-split-t1.00-f0.90-on-r1.pdf", "W2a", true),
        ("G200-off-on-r1.pdf", "G200", false),
    ];

    public static async Task<int> RunAsync(string evidenceDirectory, string fixturesDirectory, string outDirectory, int timeoutSeconds, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outDirectory);
        var sums = Sums.Read(Path.Combine(fixturesDirectory, FixtureFile.SumsFileName));
        var results = new List<object>();
        var all = true;
        foreach (var (file, fixtureId, split) in Pdfs)
        {
            var fixture = FixtureFile.ReadVerified(fixturesDirectory, fixtureId, sums);
            var reading = await Account.ReadAsync(Path.Combine(evidenceDirectory, file), fixture, true, TimeSpan.FromSeconds(timeoutSeconds), cancellation);
            var entry = new StrictEntry { Run = file, Markup = "grid", FixtureId = fixtureId, FooterOn = true, Repetition = 1 };
            Account.Judge(entry, reading, fixture, null, null);
            string known;
            string observed;
            bool holds;
            var rule1 = entry.Rules.First(r => r.Rule == 1);
            var rule3 = entry.Rules.First(r => r.Rule == 3);
            if (split)
            {
                known = "rule 1 PASS; rule 3 PASS (every row printed; the chain closes on the block total)";
                observed = $"rule 1 {rule1.Outcome} ({rule1.Detail}); rule 3 {rule3.Outcome} ({rule3.Detail})";
                holds = rule1.Outcome == "PASS" && rule3.Outcome == "PASS";
            }
            else
            {
                var after = fixture.Items.Select(i => i.Sequence!.Value).Where(s => s > 50).ToList();
                var present = after.Where(reading.RowPages.ContainsKey).ToList();
                known = $"rule 1 FAIL; none of the {after.Count} sequences after 50 in the reading";
                observed = $"rule 1 {rule1.Outcome} ({rule1.Detail}); {present.Count} of them present";
                holds = rule1.Outcome == "FAIL" && present.Count == 0;
            }

            all &= holds;
            Console.WriteLine($"{file}: {(holds ? "holds" : "DOES NOT HOLD")} — {observed}; rules {string.Join(' ', entry.Rules.Select(r => $"{r.Rule}:{r.Outcome}"))}, "
                + $"full evaluation {entry.FullEvaluation.Count}, character failures {entry.CharacterFailures.Count}, geometry other pages {entry.GeometryOtherPages}");
            results.Add(new
            {
                pdf = file,
                fixture = fixtureId,
                knownAnswer = known,
                observedKnown = observed,
                holds,
                pages = entry.Pages,
                observations = new
                {
                    rules = entry.Rules,
                    fullEvaluation = entry.FullEvaluation,
                    characterRowsPassed = entry.CharacterRowsPassed,
                    characterFailures = entry.CharacterFailures.Take(20),
                    characterFailureCount = entry.CharacterFailures.Count,
                    geometry = entry.Geometry.GroupBy(g => g.Class).ToDictionary(g => g.Key, g => g.Count()),
                    otherPages = entry.Geometry.Where(g => g.Class == "other").Take(10),
                    headerRowPages = entry.HeaderRowPages,
                    note = "rule 8 needs the run's record and is not evaluated here; only rules 1 and 3 (and the absent rows) are known answers",
                },
            });
        }

        await StageFolder.WriteJsonAsync(Path.Combine(outDirectory, "probe.json"), new { knownAnswersHold = all, pdfs = results });
        return all ? 0 : 1;
    }
}
