using System.Net.Http.Headers;

namespace Multiplexed.AI.Sdk.Transport
{
    /// <summary>
    /// Thread-safe mutable state for the latest runtime access-context handle.
    /// </summary>
    internal sealed class AiSdkAccessContextState
    {
        private readonly string? _headerName;
        private string? _current;

        public AiSdkAccessContextState(
            string? headerName,
            IReadOnlyDictionary<string, string>? initialHeaders)
        {
            _headerName = NormalizeHeaderName(headerName);

            if (_headerName is null || initialHeaders is null)
            {
                return;
            }

            foreach (var item in initialHeaders)
            {
                if (string.Equals(item.Key, _headerName, StringComparison.OrdinalIgnoreCase) &&
                    IsValidValue(item.Value))
                {
                    _current = item.Value.Trim();
                    break;
                }
            }
        }

        public string? HeaderName => _headerName;

        public string? Current => Volatile.Read(ref _current);

        public void Apply(HttpRequestHeaders headers)
        {
            ArgumentNullException.ThrowIfNull(headers);

            if (_headerName is null)
            {
                return;
            }

            var current = Current;
            if (string.IsNullOrWhiteSpace(current))
            {
                return;
            }

            headers.Remove(_headerName);

            if (!headers.TryAddWithoutValidation(_headerName, current))
            {
                throw new InvalidOperationException(
                    $"Failed to apply rotating access-context header '{_headerName}'.");
            }
        }

        public void Observe(HttpResponseHeaders headers)
        {
            ArgumentNullException.ThrowIfNull(headers);

            if (_headerName is null ||
                !headers.TryGetValues(_headerName, out var values))
            {
                return;
            }

            var observed = values
                .Where(value => IsValidValue(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            // Ambiguous repeated headers are not accepted as rotation authority.
            if (observed.Length != 1)
            {
                return;
            }

            Volatile.Write(ref _current, observed[0]);
        }

        private static string? NormalizeHeaderName(string? headerName)
        {
            if (headerName is null)
            {
                return null;
            }

            var normalized = headerName.Trim();

            if (normalized.Length == 0 ||
                normalized.Contains(':') ||
                normalized.Any(char.IsControl))
            {
                throw new ArgumentException(
                    "AccessContextHeaderName must be a valid HTTP header name or null.",
                    nameof(headerName));
            }

            return normalized;
        }

        private static bool IsValidValue(string? value) =>
            !string.IsNullOrWhiteSpace(value) &&
            !value.Any(character => character is '\r' or '\n');
    }
}
