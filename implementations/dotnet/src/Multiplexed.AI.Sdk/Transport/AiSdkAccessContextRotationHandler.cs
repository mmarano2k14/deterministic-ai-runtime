namespace Multiplexed.AI.Sdk.Transport
{
    /// <summary>
    /// Applies the latest access-context handle immediately before each HTTP request
    /// and captures a rotated handle from each HTTP response.
    /// </summary>
    internal sealed class AiSdkAccessContextRotationHandler : DelegatingHandler
    {
        private readonly AiSdkAccessContextState _state;

        public AiSdkAccessContextRotationHandler(AiSdkAccessContextState state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            _state.Apply(request.Headers);

            var response = await base
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            _state.Observe(response.Headers);

            return response;
        }
    }
}
