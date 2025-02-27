﻿// Original: https://github.com/zymlabs/nswag-fluentvalidation
// MIT License
// Copyright (c) 2019 Zym Labs LLC
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
//         of this software and associated documentation files (the "Software"), to deal
//         in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
//         furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
//         copies or substantial portions of the Software.
//
//         THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//         IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//                                                                 FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//         AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//         LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using FastEndpoints.Swagger.ValidationProcessor;
using FastEndpoints.Swagger.ValidationProcessor.Extensions;
using FluentValidation;
using FluentValidation.Validators;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NJsonSchema;
using NJsonSchema.Generation;
using System.Collections.ObjectModel;

namespace FastEndpoints.Swagger;

public class ValidationSchemaProcessor : ISchemaProcessor
{
    private readonly FluentValidationRule[] _rules;
    private readonly IServiceProvider? _serviceProvider = IServiceResolver.ServiceProvider?.CreateScope().ServiceProvider;
    private readonly Dictionary<string, IValidator> _childAdaptorValidators = new();

    public ValidationSchemaProcessor()
    {
        _rules = CreateDefaultRules();
    }

    public void Process(SchemaProcessorContext context)
    {
        var tRequest = context.ContextualType;

        foreach (var e in MainExtensions.Endpoints.Found)
        {
            if (e.ValidatorType?.BaseType?.GenericTypeArguments.FirstOrDefault() == tRequest)
            {
                var validator = _serviceProvider?.GetRequiredService(e.ValidatorType);
                if (validator is null)
                    throw new InvalidOperationException($"Please call app.{nameof(MainExtensions.UseFastEndpoints)}() before calling app.{nameof(NSwagApplicationBuilderExtensions.UseOpenApi)}()");

                ApplyValidator(context.Schema, (IValidator)validator, "");
                break;
            }
        }
    }

    private void ApplyValidator(JsonSchema schema, IValidator validator, string propertyPrefix)
    {
        // Create dict of rules for this validator
        var rulesDict = validator.GetDictionaryOfRules();
        ApplyRulesToSchema(schema, rulesDict, propertyPrefix);
    }

    private void ApplyRulesToSchema(JsonSchema? schema, ReadOnlyDictionary<string, List<IValidationRule>> rulesDict, string propertyPrefix)
    {
        if (schema is null)
            return;

        // Add properties from current schema/class
        if (schema.ActualProperties != null)
        {
            foreach (var schemaProperty in schema.ActualProperties.Keys)
                TryApplyValidation(schema, rulesDict, schemaProperty, propertyPrefix);
        }

        // Add properties from base class
        ApplyRulesToSchema(schema.InheritedSchema, rulesDict, propertyPrefix);
    }

    private void TryApplyValidation(JsonSchema schema, ReadOnlyDictionary<string, List<IValidationRule>> rulesDict,
        string propertyName, string parameterPrefix)
    {
        // Build the full propertyname with composition route: request.child.property
        var fullPropertyName = $"{parameterPrefix}{propertyName}";

        // Try get a list of valid rules that matches this property name
        if (rulesDict.TryGetValue(fullPropertyName, out var validationRules))
        {
            // Go through each rule and apply it to the schema
            foreach (var validationRule in validationRules)
                ApplyValidationRule(schema, validationRule, propertyName);
        }

        // If the property is a child object, recursively apply validation to it adding prefix as we go down one level
        var property = schema.ActualProperties[propertyName];
        var propertySchema = property.ActualSchema;
        if (propertySchema.ActualProperties is not null && propertySchema.ActualProperties.Count > 0 && propertySchema != schema)
            ApplyRulesToSchema(propertySchema, rulesDict, $"{fullPropertyName}.");
    }

