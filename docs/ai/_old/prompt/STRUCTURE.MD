Abstractions/
 └─ AI/
    └─ Prompt/
       ├─ AiPromptProviderAttribute.cs
       ├─ AiPromptProviderDescriptor.cs
       ├─ IAiPromptExecutor.cs
       ├─ IAiPromptProvider.cs
       ├─ IAiPromptProviderResolver.cs
       ├─ IAiPromptTemplateRenderer.cs
       ├─ IAiPromptResultParser.cs
       └─ Models/
          ├─ AiPromptRequest.cs
          ├─ AiPromptResult.cs
          ├─ AiPromptProviderRequest.cs
          └─ AiPromptProviderResponse.cs

Runtime/
 └─ AI/
    ├─ Prompt/
    │   ├─ DefaultAiPromptExecutor.cs
    │   ├─ DefaultAiPromptTemplateRenderer.cs
    │   ├─ DefaultAiPromptResultParser.cs
    │   └─ Models/
    │       └─ AiPromptStepConfiguration.cs
    │
    └─ Providers/
        └─ Llm/
            ├─ AiPromptProviderDiscovery.cs
            └─ DefaultAiPromptProviderResolver.cs

Runtime/
 └─ Pipeline/
    └─ Steps/
       └─ Prompt/
          └─ AiPromptStep.cs