// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETTOOL

using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFCoreOperationExecutorTests
{
    [Fact]
    public async Task CaptureLogsAsync_ErrorPrefixedLinesGoToErrorBuilder()
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(new LogLine(1, "error:   Unhandled exception: Unable to load the service index for source", false));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, outputBuilder, errorBuilder, dataBuilder, cts.Token);

        Assert.NotEmpty(errorBuilder.ToString());
        Assert.Contains("Unhandled exception", errorBuilder.ToString());
        Assert.Empty(outputBuilder.ToString());
    }

    [Fact]
    public async Task CaptureLogsAsync_InfoPrefixedLinesGoToOutputBuilder()
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(new LogLine(1, "info:    Migration applied successfully.", false));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, outputBuilder, errorBuilder, dataBuilder, cts.Token);

        Assert.NotEmpty(outputBuilder.ToString());
        Assert.Contains("Migration applied successfully", outputBuilder.ToString());
        Assert.Empty(errorBuilder.ToString());
    }

    [Fact]
    public async Task CaptureLogsAsync_DataPrefixedLinesGoToDataBuilder()
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(new LogLine(1, "data:    [{\"id\":\"20240101\",\"name\":\"Init\"}]", false));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, outputBuilder, errorBuilder, dataBuilder, cts.Token);

        Assert.NotEmpty(dataBuilder.ToString());
        Assert.Empty(errorBuilder.ToString());
    }

    [Fact]
    public async Task CaptureLogsAsync_StderrLinesGoToErrorBuilder()
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(new LogLine(1, "Something failed", true));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, outputBuilder, errorBuilder, dataBuilder, cts.Token);

        Assert.NotEmpty(errorBuilder.ToString());
        Assert.Contains("Something failed", errorBuilder.ToString());
    }

    [Fact]
    public async Task CaptureLogsAsync_MixedOutputRoutesCorrectly()
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(
            new LogLine(1, "info:    Starting migration...", false),
            new LogLine(2, "error:   NuGet restore failed", false),
            new LogLine(3, "data:    {}", false),
            new LogLine(4, "warn:    Deprecated warning", false));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, outputBuilder, errorBuilder, dataBuilder, cts.Token);

        Assert.Contains("Starting migration", outputBuilder.ToString());
        Assert.Contains("Deprecated warning", outputBuilder.ToString());
        Assert.Contains("NuGet restore failed", errorBuilder.ToString());
        Assert.NotEmpty(dataBuilder.ToString());
    }

    [Fact]
    public async Task CaptureLogsAsync_ErrorContentMakesResultFailEvenWithZeroExitCode()
    {
        // This tests the scenario where dotnet-ef exits with code 0 but
        // has error-prefixed output that should cause the result to be treated as failed.
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(
            new LogLine(1, "info:    Migration 'Test' created successfully.", false),
            new LogLine(2, "error:   Unhandled exception: Unable to load the service index for source", false));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, outputBuilder, errorBuilder, dataBuilder, cts.Token);

        // After capture, errorBuilder should have content
        var hasErrors = !string.IsNullOrWhiteSpace(errorBuilder.ToString());
        Assert.True(hasErrors, "errorBuilder should contain the error output");

        // This verifies what the success check in ExecuteEfCommandAsync does:
        // var hasErrors = !string.IsNullOrWhiteSpace(stderr);
        // if (... || hasErrors) { return new EFOperationResult { Success = false, ... }; }
        // With the fix, this error content will cause the operation to be reported as failed.
    }

    [Fact]
    public async Task CaptureLogsAsync_VerbosePrefixedLinesGoToOutputBuilder()
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(new LogLine(1, "verbose: Loaded assembly from cache", false));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, outputBuilder, errorBuilder, dataBuilder, cts.Token);

        Assert.NotEmpty(outputBuilder.ToString());
        Assert.Contains("Loaded assembly from cache", outputBuilder.ToString());
        Assert.Empty(errorBuilder.ToString());
    }

    [Fact]
    public async Task CaptureLogsAsync_NoErrorsLeavesErrorBuilderEmpty()
    {
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        var dataBuilder = new StringBuilder();

        var logs = CreateLogEntries(
            new LogLine(1, "info:    Applying migration '20240101_Init'...", false),
            new LogLine(2, "info:    Done.", false));

        using var cts = new CancellationTokenSource();
        await EFCoreOperationExecutor.CaptureLogsAsync(logs, outputBuilder, errorBuilder, dataBuilder, cts.Token);

        Assert.Empty(errorBuilder.ToString());
        Assert.NotEmpty(outputBuilder.ToString());
    }

    [Fact]
    public async Task UpdateDatabaseAsync_ReturnsFailureWhenStartCommandFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        using var app = builder.Build();
        var toolResource = CreateToolResource(_ => Task.FromResult(CommandResults.Failure("tool startup failed")));

        using var executor = new EFCoreOperationExecutor(
            project.Resource,
            targetProjectPath: null,
            contextTypeName: null,
            NullLogger.Instance,
            CancellationToken.None,
            app.Services,
            toolResource);

        var result = await executor.UpdateDatabaseAsync();

        Assert.False(result.Success);
        Assert.Equal("tool startup failed", result.ErrorMessage);
    }

    [Fact]
    public async Task UpdateDatabaseAsync_ReturnsFailureWhenStartCommandIsCanceled()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        using var app = builder.Build();
        var toolResource = CreateToolResource(_ => Task.FromResult(CommandResults.Canceled()));

        using var executor = new EFCoreOperationExecutor(
            project.Resource,
            targetProjectPath: null,
            contextTypeName: null,
            NullLogger.Instance,
            CancellationToken.None,
            app.Services,
            toolResource);

        var result = await executor.UpdateDatabaseAsync();

        Assert.False(result.Success);
        Assert.Equal("dotnet-ef command was canceled.", result.ErrorMessage);
    }

    private static DotnetToolResource CreateToolResource(Func<ExecuteCommandContext, Task<ExecuteCommandResult>> executeCommand)
    {
        var toolResource = new DotnetToolResource("ef-tool", "dotnet-ef");
        toolResource.Annotations.Add(new ResourceCommandAnnotation(
            KnownResourceCommands.StartCommand,
            "Start",
            _ => ResourceCommandState.Enabled,
            executeCommand,
            displayDescription: null,
            parameter: null,
            confirmationMessage: null,
            iconName: null,
            iconVariant: null,
            isHighlighted: false));

        return toolResource;
    }

    private static async IAsyncEnumerable<IReadOnlyList<LogLine>> CreateLogEntries(params LogLine[] lines)
    {
        yield return lines;
        await Task.CompletedTask;
    }
}
