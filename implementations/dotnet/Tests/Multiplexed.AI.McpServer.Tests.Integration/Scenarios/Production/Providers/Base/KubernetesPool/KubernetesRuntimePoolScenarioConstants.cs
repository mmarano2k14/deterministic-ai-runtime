namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool
{
    /// <summary>
    /// Provides constants for the Kubernetes Runtime Pool readiness proof.
    /// </summary>
    public static class KubernetesRuntimePoolScenarioConstants
    {
        /// <summary>
        /// Gets the image tag built from the current Step 5D source.
        /// </summary>
        public const string RuntimeImage = KubernetesSdkScenarioConstants.RuntimeImage;

        /// <summary>
        /// Gets the Kubernetes namespace.
        /// </summary>
        public const string Namespace = "ai-runtime";

        /// <summary>
        /// Gets the Minikube NodePort host.
        /// </summary>
        public const string NodePortHost = "192.168.49.2";

        /// <summary>
        /// Gets the Redis endpoint reachable from Minikube.
        /// </summary>
        public const string RedisConnectionString =
            "host.minikube.internal:6379,abortConnect=false";

        /// <summary>
        /// Gets the MongoDB endpoint reachable from Minikube.
        /// </summary>
        public const string MongoConnectionString =
            "mongodb://host.minikube.internal:27017/?directConnection=true";
    }
}
