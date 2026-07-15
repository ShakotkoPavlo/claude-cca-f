using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

AnthropicClient client = new(){ApiKey = "" }; // reads ANTHROPIC_API_KEY env var

const int maxIterations = 4;

// ---- Least-privilege tools, one per stage ----

Tool webSearchTool = new()
{
    Name = "web_search",
    Description = "Search the web for a factual claim about the query. Returns one result — do not call again for the same query.",
    InputSchema = new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The search query." })
        },
        Required = ["query"]
    }
};

Tool readDocumentTool = new()
{
    Name = "read_document",
    Description = "Read a page from a named document and return a factual claim found there.",
    InputSchema = new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["document_name"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Name of the document to read." })
        },
        Required = ["document_name"]
    }
};

Tool readTool = new()
{
    Name = "read",
    Description = "Read-only: re-fetch the structured notes already given in your brief. Synthesis has no search or document-access capability — this is its only tool.",
    InputSchema = new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["what"] = JsonSerializer.SerializeToElement(new { type = "string", description = "What to re-read, e.g. 'notes'." })
        },
        Required = ["what"]
    }
};

// ---- Stub tool executors ----

string ExecuteWebSearch(IReadOnlyDictionary<string, JsonElement> input)
{
    string query = input["query"].GetString()!;
    return JsonSerializer.Serialize(new
    {
        source_url = "https://example-research.org/agentic-loops-2026",
        claim = $"Regarding \"{query}\": production LLM agent loops require a hard iteration cap to bound worst-case cost."
    });
}

string ExecuteReadDocument(IReadOnlyDictionary<string, JsonElement> input)
{
    string documentName = input["document_name"].GetString()!;
    return JsonSerializer.Serialize(new
    {
        document_name = documentName,
        page_number = 42,
        claim = "Deduplicating identical tool calls prevents redundant re-execution in agent loops."
    });
}

string ExecuteRead(IReadOnlyDictionary<string, JsonElement> input) => "(no additional context available beyond what's already in your brief)";

// ---- Generic subagent runner: fresh context per call, single tool, hard iteration cap, tool-call dedup ----

async Task<string> RunSubagentAsync(string label, string systemPrompt, string brief, Tool tool, Func<IReadOnlyDictionary<string, JsonElement>, string> executeTool)
{
    List<MessageParam> messages = [new() { Role = Role.User, Content = brief }];
    Dictionary<string, string> toolCallCache = [];
    int iteration = 0;

    while (true)
    {
        if (++iteration > maxIterations)
        {
            return $"[{label}] aborted: exceeded {maxIterations} iterations without EndTurn.";
        }

        Message response = await client.Messages.Create(new MessageCreateParams
        {
            MaxTokens = 1024,
            Model = Model.ClaudeSonnet5,
            System = systemPrompt,
            Messages = messages,
            Tools = [tool]
        });

        if (response.StopReason == StopReason.EndTurn)
        {
            return string.Concat(response.Content.Select(block => block.TryPickText(out TextBlock? text) ? text.Text : ""));
        }

        if (response.StopReason != StopReason.ToolUse)
        {
            return $"[{label}] aborted: unhandled stop reason {response.StopReason}.";
        }

        List<ContentBlockParam> assistantContent = [];
        List<ContentBlockParam> toolResults = [];

        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? textBlock))
            {
                assistantContent.Add(new TextBlockParam { Text = textBlock.Text });
            }
            else if (block.TryPickToolUse(out ToolUseBlock? toolUseBlock))
            {
                assistantContent.Add(new ToolUseBlockParam { ID = toolUseBlock.ID, Name = toolUseBlock.Name, Input = toolUseBlock.Input });

                string cacheKey = JsonSerializer.Serialize(toolUseBlock.Input);

                if (!toolCallCache.TryGetValue(cacheKey, out string? result))
                {
                    result = executeTool(toolUseBlock.Input);
                    toolCallCache[cacheKey] = result;
                }

                toolResults.Add(new ToolResultBlockParam { ToolUseID = toolUseBlock.ID, Content = result });
            }
        }

        messages.Add(new() { Role = Role.Assistant, Content = assistantContent });
        messages.Add(new() { Role = Role.User, Content = toolResults });
    }
}

