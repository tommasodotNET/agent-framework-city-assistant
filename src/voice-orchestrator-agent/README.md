# Voice Orchestrator Agent

A real-time voice assistant that mirrors the text-based orchestrator agent, using the `Azure.AI.VoiceLive` SDK to connect to a **GPT Realtime model deployed in Azure AI Foundry**. The backend bridges a browser WebSocket to the model's WebSocket, enabling speech-to-speech conversation with function calling. Downstream agents (restaurant, activities, accommodation) are invoked as tools via Microsoft Agent Framework (MAF) A2A protocol.

## Architecture Overview

```
┌─────────────┐   WebSocket    ┌──────────────────────┐   WebSocket (SDK)  ┌─────────────────┐
│   Browser    │◄──────────────►│  Voice Orchestrator  │◄─────────────────►│  GPT Realtime    │
│  (React UI)  │  PCM16 audio   │     Agent (.NET)     │   PCM16 audio     │  (Azure Foundry) │
│              │  + transcripts │                      │   + events        │                  │
└─────────────┘                └──────┬───────────────┘                   └─────────────────┘
                                      │ A2A (HTTP)
                          ┌───────────┼───────────┐
                          ▼           ▼           ▼
                   ┌────────────┐ ┌──────────┐ ┌───────────────┐
                   │ Restaurant │ │Activities│ │ Accommodation │
                   │   Agent    │ │  Agent   │ │    Agent      │
                   └────────────┘ └──────────┘ └───────────────┘
```

## Connecting to the GPT Realtime Model

The backend uses the `Azure.AI.VoiceLive` SDK to open a WebSocket connection to a GPT Realtime model deployed in Azure AI Foundry. Authentication uses `DefaultAzureCredential`.

```csharp
// Parse the Foundry connection string to extract the cognitiveservices endpoint
var endpoint = ParseVoiceLiveEndpoint(connectionString);

// Create the client and start a WebSocket session to the model
var client = new VoiceLiveClient(new Uri(endpoint), credential);
await using var session = await client.StartSessionAsync("gpt-realtime", cancellationToken);
```

The session is then configured with system instructions, tools, voice, and VAD settings:

```csharp
var options = new VoiceLiveSessionOptions
{
    Model = "gpt-realtime",
    Instructions = effectiveInstructions,  // system prompt + previous conversation history
    Voice = new AzureStandardVoice("en-US-Ava:DragonHDLatestNeural"),
    InputAudioFormat = InputAudioFormat.Pcm16,
    OutputAudioFormat = OutputAudioFormat.Pcm16,
    TurnDetection = new AzureSemanticVadTurnDetection
    {
        Threshold = 0.5f,
        SilenceDuration = TimeSpan.FromMilliseconds(500),
    },
    InputAudioEchoCancellation = new AudioEchoCancellation(),
    InputAudioNoiseReduction = new AudioNoiseReduction(AudioNoiseReductionType.AzureDeepNoiseSuppression),
    ToolChoice = ToolChoiceLiteral.Auto,
    InputAudioTranscription = new AudioInputTranscriptionOptions(AudioInputTranscriptionOptionsModel.Whisper1)
};

options.Modalities.Add(InteractionModality.Text);
options.Modalities.Add(InteractionModality.Audio);

foreach (var tool in functionTools)
    options.Tools.Add(tool);

await session.ConfigureSessionAsync(options);
```

## Frontend ↔ Backend Communication

### WebSocket Protocol

The frontend connects to the backend via a WebSocket at `/ws/voice?conversationId={contextId}`.

**Browser → Backend messages:**

| Type | Payload | Description |
|------|---------|-------------|
| `audio` | `{ type: "audio", data: "<base64>" }` | PCM16 audio chunk from microphone |
| `stop` | `{ type: "stop" }` | User clicked stop |

**Backend → Browser messages:**

| Type | Payload | Description |
|------|---------|-------------|
| `ready` | `{ type: "ready" }` | Model session established |
| `audio` | `{ type: "audio", data: "<base64>" }` | PCM16 audio from assistant |
| `transcript` | `{ type: "transcript", role, text, final_ }` | Streaming or final transcript |
| `status` | `{ type: "status", status }` | Status change (ready, listening, processing, function_calling) |
| `clear_audio` | `{ type: "clear_audio" }` | Stop playback (barge-in) |
| `error` | `{ type: "error", message }` | Error occurred |

