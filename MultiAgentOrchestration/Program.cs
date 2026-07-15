using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

AnthropicClient client = new(){ApiKey = "" }; // reads ANTHROPIC_API_KEY env var

const int subagentMaxIterations = 4;

// ---- Subagent tools ----

Tool webSearchTool = new()
{
    Name = "web_search",
    Description = "Search the web and return a single authoritative result for the query. " +
        "The result is final — do not call this tool again for the same query.",
    InputSchema = new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The search query." })
        },
        Required = ["query"]
    }
};

Tool readFileTool = new()
{
    Name = "read_file",
    Description = "Read the full contents of a file from disk by absolute path.",
    InputSchema = new()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["path"] = JsonSerializer.SerializeToElement(new { type = "string", description = "Absolute file path to read." })
        },
        Required = ["path"]
    }
};

string ExecuteWebSearch(IReadOnlyDictionary<string, JsonElement> input)
{
    string query = input["query"].GetString()!;
    return $"[stub search result for \"{query}\"] Deterministic best-practice answer: use hard iteration caps, " +
        "token/cost circuit breakers, duplicate tool-call dedup, no-progress detection, and human escalation after N failures.";
}

string ExecuteReadFile(IReadOnlyDictionary<string, JsonElement> input)
{
    string path = input["path"].GetString()!;
    try
    {
        return File.ReadAllText(path);
    }
    catch (Exception ex)
    {
        return $"Error reading file: {ex.Message}";
    }
}

// ---- Generic subagent runner: fresh context every call (no coordinator history), hard iteration cap, dedup, stop-reason guard ----

