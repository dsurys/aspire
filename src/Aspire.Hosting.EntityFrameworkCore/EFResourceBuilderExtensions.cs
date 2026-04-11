// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREDOTNETTOOL

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding EF Core migration management to projects.
/// </summary>
public static class EFResourceBuilderExtensions
{
    private static string GetShortTypeName(string? fullTypeName)
    {
        if (string.IsNullOrEmpty(fullTypeName))
        {
            return string.Empty;
        }
        var lastDotIndex = fullTypeName.LastIndexOf('.');
        return lastDotIndex >= 0 ? fullTypeName[(lastDotIndex + 1)..] : fullTypeName;
    }

    /// <summary>
    /// Adds EF Core migration management for a specific DbContext type identified by name.
    /// </summary>
    /// <param name="builder">The resource builder for the project.</param>
    /// <param name="name">The name of the migration resource.</param>
    /// <param name="contextTypeName">The fully qualified name of the DbContext type to manage migrations for.</param>
    /// <returns>An EF migration resource builder for chaining additional configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown if migrations for this context type have already been added.</exception>
    /// <remarks>
    /// <para>
    /// Multiple calls to this method with different context types are supported, allowing you to manage
    /// migrations for multiple DbContexts in the same project.
    /// </para>
    /// <para>
    /// This overload is useful when the DbContext type is not available at compile time, such as when
    /// using runtime-discovered context types.
    /// </para>
    /// </remarks>
    [AspireExport("addEFMigrationsWithContextType", MethodName = "addEFMigrations", Description = "Adds EF Core migration management for a specific DbContext type identified by name")]
    public static IResourceBuilder<EFMigrationResource> AddEFMigrations(
        this IResourceBuilder<ProjectResource> builder,
        [ResourceName] string name,
        string contextTypeName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(contextTypeName);

        return AddEFMigrationsCore(builder, name, contextTypeName, configureToolResource: null);
    }

    /// <summary>
    /// Adds EF Core migration management for a specific DbContext type identified by name.
    /// </summary>
    /// <param name="builder">The resource builder for the project.</param>
    /// <param name="name">The name of the migration resource.</param>
    /// <param name="contextTypeName">The fully qualified name of the DbContext type to manage migrations for.</param>
    /// <param name="configureToolResource">Optional callback to configure the dotnet-ef tool resource used for migrations.</param>
    /// <returns>An EF migration resource builder for chaining additional configuration.</returns>
    /// <exception cref="InvalidOperationException">Thrown if migrations for this context type have already been added.</exception>
    /// <remarks>
    /// <para>
    /// Multiple calls to this method with different context types are supported, allowing you to manage
    /// migrations for multiple DbContexts in the same project.
    /// </para>
    /// <para>
    /// This overload is useful when the DbContext type is not available at compile time, such as when
    /// using runtime-discovered context types.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Action<IResourceBuilder<DotnetToolResource>> callbacks are not ATS-compatible.")]
    public static IResourceBuilder<EFMigrationResource> AddEFMigrations(
        this IResourceBuilder<ProjectResource> builder,
        [ResourceName] string name,
        string contextTypeName,
        Action<IResourceBuilder<DotnetToolResource>>? configureToolResource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(contextTypeName);

        return AddEFMigrationsCore(builder, name, contextTypeName, configureToolResource);
    }

    /// <summary>
    /// Adds EF Core migration management for auto-detected DbContext types.
    /// </summary>
    /// <param name="builder">The resource builder for the project.</param>
    /// <param name="name">The name of the migration resource.</param>
    /// <returns>An EF migration resource builder for chaining additional configuration.</returns>
    [AspireExport]
    public static IResourceBuilder<EFMigrationResource> AddEFMigrations(
        this IResourceBuilder<ProjectResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return AddEFMigrationsCore(builder, name, contextTypeName: null, configureToolResource: null);
    }