### Audio Pipeline

- **Capture**: Browser uses an `AudioWorklet` processor to capture microphone at 24kHz PCM16. Chunks of 1200 samples (~50ms) are base64-encoded and sent over WebSocket.
- **Playback**: Backend sends base64 PCM16 audio deltas. Browser decodes Int16 → Float32 and schedules sequential `AudioBufferSource` nodes via `AudioContext` for gapless playback.
- **Echo cancellation & noise reduction**: Handled server-side by the model session (`AudioEchoCancellation` + `AzureDeepNoiseSuppression`).

## Starting and Stopping a Voice Session

### Start Flow

1. User clicks the 🎙️ button in the UI
2. Frontend creates a `VoiceSession`, opens WebSocket to `/ws/voice?conversationId={contextId}`
3. Frontend requests microphone access, creates `AudioContext` at 24kHz, loads `AudioWorklet`
4. Backend accepts WebSocket, loads previous conversation history from Cosmos DB
5. Backend opens a WebSocket to the GPT Realtime model via `VoiceLiveClient.StartSessionAsync`
6. Backend configures the session: system prompt (+ conversation history), tools, voice, VAD settings
7. Backend sends `{ type: "ready" }` to frontend
8. Two concurrent loops run:
   - **Client → Model**: forwards browser audio to `session.SendInputAudioAsync`
   - **Model → Client**: processes model events and forwards audio/transcripts to browser

### Stop Flow

1. User clicks the 🔊 button (or closes the page)
2. Frontend sends `{ type: "stop" }`, closes the WebSocket
3. Backend detects the close, cancels both processing loops via `CancellationTokenSource`
4. In the `finally` block, **after the WebSocket session has ended**:
   - Conversation transcript is saved to Cosmos DB
   - OpenTelemetry gen_ai traces are emitted post-hoc

Both persistence and telemetry happen **after** the voice session is fully closed — they cannot be emitted during the session because the conversation is a continuous WebSocket stream with no discrete request/response boundaries.

## Barge-in (Interruption)

When the model detects the user started speaking while the assistant is talking (`SessionUpdateInputAudioBufferSpeechStarted`):

1. Backend sends `{ type: "clear_audio" }` to the frontend
2. Frontend immediately stops all scheduled `AudioBufferSource` nodes and resets the playback timeline
3. The model automatically truncates the assistant's response and processes the user's new input

## Manual Function Calling (No Framework Automation)

> **Key difference from the text orchestrator:** The text orchestrator uses MAF's `ChatClientAgent` which **automatically** handles function calling — it detects tool calls in the model response, invokes the tools, and feeds results back in a loop. With the GPT Realtime model, **there is no such automation**. We must manually process the model's event stream, detect function call requests, execute them ourselves, and submit the results back to the model. This is because the Voice Live SDK provides a raw event stream, not a request/response abstraction.

### Step 1: Resolve A2A Agents at Startup

Each downstream agent is resolved once at startup via its A2A agent card. This produces an `AIAgent` instance that can be invoked later.

```csharp
var agents = new Dictionary<string, AIAgent>();

var agentConfigs = new Dictionary<string, string>
{
    ["restaurant_agent"] = "services__restaurantagent__https__0",
    ["activities_agent"] = "services__activitiesagent__https__0",
    ["accommodation_agent"] = "services__accommodationagent__https__0",
};

foreach (var (agentName, envVar) in agentConfigs)
{
    var url = Environment.GetEnvironmentVariable(envVar);
    var httpClient = new HttpClient { BaseAddress = new Uri(url!) };
    var cardResolver = new A2ACardResolver(
        httpClient.BaseAddress!,
        httpClient,
        agentCardPath: "/agenta2a/v1/card");

    agents[agentName] = cardResolver.GetAIAgentAsync().Result;
}
```

### Step 2: Define Agents as Tool Schemas for the Model

Each downstream agent is registered as a `VoiceLiveFunctionDefinition` — a JSON Schema that tells the model what tools are available, their parameters, and when to use them. This is equivalent to the `tools` array you'd pass to a chat completion API, but using the Voice Live SDK types:

