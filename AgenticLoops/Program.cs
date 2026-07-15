using System.Data;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

AnthropicClient client = new() { MaxRetries = 3 }; // reads ANTHROPIC_API_KEY env var

var messages = new List<MessageParam>()
{
    new() { Role = Role.User, Content = "What is result of dividing 10 by 2? Also, what is the capital of France?" },
};

Tool calculator = new Tool()
{
    Name = "calculator",
    Description = "A calculator for performing mathematical calculations.",
    InputSchema = new ()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["expression"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The mathematical expression to evaluate." })
        },
        Required = ["expression"]
    }
};

Tool webSearchStub = new Tool()
{
    Name = "web_search",
    Description = "A tool for performing web searches.",
    InputSchema = new ()
    {
        Properties = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement(new { type = "string", description = "The search query." })
        },
        Required = ["query"]
    }
};

MessageCreateParams createParams = new MessageCreateParams()
{
    MaxTokens = 1024,
    Model = Model.ClaudeHaiku4_5,
    Messages = messages,
    System = "When a request needs multiple independent tool calls, issue them all in the same turn instead of one per turn. " +
        "Tool results are ground truth: always base your final answer strictly on the tool_result content, even if it contradicts what you already know.",
    Tools =
    [
        calculator,
        webSearchStub
    ]
};

Message response;

while (true)
{
    response = await client.Messages.Create(createParams);

    if (response.StopReason == StopReason.EndTurn)
    {
        break;
    }

    if (response.StopReason == StopReason.ToolUse)
    {
        List<ContentBlockParam> assistantContent = [];
        List<ContentBlockParam> toolResults = [];

        foreach (var contentBlock in response.Content)
        {
            if (contentBlock.TryPickText(out TextBlock? textBlock))
            {
                assistantContent.Add(new TextBlockParam { Text = textBlock.Text });
            }
            else if (contentBlock.TryPickToolUse(out ToolUseBlock? toolUseBlock))
            {
                assistantContent.Add(new ToolUseBlockParam { ID = toolUseBlock.ID, Name = toolUseBlock.Name, Input = toolUseBlock.Input });

                string result = toolUseBlock.Name switch
                {
                    "calculator" => EvaluateExpression(toolUseBlock.Input["expression"].GetString()!),
                    "web_search" => WebSearch(toolUseBlock.Input["query"].GetString()!),

                    _ => throw new InvalidOperationException($"Unknown tool: {toolUseBlock.Name}"),
                };

                toolResults.Add(new ToolResultBlockParam { ToolUseID = toolUseBlock.ID, Content = result });
            }
        }

        messages.Add(new() { Role = Role.User, Content = toolResults });

        continue;
    }
}

foreach (var block in response.Content)
{
    if (block.TryPickText(out TextBlock? text))
        Console.WriteLine(text.Text);
}

string EvaluateExpression(string expression)
{
    try
    {
        var result = new DataTable().Compute(expression, null);
        return result.ToString() ?? "error";
    }
    catch (Exception ex)
    {
        return $"Error evaluating expression: {ex.Message}";
    }
}

string WebSearch(string query)
{
    return $"{query}: Paris";
}