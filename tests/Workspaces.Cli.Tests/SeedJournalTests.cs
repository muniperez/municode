// SPDX-License-Identifier: MPL-2.0

using Municode.Cli.Commands;
using Municode.Cli.Seed;

namespace Workspaces.Cli.Tests;

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
        File.WriteAllText(Path.Combine(fixture.Root, "seed", id, "operation.pending"), "{}");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(fixture.Root, "seed", id, "operation.pending"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var resumed = new SeedJournal(fixture.Root, id);
        var error = Assert.Throws<CliCommandException>(() => resumed.Load());
        Assert.Contains("interrupted journal write", error.Message, StringComparison.OrdinalIgnoreCase);
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
                "workspaces.example.net", "workspaces-v1.2.3"),
            new("account", "credential-scope", "image", "linux/amd64", 2, 4096, 40,
                [new("vps", 10m, 0.01m, "EUR", "hourly", "none", "fixture")],
                DateTimeOffset.UtcNow, "fixture"),
            new("workspaces-v1.2.3", "municode-workspaces", "https://github.com/muniperez/municode",
                new string('a', 40), "municode-workspaces-1.2.3-linux-any.tar.gz",
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
