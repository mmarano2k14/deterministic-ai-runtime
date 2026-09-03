using System.Text.Json;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Providers
{
    internal static class OpenAiRuntimeAnalysisSchema
    {
        public static JsonElement Create()
        {
            using var document = JsonDocument.Parse(
                SchemaJson);

            return document.RootElement.Clone();
        }

        private const string SchemaJson =
            """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "answer": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 3000
                },
                "summary": {
                  "type": "string",
                  "minLength": 1,
                  "maxLength": 1000
                },
                "severity": {
                  "type": "string",
                  "enum": [
                    "info",
                    "low",
                    "medium",
                    "high",
                    "critical"
                  ]
                },
                "confidence": {
                  "type": "number",
                  "minimum": 0,
                  "maximum": 1
                },
                "observations": {
                  "type": "array",
                  "maxItems": 8,
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "title": {
                        "type": "string",
                        "minLength": 1,
                        "maxLength": 200
                      },
                      "detail": {
                        "type": "string",
                        "minLength": 1,
                        "maxLength": 1000
                      },
                      "evidenceIndexes": {
                        "type": "array",
                        "maxItems": 12,
                        "items": {
                          "type": "integer",
                          "minimum": 0
                        }
                      }
                    },
                    "required": [
                      "title",
                      "detail",
                      "evidenceIndexes"
                    ]
                  }
                },
                "suggestedScenario": {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "name": {
                      "type": "string",
                      "minLength": 1,
                      "maxLength": 200
                    },
                    "rationale": {
                      "type": "string",
                      "minLength": 1,
                      "maxLength": 1000
                    },
                    "scenarioType": {
                      "type": "string",
                      "enum": [
                        "single-burst",
                        "maintained-concurrency",
                        "wave-batches",
                        "wave-batches-staggered",
                        "custom"
                      ]
                    },
                    "totalRequests": {
                      "type": "integer",
                      "minimum": 1
                    },
                    "concurrency": {
                      "type": [
                        "integer",
                        "null"
                      ],
                      "minimum": 1
                    },
                    "batchSize": {
                      "type": [
                        "integer",
                        "null"
                      ],
                      "minimum": 1
                    },
                    "delayMs": {
                      "type": "integer",
                      "minimum": 0
                    },
                    "wavePauseMs": {
                      "type": [
                        "integer",
                        "null"
                      ],
                      "minimum": 0
                    },
                    "maxInFlight": {
                      "type": "integer",
                      "minimum": 1
                    },
                    "rotationOverlapMs": {
                      "type": "integer",
                      "minimum": 0
                    },
                    "durationSeconds": {
                      "type": [
                        "integer",
                        "null"
                      ],
                      "minimum": 1
                    }
                  },
                  "required": [
                    "name",
                    "rationale",
                    "scenarioType",
                    "totalRequests",
                    "concurrency",
                    "batchSize",
                    "delayMs",
                    "wavePauseMs",
                    "maxInFlight",
                    "rotationOverlapMs",
                    "durationSeconds"
                  ]
                }
              },
              "required": [
                "answer",
                "summary",
                "severity",
                "confidence",
                "observations",
                "suggestedScenario"
              ]
            }
            """;
    }
}
