namespace McpRedmine.Tools;

/// <summary>
/// Marks a tool that changes data in Redmine. <see cref="ToolPipeline"/> rejects every such call against a
/// <c>read_only</c> target before the tool body runs, so the check cannot be forgotten in an individual tool.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WriteToolAttribute : Attribute;