```csharp
var functionTools = new List<VoiceLiveFunctionDefinition>
{
    new("restaurant_agent")
    {
        Description = "Search for restaurants in Agentburg by category, keywords, or location.",
        Parameters = BinaryData.FromObjectAsJson(new
        {
            type = "object",
            properties = new
            {
                query = new
                {
                    type = "string",
                    description = "The user's restaurant-related question or search query"
                }
            },
            required = new[] { "query" }
        })
    },
    // ... activities_agent, accommodation_agent, get_weather defined similarly
};

foreach (var tool in functionTools)
    options.Tools.Add(tool);
```

### Step 3: Detect Function Calls in the Event Stream

The model communicates through a stream of typed events. We process them in a `switch` statement inside an `await foreach` loop. When the model decides to call a tool, it emits specific events that we must handle manually:

```csharp
await foreach (var update in session.GetUpdatesAsync(cancellationToken))
{
    switch (update)
    {
        // ... audio and transcript events ...

        // The model has decided to call a function and finished streaming its arguments
        case SessionUpdateResponseFunctionCallArgumentsDone functionCallFinished:
            pendingFunctionCall = new Dictionary<string, object>
            {
                ["name"] = functionCallFinished.Name,           // e.g. "restaurant_agent"
                ["call_id"] = functionCallFinished.CallId,       // unique ID for this call
                ["arguments"] = functionCallFinished.Arguments   // JSON string, e.g. '{"query":"vegetarian restaurants"}'
            };
            break;

        // The model's response turn is complete — now we must execute any pending function call
        case SessionUpdateResponseDone:
            if (pendingFunctionCall != null)
            {
                await ExecuteFunctionCall(session, pendingFunctionCall, cancellationToken);
                pendingFunctionCall = null;
            }
            break;
    }
}
```

> **Why two events?** The model first streams the function call arguments (potentially in chunks via `SessionUpdateResponseFunctionCallArgumentsDelta`), then emits `SessionUpdateResponseFunctionCallArgumentsDone` with the complete arguments. Only after `SessionUpdateResponseDone` can we safely execute the function — this signals that the model has finished its turn and is waiting for the tool result.

### Step 4: Execute the Function Call via A2A

When we detect a function call, we manually extract the arguments, invoke the corresponding A2A agent, and feed the result back to the model:

```csharp
private async Task ExecuteFunctionCall(VoiceLiveSession session, Dictionary<string, object> callInfo, CancellationToken cancellationToken)
{
    var functionName = (string)callInfo["name"];
    var callId = (string)callInfo["call_id"];
    var arguments = (string)callInfo["arguments"];

    // Parse the arguments from the model's JSON
    var args = JsonDocument.Parse(arguments).RootElement;
    var query = args.GetProperty("query").GetString() ?? "";

    string resultText;

    if (_a2aAgents.TryGetValue(functionName, out var agent))
    {
        // Invoke the downstream agent via MAF A2A
        var messages = new List<ChatMessage> { new(ChatRole.User, query) };
        var agentResponse = await agent.RunAsync(messages);
        resultText = agentResponse.Text ?? "No response from agent";
    }
    else
    {
        resultText = $"Unknown function: {functionName}";
    }

    // Submit the tool result back to the model
    await session.AddItemAsync(new FunctionCallOutputItem(callId, resultText), cancellationToken);

    // Tell the model to generate a response based on the tool result
    await session.StartResponseAsync(cancellationToken);
}
```

> **Critical:** After calling `AddItemAsync`, you **must** call `StartResponseAsync` to prompt the model to generate a spoken response using the tool result. Without this, the model will silently wait.

### Comparison: Text vs Voice Function Calling

| Aspect | Text Orchestrator (MAF) | Voice Orchestrator (Manual) |
|--------|------------------------|---------------------------|
| Tool detection | Automatic (`FunctionInvokingChatClient`) | Manual (`switch` on event types) |
| Tool execution | Automatic (MAF invokes tools in a loop) | Manual (`ExecuteFunctionCall` method) |
| Result submission | Automatic (inserted into chat messages) | Manual (`AddItemAsync` + `StartResponseAsync`) |
| Concurrency | Built-in (`AllowConcurrentInvocation`) | Sequential (one tool at a time) |
| Agent invocation | `agent.AsAIFunction()` passed as tool | `agent.RunAsync(messages)` called explicitly |

### Registered Tools

