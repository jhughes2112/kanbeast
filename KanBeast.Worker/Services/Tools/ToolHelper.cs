using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;

namespace KanBeast.Worker.Services.Tools;

// Helper to create tool definitions and handlers from methods with [Description] attributes.
public static class ToolHelper
{
    // Adds a tool from a method to the tools list.
    // For static methods, pass null as instance and specify the type.
    public static void AddTool(List<Tool> tools, object? instance, string methodName, Type? type = null)
    {
        Type targetType = type ?? instance?.GetType() ?? throw new ArgumentException("Either instance or type must be provided");
        BindingFlags flags = instance == null 
            ? BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
            : BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        MethodInfo? method = targetType.GetMethod(methodName, flags);

        if (method == null)
        {
            throw new ArgumentException($"Method '{methodName}' not found on type '{targetType.Name}'");
        }

        string toolName = ToSnakeCase(methodName.Replace("Async", ""));
        DescriptionAttribute? descAttr = method.GetCustomAttribute<DescriptionAttribute>();
        string description = descAttr?.Description ?? methodName;

        JsonObject parameters = BuildParametersSchema(method);

        Func<JsonObject, ToolContext, Task<ToolResult>> handler = async (JsonObject args, ToolContext context) =>
        {
            object?[] methodArgs = BuildMethodArguments(method, args, context);
            object? result = method.Invoke(instance, methodArgs);

            if (result is not Task<ToolResult> taskToolResult)
            {
                throw new InvalidOperationException($"Tool method '{methodName}' must return Task<ToolResult>");
            }

            ToolResult toolResult = await taskToolResult;
            return new ToolResult(TruncateResponse(toolResult.Response), toolResult.ExitLoop, toolName);
        };

        tools.Add(new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = toolName,
                    Description = description,
                    Parameters = parameters
                }
            },
            Handler = handler
        });
    }

    // Adds multiple tools from methods.
    // For static methods, pass null as instance and specify the type.
    public static void AddTools(List<Tool> tools, object? instance, params string[] methodNames)
    {
        Type? type = instance?.GetType();
        foreach (string methodName in methodNames)
        {
            AddTool(tools, instance, methodName, type);
        }
    }

    // Overload for specifying type explicitly (for static methods).
    public static void AddTools(List<Tool> tools, Type type, params string[] methodNames)
    {
        foreach (string methodName in methodNames)
        {
            AddTool(tools, null, methodName, type);
        }
    }

    private static JsonObject BuildParametersSchema(MethodInfo method)
    {
        JsonObject properties = new JsonObject();
        JsonArray required = new JsonArray();

        foreach (ParameterInfo param in method.GetParameters())
        {
            if (param.Name == null || param.ParameterType == typeof(CancellationToken) || param.ParameterType == typeof(ToolContext))
            {
                continue;
            }

            DescriptionAttribute? paramDesc = param.GetCustomAttribute<DescriptionAttribute>();
            string paramDescription = paramDesc?.Description ?? param.Name;

            JsonObject paramSchema = new JsonObject
            {
                ["type"] = GetJsonType(param.ParameterType),
                ["description"] = paramDescription
            };

            if (param.ParameterType.IsArray)
            {
                Type? elementType = param.ParameterType.GetElementType();
                paramSchema["items"] = new JsonObject
                {
                    ["type"] = GetJsonType(elementType ?? typeof(string))
                };
            }
            else if (param.ParameterType == typeof(JsonArray))
            {
                paramSchema["items"] = new JsonObject
                {
                    ["type"] = "object"
                };
            }

            properties[param.Name] = paramSchema;

            if (!param.HasDefaultValue)
            {
                required.Add(param.Name);
            }
        }

        JsonObject schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static object?[] BuildMethodArguments(MethodInfo method, JsonObject args, ToolContext context)
    {
        ParameterInfo[] parameters = method.GetParameters();
        object?[] result = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo param = parameters[i];
            string paramName = param.Name ?? $"arg{i}";

            if (param.ParameterType == typeof(ToolContext))
            {
                result[i] = context;
            }
            else if (param.ParameterType == typeof(CancellationToken))
            {
                result[i] = context.CancellationToken;
            }
            else if (args.TryGetPropertyValue(paramName, out JsonNode? node) && node != null)
            {
                result[i] = ConvertJsonValue(node, param.ParameterType);
            }
            else if (param.HasDefaultValue)
            {
                result[i] = param.DefaultValue;
            }
            else
            {
                result[i] = GetDefaultValue(param.ParameterType);
            }
        }

        return result;
    }

    private static object? ConvertJsonValue(JsonNode node, Type targetType)
    {
        if (targetType == typeof(string))
        {
            return node.ToString();
        }
        else if (targetType == typeof(int))
        {
            return node.GetValue<int>();
        }
        else if (targetType == typeof(long))
        {
            return node.GetValue<long>();
        }
        else if (targetType == typeof(bool))
        {
            return node.GetValue<bool>();
        }
        else if (targetType == typeof(double))
        {
            return node.GetValue<double>();
        }
        else if (targetType == typeof(string[]))
        {
            if (node is JsonArray arr)
            {
                string[] items = new string[arr.Count];
                for (int i = 0; i < arr.Count; i++)
                {
                    items[i] = arr[i]?.ToString() ?? "";
                }
                return items;
            }

            return Array.Empty<string>();
        }
        else if (targetType == typeof(JsonArray))
        {
            if (node is JsonArray arr)
            {
                return arr;
            }

            return new JsonArray();
        }
        else
        {
            return node.ToString();
        }
    }

    private static string GetJsonType(Type type)
    {
        if (type == typeof(string))
        {
            return "string";
        }
        else if (type == typeof(int) || type == typeof(long))
        {
            return "integer";
        }
        else if (type == typeof(double) || type == typeof(float))
        {
            return "number";
        }
        else if (type == typeof(bool))
        {
            return "boolean";
        }
        else if (type.IsArray)
        {
            return "array";
        }
        else if (type == typeof(JsonArray))
        {
            return "array";
        }
        else
        {
            return "string";
        }
    }

    private static object? GetDefaultValue(Type type)
    {
        if (type == typeof(string))
        {
            return string.Empty;
        }
        else if (type.IsValueType)
        {
            return Activator.CreateInstance(type);
        }
        else
        {
            return null;
        }
    }

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        System.Text.StringBuilder result = new System.Text.StringBuilder();
        result.Append(char.ToLowerInvariant(name[0]));

        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                result.Append('_');
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private const int MaxResponseLength = 160000;
    private const int TruncateHeadLength = 80000;
    private const int TruncateTailLength = 80000;

    // Truncates long responses, keeping the first and last portions to save context window.
    private static string TruncateResponse(string response)
    {
        if (string.IsNullOrEmpty(response) || response.Length <= MaxResponseLength)
        {
            return response;
        }

        int omittedCount = response.Length - TruncateHeadLength - TruncateTailLength;
        string head = response.Substring(0, TruncateHeadLength);
        string tail = response.Substring(response.Length - TruncateTailLength);

        return $"{head}\n\n... [{omittedCount} characters omitted] ...\n\n{tail}";
    }
}
