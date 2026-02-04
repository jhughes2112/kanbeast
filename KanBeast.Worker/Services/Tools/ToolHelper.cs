using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;

namespace KanBeast.Worker.Services.Tools;

// Helper to create ProviderTools from methods with [Description] attributes.
public static class ToolHelper
{
    // Creates a ProviderTool from a method on the given instance.
    public static ProviderTool FromMethod(object instance, string methodName, string? overrideName = null)
    {
        Type type = instance.GetType();
        MethodInfo? method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (method == null)
        {
            throw new ArgumentException($"Method '{methodName}' not found on type '{type.Name}'");
        }

        string toolName = overrideName ?? ToSnakeCase(methodName.Replace("Async", ""));
        DescriptionAttribute? descAttr = method.GetCustomAttribute<DescriptionAttribute>();
        string description = descAttr?.Description ?? methodName;

        JsonObject parameters = BuildParametersSchema(method);

        Func<JsonObject, Task<string>> invoker = async (JsonObject args) =>
        {
            object?[] methodArgs = BuildMethodArguments(method, args);
            object? result = method.Invoke(instance, methodArgs);

            if (result is Task<string> taskString)
            {
                return await taskString;
            }
            else if (result is Task task)
            {
                await task;
                return "OK";
            }
            else
            {
                return result?.ToString() ?? "OK";
            }
        };

        return new ProviderTool
        {
            Name = toolName,
            Description = description,
            Parameters = parameters,
            InvokeAsync = invoker
        };
    }

    // Adds multiple tools from methods to a dictionary.
    public static void AddTools(Dictionary<string, ProviderTool> tools, object instance, params string[] methodNames)
    {
        foreach (string methodName in methodNames)
        {
            ProviderTool tool = FromMethod(instance, methodName);
            tools[tool.Name] = tool;
        }
    }

    private static JsonObject BuildParametersSchema(MethodInfo method)
    {
        JsonObject properties = new JsonObject();
        JsonArray required = new JsonArray();

        foreach (ParameterInfo param in method.GetParameters())
        {
            if (param.Name == null)
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

    private static object?[] BuildMethodArguments(MethodInfo method, JsonObject args)
    {
        ParameterInfo[] parameters = method.GetParameters();
        object?[] result = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            ParameterInfo param = parameters[i];
            string paramName = param.Name ?? $"arg{i}";

            if (args.TryGetPropertyValue(paramName, out JsonNode? node) && node != null)
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
                return arr.Select(n => n?.ToString() ?? "").ToArray();
            }

            return Array.Empty<string>();
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
}
