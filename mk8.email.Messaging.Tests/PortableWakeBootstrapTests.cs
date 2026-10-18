using mk8.email.Wake;

namespace mk8.email.Messaging.Tests;

[TestClass]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1812", Justification = "MSTest discovers this internal bootstrap fixture by reflection.")]
internal sealed class PortableWakeBootstrapTests
{
    [TestMethod]
    [DataRow("--serve")]
    [DataRow("--serve-worker")]
    [DataRow("--probe")]
    [DataRow("--probe-db")]
    public void ModesContainNoServiceConfigurationValues(string mode)
    {
        Assert.IsNotNull(WakeBootstrap.ParseMode([mode]));
        Assert.IsNull(WakeBootstrap.ParseMode([mode, "retired-connection-file"]));
        Assert.IsNull(WakeBootstrap.ParseMode([mode, "retired-connection-file", "retired-worker-config"]));
        Assert.IsNull(WakeBootstrap.ParseMode([mode, "--without-jmap"]));
    }

    [TestMethod]
    public void WorkerStartUsesOnlyExplicitExecutablePathsAndItsOwnRoleRoot()
    {
        var root = Directory.CreateTempSubdirectory("mk8-wake-start-");
        try
        {
            var dotnet = Path.Combine(root.FullName, "dotnet");
            var worker = Path.Combine(root.FullName, "worker.dll");
            File.WriteAllText(dotnet, "inert fixture, never executed");
            File.WriteAllText(worker, "inert fixture, never executed");
            var start = new WorkerProcessTrigger(dotnet, worker).CreateStartInfo();
            Assert.AreEqual(dotnet, start.FileName, StringComparer.Ordinal);
            Assert.AreEqual(root.FullName, start.WorkingDirectory, StringComparer.Ordinal);
            Assert.AreSequenceEqual(new[] { worker, "--drain" }, start.ArgumentList, StringComparer.Ordinal);
            Assert.IsFalse(start.UseShellExecute);
            Assert.IsEmpty(start.Environment);
        }
        finally { root.Delete(recursive: true); }
    }
}