async Task<string> RunSubagentAsync(string label, string systemPrompt, string brief, Tool tool, Func<IReadOnlyDictionary<string, JsonElement>, string> executeTool)
{
    List<MessageParam> messages =
    [
        new() { Role = Role.User, Content = brief }
    ];

    Dictionary<string, string> toolCallCache = [];
    int iteration = 0;

    while (true)
    {
        if (++iteration > subagentMaxIterations)
        {
            return $"[{label}] aborted: exceeded {subagentMaxIterations} iterations without EndTurn.";
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

async Task<string> SynthesizeAsync(string prompt)
{
    Message response = await client.Messages.Create(new MessageCreateParams
    {
        MaxTokens = 1500,
        Model = Model.ClaudeSonnet5,
        Messages = [new() { Role = Role.User, Content = prompt }]
    });

    return string.Concat(response.Content.Select(block => block.TryPickText(out TextBlock? text) ? text.Text : ""));
}

// =====================================================================
// Part 1 + 2: fully self-contained briefs, dispatched in parallel, timed vs sequential
// =====================================================================

const string AgenticLoopsPath = @"C:\Projects\claude-cca-f\AgenticLoops\Program.cs";

string webBrief =
    "Research current best practices for preventing infinite or runaway loops in LLM agentic tool-use " +
    "systems (agents that call tools in a loop, feeding results back to the model, until a stop condition). " +
    "Use the web_search tool once. Return a concise bullet list (5-8 items): technique name, one-sentence " +
    "description, and why it matters. No preamble.";

string docBrief =
    $"Using the read_file tool, read the C# file at {AgenticLoopsPath}. It implements an agentic tool-use " +
    "loop with the Anthropic .NET SDK: a while(true) loop that calls client.Messages.Create, checks " +
    "message.StopReason, executes a tool on ToolUse, and loops until EndTurn. " +
    "Analyze it for these 5 loop-safety items, each rated PRESENT/ABSENT with line-number evidence: " +
    "(a) hard iteration cap, (b) duplicate tool-call dedup, (c) handling of StopReason values other than " +
    "EndTurn/ToolUse, (d) token/cost budget tracking, (e) escalation after repeated tool failures. " +
    "Return a short table plus one summary paragraph.";

Console.WriteLine("=== Part 1+2: parallel vs sequential dispatch ===");

Stopwatch parallelTimer = Stopwatch.StartNew();
string[] parallelResults = await Task.WhenAll(
    RunSubagentAsync("web-search", "You are a focused research subagent. Stay narrowly scoped to the brief.", webBrief, webSearchTool, ExecuteWebSearch),
    RunSubagentAsync("document-analysis", "You are a focused code-analysis subagent. Use read_file to inspect the target file directly; never guess its contents.", docBrief, readFileTool, ExecuteReadFile)
);
parallelTimer.Stop();
Console.WriteLine($"[timing] parallel dispatch: {parallelTimer.ElapsedMilliseconds} ms");

string webReport = parallelResults[0];
string docReport = parallelResults[1];

// =====================================================================
// Part 3: iterative refinement — synthesize, self-critique coverage, re-delegate the gap
// =====================================================================

Console.WriteLine("\n=== Part 3: iterative refinement ===");

string synthesisPrompt =
    "You are the coordinator. Two subagents reported back on \"preventing infinite loops in LLM tool-use agents\":\n\n" +
    $"--- Web-search subagent report ---\n{webReport}\n\n" +
    $"--- Document-analysis subagent report (on AgenticLoops/Program.cs) ---\n{docReport}\n\n" +
    "Task:\n1. Synthesize both into one unified report.\n" +
    "2. Self-critique: name at most one concrete gap neither report covers, or write \"none\" if there truly is none.\n" +
    "Format exactly as:\nSYNTHESIS: <report>\nGAP: <one sentence, or \"none\">";

string synthesis = await SynthesizeAsync(synthesisPrompt);
Console.WriteLine(synthesis);

int gapIndex = synthesis.IndexOf("GAP:", StringComparison.OrdinalIgnoreCase);
string gap = gapIndex >= 0 ? synthesis[(gapIndex + 4)..].Trim() : "none";

if (!gap.Equals("none", StringComparison.OrdinalIgnoreCase) && gap.Length > 0)
{
    Console.WriteLine($"\n[refinement] gap detected: {gap}");
    string gapFillBrief = $"Answer this specific question with a short bullet list, no preamble: {gap}";
    string gapFill = await RunSubagentAsync("gap-fill", "You are a focused research subagent.", gapFillBrief, webSearchTool, ExecuteWebSearch);
    Console.WriteLine($"[refinement] gap-fill result:\n{gapFill}");
}
else
{
    Console.WriteLine("\n[refinement] no gap reported.");
}

// =====================================================================
// Part 4: reproduce the signature bug — coordinator decomposes too narrowly — then fix it
// =====================================================================

Console.WriteLine("\n=== Part 4: over-narrow decomposition bug ===");

const string broadTopic = "What does it take to run an LLM agent tool-use loop safely in production?";

// BUGGY: two arbitrary narrow slices that don't cover the topic's real dimensions.
string narrowBriefA = "Using the web_search tool, list 3 ways to detect an agent calling the same tool twice in a row. Short bullet list, no preamble.";
string narrowBriefB = $"Using the read_file tool, read {AgenticLoopsPath} and list its top-level variable names. Short bullet list, no preamble.";

string[] narrowResults = await Task.WhenAll(
    RunSubagentAsync("narrow-A", "You are a focused research subagent.", narrowBriefA, webSearchTool, ExecuteWebSearch),
    RunSubagentAsync("narrow-B", "You are a focused code-analysis subagent.", narrowBriefB, readFileTool, ExecuteReadFile)
);

string buggyReport = await SynthesizeAsync(
    $"Answer this question using ONLY the two notes below: \"{broadTopic}\"\n\n" +
    $"Note A: {narrowResults[0]}\n\nNote B: {narrowResults[1]}");

Console.WriteLine("[buggy] both subagents succeeded at their assigned (narrow) tasks:");
Console.WriteLine($"  narrow-A: {narrowResults[0]}");
Console.WriteLine($"  narrow-B: {narrowResults[1]}");
Console.WriteLine($"[buggy] synthesized report for the broad topic (incomplete despite 100% subagent success):\n{buggyReport}");

// FIX: the defect is at the coordinator's decomposition step, not the subagents.
// Add a breadth-enumeration pre-step before delegating, instead of hardcoding 2 arbitrary slices.
string dimensionsList = await SynthesizeAsync(
    $"List the 4-6 essential, non-overlapping dimensions someone must cover to fully answer: \"{broadTopic}\". " +
    "One short line per dimension, no elaboration, no numbering prefix beyond a simple list.");

Console.WriteLine($"\n[fix] coordinator breadth-enumeration pre-step:\n{dimensionsList}");

string[] dimensions = dimensionsList
    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Where(line => line.Length > 0)
    .ToArray();

string[] fixedResults = await Task.WhenAll(dimensions.Select(dimension =>
    RunSubagentAsync($"dimension:{dimension}", "You are a focused research subagent.",
        $"Using the web_search tool, give a short bullet list answering: how does \"{dimension}\" apply to running an LLM agent tool-use loop safely in production? No preamble.",
        webSearchTool, ExecuteWebSearch)));

string fixedReport = await SynthesizeAsync(
    $"Synthesize a complete answer to \"{broadTopic}\" from these per-dimension notes:\n\n" +
    string.Join("\n\n", dimensions.Zip(fixedResults, (d, r) => $"--- {d} ---\n{r}")));

Console.WriteLine($"\n[fixed] synthesized report after re-decomposing by real dimensions:\n{fixedReport}");
