using System.Globalization;
using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent;

namespace CodeAlta.Plugin.Acp;

/// <summary>
/// Translates ACP <c>elicitation/create</c> form requests into CodeAlta interactive questions and maps user answers back
/// to schema-typed <c>CreateElicitationResponse</c> payloads.
/// </summary>
internal static class AcpElicitation
{
    /// <summary>A parsed form elicitation: the questions to show and the schema fields to populate from the answers.</summary>
    internal sealed record ParsedForm(
        string? Message,
        IReadOnlyList<AgentInteractiveAskQuestion> Questions,
        IReadOnlyList<FieldBinding> Bindings);

    internal enum FieldKind
    {
        SingleSelect,
        MultiSelect,
        Boolean,
        String,
        Integer,
        Number,
    }

    internal sealed record FieldBinding(
        string Key,
        string Label,
        FieldKind Kind,
        bool Required,
        IReadOnlyList<string> OptionValues,
        string? CompanionOtherKey,
        JsonElement DefaultValue,
        bool HasSkipChoice = false,
        long? MinItems = null,
        long? MaxItems = null,
        double? Minimum = null,
        double? Maximum = null,
        long? MinLength = null,
        long? MaxLength = null);

    /// <summary>
    /// Parses an ACP <c>elicitation/create</c> form request into interactive questions and response bindings.
    /// Returns <see langword="null"/> when the schema is empty or contains unsupported property types.
    /// </summary>
    public static ParsedForm? ParseForm(JsonElement parameters)
    {
        var message = AcpJson.GetString(parameters, "message");
        if (!AcpJson.TryGetObject(parameters, "requestedSchema", out var schema) ||
            !AcpJson.TryGetObject(schema, "properties", out var properties))
        {
            return null;
        }

        if (AcpJson.GetString(schema, "type") is { } rootType && !string.Equals(rootType, "object", StringComparison.Ordinal))
        {
            return null;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (AcpJson.TryGetArray(schema, "required", out var requiredArray))
        {
            foreach (var item in requiredArray.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                {
                    required.Add(name);
                }
            }
        }

        var rawProperties = new List<(string Key, JsonElement Schema)>();
        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            rawProperties.Add((property.Name, property.Value));
        }

        if (rawProperties.Count == 0)
        {
            return null;
        }

        var consumed = new HashSet<int>();
        var questions = new List<AgentInteractiveAskQuestion>();
        var bindings = new List<FieldBinding>();
        for (var i = 0; i < rawProperties.Count; i++)
        {
            if (consumed.Contains(i))
            {
                continue;
            }

            var (key, prop) = rawProperties[i];
            var isRequired = required.Contains(key);
            var title = AcpJson.GetString(prop, "title");
            var description = AcpJson.GetString(prop, "description");
            var label = !string.IsNullOrWhiteSpace(title) ? title.Trim() : key;
            var questionText = !string.IsNullOrWhiteSpace(title) ? title.Trim() : !string.IsNullOrWhiteSpace(description) ? description.Trim() : key;
            var tabTitle = ShortTitle(title ?? key, questions.Count + 1);
            var detail = !string.IsNullOrWhiteSpace(description) && !string.Equals(description.Trim(), questionText, StringComparison.OrdinalIgnoreCase)
                ? description.Trim()
                : null;
            var defaultValue = prop.TryGetProperty("default", out var def) ? def.Clone() : default;

            var type = AcpJson.GetString(prop, "type");
            if (TryReadSingleSelect(prop, out var singleOptions))
            {
                var (otherKey, otherFreeform) = TryConsumeCompanionOther(rawProperties, i, key, required, consumed);
                if (defaultValue.ValueKind == JsonValueKind.String && defaultValue.GetString() is { } defaultStr)
                {
                    var defaultIndex = singleOptions.FindIndex(option => string.Equals(option.Value, defaultStr, StringComparison.Ordinal));
                    if (defaultIndex > 0)
                    {
                        var defaultOption = singleOptions[defaultIndex];
                        singleOptions.RemoveAt(defaultIndex);
                        singleOptions.Insert(0, defaultOption);
                    }
                }

                var hasSkip = !isRequired && otherFreeform is null && defaultValue.ValueKind != JsonValueKind.String;
                var choices = new List<AgentInteractiveAskChoice>(singleOptions.Count + (hasSkip ? 1 : 0));
                var values = new List<string>(singleOptions.Count + (hasSkip ? 1 : 0));
                if (hasSkip)
                {
                    choices.Add(new AgentInteractiveAskChoice("Leave blank (skip)"));
                    values.Add(string.Empty);
                }

                foreach (var option in singleOptions)
                {
                    choices.Add(new AgentInteractiveAskChoice(option.Label, option.Description));
                    values.Add(option.Value);
                }

                questions.Add(new AgentInteractiveAskQuestion(
                    tabTitle,
                    questionText,
                    detail,
                    choices,
                    AllowMultiSelect: false,
                    Freeform: otherFreeform));
                bindings.Add(new FieldBinding(key, label, FieldKind.SingleSelect, isRequired, values, otherKey, defaultValue, HasSkipChoice: hasSkip));
                continue;
            }

            if (string.Equals(type, "array", StringComparison.Ordinal))
            {
                if (!TryReadMultiSelect(prop, out var multiOptions))
                {
                    return null;
                }

                var (otherKey, otherFreeform) = TryConsumeCompanionOther(rawProperties, i, key, required, consumed);
                questions.Add(new AgentInteractiveAskQuestion(
                    tabTitle,
                    questionText,
                    detail,
                    [.. multiOptions.Select(static option => new AgentInteractiveAskChoice(option.Label, option.Description))],
                    AllowMultiSelect: true,
                    Freeform: otherFreeform));
                bindings.Add(new FieldBinding(
                    key,
                    label,
                    FieldKind.MultiSelect,
                    isRequired,
                    [.. multiOptions.Select(static option => option.Value)],
                    otherKey,
                    defaultValue,
                    MinItems: AcpJson.GetInt64(prop, "minItems"),
                    MaxItems: AcpJson.GetInt64(prop, "maxItems")));
                continue;
            }

            if (string.Equals(type, "boolean", StringComparison.Ordinal))
            {
                var hasDefault = defaultValue.ValueKind is JsonValueKind.True or JsonValueKind.False;
                var hasSkip = !isRequired && !hasDefault;
                var defaultFalse = defaultValue.ValueKind == JsonValueKind.False;
                List<AgentInteractiveAskChoice> choices = hasSkip
                    ? [new("Leave blank (skip)"), new("Yes"), new("No")]
                    : defaultFalse
                        ? [new("No"), new("Yes")]
                        : [new("Yes"), new("No")];
                List<string> values = hasSkip
                    ? [string.Empty, "true", "false"]
                    : defaultFalse
                        ? ["false", "true"]
                        : ["true", "false"];
                questions.Add(new AgentInteractiveAskQuestion(tabTitle, questionText, detail, choices));
                bindings.Add(new FieldBinding(key, label, FieldKind.Boolean, isRequired, values, null, defaultValue, HasSkipChoice: hasSkip));
                continue;
            }

            if (type is "integer" or "number")
            {
                var kind = type == "integer" ? FieldKind.Integer : FieldKind.Number;
                var placeholder = BuildNumericPlaceholder(prop, kind, defaultValue);
                questions.Add(new AgentInteractiveAskQuestion(
                    tabTitle,
                    questionText,
                    detail,
                    [],
                    AllowMultiSelect: false,
                    Freeform: new AgentInteractiveAskFreeform(Title: null, Placeholder: placeholder)));
                bindings.Add(new FieldBinding(
                    key,
                    label,
                    kind,
                    isRequired,
                    [],
                    null,
                    defaultValue,
                    Minimum: AcpJson.GetDouble(prop, "minimum"),
                    Maximum: AcpJson.GetDouble(prop, "maximum")));
                continue;
            }

            if (string.Equals(type, "string", StringComparison.Ordinal))
            {
                var stringPlaceholder = BuildStringPlaceholder(prop, defaultValue);
                questions.Add(new AgentInteractiveAskQuestion(
                    tabTitle,
                    questionText,
                    detail,
                    [],
                    AllowMultiSelect: false,
                    Freeform: new AgentInteractiveAskFreeform(Title: null, Placeholder: stringPlaceholder)));
                bindings.Add(new FieldBinding(
                    key,
                    label,
                    FieldKind.String,
                    isRequired,
                    [],
                    null,
                    defaultValue,
                    MinLength: AcpJson.GetInt64(prop, "minLength"),
                    MaxLength: AcpJson.GetInt64(prop, "maxLength")));
                continue;
            }

            // ACP v1.34.1 PrimitiveSchemaDefinition only permits string, number, integer, boolean, and string-enum array.
            return null;
        }

        return questions.Count == 0 ? null : new ParsedForm(message, questions, bindings);
    }

