﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos.FaultInjection
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Cosmos.Common;
    using Microsoft.Azure.Cosmos.FaultInjection.implementataion;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Cosmos.Tracing;
    using Microsoft.Azure.Documents;

    internal class FaultInjectionRuleProcessor
    {
        private readonly ConnectionMode connectionMode;
        private readonly CollectionCache collectionCache;
        private readonly GlobalEndpointManager globalEndpointManager;
        private readonly GlobalAddressResolver addressResolver;
        private readonly RetryOptions retryOptions;
        private readonly IRoutingMapProvider routingMapProvider;
        private readonly FaultInjectionApplicationContext applicationContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="FaultInjectionRuleProcessor"/> class.
        /// </summary>
        /// <param name="connectionMode"></param>
        /// <param name="collectionCache"></param>
        /// <param name="globalEndpointManager"></param>
        /// <param name="addressResolver"></param>
        /// <param name="retryOptions"></param>
        /// <param name="routingMapProvider"></param>
        /// <param name="applicationContext"></param>
        public FaultInjectionRuleProcessor(
            ConnectionMode connectionMode,
            CollectionCache collectionCache,
            GlobalEndpointManager globalEndpointManager,
            GlobalAddressResolver addressResolver,
            RetryOptions retryOptions,
            IRoutingMapProvider routingMapProvider,
            FaultInjectionApplicationContext applicationContext)
        {
            this.connectionMode = connectionMode;
            this.collectionCache = collectionCache ?? throw new ArgumentNullException(nameof(collectionCache));
            this.globalEndpointManager = globalEndpointManager ?? throw new ArgumentNullException(nameof(globalEndpointManager));
            this.addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
            this.retryOptions = retryOptions ?? throw new ArgumentNullException(nameof(retryOptions));
            this.routingMapProvider = routingMapProvider ?? throw new ArgumentNullException(nameof(routingMapProvider));
            this.applicationContext = applicationContext ?? throw new ArgumentNullException(nameof(applicationContext));
        }

        public async Task<IFaultInjectionRuleInternal> ProcessFaultInjectionRule(FaultInjectionRule rule)
        {
            _ = rule ?? throw new ArgumentNullException(nameof(rule));

            this.ValidateRule(rule);
            return await this.GetEffectiveRule(rule);

        }

        private void ValidateRule(FaultInjectionRule rule)
        {
            if (rule.GetCondition().GetConnectionType() == FaultInjectionConnectionType.Direct
                && this.connectionMode != ConnectionMode.Direct)
            {
                throw new ArgumentException("Direct connection mode is not supported when client is not in direct mode");
            }
        }

        private async Task<IFaultInjectionRuleInternal> GetEffectiveRule(FaultInjectionRule rule)
        {
            if (rule.GetResult().GetType() == typeof(FaultInjectionServerErrorRule))
            {
                return await this.GetEffectiveServerErrorRule(rule);
            }

            if (rule.GetResult().GetType() == typeof(FaultInjectionConnectionErrorRule))
            {
                return await this.GetEffectiveConnectionErrorRule(rule);
            }

            throw new Exception($"{rule.GetResult().GetType()} is not supported");
        }

        private async Task<IFaultInjectionRuleInternal> GetEffectiveServerErrorRule(FaultInjectionRule rule)
        {
            FaultInjectionServerErrorType errorType = ((FaultInjectionServerErrorResult)rule.GetResult()).GetServerErrorType();
            FaultInjectionConditionInternal effectiveCondition = new FaultInjectionConditionInternal();
            if (this.CanErrorLimitToOperation(errorType))
            {
                effectiveCondition.SetOperationType(this.GetEffectiveOperationType(rule.GetCondition().GetOperationType()));
            }

            if (!string.IsNullOrEmpty(rule.GetCondition().GetContainerResourceId()))
            {
                effectiveCondition.SetContainerResourceId(rule.GetCondition().GetContainerResourceId());
            }

            List<Uri> regionEndpoints = this.GetRegionEndpoints(rule.GetCondition());

            if (string.IsNullOrEmpty(rule.GetCondition().GetRegion()))
            {
                //If region is not specified, add default endpoint
                List<Uri> regionEndpointsWithDefault = new List<Uri>(regionEndpoints)
                {
                    this.globalEndpointManager.GetDefaultEndpoint()
                };
                effectiveCondition.SetRegionEndpoints(regionEndpointsWithDefault);
            }
            else
            {
                effectiveCondition.SetRegionEndpoints(regionEndpoints);
            }

            List<Uri> effectiveAddresses = await BackoffRetryUtility<List<Uri>>.ExecuteAsync(
                () => this.ResolvePhyicalAddresses(
                    regionEndpoints,
                    rule.GetCondition(),
                    this.IsWriteOnly(rule.GetCondition())),
                new FaultInjectionRuleProcessorRetryPolicy(this.retryOptions));

            if (!this.CanErrorLimitToOperation(errorType))
            {
                effectiveAddresses = effectiveAddresses.Select(address => 
                    new Uri(string.Format(
                        "{0}://{1}:{2}/",
                        address.Scheme.ToString(),
                        address.Host.ToString(),
                        address.Port.ToString()))).ToList();
            }

            effectiveCondition.SetAddresses(effectiveAddresses);

            FaultInjectionServerErrorResult result = (FaultInjectionServerErrorResult)rule.GetResult();

            return new FaultInjectionServerErrorRule(
                id: rule.GetId(),
                enabled: rule.IsEnabled(),
                delay: rule.GetStartDelay(),
                duration: rule.GetDuration(),
                hitLimit: rule.GetHitLimit(),
                connectionType: rule.GetCondition().GetConnectionType(),
                condition: effectiveCondition,
                result: new FaultInjectionServerErrorResultInternal(
                    result.GetServerErrorType(),
                    result.GetTimes(),
                    result.GetDelay(),
                    result.GetSuppressServiceRequests(),
                    this.applicationContext));

        }

        private async Task<IFaultInjectionRuleInternal> GetEffectiveConnectionErrorRule(FaultInjectionRule rule)
        {
            List<Uri> regionEndpoints = this.GetRegionEndpoints(rule.GetCondition());

            List<Uri> resolvedPhysicalAdresses = await this.ResolvePhyicalAddresses(
                regionEndpoints,
                rule.GetCondition(),
                this.IsWriteOnly(rule.GetCondition()));

            resolvedPhysicalAdresses.ForEach(address => 
                new Uri(string.Format(
                "{0}://{1}:{2}/",
                address.Scheme.ToString(),
                address.Host.ToString(),
                address.Port.ToString())));

            FaultInjectionConnectionErrorResult result = (FaultInjectionConnectionErrorResult)rule.GetResult();
            return new FaultInjectionConnectionErrorRule(
               rule.GetId(),
               rule.IsEnabled(),
               rule.GetStartDelay(),
               rule.GetDuration(),
               regionEndpoints,
               resolvedPhysicalAdresses,
               rule.GetCondition().GetConnectionType(),
               result);
        }

        private bool CanErrorLimitToOperation(FaultInjectionServerErrorType errorType)
        {
            // Some errors should only be applied to specific operationTypes/ requests 
            // others can be applied to all operations
            return errorType != FaultInjectionServerErrorType.Gone
                && errorType != FaultInjectionServerErrorType.ConnectionDelay;
        }

        private OperationType GetEffectiveOperationType(FaultInjectionOperationType faultInjectionOperationType)
        {
            return faultInjectionOperationType switch
            {
                FaultInjectionOperationType.ReadItem => OperationType.Read,
                FaultInjectionOperationType.CreateItem => OperationType.Create,
                FaultInjectionOperationType.QueryItem => OperationType.Query,
                FaultInjectionOperationType.UpsertItem => OperationType.Upsert,
                FaultInjectionOperationType.ReplaceItem => OperationType.Replace,
                FaultInjectionOperationType.DeleteItem => OperationType.Delete,
                FaultInjectionOperationType.PatchItem => OperationType.Patch,
                _ => throw new ArgumentException($"FaultInjectionOperationType: {faultInjectionOperationType} is not supported"),
            };
        }

        private List<Uri> GetRegionEndpoints(FaultInjectionCondition condition)
        {
            bool isWriteOnlyEndpoints = this.IsWriteOnly(condition);

            if(!string.IsNullOrEmpty(condition.GetRegion()))
            {
                return new List<Uri> { this.globalEndpointManager.ResolveFaultInjectionServiceEndpoint(condition.GetRegion(), isWriteOnlyEndpoints) };
            }
            else
            {
                return isWriteOnlyEndpoints ? this.globalEndpointManager.WriteEndpoints.ToList() : this.globalEndpointManager.ReadEndpoints.ToList();
            }
        }

        private bool IsWriteOnly(FaultInjectionCondition condition)
        {
            return this.GetEffectiveOperationType(condition.GetOperationType()).IsWriteOperation();
        }

        private async Task<List<Uri>> ResolvePhyicalAddresses(
            List<Uri> regionEndpoints,
            FaultInjectionCondition condition,
            bool isWriteOnly)
        {
            FaultInjectionEndpoint addressEndpoints = condition.GetEndpoint();
            if (addressEndpoints == null || addressEndpoints == FaultInjectionEndpoint.Empty)
            {
                return new List<Uri>{ };
            }

            List<Uri> resolvedPhysicalAddresses = new List<Uri>();

            foreach (Uri regionEndpoint in regionEndpoints)
            {
                FeedRangeInternal feedRangeInternal = (FeedRangeInternal)addressEndpoints.GetFeedRange();

                //The feed range can be mapped to multiple physical partitions, get the feed range list and resolve addresses for each partition

                DocumentServiceRequest request = DocumentServiceRequest.Create(
                    operationType: OperationType.Read,
                    resourceId: condition.GetContainerResourceId(),
                    resourceType: ResourceType.Document,
                    authorizationTokenType: AuthorizationTokenType.PrimaryMasterKey);

                ContainerProperties collection = await this.collectionCache.ResolveCollectionAsync(request, CancellationToken.None, NoOpTrace.Singleton);
                IEnumerable<string> pkRanges = await feedRangeInternal.GetPartitionKeyRangesAsync(
                    this.routingMapProvider,
                    condition.GetContainerResourceId(),
                    collection.PartitionKey,
                    cancellationToken: new CancellationToken(),
                    trace: NoOpTrace.Singleton);

                foreach (string partitionKeyRange in pkRanges)
                {
                    DocumentServiceRequest fauntInjectionAddressRequest = DocumentServiceRequest.Create(
                               operationType: OperationType.Read,
                               resourceId: condition.GetContainerResourceId(),
                               resourceType: ResourceType.Document,
                               authorizationTokenType: AuthorizationTokenType.PrimaryMasterKey);

                    fauntInjectionAddressRequest.RequestContext.RouteToLocation(regionEndpoint);
                    fauntInjectionAddressRequest.RouteTo(new PartitionKeyRangeIdentity(partitionKeyRange));

                    if (isWriteOnly)
                    {
                        return new List<Uri> { this.ResolvePrimaryTransportAddressUriAsync(fauntInjectionAddressRequest, true).Result.Uri };
                    }

                    IEnumerable<Uri> resolvedEndpoints = (IEnumerable<Uri>)(await this.ResolveAllTransportAddressUriAsync(
                            fauntInjectionAddressRequest,
                            addressEndpoints.IsIncludePrimary(),
                            true)).OrderBy(address => address.Uri.ToString())
                            .Take(addressEndpoints.GetReplicaCount())
                            .Select(address => address.Uri);
                    resolvedPhysicalAddresses.AddRange(resolvedEndpoints);
                }
            }

            return resolvedPhysicalAddresses;
        }

        private async Task<IReadOnlyList<TransportAddressUri>> ResolveAllTransportAddressUriAsync(
            DocumentServiceRequest request,
            bool includePrimary,
            bool forceAddressRefresh)
        {
            PerProtocolPartitionAddressInformation partitionPerProtocolAddress = await this.ResolveAddressesHelperAsync(request, forceAddressRefresh);

            return includePrimary
                ? partitionPerProtocolAddress.ReplicaTransportAddressUris
                : partitionPerProtocolAddress.NonPrimaryReplicaTransportAddressUris;
        }

        private async Task<TransportAddressUri> ResolvePrimaryTransportAddressUriAsync(
            DocumentServiceRequest request,
            bool forceAddressRefresh)
        {
            PerProtocolPartitionAddressInformation partitionPerProtocolAddress = await this.ResolveAddressesHelperAsync(request, forceAddressRefresh);

            return partitionPerProtocolAddress.GetPrimaryAddressUri(request);
        }
        private async Task<PerProtocolPartitionAddressInformation> ResolveAddressesHelperAsync(
            DocumentServiceRequest request,
            bool forceAddressRefresh)
        {
            PartitionAddressInformation partitionAddressInformation =
               await this.addressResolver.ResolveAsync(request, forceAddressRefresh, CancellationToken.None);

            return partitionAddressInformation.Get(Documents.Client.Protocol.Tcp);
        }

        public class FaultInjectionRuleProcessorRetryPolicy : IRetryPolicy
        {
            private readonly ResourceThrottleRetryPolicy resourceThrottleRetryPolicy;
            private readonly WebExceptionRetryPolicy webExceptionRetryPolicy;

            public FaultInjectionRuleProcessorRetryPolicy(RetryOptions retryOptions)
            {
                this.resourceThrottleRetryPolicy = new ResourceThrottleRetryPolicy(
                    maxAttemptCount: retryOptions.MaxRetryAttemptsOnThrottledRequests,
                    maxWaitTimeInSeconds: retryOptions.MaxRetryWaitTimeInSeconds);
                this.webExceptionRetryPolicy = new WebExceptionRetryPolicy();
            }

            public Task<ShouldRetryResult> ShouldRetryAsync(Exception e, CancellationToken cancellationToken)
            {
                ShouldRetryResult result = this.webExceptionRetryPolicy.ShouldRetryAsync(e, cancellationToken).Result;
                if (result.ShouldRetry)
                {
                    return Task.FromResult(result);
                }

                return this.resourceThrottleRetryPolicy.ShouldRetryAsync(e, cancellationToken);
            }
        }

    }   
}