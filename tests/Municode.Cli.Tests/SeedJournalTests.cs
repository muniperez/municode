// SPDX-License-Identifier: MPL-2.0

using Municode.Cli.Commands;
using Municode.Cli.Seed;

namespace Municode.Cli.Tests;

public sealed class SeedJournalTests
{
    [Fact]
    public void JournalRoundTripsAndRejectsInterruptedPendingWrite()
    {
        using var fixture = new JournalFixture();
        var id = new string('a', 32);
        var state = State(id);
        using (var journal = new SeedJournal(fixture.Root, id, true))
        {
            journal.Save(state);
            Assert.Equal(state.Plan.Approval, journal.Load().Plan.Approval);
        }
        var journalPath = Path.Combine(fixture.Root, "seed", id, "operation.json");
        var pendingPath = journalPath + ".pending";
        var journalContents = File.ReadAllBytes(journalPath);
        byte[] pendingContents = "{}"u8.ToArray();
        SeedFiles.WriteNew(pendingPath, pendingContents);
        using var resumed = new SeedJournal(fixture.Root, id);
        var error = Assert.Throws<CliCommandException>(() => resumed.Load());
        Assert.Contains("interrupted journal write", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operation.json plus operation.json.pending", error.Message);
        Assert.Equal(journalContents, File.ReadAllBytes(journalPath));
        Assert.Equal(pendingContents, File.ReadAllBytes(pendingPath));

        var saveError = Assert.Throws<CliCommandException>(() => resumed.Save(state));
        Assert.Contains("operation.json and operation.json.pending", saveError.Message);
        Assert.Equal(journalContents, File.ReadAllBytes(journalPath));
        Assert.Equal(pendingContents, File.ReadAllBytes(pendingPath));
    }

    [Fact]
    public void JournalRejectsServerWithoutDurableCreationIntent()
    {
        using var fixture = new JournalFixture();
        var id = new string('b', 32);
        var state = State(id);
        state.Server = new("server", id, "nbg1", "cx22", "image", "203.0.113.10");
        using var journal = new SeedJournal(fixture.Root, id, true);
        journal.Save(state);
        Assert.Throws<CliCommandException>(() => journal.Load());
    }

    private static SeedState State(string id) => new()
    {
        Id = id,
        Plan = new(
            new("hetzner", "nbg1", "cx22", "console.example.com", "api.example.com",
                "workspaces.example.net", "municode-v1.2.3"),
            new("account", "credential-scope", "image", "linux/amd64", 2, 4096, 40,
                [new("vps", 10m, 0.01m, "EUR", "hourly", "none", "fixture")],
                DateTimeOffset.UtcNow, "fixture"),
            new("municode-v1.2.3", "municode", "https://github.com/muniperez/municode",
                new string('a', 40), "municode-1.2.3-linux-any.tar.gz",
                new string('b', 64), new string('c', 64), "github-sigstore"))
    };

    private sealed class JournalFixture : IDisposable
    {
        public JournalFixture()
        {
            Root = SeedTestDirectory.Create("journal");
        }

        public string Root { get; }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
