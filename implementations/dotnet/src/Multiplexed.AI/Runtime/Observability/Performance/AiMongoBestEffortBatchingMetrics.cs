using System.Diagnostics.Metrics;

namespace Multiplexed.AI.Runtime.Observability.Performance
{
    /// <summary>
    /// Emits bounded diagnostics for MongoDB best-effort batching.
    /// </summary>
    internal static class AiMongoBestEffortBatchingMetrics
    {
        private const string MeterName = "Multiplexed.AI.Runtime.MongoBestEffortBatching";

        private static readonly Meter Meter = new(MeterName, "1.0.0");

        private static readonly Counter<long> DroppedRecords = Meter.CreateCounter<long>(
            "multiplexed.ai.mongo.best_effort.dropped_records",
            unit: "records",
            description: "Best-effort MongoDB records dropped because the bounded queue was full or a batch flush failed.");

        private static readonly Counter<long> FlushFailures = Meter.CreateCounter<long>(
            "multiplexed.ai.mongo.best_effort.flush_failures",
            unit: "failures",
            description: "Best-effort MongoDB batch flush failures.");

        private static readonly Histogram<long> BatchSize = Meter.CreateHistogram<long>(
            "multiplexed.ai.mongo.best_effort.batch_size",
            unit: "records",
            description: "Number of logical records carried by each successful MongoDB best-effort batch.");

        public static void RecordDropped(
            string storeName,
            long count)
        {
            if (count <= 0)
            {
                return;
            }

            DroppedRecords.Add(
                count,
                new KeyValuePair<string, object?>("store", NormalizeStoreName(storeName)));
        }

        public static void RecordFlushFailure(
            string storeName)
        {
            FlushFailures.Add(
                1,
                new KeyValuePair<string, object?>("store", NormalizeStoreName(storeName)));
        }

        public static void RecordBatchSize(
            string storeName,
            long count)
        {
            if (count <= 0)
            {
                return;
            }

            BatchSize.Record(
                count,
                new KeyValuePair<string, object?>("store", NormalizeStoreName(storeName)));
        }

        private static string NormalizeStoreName(
            string storeName)
        {
            if (string.Equals(storeName, "trace", StringComparison.OrdinalIgnoreCase))
            {
                return "trace";
            }

            if (string.Equals(storeName, "metric", StringComparison.OrdinalIgnoreCase))
            {
                return "metric";
            }

            return "other";
        }
    }
}