    /// <summary>
    /// Validates and builds an ACP <see cref="CreateElicitationResponse"/> from the user's interactive ask response.
    /// Returns <see langword="false"/> with a user-facing <paramref name="validationError"/> when any submitted answer
    /// violates the requested schema.
    /// </summary>
    public static bool TryBuildResponse(
        ParsedForm form,
        AgentInteractiveAskResponse response,
        out CreateElicitationResponse result,
        out string? validationError)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(response);
        if (response.Cancelled)
        {
            result = CreateActionResponse("cancel");
            validationError = null;
            return true;
        }

        var answersByIndex = new Dictionary<int, AgentInteractiveAskAnswer>();
        foreach (var answer in response.Answers)
        {
            answersByIndex[answer.QuestionIndex] = answer;
        }

        var writes = new List<Action<Utf8JsonWriter>>(form.Bindings.Count);
        for (var index = 0; index < form.Bindings.Count; index++)
        {
            var binding = form.Bindings[index];
            answersByIndex.TryGetValue(index, out var answer);
            if (!TryPrepareFieldWrite(binding, answer, out var writeField, out validationError))
            {
                result = default;
                return false;
            }

            if (writeField is not null)
            {
                writes.Add(writeField);
            }
        }

        var content = AcpJson.Build(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("action", "accept");
            writer.WriteStartObject("content");
            foreach (var write in writes)
            {
                write(writer);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        result = new CreateElicitationResponse { Value = content };
        validationError = null;
        return true;
    }

    /// <summary>Builds an ACP <see cref="CreateElicitationResponse"/> from the user's interactive ask response, cancelling on schema validation failure.</summary>
    public static CreateElicitationResponse BuildResponse(ParsedForm form, AgentInteractiveAskResponse response)
        => TryBuildResponse(form, response, out var result, out _) ? result : CreateActionResponse("cancel");

    /// <summary>Builds a response from a legacy <see cref="AgentUserInputResponse"/> (fallback for hosts without interactive asks).</summary>
    public static CreateElicitationResponse BuildResponse(ParsedForm form, AgentUserInputResponse response)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(response);
        var answers = new List<AgentInteractiveAskAnswer>(form.Bindings.Count);
        var anyAnswered = false;
        for (var i = 0; i < form.Bindings.Count; i++)
        {
            var binding = form.Bindings[i];
            if (!response.Answers.TryGetValue(binding.Key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            anyAnswered = true;
            var question = form.Questions[i];
            var selected = new List<int>();
            string? freeform = null;
            if (question.Choices.Count > 0)
            {
                if (binding.Kind == FieldKind.MultiSelect)
                {
                    foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var matched = MatchChoiceIndex(question.Choices, binding.OptionValues, token, binding.HasSkipChoice);
                        if (matched >= 0 && !selected.Contains(matched))
                        {
                            selected.Add(matched);
                        }
                    }

                    if (selected.Count == 0 && binding.CompanionOtherKey is not null)
                    {
                        freeform = raw.Trim();
                    }
                }
                else
                {
                    var matched = MatchChoiceIndex(question.Choices, binding.OptionValues, raw.Trim(), binding.HasSkipChoice);
                    if (matched >= 0)
                    {
                        selected.Add(matched);
                    }
                    else if (binding.CompanionOtherKey is not null)
                    {
                        freeform = raw.Trim();
                    }
                }
            }
            else
            {
                freeform = raw.Trim();
            }

            answers.Add(new AgentInteractiveAskAnswer(i, selected, freeform));
        }

        return anyAnswered
            ? BuildResponse(form, new AgentInteractiveAskResponse(Cancelled: false, answers))
            : CreateActionResponse("cancel");
    }

    /// <summary>Converts a parsed form into a legacy <see cref="AgentUserInputForm"/> for hosts that only supply <c>OnUserInputRequest</c>.</summary>
    public static AgentUserInputForm ToUserInputForm(ParsedForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var prompts = new List<AgentUserInputPrompt>(form.Questions.Count);
        for (var i = 0; i < form.Questions.Count; i++)
        {
            var question = form.Questions[i];
            var binding = form.Bindings[i];
            var options = question.Choices.Count > 0
                ? question.Choices.Select(static choice => new AgentUserInputOption(choice.Title, choice.Description)).ToArray()
                : null;
            prompts.Add(new AgentUserInputPrompt(
                binding.Key,
                question.Question,
                question.Title,
                options,
                AllowFreeform: question.Freeform is not null || options is null));
        }

        return new AgentUserInputForm(prompts);
    }

    /// <summary>Creates a <see cref="CreateElicitationResponse"/> with only an action (<c>cancel</c> or <c>decline</c>).</summary>
    public static CreateElicitationResponse CreateActionResponse(string action)
        => new()
        {
            Value = AcpJson.Build(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("action", action);
                writer.WriteEndObject();
            }),
        };

