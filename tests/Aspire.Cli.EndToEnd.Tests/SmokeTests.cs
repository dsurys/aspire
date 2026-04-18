// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end smoke tests for the core Aspire CLI starter scenarios and JSON contracts.
/// </summary>
public sealed class SmokeTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task CreateAndRunDefaultAspireStarterProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();

        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        // Prepare Docker environment (prompt counting, umask, env vars)
        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        // Install the Aspire CLI
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new project using aspire new with the default starter options.
        await auto.AspireNewAsync("AspireStarterApp", counter);

        // Run the project with aspire run and persist the transcript for failed-run debugging.
        await auto.AspireRunUntilReadyAsync(workspace);

        // Stop the running apphost with Ctrl+C
        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Creates a starter project, starts it with <c>aspire start --format json</c>,
    /// validates machine-readable JSON contracts, and verifies the web frontend endpoint
    /// responds with HTTP 200.
    /// </summary>
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task StarterJsonContractsAndEndpointsRespond()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.AspireNewAsync("EndpointTest", counter, useRedisCache: false);

        await auto.TypeAsync("cd EndpointTest");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter);
        await auto.PersistAspireStartJsonAsync(workspace, counter);
        CliE2ETestHelpers.AssertAspireStartJsonContract(workspace);

        await auto.TypeAsync("aspire wait webfrontend --status up --timeout 300");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("is up (running).", timeout: TimeSpan.FromMinutes(6));
        await auto.WaitForSuccessPromptAsync(counter);

        var psJsonPath = await auto.CaptureJsonOutputAsync(
            "aspire ps --format json",
            workspace,
            counter,
            "ps.json");
        CliE2ETestHelpers.AssertPsJsonContract(psJsonPath);

        await auto.AssertResourcesExistAsync(counter, "webfrontend", "apiservice");

        var webfrontendJsonPath = await auto.CaptureJsonOutputAsync(
            "aspire describe webfrontend --format json",
            workspace,
            counter,
            "webfrontend.json");
        var webUrl = CliE2ETestHelpers.GetFirstLocalhostUrlFromJsonFile(webfrontendJsonPath);
        await auto.AssertUrlRespondsAsync(webUrl, "webfrontend", counter);

        await auto.AspireStopAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
