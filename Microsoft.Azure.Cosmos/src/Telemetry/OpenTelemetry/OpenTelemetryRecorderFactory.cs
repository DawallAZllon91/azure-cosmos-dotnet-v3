﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// ------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Telemetry
{
    using System;
    using System.Diagnostics;
    using global::Azure.Core;
    using Microsoft.Azure.Cosmos.Tracing;

    /// <summary>
    /// This class is used to generate Activities with Azure.Cosmos.Operation Source Name
    /// </summary>
    internal static class OpenTelemetryRecorderFactory
    {
        /// <summary>
        /// Singleton to make sure we only have one instance of the DiagnosticScopeFactory and pattern matching of listener happens only once
        /// </summary>
        private static readonly Lazy<DiagnosticScopeFactory> LazyScopeFactory = new Lazy<DiagnosticScopeFactory>(
            valueFactory: () => new DiagnosticScopeFactory(
                           clientNamespace: $"{OpenTelemetryAttributeKeys.DiagnosticNamespace}",
                           resourceProviderNamespace: OpenTelemetryAttributeKeys.ResourceProviderNamespace,
                           isActivityEnabled: true,
                           suppressNestedClientActivities: true, 
                           isStable: false),
            isThreadSafe: true);

        public static OpenTelemetryCoreRecorder CreateRecorder(string operationName,
            string containerName,
            string databaseName,
            Documents.OperationType operationType,
            RequestOptions requestOptions, 
            ITrace trace,
            CosmosClientContext clientContext)
        {
            OpenTelemetryCoreRecorder openTelemetryRecorder = default;
            if (clientContext is { ClientOptions.CosmosClientTelemetryOptions.DisableDistributedTracing: false })
            {
                // If there is no source then it will return default otherwise a valid diagnostic scope
                DiagnosticScope scope = LazyScopeFactory.Value.CreateScope(name: $"{OpenTelemetryAttributeKeys.OperationPrefix}.{operationName}",
                                 kind: clientContext.ClientOptions.ConnectionMode == ConnectionMode.Gateway ? ActivityKind.Internal : ActivityKind.Client);

                // Need a parent activity id associated with the operation which is logged in diagnostics and used for tracing purpose.
                // If there are listeners at operation level then scope is enabled and it tries to create activity.
                // However, if available listeners are not subscribed to operation level event then it will lead to scope being enabled but no activity is created.
                if (scope.IsEnabled)
                {
                    scope.SetDisplayName($"{operationName} {containerName}");

                    openTelemetryRecorder = OpenTelemetryCoreRecorder.CreateOperationLevelParentActivity(
                        operationScope: scope,
                        operationName: operationName,
                        containerName: containerName,
                        databaseName: databaseName,
                        operationType: operationType,
                        clientContext: clientContext,
                        config: requestOptions?.CosmosThresholdOptions ?? clientContext.ClientOptions?.CosmosClientTelemetryOptions.CosmosThresholdOptions);
                }

                // Safety check as diagnostic logs should not break the code.
                if (Activity.Current?.TraceId != null)
                {
                    trace.AddDatum("DistributedTraceId", Activity.Current.TraceId);
                }
            }
            return openTelemetryRecorder;
        }
    }
}