| Tool Name | Description |
|-----------|-------------|
| `restaurant_agent` | Searches restaurants by category, keywords, or location |
| `activities_agent` | Finds museums, theaters, cultural events, and attractions |
| `accommodation_agent` | Finds hotels, B&Bs, hostels, and accommodations |
| `get_weather` | Returns mock weather data for a location |

## Conversation Persistence (Cosmos DB)

Voice conversations are persisted to the same `conversations` Cosmos DB container used by the text orchestrator. **All persistence happens after the voice session ends** — since the conversation is a continuous WebSocket stream, messages are collected in memory during the session and batch-saved to Cosmos once the user disconnects.

### Saving (after session ends)

All conversation messages (user transcripts, assistant transcripts, tool calls, tool responses) are saved as individual documents:

```json
{
  "id": "guid",
  "conversationId": "{contextId}-voice",
  "timestamp": 1700000000,
  "role": "user | assistant | tool",
  "message": "transcript text or JSON for tool interactions",
  "type": "ChatMessage",
  "ttl": 604800
}
```

- The `-voice` suffix on `conversationId` differentiates voice from text conversations
- Tool calls are stored as `{ tool, arguments }` JSON
- Tool responses are stored as `{ tool, result }` JSON
- Documents have a 7-day TTL

### Loading (on session start)

When a new voice session starts with a `conversationId`, previous messages are loaded from Cosmos DB and appended to the system instructions. The model receives the history as context but is explicitly told not to respond to it:

```csharp
var conversationHistory = await LoadConversationHistoryAsync();
var effectiveInstructions = _instructions + conversationHistory;
// effectiveInstructions is then passed to ConfigureSessionAsync
```

The appended history looks like:

```
PREVIOUS CONVERSATION HISTORY (for context only):
DO NOT respond to or repeat any of this — it is background context only.
Wait for the user to speak first, then use this context to provide continuity if relevant.
---
User: Find vegetarian restaurants near the city center
Tool Result: {"tool":"restaurant_agent","result":"Here are some restaurants..."}
Assistant: Here are some great vegetarian restaurants...
---
```

This allows the voice assistant to maintain context across sessions without replaying past messages.

## Telemetry (OpenTelemetry gen_ai Traces)

Since the conversation is a continuous WebSocket stream, standard gen_ai traces can't be emitted in real-time. Instead, conversation events are collected in memory during the session and traces are emitted **post-hoc after the session ends**, alongside conversation persistence.

```csharp
finally
{
    _sessionEndTime = DateTimeOffset.UtcNow;
    EmitTraces();              // post-hoc gen_ai spans
    await SaveConversationAsync();  // Cosmos DB persistence
}
```

### Trace Structure

```
chat gpt-realtime (CLIENT span)
├── execute_tool restaurant_agent (INTERNAL span)
├── execute_tool activities_agent (INTERNAL span)
└── execute_tool accommodation_agent (INTERNAL span)
```

### Root Span Attributes (`chat gpt-realtime`)

| Attribute | Value |
|-----------|-------|
| `gen_ai.operation.name` | `chat` |
| `gen_ai.provider.name` | `azure.ai.openai` |
| `gen_ai.request.model` | `gpt-realtime` |
| `gen_ai.output.type` | `speech` |
| `gen_ai.system_instructions` | System prompt (JSON array) |
| `gen_ai.tool.definitions` | Tool schemas (JSON array) |
| `gen_ai.input.messages` | User utterances + tool calls + tool responses |
| `gen_ai.output.messages` | Assistant text responses |
| `server.address` | Foundry endpoint host |

### Tool Span Attributes (`execute_tool {name}`)

| Attribute | Value |
|-----------|-------|
| `gen_ai.operation.name` | `execute_tool` |
| `gen_ai.tool.name` | Agent name |
| `gen_ai.tool.call.id` | Model call ID |
| `gen_ai.tool.type` | `function` |
| `gen_ai.tool.call.arguments` | Function arguments JSON |
| `gen_ai.tool.call.result` | A2A agent response text |

Tool spans have accurate start/end timestamps from actual A2A execution, providing visibility into agent latency.

## Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `VoiceLive:Model` | `gpt-realtime` | Model deployment name in Foundry |
| `VoiceLive:Voice` | `en-US-Ava:DragonHDLatestNeural` | Azure HD voice |
| `ConnectionStrings:foundry` | (from Aspire) | Foundry connection string (uses `Endpoint` key for the cognitiveservices endpoint) |