    /// <summary>
    /// Adds EF Core migration management for auto-detected DbContext types.
    /// </summary>
    /// <param name="builder">The resource builder for the project.</param>
    /// <param name="name">The name of the migration resource.</param>
    /// <param name="configureToolResource">Optional callback to configure the dotnet-ef tool resource used for migrations.</param>
    /// <returns>An EF migration resource builder for chaining additional configuration.</returns>
    [AspireExportIgnore(Reason = "Action<IResourceBuilder<DotnetToolResource>> callbacks are not ATS-compatible.")]
    public static IResourceBuilder<EFMigrationResource> AddEFMigrations(
        this IResourceBuilder<ProjectResource> builder,
        [ResourceName] string name,
        Action<IResourceBuilder<DotnetToolResource>>? configureToolResource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return AddEFMigrationsCore(builder, name, contextTypeName: null, configureToolResource);
    }

    private static IResourceBuilder<EFMigrationResource> AddEFMigrationsCore(
        IResourceBuilder<ProjectResource> builder,
        string name,
        string? contextTypeName,
        Action<IResourceBuilder<DotnetToolResource>>? configureToolResource)
    {
        // Check for duplicate context types and null/non-null conflicts
        var existingMigrations = builder.ApplicationBuilder.Resources
            .OfType<EFMigrationResource>()
            .Where(r => r.ProjectResource == builder.Resource)
            .ToList();

        if (contextTypeName != null)
        {
            if (existingMigrations.Any(r => r.ContextTypeName == contextTypeName))
            {
                throw new InvalidOperationException(
                    $"The DbContext type '{GetShortTypeName(contextTypeName)}' has already been registered for EF migrations on resource '{builder.Resource.Name}'.");
            }

            if (existingMigrations.Any(r => r.ContextTypeName == null))
            {
                throw new InvalidOperationException(
                    $"Cannot add migrations for a specific DbContext type when auto-detected migrations have already been registered on resource '{builder.Resource.Name}'.");
            }
        }
        else
        {
            if (existingMigrations.Any())
            {
                throw new InvalidOperationException(
                    $"Cannot add auto-detected migrations when migrations for specific DbContext types have already been registered on resource '{builder.Resource.Name}'.");
            }
        }

        var migrationResource = new EFMigrationResource(name, builder.Resource, contextTypeName)
        {
            ConfigureToolResource = configureToolResource
        };

        var innerBuilder = builder.ApplicationBuilder
            .AddResource(migrationResource)
            .WithParentRelationship(builder)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "EFMigration",
                Properties = [],
                State = new ResourceStateSnapshot(KnownResourceStates.NotStarted, KnownResourceStateStyles.Info)
            })
            .WithIconName("Database")
            .WithPipelineStepFactory(CreateMigrationPipelineStep)
            .WithAnnotation(new PipelineConfigurationAnnotation(context =>
            {
                // Wire the apply-migration-bundle step to run after all other deploy
                // prerequisites have completed. The bundle must be applied only after the
                // database and any dependent services are fully deployed, regardless of the
                // deployment target (Azure, Docker Compose, Kubernetes, etc.).
                //
                // We find all steps that are required by the Deploy aggregation step and
                // make the apply step depend on them. This is deployment-target agnostic —
                // it works whether the deployment uses ProvisionInfrastructure, DeployCompute,
                // docker-compose-up, helm-deploy, or any other mechanism.
                var applyStepName = $"{migrationResource.Name}-apply-migration-bundle";

                var otherDeployPrereqs = context.Steps
                    .Where(s => s.Name != applyStepName && s.RequiredBySteps.Contains(WellKnownPipelineSteps.Deploy));

                var applySteps = context.Steps.Where(s => s.Name == applyStepName);
                applySteps.DependsOn(otherDeployPrereqs);
            }));

        AddEFMigrationCommands(innerBuilder, migrationResource, contextTypeName);

        return innerBuilder;
    }

    private static IEnumerable<PipelineStep> CreateMigrationPipelineStep(PipelineStepFactoryContext context)
    {
        if (context.Resource is not EFMigrationResource migrationResource
            || (!migrationResource.PublishAsMigrationScript && !migrationResource.PublishAsMigrationBundle))
        {
            return [];
        }

        var steps = new List<PipelineStep>();

        var scriptStepName = migrationResource.PublishAsMigrationScript
            ? $"{migrationResource.Name}-generate-migration-script"
            : null;

        if (migrationResource.PublishAsMigrationScript)
        {
            steps.Add(new PipelineStep
            {
                Name = scriptStepName!,
                Description = $"Generate EF Core migration SQL script for {migrationResource.Name}",
                Resource = migrationResource,
                RequiredBySteps = [WellKnownPipelineSteps.Publish],
                Action = stepContext => ExecutePublishPipelineOperationAsync(
                    stepContext, migrationResource, "migration script",
                    (executor, outputDir) =>
                    {
                        var outputPath = outputDir is not null
                            ? Path.Combine(outputDir, migrationResource.Name + ".sql")
                            : null;
                        return executor.GenerateMigrationScriptAsync(
                            outputPath,
                            migrationResource.ScriptIdempotent,
                            migrationResource.ScriptNoTransactions);
                    })
            });
        }

        if (migrationResource.PublishAsMigrationBundle)
        {
            var generateStepName = $"{migrationResource.Name}-generate-migration-bundle";

            steps.Add(new PipelineStep
            {
                Name = generateStepName,
                Description = $"Generate EF Core migration bundle for {migrationResource.Name}",
                Resource = migrationResource,
                DependsOnSteps = scriptStepName is not null ? [scriptStepName] : [], // Make sure these don't run in parallel as the underlying tool resource is not thread safe
                RequiredBySteps = [WellKnownPipelineSteps.Publish],
                Action = stepContext => ExecutePublishPipelineOperationAsync(
                    stepContext, migrationResource, "migration bundle",
                    (executor, outputDir) =>
                    {
                        var outputPath = outputDir is not null
                            ? Path.Combine(outputDir, GetBundleFileName(migrationResource))
                            : null;
                        return executor.GenerateMigrationBundleAsync(
                            outputPath,
                            migrationResource.BundleTargetRuntime,
                            migrationResource.BundleSelfContained);
                    })
            });

            if (migrationResource.BundleApplyOnDeploy)
            {
                steps.Add(new PipelineStep
                {
                    Name = $"{migrationResource.Name}-apply-migration-bundle",
                    Description = $"Apply EF Core migration bundle for {migrationResource.Name}",
                    Resource = migrationResource,
                    DependsOnSteps = [generateStepName],
                    RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                    Action = stepContext => ApplyMigrationBundleAsync(stepContext, migrationResource)
                });
            }
        }

        return steps;
    }

    private static async Task ExecutePublishPipelineOperationAsync(
        PipelineStepContext stepContext,
        EFMigrationResource migrationResource,
        string operationName,
        Func<EFCoreOperationExecutor, string?, Task<EFOperationResult>> executeOperation)
    {
        var logger = stepContext.Logger;
#pragma warning disable ASPIREPIPELINES004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var pipelineOutputService = stepContext.Services.GetRequiredService<IPipelineOutputService>();
#pragma warning restore ASPIREPIPELINES004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        using var executor = new EFCoreOperationExecutor(
            migrationResource.ProjectResource,
            migrationResource.MigrationsProjectPath,
            migrationResource.ContextTypeName,
            logger,
            stepContext.CancellationToken,
            stepContext.Services,
            migrationResource.ToolResource);

        var outputDir = Path.Combine(pipelineOutputService.GetOutputDirectory(), "efmigrations");
        Directory.CreateDirectory(outputDir);

        logger.LogInformation("Generating {Operation} for '{ResourceName}'...", operationName, migrationResource.Name);
        var result = await executeOperation(executor, outputDir).ConfigureAwait(false);

        if (result.Success)
        {
            logger.LogInformation("{Operation} generated successfully for '{ResourceName}'.", operationName, migrationResource.Name);
        }
        else
        {
            throw new InvalidOperationException($"Failed to generate {operationName} for '{migrationResource.Name}': {result.ErrorMessage}");
        }
    }

    private static string GetBundleFileName(EFMigrationResource migrationResource)
        => migrationResource.Name + (OperatingSystem.IsWindows() ? ".exe" : "");

    private static async Task ApplyMigrationBundleAsync(PipelineStepContext stepContext, EFMigrationResource migrationResource)
    {
        var logger = stepContext.Logger;
#pragma warning disable ASPIREPIPELINES004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var pipelineOutputService = stepContext.Services.GetRequiredService<IPipelineOutputService>();
#pragma warning restore ASPIREPIPELINES004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        var outputDir = Path.Combine(pipelineOutputService.GetOutputDirectory(), "efmigrations");
        var bundlePath = Path.Combine(outputDir, GetBundleFileName(migrationResource));

        if (!File.Exists(bundlePath))
        {
            throw new InvalidOperationException($"Migration bundle not found at '{bundlePath}'.");
        }

        // Resolve the connection string from a waited-on resource. Migration bundles require an explicit
        // connection string during deployment because the deployed environment doesn't have the AppHost's
        // local configuration available.
        var connectionString = await ResolveConnectionStringAsync(migrationResource, stepContext.CancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Cannot apply migration bundle for '{migrationResource.Name}': no connection string could be resolved. Add WaitFor(...) to a resource that provides one.");
        }

        logger.LogInformation("Applying migration bundle for '{ResourceName}'...", migrationResource.Name);

        var startInfo = new ProcessStartInfo(bundlePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--connection");
        startInfo.ArgumentList.Add(connectionString);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            startInfo.ArgumentList.Add("--verbose");
        }

        startInfo.ArgumentList.Add("--prefix-output");

        using var process = Process.Start(startInfo);

        if (process is null)
        {
            throw new InvalidOperationException(
                $"Failed to start migration bundle process for '{migrationResource.Name}'.");
        }

        var stderrBuilder = new StringBuilder();
        var stdoutTask = EFCoreOperationExecutor.StreamOutputAsync(
            process.StandardOutput, logger, isErrorOutput: false, captureBuilder: null, stepContext.CancellationToken);
        var stderrTask = EFCoreOperationExecutor.StreamOutputAsync(
            process.StandardError, logger, isErrorOutput: true, captureBuilder: stderrBuilder, stepContext.CancellationToken);

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await process.WaitForExitAsync(stepContext.CancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var errorMessage = stderrBuilder.ToString();
            throw new InvalidOperationException(
                $"Migration bundle failed for '{migrationResource.Name}' with exit code {process.ExitCode}: {errorMessage}");
        }

        logger.LogInformation("Migration bundle applied successfully for '{ResourceName}'.", migrationResource.Name);
    }

    private static async Task<ExecuteCommandResult> StartEfToolResourceAsync(ExecuteCommandContext context, DotnetToolResource toolResource)
    {
        try
        {
            var notificationService = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();

            var executableAnnotation = toolResource.Annotations.OfType<ExecutableAnnotation>().LastOrDefault();
            if (executableAnnotation is null)
            {
                return new ExecuteCommandResult
                {
                    Success = false,
                    Message = $"Executable configuration was not found for EF tool resource '{context.ResourceName}'."
                };
            }

            var executionContext = context.ServiceProvider.GetService<DistributedApplicationExecutionContext>()
                ?? new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

            var executionConfiguration = await ExecutionConfigurationBuilder.Create(toolResource)
                .WithEnvironmentVariablesConfig()
                .BuildAsync(executionContext, context.Logger, context.CancellationToken).ConfigureAwait(false);

            if (executionConfiguration.Exception is not null)
            {
                await notificationService.PublishUpdateAsync(toolResource, s => s with
                {
                    State = KnownResourceStates.FailedToStart
                }).ConfigureAwait(false);

                return new ExecuteCommandResult
                {
                    Success = false,
                    Message = executionConfiguration.Exception.Message
                };
            }

            var startInfo = new ProcessStartInfo(executableAnnotation.Command)
            {
                WorkingDirectory = executableAnnotation.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Build command-line arguments by directly invoking each annotation's Callback.
            // We intentionally bypass ExecutionConfigurationBuilder.WithArgumentsConfig() here
            // because it uses EvaluateOnceAsync which caches callback results. When the tool
            // resource is reused across sequential EF commands (e.g., script then bundle),
            // the cached BuildToolExecArguments callback does not re-populate the shared
            // callbackContext.Args list, so later annotations (the per-command EF args) run
            // against an empty list.
            if (toolResource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var cmdLineAnnotations))
            {
                IList<object> args = [];
                var callbackContext = new CommandLineArgsCallbackContext(args, toolResource, context.CancellationToken)
                {
                    Logger = context.Logger,
                    ExecutionContext = executionContext
                };

                foreach (var ann in cmdLineAnnotations)
                {
                    await ann.Callback(callbackContext).ConfigureAwait(false);
                }

                foreach (var arg in callbackContext.Args)
                {
                    startInfo.ArgumentList.Add(arg.ToString()!);
                }
            }

            foreach (var kvp in executionConfiguration.EnvironmentVariables)
            {
                startInfo.Environment[kvp.Key] = kvp.Value;
            }

            await notificationService.PublishUpdateAsync(toolResource, s => s with
            {
                State = KnownResourceStates.Starting,
                StartTimeStamp = DateTime.UtcNow,
                StopTimeStamp = null
            }).ConfigureAwait(false);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                await notificationService.PublishUpdateAsync(toolResource, s => s with
                {
                    State = KnownResourceStates.FailedToStart
                }).ConfigureAwait(false);

                return new ExecuteCommandResult
                {
                    Success = false,
                    Message = $"Failed to start EF tool resource '{context.ResourceName}'."
                };
            }

            await notificationService.PublishUpdateAsync(toolResource, s => s with
            {
                State = KnownResourceStates.Running
            }).ConfigureAwait(false);

            var resourceLoggerService = context.ServiceProvider.GetRequiredService<ResourceLoggerService>();
            var resourceLogger = resourceLoggerService.GetLogger(toolResource);

            var stderrBuilder = new StringBuilder();
            var stdoutTask = EFCoreOperationExecutor.StreamOutputAsync(
                process.StandardOutput, resourceLogger, isErrorOutput: false, captureBuilder: null, context.CancellationToken);
            var stderrTask = EFCoreOperationExecutor.StreamOutputAsync(
                process.StandardError, resourceLogger, isErrorOutput: true, captureBuilder: stderrBuilder, context.CancellationToken);

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await process.WaitForExitAsync(context.CancellationToken).ConfigureAwait(false);

            var finalState = process.ExitCode == 0 ? KnownResourceStates.Finished : KnownResourceStates.FailedToStart;
            await notificationService.PublishUpdateAsync(toolResource, s => s with
            {
                State = finalState,
                StopTimeStamp = DateTime.UtcNow
            }).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var errorMessage = stderrBuilder.ToString();
                return new ExecuteCommandResult
                {
                    Success = false,
                    Message = string.IsNullOrWhiteSpace(errorMessage)
                        ? $"EF tool resource '{context.ResourceName}' exited with code {process.ExitCode}."
                        : errorMessage
                };
            }

            return CommandResults.Success();
        }
        catch (OperationCanceledException)
        {
            return CommandResults.Canceled();
        }
        catch (Exception ex)
        {
            return new ExecuteCommandResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    private static async Task<string?> ResolveConnectionStringAsync(
        EFMigrationResource migrationResource,
        CancellationToken cancellationToken)
    {
        // Find IResourceWithConnectionString dependencies from the migration resource's WaitAnnotations
        if (!migrationResource.TryGetAnnotationsOfType<WaitAnnotation>(out var waitAnnotations))
        {
            return null;
        }

        foreach (var waitAnnotation in waitAnnotations)
        {
            if (waitAnnotation.Resource is IResourceWithConnectionString connectionStringResource)
            {
                var connectionString = await connectionStringResource.GetConnectionStringAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(connectionString))
                {
                    return connectionString;
                }
            }
        }

        return null;
    }

    private const string EFToolPackageId = "dotnet-ef";

    private static void AddEFMigrationCommands(
        IResourceBuilder<EFMigrationResource> migrationBuilder,
        EFMigrationResource migrationResource,
        string? contextTypeName)
    {
        var contextShortName = GetShortTypeName(contextTypeName);

        // Create hidden DotnetToolResource for running EF commands
        var toolName = $"ef-tool-{migrationResource.Name}";
        var startupProjectDir = Path.GetDirectoryName(migrationResource.ProjectResource.GetProjectMetadata().ProjectPath)!;
        var toolBuilder = migrationBuilder.ApplicationBuilder.AddDotnetTool(toolName, EFToolPackageId)
            .WithParentRelationship(migrationBuilder)
            .WithWorkingDirectory(startupProjectDir)
            .WithExplicitStart()
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "Tool",
                Properties = [],
                IsHidden = true
            });

        // Register the EF-specific start command. The tool resource is captured by the closure
        // so it works in both run mode (via resource commands) and publish mode (via pipeline steps).
        var toolResource = toolBuilder.Resource;
        toolBuilder.WithCommand(
            name: EFCoreOperationExecutor.ToolStartCommandName,
            displayName: "Start",
            executeCommand: context => StartEfToolResourceAsync(context, toolResource));

        migrationResource.ConfigureToolResource?.Invoke(toolBuilder);

        // Copy environment annotations from project resource to tool resource
        if (migrationResource.ProjectResource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var envCallbacks))
        {
            foreach (var callback in envCallbacks)
            {
                toolBuilder.WithAnnotation(callback);
            }
        }

        migrationResource.ToolResource = toolBuilder.Resource;

        migrationBuilder.WithCommand(
            name: "ef-database-update",
            displayName: "Update Database",
            executeCommand: context => ExecuteEFCommandAsync(
                context,
                "Update Database",
                migrationResource,
                executor => executor.UpdateDatabaseAsync()),
            commandOptions: new CommandOptions
            {
                Description = "Apply pending migrations to the database",
                IconName = "ArrowSync",
                IconVariant = IconVariant.Regular,
                UpdateState = context => GetCommandState(context, migrationResource)
            });

        migrationBuilder.WithCommand(
            name: "ef-database-drop",
            displayName: "Drop Database",
            executeCommand: context => ExecuteEFCommandAsync(
                context,
                "Drop Database",
                migrationResource,
                executor => executor.DropDatabaseAsync()),
            commandOptions: new CommandOptions
            {
                Description = "Delete the database",
                IconName = "Delete",
                IconVariant = IconVariant.Regular,
                ConfirmationMessage = "Are you sure you want to drop the database? This action cannot be undone.",
                UpdateState = context => GetCommandState(context, migrationResource)
            });

        migrationBuilder.WithCommand(
            name: "ef-database-reset",
            displayName: "Reset Database",
            executeCommand: context => ExecuteEFCommandAsync(
                context,
                "Reset Database",
                migrationResource,
                executor => executor.ResetDatabaseAsync()),
            commandOptions: new CommandOptions
            {
                Description = "Drop and recreate the database with all migrations applied",
                IconName = "ArrowReset",
                IconVariant = IconVariant.Regular,
                ConfirmationMessage = "Are you sure you want to reset the database? This will delete all data and cannot be undone.",
                UpdateState = context => GetCommandState(context, migrationResource)
            });

        migrationBuilder.WithCommand(
            name: "ef-migrations-add",
            displayName: "Add Migration...",
            executeCommand: context => ExecuteAddMigrationCommandAsync(context, migrationResource),
            commandOptions: new CommandOptions
            {
                Description = "Create a new migration. Note: The target project will need to be recompiled after adding a migration.",
                IconName = "Add",
                IconVariant = IconVariant.Regular,
                UpdateState = context => GetCommandState(context, migrationResource)
            });

        migrationBuilder.WithCommand(
            name: "ef-migrations-remove",
            displayName: "Remove Migration",
            executeCommand: context => ExecuteRemoveMigrationCommandAsync(context, migrationResource),
            commandOptions: new CommandOptions
            {
                Description = "Remove the last migration. Note: The target project will need to be recompiled after removing a migration.",
                IconName = "Subtract",
                IconVariant = IconVariant.Regular,
                UpdateState = context => GetCommandState(context, migrationResource)
            });

        migrationBuilder.WithCommand(
            name: "ef-database-status",
            displayName: "Get Database Status",
            executeCommand: context => ExecuteGetStatusCommandAsync(context, migrationResource),
            commandOptions: new CommandOptions
            {
                Description = "Show the current migration status of the database",
                IconName = "Info",
                IconVariant = IconVariant.Regular,
                UpdateState = context => GetCommandState(context, migrationResource)
            });
    }

    private static ResourceCommandState GetCommandState(UpdateCommandStateContext _, EFMigrationResource migrationResource)
    {
        if (migrationResource.RequiresRebuild || migrationResource.IsExecutingCommand)
        {
            return ResourceCommandState.Disabled;
        }

        return ResourceCommandState.Enabled;
    }

    private static Task<ExecuteCommandResult> ExecuteEFCommandAsync(
        ExecuteCommandContext context,
        string operationDisplayName,
        EFMigrationResource migrationResource,
        Func<EFCoreOperationExecutor, Task<EFOperationResult>> executeOperation) =>
        ExecuteWithStateManagementAsync(
            context,
            operationDisplayName,
            migrationResource,
            waitForDependencies: true,
            async (executor, logger, _) =>
            {
                var result = await executeOperation(executor).ConfigureAwait(false);

                if (result.Success)
                {
                    logger.LogInformation("EF Core {Operation} command completed successfully.", operationDisplayName);
                    return CommandResults.Success();
                }

                logger.LogError("EF Core {Operation} command failed: {Error}", operationDisplayName, result.ErrorMessage);
                return CommandResults.Failure(result.ErrorMessage);
            });

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only
    /// <summary>
    /// Common wrapper that handles state management and exception handling for EF commands.
    /// </summary>
    private static async Task<ExecuteCommandResult> ExecuteWithStateManagementAsync(
        ExecuteCommandContext context,
        string operationDisplayName,
        EFMigrationResource migrationResource,
        bool waitForDependencies,
        Func<EFCoreOperationExecutor, ILogger, IInteractionService?, Task<ExecuteCommandResult>> executeOperation)
    {
        var resourceLoggerService = context.ServiceProvider.GetRequiredService<ResourceLoggerService>();
        var resourceNotificationService = context.ServiceProvider.GetRequiredService<ResourceNotificationService>();
        var interactionService = context.ServiceProvider.GetService<IInteractionService>();
        var logger = resourceLoggerService.GetLogger(migrationResource);

        if (migrationResource.IsExecutingCommand)
        {
            return CommandResults.Failure($"Another command is already running on this resource.");
        }

        migrationResource.IsExecutingCommand = true;

        try
        {
            if (waitForDependencies)
            {
                await UpdateStateAsync(resourceNotificationService, migrationResource, KnownResourceStates.Waiting, KnownResourceStateStyles.Info).ConfigureAwait(false);
                await resourceNotificationService.WaitForDependenciesAsync(migrationResource, context.CancellationToken).ConfigureAwait(false);
            }

            await UpdateStateAsync(resourceNotificationService, migrationResource, KnownResourceStates.Running, KnownResourceStateStyles.Info).ConfigureAwait(false);

            logger.LogInformation("Executing EF Core {Operation} command...", operationDisplayName);

            using var executor = new EFCoreOperationExecutor(
                migrationResource.ProjectResource,
                migrationResource.MigrationsProjectPath,
                migrationResource.ContextTypeName,
                logger,
                context.CancellationToken,
                context.ServiceProvider,
                migrationResource.ToolResource);

            var result = await executeOperation(executor, logger, interactionService).ConfigureAwait(false);

            migrationResource.IsExecutingCommand = false;
            if (result.Success)
            {
                await UpdateStateAsync(resourceNotificationService, migrationResource, KnownResourceStates.Finished, KnownResourceStateStyles.Info).ConfigureAwait(false);
            }
            else
            {
                await UpdateStateAsync(resourceNotificationService, migrationResource, KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error).ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            migrationResource.IsExecutingCommand = false;
            await UpdateStateAsync(resourceNotificationService, migrationResource, KnownResourceStates.NotStarted, KnownResourceStateStyles.Info).ConfigureAwait(false);
            logger.LogWarning("EF Core {Operation} command was cancelled.", operationDisplayName);
            return CommandResults.Canceled();
        }
        catch (Exception ex)
        {
            migrationResource.IsExecutingCommand = false;
            await UpdateStateAsync(resourceNotificationService, migrationResource, KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error).ConfigureAwait(false);
            logger.LogError(ex, "EF Core {Operation} command failed with exception.", operationDisplayName);
            return CommandResults.Failure(ex);
        }
    }

    private static Task UpdateStateAsync(
        ResourceNotificationService resourceNotificationService,
        EFMigrationResource migrationResource,
        string state,
        string style) =>
        resourceNotificationService.PublishUpdateAsync(migrationResource, s => s with
        {
            State = new ResourceStateSnapshot(state, style)
        });

    private static Task<ExecuteCommandResult> ExecuteAddMigrationCommandAsync(
        ExecuteCommandContext context,
        EFMigrationResource migrationResource) =>
        ExecuteWithStateManagementAsync(
            context,
            "Add Migration",
            migrationResource,
            waitForDependencies: false,
            async (executor, logger, interaction) =>
            {
                string migrationName;
                if (interaction == null || !interaction.IsAvailable)
                {
                    migrationName = $"Migration_{DateTime.UtcNow:yyyyMMddHHmmss}";
                }
                else
                {
                    var inputResult = await interaction.PromptInputAsync(
                        title: "Add Migration",
                        message: "Enter the name for the new migration.",
                        inputLabel: "Migration Name",
                        placeHolder: "e.g. InitialCreate",
                        cancellationToken: context.CancellationToken).ConfigureAwait(false);

                    if (inputResult.Canceled || string.IsNullOrWhiteSpace(inputResult.Data?.Value))
                    {
                        // Throwing OperationCanceledException lets the wrapper handle state update
                        throw new OperationCanceledException();
                    }

                    migrationName = inputResult.Data.Value;
                }

                var result = await executor.AddMigrationAsync(
                    migrationName,
                    migrationResource.MigrationOutputDirectory,
                    migrationResource.MigrationNamespace).ConfigureAwait(false);

                if (result.Success)
                {
                    logger.LogInformation("Migration '{MigrationName}' created successfully.", migrationName);

                    migrationResource.RequiresRebuild = true;

                    if (interaction != null && interaction.IsAvailable)
                    {
                        await interaction.PromptNotificationAsync(
                            title: "Migration Created",
                            message: $"Migration '{migrationName}' was added successfully.\n\nThe target project needs to be recompiled before the migration can be applied.",
                            options: new NotificationInteractionOptions
                            {
                                Intent = MessageIntent.Warning,
                                ShowSecondaryButton = false
                            },
                            cancellationToken: context.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.LogWarning("Migration '{MigrationName}' was added successfully. The target project needs to be recompiled before the migration can be applied.", migrationName);
                    }

                    return CommandResults.Success();
                }

                logger.LogError("Add Migration command failed: {Error}", result.ErrorMessage);
                return CommandResults.Failure(result.ErrorMessage);
            });

    private static Task<ExecuteCommandResult> ExecuteRemoveMigrationCommandAsync(
        ExecuteCommandContext context,
        EFMigrationResource migrationResource) =>
        ExecuteWithStateManagementAsync(
            context,
            "Remove Migration",
            migrationResource,
            waitForDependencies: false,
            async (executor, logger, interactionService) =>
            {
                var result = await executor.RemoveMigrationAsync().ConfigureAwait(false);

                if (result.Success)
                {
                    logger.LogInformation("Migration removed successfully.");

                    migrationResource.RequiresRebuild = true;

                    if (interactionService != null && interactionService.IsAvailable)
                    {
                        await interactionService.PromptNotificationAsync(
                            title: "Migration Removed",
                            message: "The last migration was removed successfully.\n\nThe target project needs to be recompiled.",
                            options: new NotificationInteractionOptions
                            {
                                Intent = MessageIntent.Warning,
                                ShowSecondaryButton = false
                            },
                            cancellationToken: context.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.LogWarning("The last migration was removed successfully. The target project needs to be recompiled.");
                    }

                    return CommandResults.Success();
                }

                logger.LogError("Remove Migration command failed: {Error}", result.ErrorMessage);
                return CommandResults.Failure(result.ErrorMessage);
            });

    private static Task<ExecuteCommandResult> ExecuteGetStatusCommandAsync(
        ExecuteCommandContext context,
        EFMigrationResource migrationResource) =>
        ExecuteWithStateManagementAsync(
            context,
            "Get Database Status",
            migrationResource,
            waitForDependencies: false,
            async (executor, logger, interactionService) =>
            {
                var result = await executor.GetDatabaseStatusAsync().ConfigureAwait(false);

                if (result.Success)
                {
                    if (interactionService != null && interactionService.IsAvailable)
                    {
                        await interactionService.PromptMessageBoxAsync(
                            title: "Database Migration Status",
                            message: result.Output ?? "No migration information available.",
                            options: new MessageBoxInteractionOptions
                            {
                                Intent = MessageIntent.Information,
                                ShowSecondaryButton = false,
                                EnableMessageMarkdown = true
                            },
                            cancellationToken: context.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        logger.LogInformation("Database status:\n{Status}", result.Output);
                    }

                    return CommandResults.Success();
                }

                logger.LogError("Get Database Status command failed: {Error}", result.ErrorMessage);
                return CommandResults.Failure(result.ErrorMessage);
            });
#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only
}