    private void ApplyValidationRule(JsonSchema schema, IValidationRule validationRule, string propertyName)
    {
        foreach (var ruleComponent in validationRule.Components)
        {
            var propertyValidator = ruleComponent.Validator;

            // 1. If the propertyValidator is a ChildValidatorAdaptor (RuleFor(....).SetValidator(new MyChildValidator())), we need to get the underlying validator
            if (propertyValidator.Name == "ChildValidatorAdaptor")
            {
                // Get underlying validator using reflection
                var validatorTypeObj = propertyValidator.GetType()
                    ?.GetProperty("ValidatorType")
                    ?.GetValue(propertyValidator);
                // Check if something went wrong
                if (validatorTypeObj is null || validatorTypeObj is not Type validatorType)
                    throw new InvalidOperationException("ChildValidatorAdaptor.ValidatorType is null");

                // Retrieve or create an instance of the validator
                if (!_childAdaptorValidators.TryGetValue(validatorType.FullName!, out var childValidator))
                    childValidator = _childAdaptorValidators[validatorType.FullName!] = (IValidator)Activator.CreateInstance(validatorType)!;

                // Apply the validator to the schema. Again, recursively
                ApplyValidator(schema.ActualProperties[propertyName].ActualSchema, childValidator, "");

                continue;
            }

            // 2. Normal property validator processing
            foreach (var rule in _rules)
            {
                if (!rule.Matches(propertyValidator))
                    continue;

                try
                {
                    rule.Apply(new RuleContext(schema, propertyName, propertyValidator));
                }
                catch { }
            }
        }
    }

