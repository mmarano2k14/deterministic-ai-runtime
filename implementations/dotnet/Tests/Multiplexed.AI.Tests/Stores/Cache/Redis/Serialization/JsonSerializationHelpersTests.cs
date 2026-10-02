using Multiplexed.AI.Stores.Cache.Redis.Serialization;
using System.Text.Json;
using Xunit;

namespace Multiplexed.AI.Tests.Stores.Cache.Redis.Serialization
{
    public sealed class JsonSerializationHelpersTests
    {
        [Fact]
        public void RepairRecordJson_Normalizes_Lua_Corrupted_ExecutionContext_Collections()
        {
            const string json =
                """
                {
                  "CompletedSteps": {},
                  "ExecutionContextSnapshot": {
                    "Namespaces": [
                      {
                        "Name": "default",
                        "Trns": {}
                      }
                    ]
                  }
                }
                """;

            var repaired = JsonSerializationHelpers.RepairRecordJson(json);
            using var document = JsonDocument.Parse(repaired);
            var root = document.RootElement;

            Assert.Equal(JsonValueKind.Array, root.GetProperty("CompletedSteps").ValueKind);
            var namespaces = root.GetProperty("ExecutionContextSnapshot").GetProperty("Namespaces");
            Assert.Equal(JsonValueKind.Array, namespaces.ValueKind);
            Assert.Equal(
                JsonValueKind.Array,
                namespaces[0].GetProperty("Trns").ValueKind);
        }

        [Fact]
        public void RepairRecordJson_Preserves_NonEmpty_ExecutionContext_Collections()
        {
            const string json =
                """
                {
                  "CompletedSteps": ["work"],
                  "ExecutionContextSnapshot": {
                    "Namespaces": [
                      {
                        "Name": "default",
                        "Trns": ["urn:matrix:test"]
                      }
                    ]
                  }
                }
                """;

            var repaired = JsonSerializationHelpers.RepairRecordJson(json);
            using var document = JsonDocument.Parse(repaired);
            var trns = document.RootElement
                .GetProperty("ExecutionContextSnapshot")
                .GetProperty("Namespaces")[0]
                .GetProperty("Trns");

            Assert.Single(trns.EnumerateArray());
            Assert.Equal("urn:matrix:test", trns[0].GetString());
        }
    }
}
