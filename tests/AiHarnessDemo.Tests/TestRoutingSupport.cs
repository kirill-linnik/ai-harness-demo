using AiHarnessDemo.Core.Domain;
using AiHarnessDemo.Data;
using AiHarnessDemo.Services;
using Microsoft.EntityFrameworkCore;

namespace AiHarnessDemo.Tests;

internal sealed class FixedModelRouter : IModelRouter
{
    public Task<RoutingDecision> SelectAsync(
        RoutingRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new RoutingDecision
        {
            FlowStepId = request.FlowStepId,
            TaskProfileId = Guid.NewGuid(),
            ModelCatalogSnapshotId = Guid.NewGuid(),
            SelectedModel = "fixture-model",
            SelectedEffort = "fixture-effort",
            Strategy = request.Strategy,
            PredictedQuality = 0.9,
            PredictedAcceptedTimeSeconds = 10,
            PredictedPremiumRequests = 1,
            Confidence = 0.5,
            Uncertainty = 0.5,
            Reason = "Deterministic test routing decision."
        });
}

internal static class TestRoutingSupport
{
    public static RoutingObservationRecorder Recorder(
        IDbContextFactory<HarnessDbContext> databaseFactory) =>
        new(databaseFactory, TimeProvider.System);
}
