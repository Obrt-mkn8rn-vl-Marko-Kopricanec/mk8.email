namespace mk8.email.Infrastructure.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability", "CA1515",
    Justification = "MSTest discovers this public test class by reflection.")]
public sealed class AidePolicyTests
{
    private static readonly string[] RequiredNonRecursiveExclusions =
    [
        "-/tmp/",
        "-/var/tmp/",
        @"-/home/codex/\.cache$",
        @"-/home/codex/\.codex$",
        @"-/home/codex/\.codex-devops-backups$",
        @"-/home/codex/\.dotnet$",
        @"-/home/codex/\.local/share$",
        @"-/home/codex/\.local/state$",
        @"-/home/codex/\.nuget/packages$",
        @"-/home/codex/\.tmp$",
        "-/home/codex/Codex$",
        @"-/home/mkn8rn/\.cache$",
        @"-/home/mkn8rn/\.local/state/wireplumber$",
        @"-/home/mkn8rn/\.bash_history$",
        "-/root/BraloGruppeColdArchive$",
    ];

    private static readonly HashSet<string> AllowedDeviceExclusions =
    [
        "-/dev/char",
        "-/dev/input/by-id",
        "-/dev/input/by-path",
    ];

    [TestMethod]
    public void PolicyExcludesOnlyTransientHomeTrees()
    {
        var rules = ReadRules();

        foreach (var requiredRule in RequiredNonRecursiveExclusions)
            CollectionAssert.Contains(rules, requiredRule);

        var homeRules = rules.Where(rule =>
                rule.StartsWith("-/home/", StringComparison.Ordinal) ||
                rule.StartsWith("!/home/", StringComparison.Ordinal))
            .ToArray();
        var expectedHomeRules = RequiredNonRecursiveExclusions
            .Where(rule => rule.StartsWith("-/home/", StringComparison.Ordinal))
            .ToArray();
        CollectionAssert.AreEquivalent(expectedHomeRules, homeRules);
        CollectionAssert.DoesNotContain(rules, "-/home/codex$");
        CollectionAssert.DoesNotContain(rules, "-/home$");
    }

    [TestMethod]
    public void PolicyPreservesSystemAndDeviceCoverage()
    {
        var rules = ReadRules();
        var forbiddenSystemRoots = new[]
        {
            "/", "/bin", "/boot", "/etc", "/lib", "/lib32", "/lib64", "/opt",
            "/root", "/sbin", "/srv", "/usr", "/var",
        };

        foreach (var root in forbiddenSystemRoots)
        {
            foreach (var selector in new[] { "-", "!" })
            {
                Assert.IsFalse(
                    rules.Contains($"{selector}{root}") ||
                    rules.Contains($"{selector}{root}$") ||
                    rules.Contains($"{selector}{root}/"),
                    $"The AIDE policy must not exclude the system root {root}.");
            }
        }

        var deviceRules = rules.Where(rule =>
                rule.StartsWith("-/dev", StringComparison.Ordinal) ||
                rule.StartsWith("!/dev", StringComparison.Ordinal))
            .ToArray();
        CollectionAssert.AreEquivalent(AllowedDeviceExclusions.ToArray(), deviceRules);
    }

    [TestMethod]
    public void PolicyDoesNotTraverseProtectedArchive()
    {
        var rules = ReadRules();

        CollectionAssert.Contains(rules, "-/root/BraloGruppeColdArchive$");
        Assert.IsFalse(
            rules.Any(rule => rule.Contains("20260909", StringComparison.Ordinal)),
            "The policy must stop at the protected archive parent.");
    }

    [TestMethod]
    public void PolicyKeepsTemporaryDirectoryMetadata()
    {
        var rules = ReadRules();

        CollectionAssert.Contains(rules, "-/tmp/");
        CollectionAssert.Contains(rules, "-/var/tmp/");
        CollectionAssert.DoesNotContain(rules, "-/tmp$");
        CollectionAssert.DoesNotContain(rules, "-/var/tmp$");
    }

    private static string[] ReadRules()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "DeploymentAssets", "90_mk8_dynamic_data");
        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
    }
}