string ExtractJson(string text)
{
    int start = text.IndexOf('{');
    int end = text.LastIndexOf('}');
    return start >= 0 && end > start ? text[start..(end + 1)] : "{}";
}

string StripMetadata(string json)
{
    using JsonDocument doc = JsonDocument.Parse(json);
    string? claim = doc.RootElement.TryGetProperty("claim", out JsonElement c) ? c.GetString() : null;
    return JsonSerializer.Serialize(new { claim });
}

// =====================================================================
// Stage 1 + 2 (parallel): search and analysis — each least-privilege, each self-contained
// =====================================================================

const string topic = "loop-safety patterns for agentic tool-use systems";
const string documentName = "internal-agent-safety-notes.md";

string searchBrief =
    $"Using the web_search tool once, find one factual claim about: {topic}. " +
    "Return ONLY a JSON object with exactly these fields: source_url, claim. No prose outside the JSON.";

string analysisBrief =
    $"Using the read_document tool once, read the document named \"{documentName}\" and extract one factual claim from it. " +
    "Return ONLY a JSON object with exactly these fields: document_name, page_number, claim. No prose outside the JSON.";

Console.WriteLine("=== Stage 1+2: search + analysis (parallel, least-privilege tools) ===");

string[] stageResults = await Task.WhenAll(
    RunSubagentAsync("search", "You are a search subagent. You have only the web_search tool — no document or synthesis access.", searchBrief, webSearchTool, ExecuteWebSearch),
    RunSubagentAsync("analysis", "You are a document-analysis subagent. You have only the read_document tool — no web access, no synthesis access.", analysisBrief, readDocumentTool, ExecuteReadDocument)
);

string searchJson = ExtractJson(stageResults[0]);
string analysisJson = ExtractJson(stageResults[1]);

Console.WriteLine($"search stage JSON:   {searchJson}");
Console.WriteLine($"analysis stage JSON: {analysisJson}");

// =====================================================================
// Stage 3: synthesis — least privilege (Read only), fed structured JSON WITH metadata
// =====================================================================

Console.WriteLine("\n=== Stage 3: synthesis WITH metadata (source_url / document_name / page_number) ===");

string synthesisSystemPrompt =
    "You are a synthesis subagent. You have only a read-only tool (no web_search, no read_document) — " +
    "you cannot fetch anything new. Work strictly from the structured notes given to you.";

string citationInstruction =
    "Write a short report (2-3 sentences) using ONLY the structured notes below. " +
    "Every claim MUST be followed by an inline citation: use (source_url) for web notes and (document_name, page N) for document notes. " +
    "If you cannot cite a claim, drop it.\n\nNotes:\n";

string reportWithMetadata = await RunSubagentAsync("synthesis", synthesisSystemPrompt, citationInstruction + searchJson + "\n" + analysisJson, readTool, ExecuteRead);
Console.WriteLine(reportWithMetadata);

bool citedWithMetadata = reportWithMetadata.Contains("http", StringComparison.OrdinalIgnoreCase) || reportWithMetadata.Contains("page", StringComparison.OrdinalIgnoreCase);
Console.WriteLine($"[check] citations present: {citedWithMetadata}");

// =====================================================================
// Stage 4: same synthesis subagent, same instructions — strip source_url/document_name/page_number from the notes
// =====================================================================

Console.WriteLine("\n=== Stage 4: same synthesis subagent, metadata stripped from the brief ===");

string searchJsonStripped = StripMetadata(searchJson);
string analysisJsonStripped = StripMetadata(analysisJson);

string reportStripped = await RunSubagentAsync("synthesis", synthesisSystemPrompt, citationInstruction + searchJsonStripped + "\n" + analysisJsonStripped, readTool, ExecuteRead);
Console.WriteLine(reportStripped);

bool citedStripped = reportStripped.Contains("http", StringComparison.OrdinalIgnoreCase) || reportStripped.Contains("page", StringComparison.OrdinalIgnoreCase);
Console.WriteLine($"[check] citations present: {citedStripped}");

Console.WriteLine(citedWithMetadata && !citedStripped
    ? "\n[conclusion] Same subagent, same instructions — citations vanished only once metadata was stripped from context. The failure lives in context passing, not in synthesis."
    : "\n[conclusion] Inconclusive this run — check the two reports above.");