    private static FluentValidationRule[] CreateDefaultRules() => new[]
    {
        new FluentValidationRule("Required")
        {
            Matches = propertyValidator => propertyValidator is INotNullValidator or INotEmptyValidator,
            Apply = context =>
            {
                var schema = context.Schema;
                if (schema == null)
                    return;
                if (!schema.RequiredProperties.Contains(context.PropertyKey))
                    schema.RequiredProperties.Add(context.PropertyKey);
            }
        },
        new FluentValidationRule("NotNull")
        {
            Matches = propertyValidator => propertyValidator is INotNullValidator,
            Apply = context =>
            {
                var schema = context.Schema;
                var properties = schema.ActualProperties;
                properties[context.PropertyKey].IsNullableRaw = false;
                if (properties[context.PropertyKey].Type.HasFlag(JsonObjectType.Null))
                    properties[context.PropertyKey].Type &= ~JsonObjectType.Null; // Remove nullable
                var oneOfsWithReference = properties[context.PropertyKey].OneOf
                    .Where(x => x.Reference != null)
                    .ToList();
                if (oneOfsWithReference.Count == 1)
                {
                    // Set the Reference directly instead and clear the OneOf collection
                    properties[context.PropertyKey].Reference = oneOfsWithReference.Single();
                    properties[context.PropertyKey].OneOf.Clear();
                }
            }
        },
        new FluentValidationRule("NotEmpty")
        {
            Matches = propertyValidator => propertyValidator is INotEmptyValidator,
            Apply = context =>
            {
                var schema = context.Schema;
                var properties = schema.ActualProperties;
                properties[context.PropertyKey].IsNullableRaw = false;
                if (properties[context.PropertyKey].Type.HasFlag(JsonObjectType.Null))
                    properties[context.PropertyKey].Type &= ~JsonObjectType.Null; // Remove nullable
                var oneOfsWithReference = properties[context.PropertyKey].OneOf
                    .Where(x => x.Reference != null)
                    .ToList();
                if (oneOfsWithReference.Count == 1)
                {
                    // Set the Reference directly instead and clear the OneOf collection
                    properties[context.PropertyKey].Reference = oneOfsWithReference.Single();
                    properties[context.PropertyKey].OneOf.Clear();
                }
                properties[context.PropertyKey].MinLength = 1;
            }
        },
        new FluentValidationRule("Length")
        {
            Matches = propertyValidator => propertyValidator is ILengthValidator,
            Apply = context =>
            {
                var schema = context.Schema;
                var properties = schema.ActualProperties;
                var lengthValidator = (ILengthValidator)context.PropertyValidator;
                if (lengthValidator.Max > 0)
                    properties[context.PropertyKey].MaxLength = lengthValidator.Max;
                if (lengthValidator.GetType() == typeof(MinimumLengthValidator<>) ||
                    lengthValidator.GetType() == typeof(ExactLengthValidator<>) ||
                    properties[context.PropertyKey].MinLength == null)
                {
                    properties[context.PropertyKey].MinLength = lengthValidator.Min;
                }
            }
        },
        new FluentValidationRule("Pattern")
        {
            Matches = propertyValidator => propertyValidator is IRegularExpressionValidator,
            Apply = context =>
            {
                var regularExpressionValidator = (IRegularExpressionValidator)context.PropertyValidator;
                var schema = context.Schema;
                var properties = schema.ActualProperties;
                properties[context.PropertyKey].Pattern = regularExpressionValidator.Expression;
            }
        },
        new FluentValidationRule("Comparison")
        {
            Matches = propertyValidator => propertyValidator is IComparisonValidator,
            Apply = context =>
            {
                var comparisonValidator = (IComparisonValidator)context.PropertyValidator;
                if (comparisonValidator.ValueToCompare.IsNumeric())
                {
                    var valueToCompare = Convert.ToDecimal(comparisonValidator.ValueToCompare);
                    var schema = context.Schema;
                    var properties = schema.ActualProperties;
                    var schemaProperty = properties[context.PropertyKey];
                    if (comparisonValidator.Comparison == Comparison.GreaterThanOrEqual)
                    {
                        schemaProperty.Minimum = valueToCompare;
                    }
                    else if (comparisonValidator.Comparison == Comparison.GreaterThan)
                    {
                        schemaProperty.Minimum = valueToCompare;
                        schemaProperty.IsExclusiveMinimum = true;
                    }
                    else if (comparisonValidator.Comparison == Comparison.LessThanOrEqual) { schemaProperty.Maximum = valueToCompare; } else if (comparisonValidator.Comparison == Comparison.LessThan)
                    {
                        schemaProperty.Maximum = valueToCompare;
                        schemaProperty.IsExclusiveMaximum = true;
                    }
                }
            }
        },
        new FluentValidationRule("Between")
        {
            Matches = propertyValidator => propertyValidator is IBetweenValidator,
            Apply = context =>
            {
                var betweenValidator = (IBetweenValidator)context.PropertyValidator;
                var schema = context.Schema;
                var properties = schema.ActualProperties;
                var schemaProperty = properties[context.PropertyKey];
                if (betweenValidator.From.IsNumeric())
                {
                    if (betweenValidator.GetType().IsSubClassOfGeneric(typeof(ExclusiveBetweenValidator<,>)))
                        schemaProperty.ExclusiveMinimum = Convert.ToDecimal(betweenValidator.From);
                    else
                        schemaProperty.Minimum = Convert.ToDecimal(betweenValidator.From);
                }
                if (betweenValidator.To.IsNumeric())
                {
                    if (betweenValidator.GetType().IsSubClassOfGeneric(typeof(ExclusiveBetweenValidator<,>)))
                        schemaProperty.ExclusiveMaximum = Convert.ToDecimal(betweenValidator.To);
                    else
                        schemaProperty.Maximum = Convert.ToDecimal(betweenValidator.To);
                }
            }
        },
        new FluentValidationRule("AspNetCoreCompatibleEmail")
        {
            Matches = propertyValidator => propertyValidator.GetType().IsSubClassOfGeneric(typeof(AspNetCoreCompatibleEmailValidator<>)),
            Apply = context =>
            {
                var schema = context.Schema;
                var properties = schema.ActualProperties;
                properties[context.PropertyKey].Pattern = "^[^@]+@[^@]+$"; // [^@] All chars except @
            }
        },
    };
}