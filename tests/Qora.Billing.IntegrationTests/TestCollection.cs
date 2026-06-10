namespace Qora.Billing.IntegrationTests;

/// <summary>
/// Shared collection definition that ensures all integration tests use the same
/// BillingApiFactory instance and do not run in parallel (WebApplicationFactory
/// is not safe to instantiate multiple times concurrently).
/// </summary>
[CollectionDefinition("Integration")]
public class IntegrationTestCollection : ICollectionFixture<BillingApiFactory>
{
}
