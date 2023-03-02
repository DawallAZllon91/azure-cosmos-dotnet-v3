//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

// This file isn't generated, but this comment is necessary to exclude it from StyleCop analysis.
// <auto-generated/>

namespace Microsoft.Data.Encryption.AzureKeyVaultProvider
{
    internal static class Constants
    {
        /// <summary>
        /// Azure Key Vault Domain Name
        /// </summary>
        internal static readonly string[] AzureKeyVaultPublicDomainNames = new string[] {
            @"vault.azure.net", // default
            @"vault.azure.cn", // Azure China
            @"vault.usgovcloudapi.net", // US Government
            @"vault.microsoftazure.de", // Azure Germany
            @"managedhsm.azure.net", // public HSM vault
            @"managedhsm.azure.cn", // Azure China HSM vault
            @"managedhsm.usgovcloudapi.net", // US Government HSM vault
            @"managedhsm.microsoftazure.de" // Azure Germany HSM vault
            };
    }
}