    private static bool TryPrepareFieldWrite(
        FieldBinding binding,
        AgentInteractiveAskAnswer? answer,
        out Action<Utf8JsonWriter>? writeField,
        out string? error)
    {
        writeField = null;
        error = null;
        var freeform = string.IsNullOrWhiteSpace(answer?.FreeformText) ? null : answer!.FreeformText!.Trim();
        var selected = answer?.SelectedChoiceIndexes ?? [];
        switch (binding.Kind)
        {
            case FieldKind.SingleSelect:
                if (binding.CompanionOtherKey is { } otherKey && freeform is not null)
                {
                    if (binding.Required)
                    {
                        if (!TryGetOptionValue(binding, selected, out var requiredChoice))
                        {
                            error = $"Please select an option for '{binding.Label}'.";
                            return false;
                        }

                        writeField = writer =>
                        {
                            writer.WriteString(otherKey, freeform);
                            writer.WriteString(binding.Key, requiredChoice);
                        };
                    }
                    else
                    {
                        // When the user supplies custom "Other" text on an optional select (such as agy's q1 + q1_other),
                        // send the companion field rather than the radio group's implicit default option.
                        writeField = writer => writer.WriteString(otherKey, freeform);
                    }

                    return true;
                }

                if (TryGetOptionValue(binding, selected, out var chosen))
                {
                    writeField = writer => writer.WriteString(binding.Key, chosen);
                    return true;
                }

                if (binding.HasSkipChoice && IsSkipSelected(selected))
                {
                    return true;
                }

                if (binding.Required)
                {
                    if (binding.DefaultValue.ValueKind == JsonValueKind.String && binding.DefaultValue.GetString() is { } defChoice)
                    {
                        writeField = writer => writer.WriteString(binding.Key, defChoice);
                        return true;
                    }

                    error = $"Please select an option for '{binding.Label}'.";
                    return false;
                }

                return true;

            case FieldKind.MultiSelect:
                var values = new List<string>();
                foreach (var idx in selected.Distinct().Order())
                {
                    if ((uint)idx < (uint)binding.OptionValues.Count)
                    {
                        values.Add(binding.OptionValues[idx]);
                    }
                }

                var hasCompanionOther = binding.CompanionOtherKey is not null && freeform is not null;
                if (binding.MaxItems is { } maxItems && values.Count > maxItems)
                {
                    error = $"Select at most {maxItems} option(s) for '{binding.Label}'.";
                    return false;
                }

                if ((values.Count > 0 || (binding.Required && !hasCompanionOther)) &&
                    binding.MinItems is { } minItems && values.Count < minItems)
                {
                    error = $"Select at least {minItems} option(s) for '{binding.Label}'.";
                    return false;
                }

                if (binding.Required && values.Count == 0 && !hasCompanionOther)
                {
                    error = $"Select at least one option for '{binding.Label}'.";
                    return false;
                }

                if (values.Count > 0 || binding.Required || hasCompanionOther)
                {
                    writeField = writer =>
                    {
                        if (values.Count > 0 || binding.Required)
                        {
                            writer.WriteStartArray(binding.Key);
                            foreach (var value in values)
                            {
                                writer.WriteStringValue(value);
                            }

                            writer.WriteEndArray();
                        }

                        if (binding.CompanionOtherKey is { } multiOtherKey && freeform is not null)
                        {
                            writer.WriteString(multiOtherKey, freeform);
                        }
                    };
                }

                return true;

            case FieldKind.Boolean:
                if (TryGetOptionValue(binding, selected, out var boolValue))
                {
                    var boolResult = string.Equals(boolValue, "true", StringComparison.Ordinal);
                    writeField = writer => writer.WriteBoolean(binding.Key, boolResult);
                    return true;
                }

                if (binding.HasSkipChoice && IsSkipSelected(selected))
                {
                    return true;
                }

                if (binding.Required)
                {
                    if (binding.DefaultValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        var defaultBool = binding.DefaultValue.GetBoolean();
                        writeField = writer => writer.WriteBoolean(binding.Key, defaultBool);
                        return true;
                    }

                    error = $"Please choose Yes or No for '{binding.Label}'.";
                    return false;
                }

                return true;

            case FieldKind.Integer:
                if (freeform is not null)
                {
                    if (!long.TryParse(freeform, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                    {
                        error = $"Enter a valid integer for '{binding.Label}'.";
                        return false;
                    }

                    if ((binding.Minimum is { } minInt && intValue < minInt) ||
                        (binding.Maximum is { } maxInt && intValue > maxInt))
                    {
                        error = FormatNumericRangeError(binding.Label, binding.Minimum, binding.Maximum);
                        return false;
                    }

                    writeField = writer => writer.WriteNumber(binding.Key, intValue);
                    return true;
                }

                if (binding.DefaultValue.ValueKind == JsonValueKind.Number && binding.DefaultValue.TryGetInt64(out var defaultInt))
                {
                    writeField = writer => writer.WriteNumber(binding.Key, defaultInt);
                    return true;
                }

                if (binding.Required)
                {
                    error = $"Please enter an integer for '{binding.Label}'.";
                    return false;
                }

                return true;

            case FieldKind.Number:
                if (freeform is not null)
                {
                    if (!double.TryParse(freeform, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue) || !double.IsFinite(doubleValue))
                    {
                        error = $"Enter a valid number for '{binding.Label}'.";
                        return false;
                    }

                    if ((binding.Minimum is { } minNum && doubleValue < minNum) ||
                        (binding.Maximum is { } maxNum && doubleValue > maxNum))
                    {
                        error = FormatNumericRangeError(binding.Label, binding.Minimum, binding.Maximum);
                        return false;
                    }

                    writeField = writer => writer.WriteNumber(binding.Key, doubleValue);
                    return true;
                }

                if (binding.DefaultValue.ValueKind == JsonValueKind.Number && binding.DefaultValue.TryGetDouble(out var defaultDouble) && double.IsFinite(defaultDouble))
                {
                    writeField = writer => writer.WriteNumber(binding.Key, defaultDouble);
                    return true;
                }

                if (binding.Required)
                {
                    error = $"Please enter a number for '{binding.Label}'.";
                    return false;
                }

                return true;

            case FieldKind.String:
                var effective = freeform ?? (binding.DefaultValue.ValueKind == JsonValueKind.String ? binding.DefaultValue.GetString() : null);
                if (effective is null)
                {
                    if (binding.Required)
                    {
                        error = $"Please enter a value for '{binding.Label}'.";
                        return false;
                    }

                    return true;
                }

                if (binding.MinLength is { } minLen && effective.Length < minLen)
                {
                    error = $"'{binding.Label}' must be at least {minLen} character(s).";
                    return false;
                }

                if (binding.MaxLength is { } maxLen && effective.Length > maxLen)
                {
                    error = $"'{binding.Label}' must be at most {maxLen} character(s).";
                    return false;
                }

                writeField = writer => writer.WriteString(binding.Key, effective);
                return true;

            default:
                return true;
        }
    }

    private static string FormatNumericRangeError(string label, double? minimum, double? maximum)
        => (minimum, maximum) switch
        {
            ({ } min, { } max) => $"'{label}' must be between {min.ToString(CultureInfo.InvariantCulture)} and {max.ToString(CultureInfo.InvariantCulture)}.",
            ({ } min, null) => $"'{label}' must be at least {min.ToString(CultureInfo.InvariantCulture)}.",
            (null, { } max) => $"'{label}' must be at most {max.ToString(CultureInfo.InvariantCulture)}.",
            _ => $"Invalid number for '{label}'.",
        };

    private static bool IsSkipSelected(IReadOnlyList<int> selected) => selected.Count == 0 || selected[0] == 0;

    private static bool TryGetOptionValue(FieldBinding binding, IReadOnlyList<int> selected, out string value)
    {
        foreach (var index in selected)
        {
            if (binding.HasSkipChoice && index == 0)
            {
                value = string.Empty;
                return false;
            }

            if ((uint)index < (uint)binding.OptionValues.Count)
            {
                value = binding.OptionValues[index];
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static int MatchChoiceIndex(IReadOnlyList<AgentInteractiveAskChoice> choices, IReadOnlyList<string> values, string answer, bool hasSkipChoice)
    {
        var start = hasSkipChoice ? 1 : 0;
        for (var i = start; i < choices.Count; i++)
        {
            if (string.Equals(choices[i].Title, answer, StringComparison.OrdinalIgnoreCase) ||
                (i < values.Count && string.Equals(values[i], answer, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }

        return -1;
    }

    // Detects an optional `<key>_other` plain-string companion property immediately after a select/multi-select field
    // (such as `agy acp`'s `q1` + `q1_other`), merging it as the question's freeform "Other" input.
    private static (string? Key, AgentInteractiveAskFreeform? Freeform) TryConsumeCompanionOther(
        List<(string Key, JsonElement Schema)> properties,
        int index,
        string baseKey,
        HashSet<string> required,
        HashSet<int> consumed)
    {
        var expectedKey = baseKey + "_other";
        for (var j = index + 1; j < properties.Count; j++)
        {
            if (consumed.Contains(j))
            {
                continue;
            }

            var (candidateKey, candidateSchema) = properties[j];
            if (!string.Equals(candidateKey, expectedKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (required.Contains(candidateKey) || !IsPlainStringSchema(candidateSchema))
            {
                return (null, null);
            }

            consumed.Add(j);
            var title = AcpJson.GetString(candidateSchema, "title") ?? "Other";
            var placeholder = AcpJson.GetString(candidateSchema, "description");
            return (candidateKey, new AgentInteractiveAskFreeform(title, placeholder));
        }

        return (null, null);
    }

    private static bool IsPlainStringSchema(JsonElement schema)
        => string.Equals(AcpJson.GetString(schema, "type"), "string", StringComparison.Ordinal) &&
           !schema.TryGetProperty("enum", out _) &&
           !schema.TryGetProperty("oneOf", out _);

    private static bool TryReadSingleSelect(JsonElement schema, out List<(string Value, string Label, string? Description)> options)
    {
        options = [];
        var type = AcpJson.GetString(schema, "type");
        if (type is not null && !string.Equals(type, "string", StringComparison.Ordinal))
        {
            return false;
        }

        if (AcpJson.TryGetArray(schema, "oneOf", out var oneOf))
        {
            ReadTitledOptions(oneOf, options);
            return options.Count > 0;
        }

        if (AcpJson.TryGetArray(schema, "enum", out var values))
        {
            var names = AcpJson.TryGetArray(schema, "enumNames", out var enumNames) ? enumNames : default;
            var index = 0;
            foreach (var item in values.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                {
                    var rawLabel = names.ValueKind == JsonValueKind.Array && index < names.GetArrayLength() && names[index].ValueKind == JsonValueKind.String
                        ? names[index].GetString()
                        : null;
                    var label = !string.IsNullOrWhiteSpace(rawLabel) ? rawLabel.Trim()
                        : !string.IsNullOrWhiteSpace(value) ? value.Trim()
                        : $"Option {options.Count + 1}";
                    options.Add((value, label, null));
                }

                index++;
            }

            return options.Count > 0;
        }

        return false;
    }

    private static bool TryReadMultiSelect(JsonElement schema, out List<(string Value, string Label, string? Description)> options)
    {
        options = [];
        if (!AcpJson.TryGetObject(schema, "items", out var items))
        {
            return false;
        }

        if (AcpJson.TryGetArray(items, "anyOf", out var anyOf))
        {
            ReadTitledOptions(anyOf, options);
            return options.Count > 0;
        }

        if (AcpJson.TryGetArray(items, "oneOf", out var oneOf))
        {
            ReadTitledOptions(oneOf, options);
            return options.Count > 0;
        }

        if (AcpJson.TryGetArray(items, "enum", out var values))
        {
            foreach (var item in values.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                {
                    var label = !string.IsNullOrWhiteSpace(value) ? value.Trim() : $"Option {options.Count + 1}";
                    options.Add((value, label, null));
                }
            }

            return options.Count > 0;
        }

        return false;
    }

    private static void ReadTitledOptions(JsonElement array, List<(string Value, string Label, string? Description)> options)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var value = AcpJson.GetString(item, "const");
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var rawLabel = AcpJson.GetString(item, "title");
            var label = !string.IsNullOrWhiteSpace(rawLabel) ? rawLabel.Trim()
                : !string.IsNullOrWhiteSpace(value) ? value.Trim()
                : $"Option {options.Count + 1}";
            var description = AcpJson.GetString(item, "description");
            options.Add((value, label, description));
        }
    }

    private static string? BuildNumericPlaceholder(JsonElement schema, FieldKind kind, JsonElement defaultValue)
    {
        var parts = new List<string>();
        parts.Add(kind == FieldKind.Integer ? "Enter an integer" : "Enter a number");
        var hasMin = schema.TryGetProperty("minimum", out var min) && min.ValueKind == JsonValueKind.Number;
        var hasMax = schema.TryGetProperty("maximum", out var max) && max.ValueKind == JsonValueKind.Number;
        if (hasMin && hasMax)
        {
            parts.Add($"({min.GetRawText()}–{max.GetRawText()})");
        }
        else if (hasMin)
        {
            parts.Add($"(>= {min.GetRawText()})");
        }
        else if (hasMax)
        {
            parts.Add($"(<= {max.GetRawText()})");
        }

        if (defaultValue.ValueKind == JsonValueKind.Number)
        {
            parts.Add($"Default: {defaultValue.GetRawText()}");
        }

        return string.Join(' ', parts);
    }

    private static string? BuildStringPlaceholder(JsonElement schema, JsonElement defaultValue)
    {
        if (defaultValue.ValueKind == JsonValueKind.String && defaultValue.GetString() is { Length: > 0 } def)
        {
            return $"Default: {def}";
        }

        return AcpJson.GetString(schema, "format") switch
        {
            "email" => "name@example.com",
            "uri" => "https://…",
            "date" => "YYYY-MM-DD",
            "date-time" => "YYYY-MM-DDTHH:MM:SSZ",
            _ => null,
        };
    }

    private static string ShortTitle(string text, int index)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return $"Question {index}";
        }

        return trimmed.Length <= 40 ? trimmed : trimmed[..39].TrimEnd() + "…";
    }
}
