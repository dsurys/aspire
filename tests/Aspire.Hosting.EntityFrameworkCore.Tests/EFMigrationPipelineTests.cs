// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFMigrationPipelineTests
{
    [Fact]
    public async Task BundleOnlyProducesGenerateStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        var generateStep = Assert.Single(steps);
        Assert.Equal("mymigrations-generate-migration-bundle", generateStep.Name);
        Assert.Contains(WellKnownPipelineSteps.Publish, generateStep.RequiredBySteps);
        Assert.Empty(generateStep.DependsOnSteps);
    }

    [Fact]
    public async Task BundleWithApplyOnDeployProducesGenerateAndApplySteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle(applyOnDeploy: true);

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        Assert.Equal(2, steps.Count);

        var generateStep = Assert.Single(steps, s => s.Name == "mymigrations-generate-migration-bundle");
        Assert.Contains(WellKnownPipelineSteps.Publish, generateStep.RequiredBySteps);

        var applyStep = Assert.Single(steps, s => s.Name == "mymigrations-apply-migration-bundle");
        Assert.Contains(WellKnownPipelineSteps.Deploy, applyStep.RequiredBySteps);
        Assert.Contains(generateStep.Name, applyStep.DependsOnSteps);
    }

    [Fact]
    public async Task ScriptOnlyProducesScriptStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript();

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        var scriptStep = Assert.Single(steps);
        Assert.Equal("mymigrations-generate-migration-script", scriptStep.Name);
        Assert.Contains(WellKnownPipelineSteps.Publish, scriptStep.RequiredBySteps);
    }

    [Fact]
    public async Task ScriptAndBundleProducesAllStepsWithDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript()
            .PublishAsMigrationBundle();

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        Assert.Equal(2, steps.Count);

        var scriptStep = Assert.Single(steps, s => s.Name == "mymigrations-generate-migration-script");
        var bundleStep = Assert.Single(steps, s => s.Name == "mymigrations-generate-migration-bundle");

        // Bundle generation depends on script to avoid parallel tool usage
        Assert.Contains(scriptStep.Name, bundleStep.DependsOnSteps);
    }

    [Fact]
    public async Task ScriptAndBundleWithApplyProducesThreeSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript()
            .PublishAsMigrationBundle(applyOnDeploy: true);

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        Assert.Equal(3, steps.Count);

        var scriptStep = Assert.Single(steps, s => s.Name == "mymigrations-generate-migration-script");
        var generateStep = Assert.Single(steps, s => s.Name == "mymigrations-generate-migration-bundle");
        var applyStep = Assert.Single(steps, s => s.Name == "mymigrations-apply-migration-bundle");

        Assert.Contains(scriptStep.Name, generateStep.DependsOnSteps);
        Assert.Contains(generateStep.Name, applyStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Deploy, applyStep.RequiredBySteps);
    }

    [Fact]
    public async Task NoPublishOptionsProducesNoSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var steps = await CreateStepsAsync(builder, migrations.Resource);

        Assert.Empty(steps);
    }

    [Fact]
    public async Task ConfigurationAnnotationWiresApplyStepToDeployPrereqs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle(applyOnDeploy: true);

        var migrationSteps = await CreateStepsAsync(builder, migrations.Resource);
        var applyStep = Assert.Single(migrationSteps, s => s.Name == "mymigrations-apply-migration-bundle");

        // Simulate other steps that are RequiredBy Deploy (e.g., docker-compose-up, helm-deploy)
        var otherDeployPrereq = new PipelineStep
        {
            Name = "docker-compose-up-env",
            Action = _ => Task.CompletedTask,
            RequiredBySteps = [WellKnownPipelineSteps.Deploy]
        };

        // Build the full step list for configuration
        var allSteps = new List<PipelineStep>(migrationSteps) { otherDeployPrereq };

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var configAnnotation = Assert.Single(migrations.Resource.Annotations.OfType<PipelineConfigurationAnnotation>());
        var configContext = new PipelineConfigurationContext
        {
            Services = serviceProvider,
            Steps = allSteps,
            Model = serviceProvider.GetRequiredService<DistributedApplicationModel>()
        };

        await configAnnotation.Callback(configContext);

        // The apply step should now depend on the other deploy prerequisite
        Assert.Contains(otherDeployPrereq.Name, applyStep.DependsOnSteps);
    }

    [Fact]
    public async Task ConfigurationAnnotationDoesNotWireWhenNoApplyStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        // Bundle without applyOnDeploy — no apply step is generated
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        var migrationSteps = await CreateStepsAsync(builder, migrations.Resource);
        Assert.DoesNotContain(migrationSteps, s => s.Name == "mymigrations-apply-migration-bundle");

        var otherDeployPrereq = new PipelineStep
        {
            Name = "docker-compose-up-env",
            Action = _ => Task.CompletedTask,
            RequiredBySteps = [WellKnownPipelineSteps.Deploy]
        };

        var allSteps = new List<PipelineStep>(migrationSteps) { otherDeployPrereq };

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var configAnnotation = Assert.Single(migrations.Resource.Annotations.OfType<PipelineConfigurationAnnotation>());
        var configContext = new PipelineConfigurationContext
        {
            Services = serviceProvider,
            Steps = allSteps,
            Model = serviceProvider.GetRequiredService<DistributedApplicationModel>()
        };

        // Should not throw when apply step is absent
        await configAnnotation.Callback(configContext);

        // The other deploy prereq should not have gained any new dependencies
        Assert.Empty(otherDeployPrereq.DependsOnSteps);
    }

    [Fact]
    public async Task ConfigurationAnnotationSkipsApplyStepSelfDependency()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle(applyOnDeploy: true);

        var migrationSteps = await CreateStepsAsync(builder, migrations.Resource);
        var applyStep = Assert.Single(migrationSteps, s => s.Name == "mymigrations-apply-migration-bundle");

        // The apply step itself is RequiredBy Deploy — it should NOT depend on itself
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var configAnnotation = Assert.Single(migrations.Resource.Annotations.OfType<PipelineConfigurationAnnotation>());
        var configContext = new PipelineConfigurationContext
        {
            Services = serviceProvider,
            Steps = migrationSteps,
            Model = serviceProvider.GetRequiredService<DistributedApplicationModel>()
        };

        await configAnnotation.Callback(configContext);

        Assert.DoesNotContain(applyStep.Name, applyStep.DependsOnSteps);
    }

    [Fact]
    public void PublishAsMigrationBundleAddsConfigurationAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        var configAnnotations = migrations.Resource.Annotations.OfType<PipelineConfigurationAnnotation>().ToList();
        Assert.Single(configAnnotations);
    }

    [Fact]
    public void ConfigurationAnnotationIsAlwaysPresent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        // Configuration annotation should be present even without bundle mode,
        // because it's added unconditionally for pipeline wiring
        var configAnnotations = migrations.Resource.Annotations.OfType<PipelineConfigurationAnnotation>().ToList();
        Assert.Single(configAnnotations);
    }

    [Fact]
    public void WaitForConnectionStringResourceAddsWaitAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db = builder.AddResource(new TestDatabaseResource("mydb"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db);

        var waitAnnotations = migrations.Resource.Annotations.OfType<WaitAnnotation>().ToList();
        Assert.Single(waitAnnotations);

        // The waited-on resource should be the IResourceWithConnectionString
        var waitedResource = waitAnnotations[0].Resource;
        Assert.IsAssignableFrom<IResourceWithConnectionString>(waitedResource);
        Assert.Equal("mydb", waitedResource.Name);
    }

    [Fact]
    public void WaitForMultipleResourcesAddsMultipleAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var db1 = builder.AddResource(new TestDatabaseResource("db1"));
        var db2 = builder.AddResource(new TestDatabaseResource("db2"));
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .WaitFor(db1)
            .WaitFor(db2);

        var waitAnnotations = migrations.Resource.Annotations.OfType<WaitAnnotation>().ToList();
        Assert.Equal(2, waitAnnotations.Count);
    }

    [Fact]
    public void MigrationWithoutWaitAnnotationsHasNoConnectionStringDependency()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        var waitAnnotations = migrations.Resource.Annotations.OfType<WaitAnnotation>().ToList();
        Assert.Empty(waitAnnotations);
    }

    [Fact]
    public void AddEFMigrations_HiddenToolResourceHasDedicatedStartCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!);

        var startCommand = migrations.Resource.ToolResource.Annotations
            .OfType<ResourceCommandAnnotation>()
            .SingleOrDefault(a => a.Name == EFCoreOperationExecutor.ToolStartCommandName);

        Assert.NotNull(startCommand);
    }

    [Fact]
    public void PublishAsMigrationBundleSetsResourceProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle(targetRuntime: "linux-x64", selfContained: true, applyOnDeploy: true);

        Assert.True(migrations.Resource.PublishAsMigrationBundle);
        Assert.Equal("linux-x64", migrations.Resource.BundleTargetRuntime);
        Assert.True(migrations.Resource.BundleSelfContained);
        Assert.True(migrations.Resource.BundleApplyOnDeploy);
    }

    [Fact]
    public void PublishAsMigrationBundleDefaultsApplyOnDeployToFalse()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationBundle();

        Assert.True(migrations.Resource.PublishAsMigrationBundle);
        Assert.False(migrations.Resource.BundleApplyOnDeploy);
    }

    [Fact]
    public void PublishAsMigrationScriptSetsResourceProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("myproject");
        var migrations = project.AddEFMigrations("mymigrations", typeof(TestDbContext).FullName!)
            .PublishAsMigrationScript(idempotent: true, noTransactions: true);

        Assert.True(migrations.Resource.PublishAsMigrationScript);
        Assert.True(migrations.Resource.ScriptIdempotent);
        Assert.True(migrations.Resource.ScriptNoTransactions);
        Assert.False(migrations.Resource.PublishAsMigrationBundle);
    }

    private static async Task<List<PipelineStep>> CreateStepsAsync(
        IDistributedApplicationTestingBuilder builder,
        EFMigrationResource migrationResource)
    {
        var annotation = Assert.Single(migrationResource.Annotations.OfType<PipelineStepAnnotation>());
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var pipelineContext = new PipelineContext(
            serviceProvider.GetRequiredService<DistributedApplicationModel>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            serviceProvider,
            NullLogger.Instance,
            CancellationToken.None);

        return (await annotation.CreateStepsAsync(new PipelineStepFactoryContext
        {
            PipelineContext = pipelineContext,
            Resource = migrationResource
        })).ToList();
    }

    // Test classes for DbContext types
    private sealed class TestDbContext { }

    /// <summary>
    /// A minimal test resource that implements IResourceWithConnectionString and IResourceWithWaitSupport.
    /// </summary>
    private sealed class TestDatabaseResource(string name) : Resource(name), IResourceWithConnectionString, IResourceWithWaitSupport
    {
        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"Host=localhost;Database={Name}");
    }
}
