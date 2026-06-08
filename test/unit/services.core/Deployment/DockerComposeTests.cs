// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Test.Deployment;

/// <summary>
/// Verifies the self-hosted Docker Compose file declares every service required to run the
/// product end-to-end. Catches the regression where a worker or healthcheck definition is
/// accidentally removed from <c>deployment/server/docker/docker-compose.yml</c> — without it the
/// dashboard shows scheduled jobs but nothing executes.
/// </summary>
public sealed class DockerComposeTests
{
    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "machine-info.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate machine-info.slnx walking up from " + AppContext.BaseDirectory);
    }

    private static string ReadComposeFile()
    {
        string composePath = Path.Combine(FindRepoRoot(), "deployment", "server", "docker", "docker-compose.yml");

        return File.ReadAllText(composePath);
    }

    [Test]
    public async Task ComposeFile_DeclaresServicesWorker()
    {
        string text = ReadComposeFile();

        await Assert.That(text).Contains("services-worker:");
    }

    [Test]
    public async Task ComposeFile_ServicesWorker_UsesPublishedImage()
    {
        string text = ReadComposeFile();

        await Assert.That(text).Contains("ghcr.io/framlux/vord/services-worker:");
    }

    [Test]
    public async Task ComposeFile_ServicesWorker_DependsOnMigrationRunner()
    {
        string text = ReadComposeFile();

        // Anchor on the services-worker block; the depends_on entry following it must include
        // migration-runner with service_healthy. Lightweight assertion — the YAML is short.
        int workerIdx = text.IndexOf("services-worker:", StringComparison.Ordinal);
        int webIdx = text.IndexOf("web:", workerIdx, StringComparison.Ordinal);
        await Assert.That(workerIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(webIdx).IsGreaterThan(workerIdx);
        string workerBlock = text[workerIdx..webIdx];

        await Assert.That(workerBlock).Contains("migration-runner:");
        await Assert.That(workerBlock).Contains("service_healthy");
    }

    [Test]
    public async Task ComposeFile_ServicesWorker_HasHealthcheck()
    {
        string text = ReadComposeFile();
        int workerIdx = text.IndexOf("services-worker:", StringComparison.Ordinal);
        int webIdx = text.IndexOf("web:", workerIdx, StringComparison.Ordinal);
        string workerBlock = text[workerIdx..webIdx];

        await Assert.That(workerBlock).Contains("healthcheck:");
        await Assert.That(workerBlock).Contains("/healthz");
    }
}
