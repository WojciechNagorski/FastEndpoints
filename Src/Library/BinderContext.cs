﻿#pragma warning disable CA1822 

using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastEndpoints;

/// <summary>
/// binder context supplied to custom request binders.
/// </summary>
public struct BinderContext : IServiceResolver
{
    /// <summary>
    /// the http context of the current request
    /// </summary>
    public HttpContext HttpContext { get; init; }

    /// <summary>
    /// a list of validation failures for the endpoint. you can add your own validation failures for properties of the request dto using this property.
    /// </summary>
    public List<ValidationFailure> ValidationFailures { get; init; }

    /// <summary>
    /// the configured json serializer options of the app, which was specified at app startup.
    /// </summary>
    public JsonSerializerOptions SerializerOptions => Config.SerializerOpts;

    /// <summary>
    /// if the current endpoint is configured with a json serializer context, it will be provided to the custom request binder with this property.
    /// </summary>
    public JsonSerializerContext? JsonSerializerContext { get; init; }

    public BinderContext(HttpContext httpContext, List<ValidationFailure> validationFailures, JsonSerializerContext? jsonSerializerContext)
    {
        HttpContext = httpContext;
        ValidationFailures = validationFailures;
        JsonSerializerContext = jsonSerializerContext;
    }

    /// <summary>
    /// try to resolve an instance for the given type from the dependency injection container. will return null if unresolvable.
    /// </summary>
    /// <typeparam name="TService">the type of the service to resolve</typeparam>
    public TService? TryResolve<TService>() where TService : class
        => HttpContext.RequestServices.GetService<TService>();

    /// <summary>
    /// try to resolve an instance for the given type from the dependency injection container. will return null if unresolvable.
    /// </summary>
    /// <param name="typeOfService">the type of the service to resolve</param>
    public object? TryResolve(Type typeOfService)
        => HttpContext.RequestServices.GetService(typeOfService);

    /// <summary>
    /// resolve an instance for the given type from the dependency injection container. will throw if unresolvable.
    /// </summary>
    /// <typeparam name="TService">the type of the service to resolve</typeparam>
    /// <exception cref="InvalidOperationException">Thrown if requested service cannot be resolved</exception>
    public TService Resolve<TService>() where TService : class
        => HttpContext.RequestServices.GetRequiredService<TService>();

    /// <summary>
    /// resolve an instance for the given type from the dependency injection container. will throw if unresolvable.
    /// </summary>
    /// <param name="typeOfService">the type of the service to resolve</param>
    /// <exception cref="InvalidOperationException">Thrown if requested service cannot be resolved</exception>
    public object Resolve(Type typeOfService)
        => HttpContext.RequestServices.GetRequiredService(typeOfService);
}
